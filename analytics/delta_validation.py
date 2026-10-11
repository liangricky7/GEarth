"""Bounded Delta qualification; only scalar aggregates reach the driver."""
from .transform import TABLES, AGENT_TYPES, COUNTRY_TYPES, AGENT_JSON_FACTS, COUNTRY_JSON_FACTS, LEGACY_AGENT_JSON_FACTS, LEGACY_COUNTRY_JSON_FACTS


def assert_coverage(manifest, bronze, silver, derived):
    """Pure validation used by the notebook and offline tests."""
    expected_rows = manifest["record_count"]
    expected_cursor = manifest["run"]["through_sequence"]
    if any(bronze.get(key) != expected for key, expected in (("rows", expected_rows), ("unique_sequences", expected_rows),
            ("first_sequence", 1), ("last_sequence", expected_cursor))):
        raise ValueError("Delta Bronze coverage disagrees with pinned manifest")
    for kind, table in TABLES.items():
        expected = manifest["counts_by_kind"].get(kind, 0)
        if bronze.get("kind_" + kind) != expected:
            raise ValueError("Delta Bronze kind count disagrees with pinned manifest: " + kind)
        stats = silver.get(table, {})
        if stats.get("rows") != expected or stats.get("unique_sequences") != expected:
            raise ValueError("Delta Silver kind coverage disagrees with manifest: " + table)
    for table in ("decision_options", "event_edges", "infrastructure_daily"):
        if table == "infrastructure_daily" and table not in derived:
            continue  # Older qualification receipts did not describe this table.
        stats = silver.get(table, {})
        if stats.get("rows") != derived.get(table) or stats.get("unique_keys") != derived.get(table):
            raise ValueError("Delta derived-table coverage disagrees with source Bronze: " + table)


def assert_source_manifest(frame, manifest_sha256, functions, table):
    if "source_manifest_sha256" not in frame.columns:
        raise ValueError("Delta dataset missing source manifest: " + table)
    source = functions.col("source_manifest_sha256")
    if frame.where(source.isNull() | (source != manifest_sha256)).limit(1).count():
        raise ValueError("Delta dataset source manifest disagreement: " + table)


def snapshot_source_projection(source, types, json_facts, functions):
    """Independently rebuild expected snapshot values from original Bronze JSON."""
    F = functions
    fields = [F.get_json_object("payload_json", "$." + name).cast(datatype).alias(name)
        for name, datatype in types.items()]
    legacy = LEGACY_AGENT_JSON_FACTS if types is AGENT_TYPES else LEGACY_COUNTRY_JSON_FACTS if types is COUNTRY_TYPES else ()
    fields.extend((F.get_json_object("payload_json", "$." + name) if name in legacy else
        F.when(F.array_contains(F.json_object_keys(F.col("payload_json")), name),
            F.coalesce(F.get_json_object("payload_json", "$." + name), F.lit("null")))
            .otherwise(F.lit(None).cast("string"))).alias(name + "_json") for name in json_facts)
    return source.select("run_id", "sequence", "record_sha256", "source_manifest_sha256", *fields)


def assert_projected_values(actual, expected, functions, table):
    """Distributed null-safe equality in both directions; collect no row values."""
    columns = expected.columns
    if any(name not in actual.columns for name in columns):
        raise ValueError("Silver typed projection missing columns: " + table)
    matches = [functions.col("actual." + name).eqNullSafe(functions.col("source." + name)) for name in columns]
    if (actual.alias("actual").join(expected.alias("source"), matches, "left_anti").limit(1).count()
            or expected.alias("source").join(actual.alias("actual"), matches, "left_anti").limit(1).count()):
        raise ValueError("Silver typed projection/source value disagreement: " + table)


