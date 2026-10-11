"""Synthetic contract fixtures; no real-run or Databricks execution claims."""
import copy
import json
from pathlib import Path
import tempfile
import unittest

from analytics.choice import candidate_matches, matching_indices
from analytics.conditions import comparison_conditions
from analytics.export import canonical, export_run
from analytics.report import build_report
from analytics.schema import nullable_additions
from analytics.transform import typed_record
from test_pipeline import fixture, fake_fetch


def feature_metadata():
    return {"seed": 7, "mode": "RULE", "tick_limit": 35, "initial_population": 200,
            "engine_commit": "a" * 40, "worker_capacity": 8, "tick_interval_ms": 100,
            "compute_providers": [], "government_rotation": 0,
            "capture_contract_version": "gearth.public_services.v1",
            "features": {"public_services": "v1", "bounded_integer_choices": "v1"},
            "active_features": ["school", "public_services", "planner_quotas", "bounded_integer_choices"],
            "banking_paused": True, "economy_configuration": {
                "school": {"course_days": 28}, "health": {"sick_below": 40},
                "quality_of_life": {"parts_basis": "snapshot_state_recomputed"},
                "planning": {"planning_weeks": 4}, "banking": {"paused": True},
                "accounting": {"deposit_minor": "claim_on_bank_cash_not_additional_money"}}}


class ChoicesAndConditionsTests(unittest.TestCase):
    def test_bounded_integer_choice_preserves_fixed_keys_and_exact_types(self):
        candidate = {"action_type": "set_plan", "params": {"sector": "school", "workers": {"min": 0, "max": 12, "default": 3}}}
        for value in (0, 3, 12):
            self.assertTrue(candidate_matches(candidate, {"action_type": "set_plan", "params": {"sector": "school", "workers": value}}))
        for value in (-1, 13, True, 3.0, "3", None, {"min": 0, "max": 12}):
            self.assertFalse(candidate_matches(candidate, {"action_type": "set_plan", "params": {"sector": "school", "workers": value}}))
        self.assertFalse(candidate_matches(candidate, {"action_type": "set_plan", "params": {"workers": 3}}))
        self.assertFalse(candidate_matches(candidate, {"action_type": "set_plan", "params": {"sector": "clinic", "workers": 3}}))
        self.assertFalse(candidate_matches({"action_type": "x", "params": {"a": 1}}, {"action_type": "x", "params": {"a": True}}))
        self.assertFalse(candidate_matches({"action_type": "x", "params": {"a": {"min": 0, "max": 2**31}}}, {"action_type": "x", "params": {"a": 1}}))
        action = {"action_type": "set_plan", "params": {"sector": "school", "workers": 5}}
        self.assertEqual(matching_indices([candidate, candidate], action), [0, 1])

    def test_condition_signature_matches_order_but_distinguishes_features_config_commit(self):
        metadata = feature_metadata()
        expected = comparison_conditions(metadata)
        self.assertTrue(expected["known"])
        reordered = copy.deepcopy(metadata)
        reordered.update(seed=99, government_rotation=4, active_features=list(reversed(metadata["active_features"])))
        self.assertEqual(comparison_conditions(reordered)["signature"], expected["signature"])
        for key, value in (("engine_commit", "b" * 40), ("features", {"public_services": "v2", "bounded_integer_choices": "v1"}),
                           ("active_features", ["public_services"]), ("economy_configuration", {**metadata["economy_configuration"], "school": {"course_days": 14}})):
            changed = copy.deepcopy(metadata)
            changed[key] = value
            self.assertNotEqual(comparison_conditions(changed)["signature"], expected["signature"])

    def test_missing_legacy_and_model_and_banking_conditions_are_unknown(self):
        self.assertFalse(comparison_conditions(fixture()[1][0]["payload"])["known"])
        metadata = feature_metadata()
        metadata["mode"] = "MODEL"
        self.assertIn("model_assignment", comparison_conditions(metadata)["unknown"])
        metadata["compute_providers"] = [{"model": "known-model"}]
        self.assertTrue(comparison_conditions(metadata)["known"])
        metadata["banking_paused"] = False
        self.assertIn("banking_flag_configuration_agreement", comparison_conditions(metadata)["unknown"])
        metadata["engine_commit"] = ""
        self.assertIn("engine_commit_valid_sha", comparison_conditions(metadata)["unknown"])
        metadata["seed"] = None
        self.assertIn("seed_pairing_key", comparison_conditions(metadata)["unknown"])

    def test_schema_evolution_adds_only_missing_nullable_projections(self):
        old = {"run_id": "string", "payload_json": "string", "record_sha256": "string", "cash_minor": "bigint"}
        new = {**old, "is_student": "boolean", "offered_params_json": "string"}
        self.assertEqual(nullable_additions(old, new), {"is_student": "boolean", "offered_params_json": "string"})
        self.assertEqual(nullable_additions(new, new), {})
        self.assertEqual(old["cash_minor"], "bigint")
        with self.assertRaisesRegex(ValueError, "Unsafe type change"):
            nullable_additions(old, {**new, "cash_minor": "double"})


