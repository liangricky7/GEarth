"""New main war projections with genuine absence preserved in older capture."""
import copy
import json
from pathlib import Path
import tempfile
import unittest

from analytics.conditions import comparison_conditions
from analytics.export import export_run
from analytics.report import build_report
from analytics.schema import economy_backfill_fields
from analytics.transform import (CAPTURE_VERSION_V3 as CAPTURE_VERSION, CAPTURE_VERSION_V2, REQUIRED_AGENT_V2, REQUIRED_AGENT_V3,
    REQUIRED_COUNTRY_V3, capture_requirements, typed_record, validate_capture)
from test_economy_capture import record
from test_pipeline import fixture, fake_fetch
from test_public_services import feature_metadata


class WarCaptureTests(unittest.TestCase):
    def test_war_agent_and_country_columns_preserve_actual_values_and_json(self):
        agent = typed_record(record("agent_day", {"agent_id": "soldier", "cash_minor": 0,
            "war_weariness": 0.2, "military_paid_tick": -1, "military_pay_minor": 0}))[1]
        self.assertEqual((agent["war_weariness"], agent["military_paid_tick"], agent["military_pay_minor"]), (0.2, -1, 0))
        relationship = {"id": "a-b", "first": "a", "second": "b", "score": -25, "trade_agreement": False,
            "at_war": True, "aggressor": "a", "proposal_terms": None, "delivered_value_minor": 0}
        payload = {"id": "a", "missiles": 0, "war_weeks": 1, "no_fit_soldier_weeks": 0, "war_weariness": 0.1,
            "unrest_since_tick": -1, "tax_mark_minor": 123, "war_accounting_initialized": True,
            "government_id": "market_democracy", "leadership_term": 2, "next_election_tick": 100, "wars": 1,
            "relationships": [relationship], "military": {"soldiers_fit": 0, "strength": 0, "weekly_salary_minor": 0},
            "mission": {"average_quality_of_life_at_least": 40, "average_quality_of_life": 22.5,
                "unrest_since_tick": None, "unrest_comes_to_a_head_tick": None}}
        row = typed_record(record("nation_day", payload))[1]
        self.assertEqual(row["tax_mark_minor"], 123)
        self.assertIs(row["war_accounting_initialized"], True)
        for name in ("relationships", "military", "mission"):
            self.assertEqual(json.loads(row[name + "_json"]), payload[name])
        self.assertEqual(json.loads(row["payload_json"])["relationships"][0]["score"], -25)
        old = typed_record(record("nation_day", {"id": "a"}))[1]
        self.assertIsNone(old["relationships_json"])
        self.assertIsNone(old["missiles"])
        self.assertEqual(typed_record(record("nation_day", {"id": "a", "relationships": []}))[1]["relationships_json"], "[]")
        for table, name in (("agent_daily", "military_pay_minor"), ("country_daily", "relationships_json")):
            self.assertIn(name, economy_backfill_fields("workspace.gearth_silver." + table))

    def test_v2_snapshot_does_not_retroactively_require_war(self):
        value = record("agent_day", {"agent_id": "a", "cash_minor": 0, **{key: None for key in REQUIRED_AGENT_V2}})
        validate_capture(value, CAPTURE_VERSION_V2)
        with self.assertRaisesRegex(ValueError, "capture omitted fields"):
            validate_capture(value, CAPTURE_VERSION)
        value["payload"].update({key: None for key in REQUIRED_AGENT_V3})
        validate_capture(value, CAPTURE_VERSION)
        self.assertNotIn("relationships", capture_requirements("nation_day", CAPTURE_VERSION_V2))
        self.assertIn("relationships", capture_requirements("nation_day", CAPTURE_VERSION))

    def test_v2_conditions_keep_old_feature_signature_and_v3_requires_war_evidence(self):
        metadata = feature_metadata()
        metadata["capture_contract_version"] = CAPTURE_VERSION_V2
        metadata["features"].update({name: "v1" for name in ("infrastructure", "part_time_school", "wage_arrears", "price_response", "currency_trade")})
        metadata["economy_configuration"].update(labor={"arrears_strike_weeks": 2}, infrastructure={"phase": 2}, currency_trade={"step": 0.01})
        metadata["economy_configuration"]["school"]["part_time_course_days"] = 56
        previous = comparison_conditions(metadata)
        self.assertTrue(previous["known"])
        changed = copy.deepcopy(metadata)
        changed["capture_contract_version"] = CAPTURE_VERSION
        self.assertFalse(comparison_conditions(changed)["known"])
        changed["features"]["war_diplomacy"] = "phase3_v1"
        changed["economy_configuration"]["war_diplomacy"] = {"unrest_days": 28}
        current = comparison_conditions(changed)
        self.assertTrue(current["known"])
        self.assertNotEqual(previous["signature"], current["signature"])
        self.assertEqual(comparison_conditions(metadata), previous)

    def test_report_field_requirements_follow_original_capture_version(self):
        for version, required in ((CAPTURE_VERSION_V2, REQUIRED_AGENT_V2), (CAPTURE_VERSION, REQUIRED_AGENT_V3)):
            with self.subTest(version=version), tempfile.TemporaryDirectory() as temp:
                metadata, records = fixture()
                metadata["capture_contract_version"] = records[0]["payload"]["capture_contract_version"] = version
                for item in records:
                    item["payload"].update({name: None for name in capture_requirements(item["kind"], version)})
                export = Path(temp) / "export"
                export_run("http://localhost", metadata["run_id"], export, fetch=fake_fetch(metadata, records))
                report = build_report(export / "manifest.json")
                self.assertEqual(report["economy_capture"]["contract_version"], version)
                fields = report["economy_capture"]["fields"]["agent_day"]
                self.assertEqual("military_paid_tick" in fields, version == CAPTURE_VERSION)
                if version == CAPTURE_VERSION:
                    self.assertEqual(fields["military_paid_tick"]["present_count"], 4)
                    self.assertEqual(fields["military_paid_tick"]["null_count"], 4)

    def test_invalid_war_types_and_duplicate_relations_are_rejected(self):
        for payload in ({"war_weariness": "0.1"}, {"military": {"soldiers_fit": False}},
            {"mission": {"average_quality_of_life": "40"}}, {"relationships": [{"id": "x", "score": "-20"}]},
            {"relationships": [{"id": "x"}, {"id": "x"}]}, {"relationships": [None]}):
            with self.subTest(payload=payload), self.assertRaises(ValueError):
                typed_record(record("nation_day", {"id": "a", **payload}))
