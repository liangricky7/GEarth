"""Existing-main economy capture/projection contracts, including old-run gaps."""
import copy
import json
from pathlib import Path
import tempfile
import unittest

from analytics.browse import DATASETS, validate_bundle
from analytics.delta_validation import assert_coverage
from analytics.conditions import comparison_conditions
from analytics.export import export_run
from analytics.report import build_report
from analytics.transform import (AGENT_TYPES, COUNTRY_TYPES, PROJECT_TYPES, CAPTURE_VERSION,
    REQUIRED_AGENT_V2, REQUIRED_COUNTRY_V2, TABLES, infrastructure_rows, typed_record, validate_capture)
from test_pipeline import fixture, fake_fetch
from test_public_services import feature_metadata
import test_browse


def record(kind, payload, sequence=12, frame="day"):
    return {"run_id": "synthetic-run", "sequence": sequence, "revision": 2, "tick": 7,
        "kind": kind, "payload": {"frame_kind": frame, **payload}}


def project(**changes):
    return {"id": "roads-1", "type": "roads", "level": 1, "condition": 100,
        "cost_minor": 31000, "paid_minor": 0, "started_tick": 7, "ready_tick": 35,
        "complete": False, "progress_days": 0, "required_days": 28, "progress_fraction": 0.0,
        "progress_basis": "engine_remaining_schedule_ticks", "required_builders": 4, **changes}


