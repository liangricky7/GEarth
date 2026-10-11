"""v4 additions preserve recorded clinic/credit facts and earlier v3 evidence."""
import copy
import json
from pathlib import Path
import tempfile
import unittest

from analytics.conditions import comparison_conditions
from analytics.export import export_run
from analytics.report import build_report
from analytics.schema import economy_backfill_fields
from analytics.transform import (CAPTURE_VERSION, CAPTURE_VERSION_V3, REQUIRED_AGENT_V3, REQUIRED_AGENT_V4,
    REQUIRED_COUNTRY_V3, REQUIRED_COUNTRY_V4, capture_requirements, typed_record, validate_capture)
from test_economy_capture import record
from test_pipeline import fixture, fake_fetch
from test_public_services import feature_metadata


def credit(**changes):
    return {"id": "loan1", "kind": "treasury", "bank_id": "bank_a", "borrower_id": "government_b",
        "principal_minor": 10000, "principal_left_minor": 9000, "weekly_principal_minor": 500,
        "weekly_interest_minor": 10, "rate_pct": 2, "missed_payments": 4, "provision_minor": 1800,
        "sector": None, "launch_price_minor": 0, "requested_tick": 7, "request_event_id": "request1",
        "status": "active", "deposit_created": True, "bank_country_id": "a", "borrower_country_id": "b", **changes}


def metadata_v3():
    metadata = feature_metadata()
    metadata["capture_contract_version"] = CAPTURE_VERSION_V3
    metadata["features"].update({name: "v1" for name in ("infrastructure", "part_time_school", "wage_arrears",
        "price_response", "currency_trade", "war_diplomacy")})
    metadata["economy_configuration"].update(labor={"arrears_strike_weeks": 2}, infrastructure={"phase": 2},
        currency_trade={"step": 0.01}, war_diplomacy={"unrest_days": 28})
    metadata["economy_configuration"]["school"]["part_time_course_days"] = 56
    return metadata