def qualify_delta(spark, manifest, manifest_sha256):
    from pyspark.sql import functions as F
    run = manifest["run"]
    run_id = run["run_id"]
    bronze_frame = spark.table("workspace.gearth_bronze.audit").where(F.col("run_id") == run_id)
    assert_source_manifest(bronze_frame, manifest_sha256, F, "bronze.audit")
    bronze = bronze_frame.agg(F.count("*").alias("rows"), F.countDistinct("sequence").alias("unique_sequences"),
        F.min("sequence").alias("first_sequence"), F.max("sequence").alias("last_sequence"),
        *[F.sum(F.when(F.col("kind") == kind, 1).otherwise(0)).alias("kind_" + kind) for kind in TABLES]).first().asDict()
    silver = {}
    for table in TABLES.values():
        frame = spark.table("workspace.gearth_silver." + table).where(F.col("run_id") == run_id)
        assert_source_manifest(frame, manifest_sha256, F, "silver." + table)
        silver[table] = frame.agg(F.count("*").alias("rows"), F.countDistinct("sequence").alias("unique_sequences")).first().asDict()
        kind = next(kind for kind, name in TABLES.items() if name == table)
        source_rows = bronze_frame.where(F.col("kind") == kind)
        source = source_rows.select("sequence", "record_sha256")
        # Cardinality alone cannot detect substituting a different source record.
        if frame.select("sequence", "record_sha256").join(source, ["sequence", "record_sha256"], "left_anti").limit(1).count():
            raise ValueError("Silver source-record identity/hash disagreement: " + table)
        if kind in ("agent_day", "nation_day"):
            types, json_facts = (AGENT_TYPES, AGENT_JSON_FACTS) if kind == "agent_day" else (COUNTRY_TYPES, COUNTRY_JSON_FACTS)
            assert_projected_values(frame, snapshot_source_projection(source_rows, types, json_facts, F), F, table)
    from .transform import PROJECT_TYPES
    project_schema = "array<struct<" + ",".join(name + ":" + datatype for name, datatype in PROJECT_TYPES.items()) + ">>"
    expected_projects = bronze_frame.where(F.col("kind") == "nation_day").select(
        "sequence", "revision", "payload_json", "record_sha256", "source_manifest_sha256",
        F.get_json_object("payload_json", "$.infrastructure").alias("infrastructure_json"),
        F.coalesce(F.get_json_object("payload_json", "$.country_id"), F.get_json_object("payload_json", "$.id")).alias("country_id"),
        "tick", F.coalesce(F.get_json_object("payload_json", "$.frame_kind"), F.lit("day")).alias("frame_kind"),
        F.posexplode(F.from_json(F.get_json_object("payload_json", "$.infrastructure"), project_schema)).alias("project_index", "project"))
    project_frame = spark.table("workspace.gearth_silver.infrastructure_daily").where(F.col("run_id") == run_id)
    assert_source_manifest(project_frame, manifest_sha256, F, "silver.infrastructure_daily")
    project_keys = ["country_id", "tick", "frame_kind", "project_id"]
    silver["infrastructure_daily"] = project_frame.agg(F.count("*").alias("rows"),
        F.countDistinct(*project_keys).alias("unique_keys")).first().asDict()
    expected_identity = expected_projects.select("sequence", "revision", "payload_json", "record_sha256", "source_manifest_sha256", "country_id", "tick", "frame_kind",
        F.expr("get_json_object(infrastructure_json,concat('$[',cast(project_index as string),']'))").alias("project_json"),
        F.when(F.col("project.complete") == True, F.lit("complete"))
         .when(F.col("project.complete") == False, F.lit("under_construction")).alias("status"),
        F.col("project.id").alias("project_id"), *[F.col("project." + name).alias(name) for name in PROJECT_TYPES if name != "id"])
    assert_projected_values(project_frame, expected_identity, F, "infrastructure_daily")
    derived = {
        "decision_options": bronze_frame.where(F.col("kind") == "decision_request").select(
            F.explode(F.from_json(F.get_json_object("payload_json", "$.job.candidates"), "array<struct<action_type:string>>"))).count(),
        "infrastructure_daily": expected_projects.count(),
        "event_edges": bronze_frame.where(F.col("kind") == "event").select(
            F.explode(F.from_json(F.get_json_object("payload_json", "$.cause_ids"), "array<string>"))).count(),
    }
    for table, keys in (("decision_options", ["decision_id", "option_index"]), ("event_edges", ["event_id", "cause_event_id"])):
        frame = spark.table("workspace.gearth_silver." + table).where(F.col("run_id") == run_id)
        assert_source_manifest(frame, manifest_sha256, F, "silver." + table)
        silver[table] = frame.agg(F.count("*").alias("rows"), F.countDistinct(*keys).alias("unique_keys")).first().asDict()
    assert_coverage(manifest, bronze, silver, derived)
    return {"schema_version": "gearth.delta-qualification.v1", "run_id": run_id,
            "through_revision": run["through_revision"], "through_sequence": run["through_sequence"],
            "source_manifest_sha256": manifest_sha256, "status": "pass",
            "bronze": bronze, "silver": silver, "expected_derived_counts": derived}
