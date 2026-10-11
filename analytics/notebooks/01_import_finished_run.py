# Databricks notebook source
# MAGIC %md
# MAGIC # Import a finished Faultline run
# MAGIC Run only after review and after all immutable export files are uploaded.
# MAGIC The default catalog is `workspace`; managed Delta tables are unpartitioned.
# MAGIC This validates every chunk before writing, then imports one compressed chunk
# MAGIC at a time. It performs no operational database reads and no model inference.

# COMMAND ----------
dbutils.widgets.text("export_directory", "/Volumes/workspace/faultline_bronze/run_exports/REPLACE_RUN_DIRECTORY")
dbutils.widgets.text("library_zip", "/Volumes/workspace/faultline_bronze/run_exports/_code/faultline-analytics.zip")
import sys
from pathlib import Path
sys.path.insert(0, dbutils.widgets.get("library_zip"))
from analytics.validate import validate, read_manifest, safe_file, iter_records
from analytics.export import canonical
from analytics.report import build_report
from analytics.delta_validation import qualify_delta
from analytics.transform import AGENT_TYPES, COUNTRY_TYPES
from analytics.conditions import comparison_conditions
from analytics.schema import nullable_additions
from pyspark.sql import functions as F
from delta.tables import DeltaTable

export_directory = Path(dbutils.widgets.get("export_directory"))
manifest_path = export_directory / "manifest.json"
integrity = validate(manifest_path)  # bounded driver iteration, never collect audit
manifest = read_manifest(manifest_path)
run = manifest["run"]
manifest_hash = integrity["source_manifest_sha256"]
metadata_rows = iter_records(manifest_path, manifest)
try:
    original_metadata = next(metadata_rows)["payload"]
finally:
    metadata_rows.close()
conditions = comparison_conditions(original_metadata)
display(spark.createDataFrame([integrity]))

# COMMAND ----------
for schema in ("faultline_bronze", "faultline_silver", "faultline_gold"):
    spark.sql(f"CREATE SCHEMA IF NOT EXISTS workspace.{schema}")


def insert_only(frame, table, keys):
    """Immutable MERGE: equal replay is harmless, conflicting replay is rejected."""
    if not spark.catalog.tableExists(table):
        # Even zero-decision RULE runs expose typed empty investigation tables.
        frame.limit(0).write.format("delta").mode("error").saveAsTable(table)
    else:
        # Add nullable projections only. Existing raw/hash/facts remain immutable;
        # older rows retain null for unavailable new observations.
        existing_types = {field.name: field.dataType.simpleString() for field in spark.table(table).schema.fields}
        new_types = {field.name: field.dataType.simpleString() for field in frame.schema.fields}
        additions = ["`" + name.replace("`", "``") + "` " + datatype
                     for name, datatype in nullable_additions(existing_types, new_types).items()]
        if additions:
            spark.sql(f"ALTER TABLE {table} ADD COLUMNS (" + ", ".join(additions) + ")")
    if frame.limit(1).count() == 0:
        return
    duplicates = frame.groupBy(*keys).count().where("count > 1")
    if duplicates.limit(1).count():
        raise ValueError(f"Duplicate immutable keys in {table}")
    existing = spark.table(table)
    condition = [F.col("s." + key) == F.col("t." + key) for key in keys]
    conflict = frame.alias("s").join(existing.alias("t"), condition, "inner").where(F.col("s.record_sha256") != F.col("t.record_sha256"))
    if conflict.limit(1).count():
        raise ValueError(f"Conflicting duplicate keys in {table}; refusing overwrite")
    merge_condition = " AND ".join(f"t.{key} = s.{key}" for key in keys)
    DeltaTable.forName(spark, table).alias("t").merge(frame.alias("s"), merge_condition).whenNotMatchedInsertAll().execute()


def p(path, type="string"):
    return F.get_json_object("payload_json", "$." + path).cast(type)


