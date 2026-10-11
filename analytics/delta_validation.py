"""Bounded Delta qualification; only scalar aggregates reach the driver."""
from .transform import TABLES


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
    for table in ("decision_options", "event_edges"):
        stats = silver.get(table, {})
        if stats.get("rows") != derived.get(table) or stats.get("unique_keys") != derived.get(table):
            raise ValueError("Delta derived-table coverage disagrees with source Bronze: " + table)


def qualify_delta(spark, manifest, manifest_sha256):
    from pyspark.sql import functions as F
    run = manifest["run"]
    run_id = run["run_id"]
    bronze_frame = spark.table("workspace.faultline_bronze.audit").where(F.col("run_id") == run_id)
    bronze = bronze_frame.agg(F.count("*").alias("rows"), F.countDistinct("sequence").alias("unique_sequences"),
        F.min("sequence").alias("first_sequence"), F.max("sequence").alias("last_sequence"),
        *[F.sum(F.when(F.col("kind") == kind, 1).otherwise(0)).alias("kind_" + kind) for kind in TABLES]).first().asDict()
    silver = {}
    for table in TABLES.values():
        frame = spark.table("workspace.faultline_silver." + table).where(F.col("run_id") == run_id)
        silver[table] = frame.agg(F.count("*").alias("rows"), F.countDistinct("sequence").alias("unique_sequences")).first().asDict()
        source = bronze_frame.where(F.col("kind") == next(kind for kind, name in TABLES.items() if name == table)).select("sequence", "record_sha256")
        # Cardinality alone cannot detect substituting a different source record.
        if frame.select("sequence", "record_sha256").join(source, ["sequence", "record_sha256"], "left_anti").limit(1).count():
            raise ValueError("Silver source-record identity/hash disagreement: " + table)
    derived = {
        "decision_options": bronze_frame.where(F.col("kind") == "decision_request").select(
            F.explode(F.from_json(F.get_json_object("payload_json", "$.job.candidates"), "array<struct<action_type:string>>"))).count(),
        "event_edges": bronze_frame.where(F.col("kind") == "event").select(
            F.explode(F.from_json(F.get_json_object("payload_json", "$.cause_ids"), "array<string>"))).count(),
    }
    for table, keys in (("decision_options", ["decision_id", "option_index"]), ("event_edges", ["event_id", "cause_event_id"])):
        frame = spark.table("workspace.faultline_silver." + table).where(F.col("run_id") == run_id)
        silver[table] = frame.agg(F.count("*").alias("rows"), F.countDistinct(*keys).alias("unique_keys")).first().asDict()
    assert_coverage(manifest, bronze, silver, derived)
    return {"schema_version": "faultline.delta-qualification.v1", "run_id": run_id,
            "through_revision": run["through_revision"], "through_sequence": run["through_sequence"],
            "source_manifest_sha256": manifest_sha256, "status": "pass",
            "bronze": bronze, "silver": silver, "expected_derived_counts": derived}
