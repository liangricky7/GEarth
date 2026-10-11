"""Synthetic fixtures only; these are not evidence from a real simulation."""
import copy
import gzip
import json
from pathlib import Path
import tempfile
import unittest

from analytics.export import canonical, export_run
from analytics.delta_validation import assert_coverage
from analytics.report import build_report
from analytics.transform import typed_record
from analytics.validate import file_sha256, validate


def fixture():
    payloads = [
        ("run_metadata", 0, {"seed": 7, "mode": "RULE", "initial_population": 2, "government_rotation": 0, "tick_limit": 1, "engine_commit": None}),
        ("event", 0, {"event_id": "e1", "seq": 1, "event_type": "started", "entity_ids": ["a"], "cause_ids": [], "payload": {}}),
        ("opening_balance", 0, {"account_id": "a", "account_type": "agent", "balance_minor": 100, "agent": {"role": "citizen", "country_id": "n1", "cash_minor": 100}}),
        ("opening_balance", 0, {"account_id": "b", "account_type": "agent", "balance_minor": 100, "agent": {"role": "business", "country_id": "n1", "cash_minor": 100}}),
        ("decision_request", 1, {"decision_id": "d1", "agent_id": "a", "role": "citizen", "country_id": "n1", "observation_tick": 0,
                                   "job": {"observation": {"note": "exact original context", "cash_minor": 100}, "candidates": [{"action_type": "hold", "params": {}}]}}),
        ("decision_result", 1, {"decision_id": "d1", "agent_id": "a", "result": {"action": {"action_type": "hold", "params": {}, "reason": "stated reason"}, "source": "rule_fallback"},
                                  "status": "accepted", "accepted": True, "fallback": True, "selected_option_index": 0}),
        ("transaction", 1, {"transaction_id": "t1", "sender_account_id": "a", "recipient_account_id": "b", "amount_minor": 20, "kind": "purchase", "transaction_group_id": "g1", "routine_id": "cycle1"}),
        ("agent_day", 1, {"agent_id": "a", "role": "citizen", "country_id": "n1", "cash_minor": 80, "frame_kind": "day"}),
        ("agent_day", 1, {"agent_id": "b", "role": "business", "country_id": "n1", "cash_minor": 120, "frame_kind": "day"}),
        # Late same-tick transfer must be retained in end snapshots/reconciliation.
        ("transaction", 1, {"transaction_id": "t2", "sender_account_id": "b", "recipient_account_id": "a", "amount_minor": 5, "kind": "refund"}),
        ("agent_day", 1, {"agent_id": "a", "role": "citizen", "country_id": "n1", "cash_minor": 85, "frame_kind": "end"}),
        ("agent_day", 1, {"agent_id": "b", "role": "business", "country_id": "n1", "cash_minor": 115, "frame_kind": "end"}),
        ("nation_day", 1, {"id": "n1", "government": "synthetic", "frame_kind": "day", "residents": 1, "escrow_accounts": []}),
        ("nation_day", 1, {"id": "n1", "government": "synthetic", "frame_kind": "end", "residents": 1, "escrow_accounts": []}),
    ]
    records = [{"run_id": "synthetic-test-run", "sequence": index + 1, "tick": tick, "revision": 1 if tick == 0 else 2, "kind": kind, "payload": payload}
               for index, (kind, tick, payload) in enumerate(payloads)]
    metadata = {"run_id": "synthetic-test-run", "status": "completed", "tick": 1, "through_revision": 2,
                "through_sequence": len(records), "through_event_sequence": 1, "seed": 7, "mode": "RULE", "population": 2,
                "initial_population": 2, "initial_cash_minor": 200, "metadata_unknown": ["engine_commit"],
                "analytics_enabled": True, "audit_available": True, "audit_from_tick_zero": True, "audit_schema_version": 1,
                "synthetic": True}
    return metadata, records


def fake_fetch(metadata, records):
    def fetch(path, params):
        if path.endswith("export-metadata"):
            return metadata
        assert params["through_revision"] == metadata["through_revision"]
        assert params["through_sequence"] == metadata["through_sequence"]
        page = [row for row in records if row["sequence"] > params["after"]][:params["limit"]]
        cursor = page[-1]["sequence"] if page else params["after"]
        return {"run_id": metadata["run_id"], "through_revision": metadata["through_revision"], "through_sequence": metadata["through_sequence"],
                "records": page, "next_after": cursor, "has_more": cursor < metadata["through_sequence"]}
    return fetch


class PipelineTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.metadata, self.records = fixture()

    def tearDown(self):
        self.temp.cleanup()

    def export(self, **kwargs):
        export_run("http://localhost:5000", self.metadata["run_id"], self.root / "export", page_size=3, chunk_rows=4,
                   fetch=fake_fetch(self.metadata, self.records), **kwargs)
        return self.root / "export" / "manifest.json"

    def rewrite(self, path, mutate):
        manifest = json.loads(path.read_text())
        mutate(manifest)
        path.write_bytes(canonical(manifest) + b"\n")

    def test_roundtrip_bounded_chunks_and_pins(self):
        manifest_path = self.export()
        result = validate(manifest_path)
        self.assertEqual(result["record_count"], len(self.records))
        self.assertEqual(len(json.loads(manifest_path.read_text())["files"]), 4)

    def test_large_context_rotates_by_uncompressed_bytes(self):
        self.records[4]["payload"]["job"]["observation"]["large_context"] = "x" * 5000
        self.records[5]["payload"]["result"]["provider_response"] = "y" * 3000
        manifest = export_run("http://localhost", self.metadata["run_id"], self.root / "export", page_size=3,
                              chunk_rows=10000, chunk_bytes=6000, fetch=fake_fetch(self.metadata, self.records))
        self.assertGreater(len(manifest["files"]), 1)
        self.assertTrue(all(entry["row_count"] < 10000 and entry["uncompressed_bytes"] <= 6000 for entry in manifest["files"]))
        self.assertEqual(sum(entry["row_count"] for entry in manifest["files"]), len(self.records))
        validate(self.root / "export" / "manifest.json")

    def test_single_row_limit_is_enforced(self):
        self.records[4]["payload"]["job"]["observation"]["large_context"] = "x" * (2 * 1024 * 1024)
        with self.assertRaisesRegex(ValueError, "bounded-record limit"):
            self.export()
        self.assertFalse((self.root / "export" / "manifest.json").exists())

    def test_same_tick_final_states_and_no_double_count(self):
        report = build_report(self.export())
        self.assertEqual(report["summary"]["transaction_count"], 2)
        self.assertEqual(report["summary"]["transfer_minor"], 25)
        self.assertEqual(report["summary"]["fallback_count"], 1)
        checks = {row["name"]: row["status"] for row in report["checks"]}
        self.assertEqual(checks["ledger_account_reconciliation"], "pass")
        self.assertEqual(checks["cash_plus_escrow_conservation"], "pass")
        self.assertEqual(report["agents"][0]["cash_minor"], 85)
        self.assertTrue(report["completeness"]["synthetic"])

    def test_corrupted_day_cannot_hide_behind_correct_final_balance(self):
        self.records[7]["payload"]["cash_minor"] = 79
        report = build_report(self.export())
        checks = {row["name"]: row for row in report["checks"]}
        self.assertEqual(checks["ledger_account_reconciliation"]["status"], "pass")
        self.assertEqual(checks["daily_account_reconciliation"]["status"], "fail")
        self.assertEqual(checks["daily_account_reconciliation"]["details"]["mismatches"], 1)
        self.assertEqual(checks["daily_account_reconciliation"]["details"]["examples"][0]["expected_minor"], 80)

    def test_delta_aggregate_qualification_rejects_missing_rows(self):
        manifest_path = self.export()
        manifest = json.loads(manifest_path.read_text())
        from analytics.transform import TABLES
        bronze = {"rows": len(self.records), "unique_sequences": len(self.records), "first_sequence": 1, "last_sequence": len(self.records)}
        silver = {}
        for kind, name in TABLES.items():
            count = manifest["counts_by_kind"].get(kind, 0)
            bronze["kind_" + kind] = count
            silver[name] = {"rows": count, "unique_sequences": count}
        derived = {"decision_options": 1, "event_edges": 0}
        for name, count in derived.items():
            silver[name] = {"rows": count, "unique_keys": count}
        assert_coverage(manifest, bronze, silver, derived)
        silver["agent_daily"]["rows"] -= 1
        with self.assertRaisesRegex(ValueError, "Silver kind"):
            assert_coverage(manifest, bronze, silver, derived)
        silver["agent_daily"]["rows"] += 1
        bronze["unique_sequences"] -= 1
        with self.assertRaisesRegex(ValueError, "Bronze coverage"):
            assert_coverage(manifest, bronze, silver, derived)

    def test_nonterminal_refused(self):
        for status in ("running", "paused"):
            self.metadata["status"] = status
            with self.assertRaises(ValueError):
                export_run("http://localhost", self.metadata["run_id"], self.root / status, fetch=fake_fetch(self.metadata, self.records))

    def test_historical_unaudited_refused(self):
        self.metadata.update(analytics_enabled=False, audit_available=False, audit_from_tick_zero=False, through_sequence=0)
        with self.assertRaisesRegex(ValueError, "tick zero"):
            self.export()

    def test_neon_control_and_changed_forks_refused_before_manifest(self):
        for index, change in enumerate((None, {"country": "n1", "government": "dictatorship"})):
            records = copy.deepcopy(self.records)
            records.append({"run_id": self.metadata["run_id"], "sequence": len(records) + 1, "tick": 1, "revision": 2,
                            "kind": "event", "payload": {"event_id": "fork-event", "seq": 2, "event_type": "timeline_forked",
                            "payload": {"timeline": "synthetic-fork", "change": change}}})
            metadata = dict(self.metadata, through_sequence=len(records), through_event_sequence=2)
            destination = self.root / ("fork-" + str(index))
            with self.assertRaisesRegex(ValueError, "does not support Neon fork timelines"):
                export_run("http://localhost", metadata["run_id"], destination, fetch=fake_fetch(metadata, records))
            self.assertFalse((destination / "manifest.json").exists())

    def test_uploaded_fork_is_rejected_before_report_generation(self):
        # Simulate a pre-guard exporter with valid compressed hashes/counts, proving
        # that importer/report preflight also rejects rather than trusting metadata.
        manifest_path = self.export()
        manifest = json.loads(manifest_path.read_text())
        last_entry = manifest["files"][-1]
        chunk = manifest_path.parent / last_entry["path"]
        rows = [json.loads(line) for line in gzip.open(chunk, "rt", encoding="utf-8")]
        row = rows[-1]
        old_kind = row["kind"]
        row.update(kind="event", payload={"event_id": "fork-event", "seq": 2, "event_type": "timeline_forked", "payload": {"timeline": "synthetic-control-fork", "change": None}})
        with gzip.open(chunk, "wb") as stream:
            for record in rows:
                stream.write(canonical(record) + b"\n")
        last_entry.update(sha256=file_sha256(chunk), bytes=chunk.stat().st_size)
        manifest["counts_by_kind"][old_kind] -= 1
        manifest["counts_by_kind"]["event"] += 1
        manifest["run"]["through_event_sequence"] = 2
        manifest_path.write_bytes(canonical(manifest) + b"\n")
        with self.assertRaisesRegex(ValueError, "does not support Neon fork timelines"):
            validate(manifest_path)
        with self.assertRaisesRegex(ValueError, "does not support Neon fork timelines"):
            build_report(manifest_path)

    def test_stopped_snapshot_partial_run(self):
        self.metadata["status"] = "stopped"
        report = build_report(self.export())
        self.assertTrue(report["completeness"]["snapshot_complete"])
        self.assertFalse(report["completeness"]["run_complete"])

    def test_export_sequence_gap(self):
        self.records.pop(6)
        with self.assertRaisesRegex(ValueError, "gap"):
            self.export()
        self.assertFalse((self.root / "export" / "manifest.json").exists())

    def test_hash_damage(self):
        path = self.export()
        chunk = path.parent / "audit-000000.ndjson.gz"
        chunk.write_bytes(chunk.read_bytes() + b"bad")
        with self.assertRaisesRegex(ValueError, "hash/size"):
            validate(path)

    def test_traversal_path(self):
        path = self.export()
        self.rewrite(path, lambda manifest: manifest["files"][0].update(path="../outside.ndjson.gz"))
        with self.assertRaisesRegex(ValueError, "unsafe"):
            validate(path)

    def test_count_lie(self):
        path = self.export()
        self.rewrite(path, lambda manifest: manifest.update(record_count=999))
        with self.assertRaisesRegex(ValueError, "coverage"):
            validate(path)

    def test_integer_money(self):
        self.records[6]["payload"]["amount_minor"] = 20.5
        with self.assertRaisesRegex(ValueError, "integer"):
            self.export()

    def test_monetary_maps_check_integer_leaves(self):
        self.records[4]["payload"]["job"]["observation"].update(cheapest_price_minor={"grain": 20, "fuel": None}, startup_cost_minor={"clinic": 1000})
        validate(self.export())
        self.temp.cleanup()
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.records[4]["payload"]["job"]["observation"]["cheapest_price_minor"]["grain"] = "20"
        with self.assertRaisesRegex(ValueError, "integer minor"):
            self.export()

    def test_duplicate_transaction_identity(self):
        self.records[9]["payload"]["transaction_id"] = "t1"
        with self.assertRaisesRegex(ValueError, "semantic"):
            validate(self.export())

    def test_missing_daily_snapshot_not_certified(self):
        self.records.pop(7)
        for index, row in enumerate(self.records):
            row["sequence"] = index + 1
        self.metadata["through_sequence"] = len(self.records)
        with self.assertRaisesRegex(ValueError, "daily"):
            validate(self.export())

    def test_event_cursor_coverage(self):
        self.metadata["through_event_sequence"] = 2
        with self.assertRaisesRegex(ValueError, "event coverage"):
            validate(self.export())

    def test_canceled_result_keeps_missing_outcome(self):
        self.records[5]["payload"].update(result=None, selected_option_index=None, status="canceled", fallback=False)
        report = build_report(self.export())
        self.assertEqual(report["summary"]["fallback_count"], 0)

    def test_index_matches_original_action(self):
        self.records[5]["payload"]["result"]["action"]["params"] = {"surprise": True}
        report = build_report(self.export())
        self.assertEqual(next(check["status"] for check in report["checks"] if check["name"] == "offered_choice_index"), "fail")

    def test_typed_context_and_all_legs_preserved(self):
        table, row = typed_record(self.records[4])
        self.assertEqual(table, "decision_requests")
        self.assertIn("exact original context", row["observation_json"])
        self.assertEqual(json.loads(row["options_json"])[0]["action_type"], "hold")
        table, transaction = typed_record(self.records[6])
        self.assertEqual(transaction["amount_minor"], 20)
        self.assertEqual(transaction["transaction_group_id"], "g1")

    def test_export_is_immutable(self):
        self.export()
        with self.assertRaises(FileExistsError):
            self.export()

    def test_all_escrow_legs_reconcile(self):
        # Add an escrow deposit and settlement before the two final states.
        self.records[10:10] = [
            {"run_id": self.metadata["run_id"], "sequence": 0, "tick": 1, "revision": 2, "kind": "transaction", "payload": {
                "transaction_id": "escrow-in", "sender_account_id": "a", "recipient_account_id": "cargo1", "amount_minor": 10, "kind": "import_escrow", "transaction_group_id": "shipment1"}},
            {"run_id": self.metadata["run_id"], "sequence": 0, "tick": 1, "revision": 2, "kind": "transaction", "payload": {
                "transaction_id": "escrow-out", "sender_account_id": "cargo1", "recipient_account_id": "b", "amount_minor": 10, "kind": "import", "transaction_group_id": "shipment1"}},
        ]
        self.records[12]["payload"]["cash_minor"] = 75
        self.records[13]["payload"]["cash_minor"] = 125
        self.records[-1]["payload"]["escrow_accounts"] = [{"account_id": "cargo1", "balance_minor": 0}]
        for index, row in enumerate(self.records):
            row["sequence"] = index + 1
        self.metadata["through_sequence"] = len(self.records)
        report = build_report(self.export())
        self.assertEqual(report["summary"]["transaction_count"], 4)
        self.assertEqual(report["summary"]["transfer_minor"], 45)
        self.assertEqual(next(check["status"] for check in report["checks"] if check["name"] == "cash_plus_escrow_conservation"), "pass")

    def test_explicit_new_firm_opening_is_not_inferred(self):
        self.records[10:10] = [
            {"run_id": self.metadata["run_id"], "sequence": 0, "tick": 1, "revision": 2, "kind": "event", "payload": {
                "event_id": "e2", "seq": 2, "event_type": "business_started", "entity_ids": ["newfirm"], "cause_ids": [],
                "payload": {"account_id": "newfirm", "opening_cash_minor": 0}}},
            {"run_id": self.metadata["run_id"], "sequence": 0, "tick": 1, "revision": 2, "kind": "transaction", "payload": {
                "transaction_id": "founding", "sender_account_id": "a", "recipient_account_id": "newfirm", "amount_minor": 10, "kind": "founding_capital"}},
            {"run_id": self.metadata["run_id"], "sequence": 0, "tick": 1, "revision": 2, "kind": "agent_day", "payload": {
                "agent_id": "newfirm", "role": "business", "country_id": "n1", "cash_minor": 10, "frame_kind": "end"}},
        ]
        self.records[13]["payload"]["cash_minor"] = 75
        for index, row in enumerate(self.records):
            row["sequence"] = index + 1
        self.metadata.update(through_sequence=len(self.records), through_event_sequence=2, population=3)
        report = build_report(self.export())
        self.assertEqual(next(check["status"] for check in report["checks"] if check["name"] == "ledger_account_reconciliation"), "pass")
        # Final census count alone must not certify a substituted dynamic identity.
        self.temp.cleanup()
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        next(row for row in self.records if row["kind"] == "agent_day" and row["payload"].get("agent_id") == "newfirm")["payload"]["agent_id"] = "unrelated"
        with self.assertRaisesRegex(ValueError, "unknown agent|final end"):
            validate(self.export())

    def test_missing_country_day_is_not_certified(self):
        self.records = [row for row in self.records if not (row["kind"] == "nation_day" and row["payload"]["frame_kind"] == "day")]
        for index, row in enumerate(self.records):
            row["sequence"] = index + 1
        self.metadata["through_sequence"] = len(self.records)
        with self.assertRaisesRegex(ValueError, "country daily"):
            validate(self.export())


if __name__ == "__main__":
    unittest.main()
