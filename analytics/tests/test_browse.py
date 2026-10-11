"""Fixture-only actual-row streaming/cache qualification tests; no Spark compute."""
import gzip
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from analytics.browse import (DATASETS, MAX_ROWS, MAX_ROW_BYTES, PayloadInventory,
    catalog, read_bundle_manifest, safe_path, stream_dataset, validate_bundle)
from analytics.export import canonical


class BrowseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.run = "synthetic-run"
        self.source = "a" * 64

    def tearDown(self):
        self.temp.cleanup()

    def bundle(self, rows=None):
        if rows is None:
            rows = [{"run_id": self.run, "source_manifest_sha256": self.source,
                "payload_json": json.dumps(payload), "future_column": value}
                for payload, value in [({}, None), ({"new": None}, None), ({"new": False}, False),
                    ({"new": 0, "bank": {"reserves": 10}}, 0), ({"new": 5}, 5)]]
        names = list(rows[0]) if rows else ["run_id", "source_manifest_sha256", "payload_json"]
        columns = [{"name": name, "data_type": "string", "nullable": True,
            "non_null_count": sum(row[name] is not None for row in rows)} for name in names]
        entries = [stream_dataset((canonical(row) + b"\n" for row in rows), self.root, "silver.agent_daily",
            DATASETS["silver.agent_daily"], columns)]
        for identity, table in DATASETS.items():
            if identity != "silver.agent_daily":
                entries.append(dict(dataset_id=identity, table_name=table, availability="unavailable",
                    unavailable_reason="table_not_present", row_count=None, columns=[], payload_fields=[],
                    field_coverage_scope="unavailable", chunks=[]))
        manifest = dict(schema_version="gearth.analytics-browse.v1", run_id=self.run, through_revision=3,
            through_sequence=10, source_manifest_sha256=self.source, generated_at="2026-10-11T00:00:00Z",
            qualification_status="pass", datasets=entries)
        self.write(manifest)
        return manifest

    def write(self, manifest):
        (self.root / "browse-manifest.json").write_bytes(canonical(manifest) + b"\n")

    def test_payload_absent_null_false_zero_and_nested_are_distinct(self):
        manifest = self.bundle()
        receipt = validate_bundle(self.root)
        fields = {field["path"]: field for field in receipt["datasets"][0]["payload_fields"]}
        self.assertEqual(fields["/new"], dict(path="/new", present_count=4, null_count=1,
            non_null_count=3, zero_or_false_count=2))
        self.assertEqual(fields["/bank/reserves"]["present_count"], 1)
        self.assertEqual(receipt["datasets"][0]["columns"][-1]["non_null_count"], 3)
        self.assertIsNone(receipt["datasets"][1]["row_count"])
        self.assertEqual(receipt, catalog(manifest, hashlib.sha256((self.root / "browse-manifest.json").read_bytes()).hexdigest()))

    def test_rows_past_preview_and_across_chunk_boundary_are_all_retained(self):
        rows = [dict(run_id=self.run, source_manifest_sha256=self.source, index=index)
            for index in range(MAX_ROWS + 13)]
        manifest = self.bundle(rows)
        entry = manifest["datasets"][0]
        self.assertEqual([chunk["row_count"] for chunk in entry["chunks"]], [2000, 13])
        with gzip.open(self.root / entry["chunks"][1]["path"], "rt") as stream:
            self.assertEqual(json.loads(stream.readline())["index"], 2000)
        self.assertEqual(validate_bundle(self.root)["datasets"][0]["row_count"], 2013)

    def test_empty_typed_dataset_is_different_from_missing_table(self):
        manifest = self.bundle([])
        receipt = validate_bundle(self.root)
        self.assertEqual(receipt["datasets"][0]["row_count"], 0)
        self.assertEqual(receipt["datasets"][0]["availability"], "available")
        self.assertEqual(receipt["datasets"][1]["availability"], "unavailable")

    def test_depth_and_array_coverage_limits_are_explicit(self):
        inventory = PayloadInventory()
        inventory.add({"a": {"b": {"c": {"d": {"hidden": 1}}}}, "rows": [{"inside": 1}]})
        result = inventory.result()
        self.assertTrue(result["field_coverage_limit"]["depth_limited"])
        self.assertFalse(result["field_coverage_limit"]["array_elements"])
        self.assertNotIn("/a/b/c/d/hidden", [field["path"] for field in result["payload_fields"]])
        self.assertNotIn("/rows/inside", [field["path"] for field in result["payload_fields"]])
        with patch("analytics.browse.MAX_PATHS", 2):
            inventory = PayloadInventory()
            inventory.add({"a": 0, "b": 0, "c": 0})
            self.assertTrue(inventory.result()["field_coverage_limit"]["path_inventory_truncated"])

    def test_source_catalog_and_schema_tampering_rejected(self):
        manifest = self.bundle()
        with self.assertRaisesRegex(ValueError, "provenance"):
            validate_bundle(self.root, expected={**manifest, "run_id": "other"})
        with self.assertRaisesRegex(ValueError, "catalog"):
            validate_bundle(self.root, expected_catalog={})
        manifest["datasets"][0]["columns"][-1]["non_null_count"] = 0
        self.write(manifest)
        with self.assertRaisesRegex(ValueError, "non-null"):
            validate_bundle(self.root)

    def test_coverage_metadata_tampering_rejected(self):
        manifest = self.bundle()
        manifest["datasets"][0]["payload_fields"][0]["present_count"] = 99
        self.write(manifest)
        with self.assertRaisesRegex(ValueError, "payload coverage"):
            validate_bundle(self.root)

    def test_wrong_row_source_rejected(self):
        self.bundle([dict(run_id=self.run, source_manifest_sha256="b" * 64)])
        with self.assertRaisesRegex(ValueError, "source identity"):
            validate_bundle(self.root)

    def test_wrong_row_run_rejected(self):
        self.bundle([dict(run_id="other")])
        with self.assertRaisesRegex(ValueError, "run identity"):
            validate_bundle(self.root)

    def test_corrupt_truncated_or_expanding_gzip_rejected(self):
        manifest = self.bundle()
        chunk = manifest["datasets"][0]["chunks"][0]
        path = self.root / chunk["path"]
        path.write_bytes(path.read_bytes()[:-5])
        # Even if a malicious transport updates the compressed checksum, decoding fails.
        chunk["sha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        chunk["compressed_bytes"] = path.stat().st_size
        self.write(manifest)
        with self.assertRaises((EOFError, OSError, ValueError)):
            validate_bundle(self.root)

    def test_byte_bound_rotates_and_single_row_allowance_is_bounded(self):
        rows = [canonical(dict(run_id=self.run, text="x" * 100)) + b"\n"] * 3
        with patch("analytics.browse.MAX_CHUNK_BYTES", 200):
            entry = stream_dataset(iter(rows), self.root, "bronze.audit", DATASETS["bronze.audit"], [])
        self.assertEqual(len(entry["chunks"]), 3)
        with self.assertRaisesRegex(ValueError, "8 MB"):
            stream_dataset(iter([b"x" * (MAX_ROW_BYTES + 1)]), self.root, "bronze.audit", DATASETS["bronze.audit"], [])

    def test_unsafe_path_and_symlink_rejected(self):
        for path in ("../outside.gz", "/absolute.gz", "C:drive.gz", "one\\two.gz", "a//b.gz", "a/./b.gz"):
            with self.assertRaises(ValueError):
                safe_path(self.root, path)
        (self.root / "linked").symlink_to(self.root)
        with self.assertRaisesRegex(ValueError, "symlink"):
            safe_path(self.root, "linked/file.gz")
        manifest = self.bundle()
        manifest["datasets"][0]["chunks"][0]["path"] = "../outside.gz"
        self.write(manifest)
        with self.assertRaises(ValueError):
            read_bundle_manifest(self.root)

    def test_manifest_size_and_incomplete_inventory_rejected(self):
        manifest = self.bundle()
        with patch("analytics.browse.MAX_MANIFEST_BYTES", 2):
            with self.assertRaisesRegex(ValueError, "16 MiB"):
                read_bundle_manifest(self.root)
        manifest["datasets"].pop()
        self.write(manifest)
        with self.assertRaisesRegex(ValueError, "incomplete"):
            read_bundle_manifest(self.root)


if __name__ == "__main__":
    unittest.main()

# Spark facade executes only filter/aggregate/stage interfaces. There is no row
# collect/toLocalIterator method, so tests fail if export starts using those APIs.
class Expr:
    def __init__(self, evaluate):
        self.evaluate = evaluate
    def __eq__(self, other):
        return Expr(lambda row: self.evaluate(row) == other)
    def __ne__(self, other):
        return Expr(lambda row: self.evaluate(row) != other)
    def __or__(self, other):
        return Expr(lambda row: self.evaluate(row) or other.evaluate(row))
    def isNull(self):
        return Expr(lambda row: self.evaluate(row) is None)


class Count:
    def __init__(self, expression):
        self.expression = expression
    def alias(self, name):
        self.name = name
        return self


class SparkFacade:
    def __init__(self, rows, drop_staged=False):
        from types import SimpleNamespace
        self.rows = rows
        self.drop_staged = drop_staged
        self.catalog = SimpleNamespace(tableExists=lambda name: name == DATASETS["silver.agent_daily"])
    def table(self, name):
        return FrameFacade(self, self.rows)


class FrameFacade:
    def __init__(self, spark, rows):
        from types import SimpleNamespace
        self.spark = spark
        self.rows = rows
        self.columns = ["run_id", "source_manifest_sha256", "payload_json", "new_column"]
        fields = [SimpleNamespace(name=name, nullable=True,
            dataType=SimpleNamespace(simpleString=lambda: "string")) for name in self.columns]
        self.schema = SimpleNamespace(fields=fields)
        self.write = self
    def where(self, predicate):
        return FrameFacade(self.spark, [row for row in self.rows if predicate.evaluate(row)])
    def limit(self, count):
        return FrameFacade(self.spark, self.rows[:count])
    def count(self):
        return len(self.rows)
    def agg(self, *expressions):
        from types import SimpleNamespace
        result = {item.name: sum(item.expression.evaluate(row) is not None for row in self.rows) for item in expressions}
        return SimpleNamespace(first=lambda: SimpleNamespace(asDict=lambda: result))
    def mode(self, value):
        return self
    def option(self, *args):
        return self
    def json(self, destination):
        path = Path(destination)
        path.mkdir()
        rows = self.rows[:-1] if self.spark.drop_staged else self.rows
        (path / "part-00000.json").write_bytes(b"".join(canonical(row) + b"\n" for row in rows))
        (path / "_SUCCESS").write_bytes(b"")


class ActualDeltaExportTests(unittest.TestCase):
    def export(self, root, *, bad_source=False, drop_staged=False):
        import sys
        from types import ModuleType
        from analytics.browse import export_delta_bundle
        functions = ModuleType("pyspark.sql.functions")
        functions.col = lambda name: Expr(lambda row: row.get(name.strip("`")))
        functions.count = lambda expression: Count(expression)
        sql = ModuleType("pyspark.sql")
        sql.functions = functions
        pyspark = ModuleType("pyspark")
        pyspark.sql = sql
        source_hash = "a" * 64
        rows = [dict(run_id="synthetic-run", source_manifest_sha256=source_hash,
                    payload_json=json.dumps({"new_feature": value}), new_column=None) for value in (None, False)]
        rows += [dict(run_id="other-run", source_manifest_sha256="b" * 64, payload_json="{}", new_column="unrelated")]
        if bad_source:
            rows[0]["source_manifest_sha256"] = "b" * 64
        manifest = {"run": dict(run_id="synthetic-run", through_revision=3, through_sequence=10)}
        qualification = dict(status="pass", **manifest["run"], source_manifest_sha256=source_hash,
            bronze={"rows": 0}, silver={"agent_daily": {"rows": 2}})
        with patch.dict(sys.modules, {"pyspark": pyspark, "pyspark.sql": sql, "pyspark.sql.functions": functions}):
            return export_delta_bundle(SparkFacade(rows, drop_staged), manifest, source_hash, root, qualification)

    def test_actual_schema_run_filter_rows_and_nulls_not_offline_projection(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "browse"
            receipt = self.export(root)
            dataset = next(entry for entry in receipt["datasets"] if entry["dataset_id"] == "silver.agent_daily")
            self.assertEqual(dataset["row_count"], 2)
            self.assertEqual(dataset["columns"][-1], dict(name="new_column", data_type="string", nullable=True, non_null_count=0))
            self.assertEqual(dataset["payload_fields"][0]["present_count"], 2)
            self.assertEqual(dataset["payload_fields"][0]["null_count"], 1)
            self.assertEqual(dataset["payload_fields"][0]["zero_or_false_count"], 1)
            self.assertEqual(receipt, validate_bundle(root))

    def test_mixed_source_or_incomplete_staging_never_publishes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "browse"
            original = self.export(root)
            original_bytes = (root / "browse-manifest.json").read_bytes()
            for options in ({"bad_source": True}, {"drop_staged": True}):
                with self.assertRaises(ValueError):
                    self.export(root, **options)
                self.assertEqual((root / "browse-manifest.json").read_bytes(), original_bytes)
                self.assertEqual(validate_bundle(root), original)
            self.assertFalse(list(Path(temp).glob(".browse-*")))

    def test_republication_preserves_previous_verified_bundle(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "browse"
            original = self.export(root)
            current = self.export(root)
            archived = root / "manifests" / (original["bundle_manifest_sha256"] + ".json")
            self.assertTrue(archived.exists())
            old_manifest = json.loads(archived.read_bytes())
            self.assertEqual(catalog(old_manifest, original["bundle_manifest_sha256"]), original)
            self.assertTrue(all((root / chunk["path"]).exists() for entry in old_manifest["datasets"] for chunk in entry.get("chunks", [])))
            self.assertEqual(validate_bundle(root), current)
