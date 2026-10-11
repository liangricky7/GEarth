"""Distributed qualification contract tested with bounded frame/expression facades.

This does not claim Spark execution; the parent also runs the real notebook pilot.
"""
import ast
import json
from pathlib import Path
import unittest

from analytics.delta_validation import snapshot_source_projection, assert_projected_values, assert_source_manifest
from analytics.export import canonical
from analytics.schema import economy_backfill_fields
from analytics.transform import AGENT_TYPES, COUNTRY_TYPES, AGENT_JSON_FACTS, COUNTRY_JSON_FACTS


class Expr:
    def __init__(self, evaluate, name=None):
        self.evaluate, self.name = evaluate, name

    def alias(self, name):
        return Expr(self.evaluate, name)

    def cast(self, datatype):
        def evaluate(row):
            value = self.evaluate(row)
            if value is None:
                return None
            if datatype == "string":
                return str(value)
            if datatype == "boolean":
                return value == "true" if isinstance(value, str) else bool(value)
            return int(value) if datatype == "long" else float(value)
        return Expr(evaluate, self.name)

    def isNull(self):
        return Expr(lambda row: self.evaluate(row) is None)

    def isNotNull(self):
        return Expr(lambda row: self.evaluate(row) is not None)

    def __and__(self, other):
        return Expr(lambda row: self.evaluate(row) and other.evaluate(row))

    def __ne__(self, other):
        return Expr(lambda row: self.evaluate(row) != other)

    def __or__(self, other):
        return Expr(lambda row: self.evaluate(row) or other.evaluate(row))

    def eqNullSafe(self, other):
        return self.name, other.name


class Conditional:
    def __init__(self, condition, value):
        self.condition, self.value = condition, value

    def otherwise(self, other):
        return Expr(lambda row: (self.value if self.condition.evaluate(row) else other).evaluate(row))


class Functions:
    @staticmethod
    def col(name):
        return Expr(lambda row: row.get(name), name)

    @staticmethod
    def lit(value):
        return Expr(lambda row: value)

    @staticmethod
    def get_json_object(column, path):
        def extract(row):
            value = json.loads(row[column]).get(path[2:])
            return value if isinstance(value, str) else canonical(value).decode() if value is not None else None
        return Expr(extract)

    @staticmethod
    def json_object_keys(column):
        return Expr(lambda row: list(json.loads(column.evaluate(row))))

    @staticmethod
    def array_contains(column, value):
        return Expr(lambda row: value in column.evaluate(row))

    @staticmethod
    def coalesce(*columns):
        return Expr(lambda row: next((column.evaluate(row) for column in columns if column.evaluate(row) is not None), None))

    @staticmethod
    def when(condition, value):
        return Conditional(condition, value)


class Frame:
    def __init__(self, rows, columns=None, alias_name=None):
        self.rows = rows
        self.columns = columns if columns is not None else list(rows[0]) if rows else []
        self.alias_name = alias_name

    def where(self, condition):
        return Frame([row for row in self.rows if condition.evaluate(row)], self.columns)

    def select(self, *columns):
        names = [column if isinstance(column, str) else column.name for column in columns]
        return Frame([{name: row.get(column) if isinstance(column, str) else column.evaluate(row)
            for name, column in zip(names, columns)} for row in self.rows], names)

    def alias(self, name):
        return Frame(self.rows, self.columns, name)

    def join(self, other, conditions, how):
        assert how == "left_anti"
        def match(left, right):
            combined = {**{self.alias_name + "." + name: value for name, value in left.items()},
                **{other.alias_name + "." + name: value for name, value in right.items()}}
            return all(combined.get(first) == combined.get(second) for first, second in conditions)
        return Frame([left for left in self.rows if not any(match(left, right) for right in other.rows)], self.columns)

    def limit(self, count):
        return Frame(self.rows[:count], self.columns)

    def count(self):
        return len(self.rows)


