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
facts = final.join(runs.select("run_id", "seed", "mode", "government_rotation", "tick_limit", "population", "engine_commit", F.col("payload_json").alias("run_payload")), "run_id").join(receipts.select("run_id", "run_complete", "snapshot_complete", "source_manifest_sha256"), "run_id")
for name, path in {"residents": "residents", "unemployed": "unemployed", "approval": "approval", "unmet_needs": "unmet_needs", "gini": "gini", "treasury_minor": "government_cash_minor"}.items():
    facts = facts.withColumn(name, F.get_json_object("payload_json", "$." + path).cast("long" if name in {"residents", "unemployed", "unmet_needs", "treasury_minor"} else "double"))
facts = facts.withColumn("employment_rate", F.when(F.col("residents") > 0, 1 - F.col("unemployed") / F.col("residents")))
providers = F.from_json(F.get_json_object("run_payload", "$.compute_providers"), "array<struct<name:string,model:string,capacity:int,decision_group:string>>")
model_assignment_known = (F.size(providers) > 0) & ~F.exists(providers, lambda provider: provider["model"].isNull() | (F.length(F.trim(provider["model"])) == 0))
facts = facts.withColumn("configuration_known", F.col("engine_commit").rlike("^[0-9a-fA-F]{40}$") & F.col("government_rotation").isNotNull() & F.col("tick_limit").isNotNull() & F.col("population").isNotNull()
    & F.get_json_object("run_payload", "$.worker_capacity").isNotNull() & F.get_json_object("run_payload", "$.tick_interval_ms").isNotNull()
    & ((F.col("mode") == "RULE") | model_assignment_known))
facts = facts.withColumn("condition_signature", F.sha2(F.to_json(F.struct("mode", "tick_limit", "population", "engine_commit",
    F.get_json_object("run_payload", "$.worker_capacity").alias("worker_capacity"),
    F.get_json_object("run_payload", "$.tick_interval_ms").alias("tick_interval_ms"),
    F.get_json_object("run_payload", "$.compute_providers").alias("compute_providers"))), 256))
display(facts.select("run_id", "seed", "mode", "government_rotation", "country_id", "government_type", "run_complete", "configuration_known", "residents", "employment_rate", "approval", "unmet_needs", "gini", "treasury_minor"))

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
    comparison = paired.groupBy("government_type", "country_id", "mode").agg(
        F.countDistinct("run_id").alias("run_samples"), F.countDistinct("seed").alias("seed_samples"),
        F.avg("approval").alias("approval_mean"), F.stddev_samp("approval").alias("approval_sample_sd"),
        F.avg("employment_rate").alias("employment_rate_mean"), F.avg("unmet_needs").alias("unmet_needs_mean"),
        F.avg("gini").alias("gini_mean"), F.avg("treasury_minor").alias("treasury_mean_minor"))
    display(comparison)
    comparison.createOrReplaceTempView("faultline_government_comparison")

# COMMAND ----------
# Money stays integer at transfer level and is counted once per actual leg.
transfers = spark.table("workspace.faultline_silver.transactions")
display(transfers.groupBy("run_id", "transfer_kind").agg(F.count("*").alias("transfer_legs"), F.sum("amount_minor").alias("amount_minor")))
