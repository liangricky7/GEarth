# Databricks notebook source
# MAGIC %md
# MAGIC # Government comparison with sample counts
# MAGIC Descriptive final nation metrics include partial runs. Matched comparisons
# MAGIC require all five rotations at one seed and identical known engine/configuration.
# MAGIC Unknown configuration and unmatched seeds remain visible and are excluded from
# MAGIC claims of matched comparisons. These results describe the simulator.

# COMMAND ----------
from pyspark.sql import functions as F, Window
runs = spark.table("workspace.faultline_silver.runs")
receipts = spark.table("workspace.faultline_gold.import_receipts")
countries = spark.table("workspace.faultline_silver.country_daily")
ranked = countries.withColumn("rank", F.row_number().over(Window.partitionBy("run_id", "country_id").orderBy(F.col("sequence").desc())))
final = ranked.where("rank = 1").drop("rank")
facts = final.join(runs.select("run_id", "seed", "mode", "government_rotation", "tick_limit", "population", "engine_commit", "condition_signature", "conditions_known", "conditions_unknown_json", "features_json", "active_features_json", "banking_paused", "economy_configuration_json", F.col("payload_json").alias("run_payload")), "run_id").join(receipts.select("run_id", "run_complete", "snapshot_complete", "source_manifest_sha256"), "run_id")
for name, path in {"residents": "residents", "unemployed": "unemployed", "approval": "approval", "unmet_needs": "unmet_needs", "gini": "gini", "treasury_minor": "government_cash_minor"}.items():
    facts = facts.withColumn(name, F.get_json_object("payload_json", "$." + path).cast("long" if name in {"residents", "unemployed", "unmet_needs", "treasury_minor"} else "double"))
facts = facts.withColumn("employment_rate", F.when(F.col("residents") > 0, 1 - F.col("unemployed") / F.col("residents")))
facts = facts.withColumn("configuration_known", F.coalesce(F.col("conditions_known"), F.lit(False)) & F.col("government_rotation").isNotNull())
display(facts.select("run_id", "seed", "mode", "government_rotation", "country_id", "government_type", "run_complete", "configuration_known", "conditions_unknown_json", "features_json", "active_features_json", "banking_paused", "residents", "employment_rate", "approval", "unmet_needs", "gini", "treasury_minor"))
print("Older imports without explicit feature/configuration evidence stay descriptive and are excluded from matched sets. Banking pause and school/health/QOL/planner settings are part of the condition signature.")

# COMMAND ----------
eligible = facts.where("run_complete AND snapshot_complete AND configuration_known")
groups = eligible.select("run_id", "seed", "condition_signature", "government_rotation").dropDuplicates().groupBy("seed", "condition_signature").agg(F.countDistinct("government_rotation").alias("rotation_count"), F.countDistinct("run_id").alias("run_count"), F.sort_array(F.collect_set("government_rotation")).alias("rotations"))
matched = groups.where((F.col("rotation_count") == 5) & (F.col("rotations") == F.array(*[F.lit(index) for index in range(5)])))
paired = eligible.join(matched.select("seed", "condition_signature"), ["seed", "condition_signature"], "inner")
display(groups)
if paired.limit(1).count() == 0:
    print("Insufficient evidence: no complete five-rotation matched seed/configuration group. Descriptive rows above remain available.")
else:
    # One geography-country is fixed within each matched set; sample counts and spread
    # are explicit. No simulated agent row is treated as an independent run replicate.
    comparison = paired.groupBy("condition_signature", "government_type", "country_id", "mode").agg(
        F.countDistinct("run_id").alias("run_samples"), F.countDistinct("seed").alias("seed_samples"),
        F.avg("approval").alias("approval_mean"), F.stddev_samp("approval").alias("approval_sample_sd"),
        F.avg("employment_rate").alias("employment_rate_mean"), F.avg("unmet_needs").alias("unmet_needs_mean"),
        F.avg("gini").alias("gini_mean"), F.avg("treasury_minor").alias("treasury_mean_minor"))
    display(comparison)
    comparison.createOrReplaceTempView("faultline_government_comparison")

# COMMAND ----------
display(facts.select("run_id", "country_id", "government_type", "run_complete", "features_json", "banking_paused", "students", "graduates", "job_seekers", "hungry_residents", "too_sick_residents", "average_health", "average_wellbeing", "output_lost_to_hunger_pct", "plan_json", "qol_parts_json"))

# COMMAND ----------
# Money stays integer at transfer level and is counted once per actual leg.
transfers = spark.table("workspace.faultline_silver.transactions")
display(transfers.groupBy("run_id", "transfer_kind").agg(F.count("*").alias("transfer_legs"), F.sum("amount_minor").alias("amount_minor")))