class ProjectionQualificationTests(unittest.TestCase):
    def source(self, payload):
        return Frame([dict(run_id="run", sequence=1, record_sha256="a" * 64,
            source_manifest_sha256="b" * 64, payload_json=canonical(payload).decode())])

    def test_all_agent_and_country_scalar_types_and_json_fact_values_are_reconstructed(self):
        for types, facts in ((AGENT_TYPES, AGENT_JSON_FACTS), (COUNTRY_TYPES, COUNTRY_JSON_FACTS)):
            values = {"long": 0, "double": 1.25, "boolean": False, "string": "captured"}
            payload = {name: values[datatype] for name, datatype in types.items()}
            payload.update({name: {"zero": 0, "false": False} for name in facts})
            expected = snapshot_source_projection(self.source(payload), types, facts, Functions)
            for name in types:
                self.assertEqual(expected.rows[0][name], payload[name])
            for name in facts:
                self.assertEqual(json.loads(expected.rows[0][name + "_json"]), payload[name])
            assert_projected_values(expected, expected, Functions, "snapshot")

    def test_each_typed_or_json_column_tamper_rejected_with_source_hash_unchanged(self):
        for types, facts in ((AGENT_TYPES, AGENT_JSON_FACTS), (COUNTRY_TYPES, COUNTRY_JSON_FACTS)):
            expected = snapshot_source_projection(self.source({}), types, facts, Functions)
            for name in [*types, *(name + "_json" for name in facts)]:
                with self.subTest(column=name):
                    row = {**expected.rows[0], name: "incorrect projection"}
                    with self.assertRaisesRegex(ValueError, "value disagreement"):
                        assert_projected_values(Frame([row]), expected, Functions, "snapshot")

    def test_missing_explicit_json_null_zero_false_are_distinct(self):
        absent = snapshot_source_projection(self.source({}), {"part_time_student": "boolean", "arrears_minor": "long"},
            ("inventory",), Functions)
        explicit = snapshot_source_projection(self.source({"inventory": None, "part_time_student": False, "arrears_minor": 0}),
            {"part_time_student": "boolean", "arrears_minor": "long"}, ("inventory",), Functions)
        self.assertIsNone(absent.rows[0]["inventory_json"])
        self.assertEqual(explicit.rows[0]["inventory_json"], "null")
        self.assertIs(explicit.rows[0]["part_time_student"], False)
        self.assertEqual(explicit.rows[0]["arrears_minor"], 0)
        with self.assertRaisesRegex(ValueError, "value disagreement"):
            assert_projected_values(absent, explicit, Functions, "agent_daily")

    def test_missing_extraneous_and_source_identity_rows_rejected(self):
        expected = snapshot_source_projection(self.source({}), AGENT_TYPES, AGENT_JSON_FACTS, Functions)
        for actual in (Frame([], expected.columns), Frame([expected.rows[0], {**expected.rows[0], "sequence": 2}]),
                Frame([{**expected.rows[0], "source_manifest_sha256": "c" * 64}])):
            with self.subTest(rows=actual.rows), self.assertRaisesRegex(ValueError, "value disagreement"):
                assert_projected_values(actual, expected, Functions, "agent_daily")

    def test_source_manifest_must_match_fresh_import_not_only_other_table_rows(self):
        digest = "a" * 64
        assert_source_manifest(Frame([{"source_manifest_sha256": digest}]), digest, Functions, "silver.agent_daily")
        for value in (None, "b" * 64):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, "source manifest disagreement"):
                assert_source_manifest(Frame([{"source_manifest_sha256": value}]), digest, Functions, "silver.agent_daily")
        with self.assertRaisesRegex(ValueError, "missing source manifest"):
            assert_source_manifest(Frame([{"sequence": 1}]), digest, Functions, "silver.agent_daily")

    def test_missing_projected_column_rejected_even_for_empty_dataset(self):
        expected = Frame([], ["sequence", "record_sha256", "maintenance_funded"])
        with self.assertRaisesRegex(ValueError, "missing columns"):
            assert_projected_values(Frame([], ["sequence", "record_sha256"]), expected, Functions, "country_daily")