# All tables retain original payload, source cursor, and digest. Money is BIGINT.
projections = {
    "opening_balance": ("opening_balances", ["run_id", "account_id"], {"account_id": p("account_id"), "account_type": p("account_type"), "balance_minor": p("balance_minor", "long")}),
    "agent_day": ("agent_daily", ["run_id", "agent_id", "tick", "frame_kind"], {"agent_id": p("agent_id"), "country_id": p("country_id"), "role": p("role"), "cash_minor": p("cash_minor", "long"), "health": p("health", "double"), "wellbeing": p("wellbeing", "double"), "decision_maker": p("decision_maker", "boolean"), "employer_id": p("employer_id"), "closed": p("closed", "boolean"), "frame_kind": p("frame_kind")}),
    "nation_day": ("country_daily", ["run_id", "country_id", "tick", "frame_kind"], {"country_id": F.coalesce(p("country_id"), p("id")), "frame_kind": p("frame_kind"), "government_type": F.coalesce(p("government_type"), p("government"))}),
    "transaction": ("transactions", ["run_id", "transaction_id"], {"transaction_id": p("transaction_id"), "transaction_group_id": p("transaction_group_id"), "sender_account_id": p("sender_account_id"), "recipient_account_id": p("recipient_account_id"), "amount_minor": p("amount_minor", "long"), "transfer_kind": p("kind"), "decision_id": p("decision_id"), "event_id": p("event_id"), "routine_id": p("routine_id"), "causal_source": p("causal_source"), "good": p("good"), "quantity": p("quantity", "long"), "unit_price_minor": p("unit_price_minor", "long")}),
    "decision_request": ("decision_requests", ["run_id", "decision_id"], {"decision_id": p("decision_id"), "agent_id": p("agent_id"), "role": p("role"), "country_id": p("country_id"), "observation_tick": p("observation_tick", "long"), "observation_json": p("job.observation"), "options_json": p("job.candidates"), "prompt_contract_version": p("prompt_contract_version")}),
    "decision_result": ("decision_results", ["run_id", "decision_id"], {"decision_id": p("decision_id"), "agent_id": p("agent_id"), "status": p("status"), "accepted": p("accepted", "boolean"), "fallback": p("fallback", "boolean"), "selected_option_index": p("selected_option_index", "long"), "code": p("code"), "applied_tick": p("applied_tick", "long"), "action_event_id": p("action_event_id"), "result_json": p("result")}),
    "action_effect": ("action_effects", ["run_id", "sequence"], {"decision_id": p("decision_id"), "event_id": p("event_id"), "entity_id": p("entity_id"), "entity_type": p("entity_type"), "link_type": p("link_type"), "before_json": p("before"), "after_json": p("after")}),
    "event": ("events", ["run_id", "event_sequence"], {"event_id": F.coalesce(p("event_id"), p("id")), "event_sequence": p("seq", "long"), "event_type": p("event_type"), "source": p("source"), "cause_ids_json": p("cause_ids"), "entity_ids_json": p("entity_ids")}),
    "run_metadata": ("runs", ["run_id"], {"seed": p("seed", "long"), "mode": p("mode"), "government_rotation": p("government_rotation", "long"), "tick_limit": p("tick_limit", "long"), "population": F.coalesce(p("initial_population", "long"), p("population", "long")), "engine_commit": p("engine_commit"), "configuration_json": p("configuration")}),
}
projections["opening_balance"][2]["creation_tick"] = p("creation_tick", "long")

# Public-services v1 adds nullable fields without reinterpreting old run facts.
for field, datatype in AGENT_TYPES.items():
    projections["agent_day"][2][field] = p(field, datatype)
projections["agent_day"][2]["qol_parts_points_json"] = p("qol_parts_points")
for field, datatype in COUNTRY_TYPES.items():
    projections["nation_day"][2][field] = p(field, datatype)
for field in ("plan", "qol_parts", "needed_since", "planning_view", "last_policy_params"):
    projections["nation_day"][2][field + "_json"] = p(field)
for field in ("active_features", "features", "economy_configuration"):
    projections["run_metadata"][2][field + "_json"] = p(field)
projections["run_metadata"][2].update(banking_paused=p("banking_paused", "boolean"), capture_contract_version=p("capture_contract_version"))
projections["run_metadata"][2].update(condition_signature=F.lit(conditions["signature"]), conditions_known=F.lit(conditions["known"]),
    conditions_unknown_json=F.lit(canonical(conditions["unknown"]).decode()))