class PublicServicesReportTests(unittest.TestCase):
    def setUp(self):
        self.metadata, self.records = fixture()
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def report(self):
        export_run("http://localhost", self.metadata["run_id"], self.root / "export", fetch=fake_fetch(self.metadata, self.records))
        return build_report(self.root / "export" / "manifest.json")

    def bounded_decision(self):
        request, result = self.records[4]["payload"], self.records[5]["payload"]
        request["menu_parameter_contract"] = "fixed_or_bounded_integer.v1"
        request["job"]["candidates"] = [{"action_type": "set_plan", "params": {"sector": "school", "workers": {"min": 0, "max": 12, "default": 3}}}]
        params = {"sector": "school", "workers": 5}
        result["result"]["action"].update(action_type="set_plan", params=params)
        result.update(selected_params=params, selection_match_count=1, selection_provenance="matched original menu")

    def test_bounded_report_checks_and_raw_option_descriptors(self):
        self.bounded_decision()
        _, request = typed_record(self.records[4])
        _, result = typed_record(self.records[5])
        self.assertEqual(json.loads(request["options_json"])[0]["params"]["workers"], {"min": 0, "max": 12, "default": 3})
        self.assertEqual(json.loads(result["selected_params_json"])["workers"], 5)
        self.assertEqual(json.loads(request["payload_json"]), self.records[4]["payload"])
        report = self.report()
        check = next(row for row in report["checks"] if row["name"] == "offered_choice_index")
        self.assertEqual(check["status"], "pass")
        self.assertEqual(check["details"]["bounded_integer_choices"], 1)

    def test_outside_bound_and_forged_selected_params_fail_check_not_raw_retention(self):
        self.bounded_decision()
        self.records[5]["payload"]["result"]["action"]["params"] = {"sector": "school", "workers": 13}
        report = self.report()
        self.assertEqual(next(row["status"] for row in report["checks"] if row["name"] == "offered_choice_index"), "fail")

    def test_ambiguous_index_cannot_claim_unique_numbered_choice(self):
        self.bounded_decision()
        self.records[4]["payload"]["job"]["candidates"] *= 2
        self.records[5]["payload"]["selection_match_count"] = 2
        check = next(row for row in self.report()["checks"] if row["name"] == "offered_choice_index")
        self.assertEqual(check["status"], "fail")
        self.assertEqual(check["details"]["ambiguous_matches"], 1)

    def test_services_facts_transfers_and_deposit_claims_are_distinct(self):
        features = feature_metadata()
        for key in ("capture_contract_version", "features", "active_features", "banking_paused", "economy_configuration"):
            self.records[0]["payload"][key] = self.metadata[key] = features[key]
        for record in self.records:
            if record["kind"] == "agent_day":
                record["payload"].update(is_student=record["payload"]["agent_id"] == "a", graduated=False, seeking_better_job=True,
                    hungry=False, needs_met=0.5, school_id="school", student_until_tick=28, too_sick=False,
                    qol_parts_points={"needs": 25.5, "security": 2}, deposit_minor=900, in_kind_minor=10)
        self.records[6]["payload"]["kind"] = "tuition_subsidy"
        self.records[9]["payload"]["kind"] = "hiring_bonus"
        report = self.report()
        services = report["public_services"]
        self.assertEqual(report["analysis_version"], "1.2.0")
        self.assertEqual(services["metadata"]["economy_configuration"], features["economy_configuration"])
        self.assertEqual(services["coverage"]["status"], "pass")
        self.assertEqual(services["snapshot_summary"]["students"], {"observed_count": 1, "eligible_known_count": 1, "missing_count": 0})
        self.assertEqual({item["kind"]: item["amount_minor"] for item in services["transfers_by_kind"]}, {"tuition_subsidy": 20, "hiring_bonus": 5})
        self.assertEqual(report["summary"]["transfer_minor"], 25)
        self.assertEqual(next(row for row in report["checks"] if row["name"] == "cash_plus_escrow_conservation")["details"]["latest_minor"], 200)
        self.assertEqual(report["agents"][0]["public_services"]["deposit_minor"], 900)
        self.assertEqual(typed_record(self.records[7])[1]["needs_met"], 0.5)

    def test_legacy_run_has_null_features_and_unknown_service_coverage(self):
        report = self.report()
        self.assertTrue(all(value is None for value in report["public_services"]["metadata"].values()))
        self.assertEqual(report["public_services"]["coverage"]["status"], "unknown")
        self.assertIsNone(report["agents"][0]["public_services"]["is_student"])
        self.assertEqual(report["public_services"]["snapshot_summary"]["students"]["missing_count"], 1)

    def test_partial_feature_coverage_is_explicit(self):
        self.records[7]["payload"].update(is_student=True, hungry=False, qol_parts_points={})
        self.assertEqual(self.report()["public_services"]["coverage"]["status"], "partial")

    def test_manifest_feature_metadata_cannot_replace_original_evidence(self):
        self.metadata["banking_paused"] = True
        with self.assertRaisesRegex(ValueError, "source metadata differs"):
            self.report()

    def test_invalid_typed_fact_is_rejected_before_spark_can_cast_to_null(self):
        self.records[7]["payload"]["student_until_tick"] = "28"
        with self.assertRaisesRegex(ValueError, "Invalid typed snapshot fact"):
            self.report()

    def test_accounting_description_exception_never_allows_text_agent_claim(self):
        self.records[7]["payload"]["deposit_minor"] = "claim_on_bank_cash_not_additional_money"
        with self.assertRaisesRegex(ValueError, "integer minor"):
            self.report()

    def add_created_firm(self, private=False):
        def row(kind, payload):
            return {"run_id": self.metadata["run_id"], "sequence": 0, "tick": 1, "revision": 2, "kind": kind, "payload": payload}
        opening = row("opening_balance", {"account_id": "school", "account_type": "created_agent", "balance_minor": 0,
            "creation_tick": 1, "creation_source": "private_firm" if private else "state_firm",
            "agent": {"agent_id": "school", "role": "business", "country_id": "n1", "cash_minor": 0}})
        new = [opening, row("transaction", {"transaction_id": "school-funding", "sender_account_id": "a",
            "recipient_account_id": "school", "amount_minor": 10, "kind": "founding_capital"})]
        if private:
            new.append(row("event", {"event_id": "e2", "seq": 2, "event_type": "business_started", "payload": {
                "account_id": "school", "opening_cash_minor": 0}}))
            self.metadata["through_event_sequence"] = 2
        new.append(row("agent_day", {"agent_id": "school", "role": "business", "country_id": "n1", "cash_minor": 10, "frame_kind": "day"}))
        self.records[7]["payload"]["cash_minor"] -= 10
        self.records[10]["payload"]["cash_minor"] -= 10
        self.records[7:7] = new
        self.records.insert(len(self.records) - 2, row("agent_day", {"agent_id": "school", "role": "business", "country_id": "n1", "cash_minor": 10, "frame_kind": "end"}))
        for index, record in enumerate(self.records):
            record["sequence"] = index + 1
        self.metadata.update(through_sequence=len(self.records), population=3)

    def test_created_state_firm_is_known_with_daily_and_final_reconciliation(self):
        self.add_created_firm()
        report = self.report()
        checks = {check["name"]: check["status"] for check in report["checks"]}
        self.assertEqual(checks["daily_account_reconciliation"], "pass")
        self.assertEqual(checks["ledger_account_reconciliation"], "pass")
        self.assertEqual(checks["cash_plus_escrow_conservation"], "pass")
        self.assertEqual(report["summary"]["agent_count"], 3)

    def test_created_private_firm_and_legacy_event_share_creation_fact(self):
        self.add_created_firm(private=True)
        self.assertEqual(next(check["status"] for check in self.report()["checks"] if check["name"] == "ledger_account_reconciliation"), "pass")

    def test_created_firm_missing_daily_snapshot_is_not_certified(self):
        self.add_created_firm()
        self.records = [record for record in self.records if not (record["kind"] == "agent_day" and record["payload"]["agent_id"] == "school" and record["payload"]["frame_kind"] == "day")]
        for index, record in enumerate(self.records):
            record["sequence"] = index + 1
        self.metadata["through_sequence"] = len(self.records)
        with self.assertRaisesRegex(ValueError, "missing daily agent snapshot"):
            self.report()

    def test_created_firm_opening_after_funding_is_rejected(self):
        self.add_created_firm()
        self.records[7], self.records[8] = self.records[8], self.records[7]
        for index, record in enumerate(self.records):
            record["sequence"] = index + 1
        with self.assertRaisesRegex(ValueError, "must precede transfers"):
            self.report()


if __name__ == "__main__":
    unittest.main()