class FeedbackCaptureTests(unittest.TestCase):
    def test_nutrition_production_spoilage_and_bank_provision_values_retain_applicability(self):
        payload = {"agent_id": "bank", "cash_minor": 0, "nutrition_debt_days": 0,
            "production_remainder": 0.125, "food_spoilage_remainder": 99, "loan_loss_provisions_minor": 1800}
        row = typed_record(record("agent_day", payload))[1]
        self.assertEqual((row["nutrition_debt_days"], row["production_remainder"], row["food_spoilage_remainder"],
            row["loan_loss_provisions_minor"]), (0, 0.125, 99, 1800))
        self.assertEqual(json.loads(row["payload_json"]), {"frame_kind": "day", **payload})
        self.assertIsNone(typed_record(record("agent_day", {**payload, "loan_loss_provisions_minor": None}))[1]["loan_loss_provisions_minor"])
        old = typed_record(record("agent_day", {"agent_id": "a", "cash_minor": 0}))[1]
        self.assertIsNone(old["nutrition_debt_days"])
        self.assertIsNone(old["loan_loss_provisions_minor"])
        self.assertIn("loan_loss_provisions_minor", economy_backfill_fields("workspace.gearth_silver.agent_daily"))
        for changes in ({"nutrition_debt_days": True}, {"production_remainder": "0.1"}, {"loan_loss_provisions_minor": False}):
            with self.subTest(changes=changes), self.assertRaisesRegex(ValueError, "Invalid typed snapshot fact"):
                typed_record(record("agent_day", {**payload, **changes}))

    def test_clinic_feedback_credit_positions_preserved_without_cross_country_aggregation(self):
        last = {"tick": 7, "capacity": 10, "visits": 0, "untreated_patient_days": 3, "paid_minor": 0}
        history = [{"tick": 8, "from_minor": 50, "to_minor": 60, "before": last}]
        positions = [credit(), credit(id="loan2", status="repaid", provision_minor=0, principal_left_minor=0)]
        payload = {"id": "a", "clinic_capacity_this_week": 0, "untreated_patient_days_this_week": 0,
            "clinic_last_week": last, "clinic_funding_history": history,
            "clinic_funding": {"last_completed_week": last, "full_weeks_since_change": 0, "cheapest_visit_minor": None},
            "credit_positions": positions}
        first = typed_record(record("nation_day", payload))[1]
        second = typed_record(record("nation_day", {**payload, "id": "b"}))[1]
        self.assertEqual(first["clinic_capacity_this_week"], 0)
        for name in ("clinic_last_week", "clinic_funding_history", "clinic_funding", "credit_positions"):
            self.assertEqual(json.loads(first[name + "_json"]), payload[name])
        # Same cross-border claim is intentionally visible from both countries;
        # projection never sums positions or manufactures a global asset total.
        self.assertEqual(first["credit_positions_json"], second["credit_positions_json"])
        self.assertEqual(len(json.loads(first["credit_positions_json"])), 2)
        self.assertEqual(json.loads(first["credit_positions_json"])[0]["provision_minor"], 1800)
        old = typed_record(record("nation_day", {"id": "a"}))[1]
        self.assertIsNone(old["credit_positions_json"])
        explicit = typed_record(record("nation_day", {"id": "a", "clinic_last_week": None, "credit_positions": []}))[1]
        self.assertEqual(explicit["clinic_last_week_json"], "null")
        self.assertEqual(explicit["credit_positions_json"], "[]")

    def test_v3_contract_and_conditions_do_not_require_v4_history(self):
        before = metadata_v3()
        known = comparison_conditions(before)
        self.assertTrue(known["known"])
        value = record("agent_day", {"agent_id": "a", "cash_minor": 0, **{name: None for name in REQUIRED_AGENT_V3}})
        validate_capture(value, CAPTURE_VERSION_V3)
        with self.assertRaisesRegex(ValueError, "capture omitted fields"):
            validate_capture(value, CAPTURE_VERSION)
        value["payload"].update({name: None for name in REQUIRED_AGENT_V4})
        validate_capture(value, CAPTURE_VERSION)
        self.assertNotIn("credit_positions", capture_requirements("nation_day", CAPTURE_VERSION_V3))
        self.assertIn("credit_positions", capture_requirements("nation_day", CAPTURE_VERSION))
        metadata = copy.deepcopy(before)
        metadata["capture_contract_version"] = CAPTURE_VERSION
        self.assertFalse(comparison_conditions(metadata)["known"])
        metadata["features"].update({key: "v1" for key in ("nutrition_recovery", "clinic_feedback", "loan_provisions", "infrastructure_repairs")})
        metadata["economy_configuration"].update({key: {"version": "v1"} for key in ("nutrition_recovery", "clinic_feedback", "loan_provisions")})
        metadata["economy_configuration"]["health"]["starvation_days"] = 14
        metadata["economy_configuration"]["infrastructure"].update(maintenance_basis="funded_prevents_wear",
            repair_max_condition_per_week=10, repair_builders_required=1, repair_cost_fraction_per_condition=0.005,
            repair_materials_basis="ceil_scaled_materials")
        self.assertTrue(comparison_conditions(metadata)["known"])
        self.assertNotEqual(comparison_conditions(metadata)["signature"], known["signature"])
        self.assertEqual(comparison_conditions(before), known)

    def test_report_coverage_uses_original_v3_or_v4_contract_and_new_analysis_version(self):
        for version in (CAPTURE_VERSION_V3, CAPTURE_VERSION):
            with self.subTest(version=version), tempfile.TemporaryDirectory() as temp:
                metadata, records = fixture()
                metadata["capture_contract_version"] = records[0]["payload"]["capture_contract_version"] = version
                for item in records:
                    item["payload"].update({key: None for key in capture_requirements(item["kind"], version)})
                export = Path(temp) / "export"
                export_run("http://localhost", metadata["run_id"], export, fetch=fake_fetch(metadata, records))
                report = build_report(export / "manifest.json")
                self.assertEqual(report["analysis_version"], "1.4.0")
                agent = report["economy_capture"]["fields"]["agent_day"]
                country = report["economy_capture"]["fields"]["nation_day"]
                self.assertEqual("nutrition_debt_days" in agent, version == CAPTURE_VERSION)
                self.assertEqual("credit_positions" in country, version == CAPTURE_VERSION)

    def test_repair_order_array_presence_empty_and_legacy_missing(self):
        for value in ([], ["roads1"]):
            row = typed_record(record("nation_day", {"id": "a", "repair_orders": value}))[1]
            self.assertEqual(json.loads(row["repair_orders_json"]), value)
        self.assertIsNone(typed_record(record("nation_day", {"id": "a"}))[1]["repair_orders_json"])
        self.assertNotIn("repair_orders", REQUIRED_COUNTRY_V3)
        self.assertIn("repair_orders", REQUIRED_COUNTRY_V4)
        self.assertIn("repair_orders_json", economy_backfill_fields("workspace.gearth_silver.country_daily"))
        with self.assertRaisesRegex(ValueError, "Invalid infrastructure repair order"):
            typed_record(record("nation_day", {"id": "a", "repair_orders": [False]}))

    def test_declared_v4_credit_schema_cannot_omit_provision(self):
        payload = {key: None for key in REQUIRED_COUNTRY_V4}
        payload.update(id="a", credit_positions=[credit()])
        value = record("nation_day", payload)
        validate_capture(value, CAPTURE_VERSION)
        del payload["credit_positions"][0]["provision_minor"]
        with self.assertRaisesRegex(ValueError, "omitted credit position fields"):
            validate_capture(value, CAPTURE_VERSION)
        # Previous v3 has no new mandatory collection contract.
        validate_capture(value, CAPTURE_VERSION_V3)

    def test_malformed_clinic_credit_objects_rejected_before_spark(self):
        for payload in ({"clinic_last_week": {"paid_minor": "0"}}, {"clinic_funding_history": [None]},
            {"clinic_funding_history": [{"before": 1}]}, {"clinic_funding": {"full_weeks_since_change": True}},
            {"credit_positions": [credit(provision_minor=True)]}, {"credit_positions": [credit(), credit()]},
            {"credit_positions": [None]}, {"credit_positions": [credit(status=False)]}):
            with self.subTest(payload=payload), self.assertRaises(ValueError):
                typed_record(record("nation_day", {"id": "a", **payload}))