projections["decision_request"][2]["menu_parameter_contract"] = p("menu_parameter_contract")
projections["decision_result"][2].update(selected_params_json=F.coalesce(p("selected_params"), p("result.action.params")),
    selection_match_count=p("selection_match_count", "long"), selection_provenance=p("selection_provenance"))

# COMMAND ----------
for entry in manifest["files"]:
    chunk = str(safe_file(export_directory, entry["path"]))
    raw = spark.read.text(chunk).select(
        F.get_json_object("value", "$.run_id").alias("run_id"),
        F.get_json_object("value", "$.sequence").cast("long").alias("sequence"),
        F.get_json_object("value", "$.tick").cast("long").alias("tick"),
        F.get_json_object("value", "$.revision").cast("long").alias("revision"),
        F.get_json_object("value", "$.kind").alias("kind"),
        F.get_json_object("value", "$.payload").alias("payload_json"),
        F.col("value").alias("raw_json"), F.sha2("value", 256).alias("record_sha256"),
        F.lit(manifest_hash).alias("source_manifest_sha256"))
    # Serverless compute does not support DataFrame cache/persist APIs.
    insert_only(raw, "workspace.faultline_bronze.audit", ["run_id", "sequence"])
    for kind, (name, keys, fields) in projections.items():
        typed = raw.where(F.col("kind") == kind).select("run_id", "sequence", "tick", "revision", "payload_json", "record_sha256", "source_manifest_sha256", *[column.alias(name) for name, column in fields.items()])
        null_keys = typed.where(F.expr(" OR ".join(key + " IS NULL" for key in keys)))
        if null_keys.limit(1).count():
            raise ValueError(f"Missing immutable key in {name}")
        insert_only(typed, "workspace.faultline_silver." + name, keys)
        if kind == "event":
            edges = typed.select("run_id", "event_id", "sequence", "record_sha256", "source_manifest_sha256",
                F.explode(F.from_json("cause_ids_json", "array<string>")).alias("cause_event_id"))
            insert_only(edges, "workspace.faultline_silver.event_edges", ["run_id", "event_id", "cause_event_id"])
        elif kind == "decision_request":
            options = typed.select("run_id", "decision_id", "agent_id", "sequence", "record_sha256", "source_manifest_sha256", "payload_json",
                "options_json", F.posexplode(F.from_json("options_json", "array<struct<action_type:string>>" )).alias("option_index", "option"))
            options = options.select("run_id", "decision_id", "agent_id", "sequence", "record_sha256", "source_manifest_sha256", "payload_json", "option_index", F.col("option.action_type").alias("action_type"),
                F.lit(None).cast("string").alias("typed_params_json"),
                F.expr("get_json_object(options_json,concat('$[',cast(option_index as string),'].params'))").alias("offered_params_json"))
            insert_only(options, "workspace.faultline_silver.decision_options", ["run_id", "decision_id", "option_index"])

# COMMAND ----------
# Fail before Gold views, cached report and receipt if the stored tables disagree.
qualification = qualify_delta(spark, manifest, manifest_hash)
qualification_path = export_directory / ("qualification-" + manifest_hash[:12] + ".json")
qualification_path.write_bytes(canonical(qualification) + b"\n")
display(spark.createDataFrame([(name, stats["rows"]) for name, stats in qualification["silver"].items()], ["silver_table", "rows"]))

# COMMAND ----------
# Views preserve exactly one daily observation and the later terminal observation.
spark.sql("""CREATE OR REPLACE VIEW workspace.faultline_gold.agent_days AS
SELECT * FROM workspace.faultline_silver.agent_daily WHERE frame_kind='day'""")
spark.sql("""CREATE OR REPLACE VIEW workspace.faultline_gold.agent_latest AS
SELECT * EXCEPT (row_rank) FROM (
  SELECT *,row_number() OVER (PARTITION BY run_id,agent_id ORDER BY sequence DESC) AS row_rank
  FROM workspace.faultline_silver.agent_daily) WHERE row_rank=1""")