class EconomyProjectionTests(unittest.TestCase):
    def test_all_agent_gap_columns_retain_zero_false_null_and_original_payload(self):
        payload = {"agent_id": "a", "cash_minor": 0, "part_time_student": False,
            "arrears_minor": 0, "arrears_weeks": 0, "unpaid_hungry_weeks": 0, "insured_minor": 0,
            "price_change_tick": -1, "price_change_pct": 0, "units_before_price_change": 0,
            "profit_before_price_change_minor": 0, "price_before_price_change_minor": 0, "inventory": {"food": 0}}
        row = typed_record(record("agent_day", payload))[1]
        for key in REQUIRED_AGENT_V2:
            column = key + "_json" if key == "inventory" else key
            self.assertIn(column, row)
            self.assertEqual(json.loads(row[column]) if key == "inventory" else row[column], payload[key])
        self.assertEqual(json.loads(row["payload_json"])["part_time_student"], False)
        payload["part_time_student"] = None
        self.assertIsNone(typed_record(record("agent_day", payload))[1]["part_time_student"])
        with self.assertRaisesRegex(ValueError, "Invalid typed snapshot fact"):
            typed_record(record("agent_day", {**payload, "arrears_minor": "0"}))

    def test_country_columns_retained_json_legacy_unknown_and_empty(self):
        payload = {"id": "n1", "maintenance_funded": False, "currency_index": 1.125,
            "trade_net_week_minor": -123, "trade_gross_week_minor": 0, "bank_failures": 0,
            "defaulted_loans": 0, "deposit_insurance_paid_minor": 0, "infrastructure": [],
            "construction_materials_used": [], "borrowed_builders": {}, "endowment": {"ore": 0.25},
            "resource_imports": {"fuel": {"7": 0}}}
        row = typed_record(record("nation_day", payload))[1]
        for key in REQUIRED_COUNTRY_V2:
            column = key if key in COUNTRY_TYPES else key + "_json"
            self.assertEqual(row[column] if key in COUNTRY_TYPES else json.loads(row[column]), payload[key])
        legacy = typed_record(record("nation_day", {"id": "n1"}))[1]
        self.assertIsNone(legacy["infrastructure_json"])
        self.assertIsNone(legacy["maintenance_funded"])
        explicit_null = typed_record(record("nation_day", {"id": "n1", "infrastructure": None}))[1]
        self.assertEqual(explicit_null["infrastructure_json"], "null")
        self.assertEqual(row["infrastructure_json"], "[]")

    def test_projects_source_identity_day_end_progress_and_original_future_payload(self):
        original = record("nation_day", {"id": "n1", "infrastructure": [project(future_effect={"ore": 0})]})
        rows = infrastructure_rows(original)
        self.assertEqual(len(rows), 1)
        day = rows[0]
        self.assertEqual((day["run_id"], day["country_id"], day["tick"], day["frame_kind"], day["project_id"]),
            ("synthetic-run", "n1", 7, "day", "roads-1"))
        self.assertEqual((day["status"], day["paid_minor"], day["progress_fraction"]), ("under_construction", 0, 0.0))
        self.assertEqual((day["sequence"], day["revision"]), (12, 2))
        self.assertEqual(day["record_sha256"], typed_record(original)[1]["record_sha256"])
        self.assertEqual(json.loads(day["project_json"])["future_effect"], {"ore": 0})
        end = infrastructure_rows(record("nation_day", {"id": "n1", "infrastructure": [project(complete=True,
            progress_days=28, progress_fraction=1.0)]}, sequence=13, frame="end"))[0]
        self.assertEqual((end["frame_kind"], end["status"]), ("end", "complete"))
        self.assertNotEqual(day["record_sha256"], end["record_sha256"])
        for payload in ({"id": "n1"}, {"id": "n1", "infrastructure": None}, {"id": "n1", "infrastructure": []}):
            self.assertEqual(infrastructure_rows(record("nation_day", payload)), [])

    def test_projects_duplicate_ids_missing_ids_invalid_types_fail_before_import(self):
        for projects in ([project(), project()], [project(id="")], [project(complete="false")],
                         [project(paid_minor=True)], [project(progress_fraction=float("inf"))], [None], {}):
            with self.subTest(projects=projects), self.assertRaises(ValueError):
                typed_record(record("nation_day", {"id": "n1", "infrastructure": projects}))

    def test_v2_contract_rejects_missing_fields_without_rejecting_legacy(self):
        value = record("agent_day", {"agent_id": "a", "cash_minor": 0})
        validate_capture(value, None)
        validate_capture(value, "gearth.public_services.v1")
        with self.assertRaisesRegex(ValueError, "v2 capture omitted fields"):
            validate_capture(value, CAPTURE_VERSION)
        value["payload"].update({key: None for key in REQUIRED_AGENT_V2})
        validate_capture(value, CAPTURE_VERSION)

    def test_new_capture_metadata_cannot_claim_known_missing_feature_configuration(self):
        metadata = feature_metadata()
        metadata["capture_contract_version"] = CAPTURE_VERSION
        unknown = comparison_conditions(metadata)["unknown"]
        self.assertIn("economy_v2_feature_versions", unknown)
        self.assertIn("economy_v2_configuration_sections", unknown)
        self.assertIn("part_time_course_configuration", unknown)
        metadata["features"].update({name: "v1" for name in
            ("infrastructure", "part_time_school", "wage_arrears", "price_response", "currency_trade")})
        metadata["economy_configuration"].update(labor={"arrears_strike_weeks": 2},
            infrastructure={"progress_basis": "engine_remaining_schedule_ticks"}, currency_trade={"currency_step": 0.01})
        metadata["economy_configuration"]["school"]["part_time_course_days"] = 56
        self.assertTrue(comparison_conditions(metadata)["known"])
        nation = record("nation_day", {key: None for key in REQUIRED_COUNTRY_V2})
        nation["payload"].update(id="n1", infrastructure=[project()])
        validate_capture(nation, CAPTURE_VERSION)
        del nation["payload"]["infrastructure"][0]["progress_basis"]
        with self.assertRaisesRegex(ValueError, "omitted infrastructure project fields"):
            validate_capture(nation, CAPTURE_VERSION)

    def test_child_count_and_key_coverage_are_qualified_independently(self):
        manifest = {"record_count": 1, "run": {"through_sequence": 1}, "counts_by_kind": {"nation_day": 1}}
        bronze = {"rows": 1, "unique_sequences": 1, "first_sequence": 1, "last_sequence": 1,
            **{"kind_" + kind: int(kind == "nation_day") for kind in TABLES}}
        silver = {table: {"rows": int(kind == "nation_day"), "unique_sequences": int(kind == "nation_day")}
            for kind, table in TABLES.items()}
        derived = {"decision_options": 0, "event_edges": 0, "infrastructure_daily": 2}
        silver.update({name: {"rows": count, "unique_keys": count} for name, count in derived.items()})
        assert_coverage(manifest, bronze, silver, derived)
        for field in ("rows", "unique_keys"):
            invalid = copy.deepcopy(silver)
            invalid["infrastructure_daily"][field] = 1
            with self.assertRaisesRegex(ValueError, "infrastructure_daily"):
                assert_coverage(manifest, bronze, invalid, derived)

    def test_report_coverage_old_missing_and_new_zero_are_different(self):
        metadata, records = fixture()
        for item in records:
            if item["kind"] == "agent_day" and item["payload"]["agent_id"] == "a":
                item["payload"].update(part_time_student=False, arrears_minor=0)
        with tempfile.TemporaryDirectory() as temp:
            export = Path(temp) / "export"
            export_run("http://localhost", metadata["run_id"], export, fetch=fake_fetch(metadata, records))
            report = build_report(export / "manifest.json")
        counts = report["economy_capture"]["fields"]["agent_day"]
        self.assertEqual(counts["part_time_student"]["present_count"], 2)
        self.assertEqual(counts["part_time_student"]["zero_or_false_count"], 2)
        self.assertEqual(counts["insured_minor"]["present_count"], 0)
        self.assertEqual(report["analysis_version"], "1.3.0")


class EconomyBrowseCompatibilityTests(unittest.TestCase):
    def setUp(self):
        self.browse = test_browse.BrowseTests()
        self.browse.setUp()
        self.addCleanup(self.browse.tearDown)

    def test_previous_19_dataset_bundle_still_valid_but_other_missing_table_is_rejected(self):
        manifest = self.browse.bundle()
        self.assertEqual(len(DATASETS), 20)
        manifest["datasets"] = [item for item in manifest["datasets"] if item["dataset_id"] != "silver.infrastructure_daily"]
        self.browse.write(manifest)
        self.assertEqual(len(validate_bundle(self.browse.root)["datasets"]), 19)
        manifest["datasets"] = [item for item in manifest["datasets"] if item["dataset_id"] != "silver.events"]
        self.browse.write(manifest)
        with self.assertRaisesRegex(ValueError, "inventory is incomplete"):
            validate_bundle(self.browse.root)
