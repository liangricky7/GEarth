# Databricks notebook source
# Capture a preservation baseline before the schema metadata rename, then repair
# derived view references and verify all original table rows after the rename.
import json
import re
from pathlib import Path
from pyspark.sql import functions as F

dbutils.widgets.text("mode", "before")
dbutils.widgets.text("source_prefix", "")
prefix = dbutils.widgets.get("source_prefix")
if not re.fullmatch(r"[a-z][a-z0-9_]*", prefix):
    raise ValueError("Supply the previous schema prefix")
mode = dbutils.widgets.get("mode")
if mode not in {"before", "after"}:
    raise ValueError("mode must be before or after")
current = prefix if mode == "before" else "gearth"
baseline_path = Path(f"/Volumes/workspace/{current}_bronze/run_exports/_verification/gearth-rename-before.json")


def fingerprint(table):
    return spark.table(table).agg(F.count("*").alias("rows"),
        F.expr("bit_xor(xxhash64(to_json(struct(*))))").alias("fingerprint")).first().asDict()


if mode == "before":
    if baseline_path.exists():
        raise ValueError("Baseline already exists; preserve it")
    baseline = {"tables": {}, "views": {}}
    for layer in ("bronze", "silver", "gold"):
        for table in spark.catalog.listTables(f"workspace.{prefix}_{layer}"):
            name = f"workspace.{prefix}_{layer}.{table.name}"
            if table.tableType == "VIEW":
                baseline["views"][name] = spark.sql("SHOW CREATE TABLE " + name).first()[0]
            else:
                baseline["tables"][name] = fingerprint(name)
    baseline_path.parent.mkdir(exist_ok=True)
    baseline_path.write_text(json.dumps(baseline, sort_keys=True))
    dbutils.notebook.exit(json.dumps({"status": "baseline_saved", "tables": len(baseline["tables"]), "views": len(baseline["views"])}))
else:
    baseline = json.loads(baseline_path.read_text())
    for ddl in baseline["views"].values():
        # SHOW CREATE emits two-part view names and three-part references;
        # handle both, including quoted identifiers.
        ddl = re.sub(r"\b" + re.escape(prefix) + r"_(bronze|silver|gold)\b", r"gearth_\1", ddl)
        ddl = re.sub(r"^CREATE VIEW\b", "CREATE OR REPLACE VIEW", ddl, count=1)
        spark.sql(ddl)
    checked = []
    for old_name, before in baseline["tables"].items():
        name = old_name.replace(f"workspace.{prefix}_", "workspace.gearth_")
        after = fingerprint(name)
        if after != before:
            raise ValueError("Table rows changed during rename: " + name)
        checked.append({"table": name, **after})
    for old_name in baseline["views"]:
        name = old_name.replace(f"workspace.{prefix}_", "workspace.gearth_")
        spark.table(name).limit(1).collect()
    result = {"status": "pass", "tables": checked, "views": len(baseline["views"]),
              "method": "full-row count and xxhash64 XOR; view execution after reference repair"}
    baseline_path.with_name("gearth-rename-preserved.json").write_text(json.dumps(result, sort_keys=True))
    dbutils.notebook.exit(json.dumps(result, sort_keys=True))