spark.sql("""CREATE OR REPLACE VIEW workspace.faultline_gold.country_days AS
SELECT run_id,country_id,tick,sequence,government_type,
CAST(get_json_object(payload_json,'$.residents') AS BIGINT) AS residents,
CAST(get_json_object(payload_json,'$.unemployed') AS BIGINT) AS unemployed,
CAST(get_json_object(payload_json,'$.approval') AS DOUBLE) AS approval,
CAST(get_json_object(payload_json,'$.gini') AS DOUBLE) AS gini,
CAST(get_json_object(payload_json,'$.unmet_needs') AS BIGINT) AS unmet_needs,
CAST(get_json_object(payload_json,'$.government_cash_minor') AS BIGINT) AS treasury_minor,
payload_json,source_manifest_sha256
FROM workspace.faultline_silver.country_daily WHERE frame_kind='day'""")
spark.sql("""CREATE OR REPLACE VIEW workspace.faultline_gold.public_service_days AS
SELECT run_id,country_id,tick,sequence,government_type,students,graduates,job_seekers,hungry_residents,
too_sick_residents,average_health,average_wellbeing,food_subsidy_pct,food_subsidy_paid_minor,
startup_grants_paid_minor,capitation_paid_minor,tuition_subsidy_pct,student_stipend,hiring_bonus_minor,
output_lost_to_hunger_pct,plan_json,qol_parts_json,planning_view_json,last_policy_params_json,
payload_json,source_manifest_sha256 FROM workspace.faultline_silver.country_daily WHERE frame_kind='day'""")
spark.sql("""CREATE OR REPLACE VIEW workspace.faultline_gold.public_service_transfers AS
SELECT * FROM workspace.faultline_silver.transactions WHERE transfer_kind IN
('startup_grant','food_subsidy','tuition','tuition_subsidy','student_stipend','hiring_bonus','capitation','public_healthcare','sick_pay')""")

# COMMAND ----------
# Emit a cached report only after the entire manifest has imported successfully.
# Local helper uses SQLite spill, keeping all-year decisions/ledger off driver RAM.
report = build_report(manifest_path, max_agents=100, work_directory="/tmp")
report_path = export_directory / ("report-" + manifest_hash[:12] + ".json")
report_bytes = canonical(report) + b"\n"
if report_path.exists() and report_path.read_bytes() != report_bytes:
    # Report generated_at changes between executions; facts/provenance remain stable.
    import json
    previous = json.loads(report_path.read_text())
    if any(previous[key] != report[key] for key in ("run_id", "through_revision", "through_sequence", "source_manifest_sha256")):
        raise ValueError("Existing cached report has conflicting source provenance")
    if previous["analysis_version"] == report["analysis_version"]:
        if any(previous[key] != report[key] for key in ("summary", "checks", "completeness", "agents", "countries", "government_comparison", "public_services", "evidence_refs")):
            raise ValueError("Existing cached report has conflicting analysis at the same version")
    else:
        import re
        previous_version = previous["analysis_version"]
        if not re.fullmatch(r"[A-Za-z0-9._-]{1,50}", previous_version):
            raise ValueError("Unsafe prior analysis version")
        archive = export_directory / ("report-" + manifest_hash[:12] + "-" + previous_version + ".json")
        old_bytes = report_path.read_bytes()
        if archive.exists() and archive.read_bytes() != old_bytes:
            raise ValueError("Prior analysis report archive conflicts")
        if not archive.exists():
            archive.write_bytes(old_bytes)
        report_path.write_bytes(report_bytes)
else:
    report_path.write_bytes(report_bytes)
receipt = {"run_id": run["run_id"], "through_revision": run["through_revision"], "through_sequence": run["through_sequence"],
           "source_manifest_sha256": manifest_hash, "record_sha256": manifest_hash, "analysis_version": report["analysis_version"],
           "snapshot_complete": True, "run_complete": run["status"] == "completed", "record_count": manifest["record_count"]}
insert_only(spark.createDataFrame([receipt]), "workspace.faultline_gold.import_receipts", ["run_id", "through_revision"])
display(spark.createDataFrame([(check["name"], check["status"]) for check in report["checks"]], ["check", "status"]))
print("Cached report:", report_path)