class SnapshotMigrationTests(unittest.TestCase):
    def test_allowlist_contains_only_new_columns_never_existing_facts_or_source_identity(self):
        agents = economy_backfill_fields("workspace.gearth_silver.agent_daily")
        countries = economy_backfill_fields("workspace.gearth_silver.country_daily")
        self.assertEqual(agents["inventory_json"], ("inventory", "json"))
        self.assertEqual(countries["bank_failures"], ("bank_failures", "long"))
        for fields in (agents, countries):
            self.assertFalse(set(fields) & {"payload_json", "record_sha256", "source_manifest_sha256", "sequence", "cash_minor",
                "qol_parts_points_json", "plan_json", "residents"})
        self.assertEqual(economy_backfill_fields("workspace.gearth_silver.infrastructure_daily"), {})

    def test_actual_notebook_backfill_retry_after_schema_added_preserves_old_evidence(self):
        source = Path(__file__).resolve().parents[1] / "notebooks" / "01_import_finished_run.py"
        function = next(node for node in ast.parse(source.read_text()).body
            if isinstance(node, ast.FunctionDef) and node.name == "enrich_existing_snapshots")
        table = "workspace.gearth_silver.agent_daily"
        original = {**{name: None for name in economy_backfill_fields(table)},
            "cash_minor": 99, "record_sha256": "a" * 64, "source_manifest_sha256": "b" * 64, "sequence": 5,
            "payload_json": canonical({"cash_minor": 99, "part_time_student": False, "arrears_minor": 0,
                "inventory": {"food": 0}, "insured_minor": None}).decode()}
        rows = [original.copy()]
        calls = []
        class Delta:
            @staticmethod
            def forName(spark, name):
                assert name == table
                return Delta()
            def update(self, condition, set):
                affected = 0
                for row in rows:
                    if condition.evaluate(row):
                        row.update({name: value.evaluate(row) for name, value in set.items()})
                        affected += 1
                calls.append(affected)
        namespace = {"economy_backfill_fields": economy_backfill_fields, "F": Functions,
            "DeltaTable": Delta, "spark": object(), "_backfilled_tables": set()}
        exec(compile(ast.Module(body=[function], type_ignores=[]), str(source), "exec"), namespace)
        backfill = namespace["enrich_existing_snapshots"]
        # The columns already exist: this models a previous job stopping after ALTER.
        backfill(table)
        self.assertIs(rows[0]["part_time_student"], False)
        self.assertEqual(rows[0]["arrears_minor"], 0)
        self.assertEqual(json.loads(rows[0]["inventory_json"]), {"food": 0})
        self.assertIsNone(rows[0]["insured_minor"])
        for key in ("cash_minor", "payload_json", "record_sha256", "source_manifest_sha256", "sequence"):
            self.assertEqual(rows[0][key], original[key])
        migrated = rows[0].copy()
        backfill(table)  # Avoid rescanning the same table for every incoming chunk.
        self.assertEqual(calls, [1])
        namespace["_backfilled_tables"].clear()  # A new notebook execution/retry.
        backfill(table)
        self.assertEqual(calls, [1, 0])
        self.assertEqual(rows[0], migrated)
        # A non-null existing new projection is never overwritten by the backfill.
        rows[0]["arrears_minor"] = 999
        namespace["_backfilled_tables"].clear()
        backfill(table)
        self.assertEqual(rows[0]["arrears_minor"], 999)

    def test_legacy_json_column_null_semantics_do_not_break_old_row_qualification(self):
        payload = {name: None for name in COUNTRY_JSON_FACTS}
        expected = snapshot_source_projection(ProjectionQualificationTests().source(payload),
            COUNTRY_TYPES, COUNTRY_JSON_FACTS, Functions)
        self.assertIsNone(expected.rows[0]["plan_json"])
        self.assertEqual(expected.rows[0]["infrastructure_json"], "null")
        assert_projected_values(expected, expected, Functions, "country_daily")
