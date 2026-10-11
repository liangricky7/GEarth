# Databricks notebook source
# MAGIC %md
# MAGIC # Data checks and replay verification
# MAGIC Reconcile final cash/escrow against opening balances and individual transfers.
# MAGIC Failed and unknown checks remain visible; a completed import receipt describes
# MAGIC import coverage and does not turn a failed reconciliation into a pass.

# COMMAND ----------
dbutils.widgets.text("run_id", "")
from pyspark.sql import functions as F, Window
run_id = dbutils.widgets.get("run_id")
if not run_id:
    raise ValueError("Set run_id")
def table(name):
    return spark.table("workspace.faultline_silver." + name).where(F.col("run_id") == run_id)

opening = table("opening_balances").select("account_id", F.col("balance_minor").alias("opening_minor"))
transactions = table("transactions")
incoming = transactions.select(F.col("recipient_account_id").alias("account_id"), F.col("amount_minor").alias("net_minor"))
outgoing = transactions.select(F.col("sender_account_id").alias("account_id"), (-F.col("amount_minor")).alias("net_minor"))
net = incoming.unionByName(outgoing).groupBy("account_id").agg(F.sum("net_minor").alias("net_minor"))
agents = table("agent_daily").withColumn("rank", F.row_number().over(Window.partitionBy("agent_id").orderBy(F.col("sequence").desc()))).where("rank = 1").select(F.col("agent_id").alias("account_id"), F.col("cash_minor").alias("latest_minor"))
nations = table("country_daily").withColumn("rank", F.row_number().over(Window.partitionBy("country_id").orderBy(F.col("sequence").desc()))).where("rank = 1")
escrow = nations.select(F.explode(F.from_json(F.get_json_object("payload_json", "$.escrow_accounts"), "array<struct<account_id:string,balance_minor:bigint>>")).alias("account")).select(F.col("account.account_id").alias("account_id"), F.col("account.balance_minor").alias("latest_minor"))
escrow_openings = escrow.select("account_id").join(opening, "account_id", "left_anti").withColumn("opening_minor", F.lit(0).cast("long"))
creation = table("events").where(F.col("event_type") == "business_started").select(
    F.get_json_object("payload_json", "$.payload.account_id").alias("account_id"),
    F.get_json_object("payload_json", "$.payload.opening_cash_minor").cast("long").alias("opening_minor"))
creation = creation.where(F.col("account_id").isNotNull() & (F.col("opening_minor") == 0)).join(opening, "account_id", "left_anti")
all_openings = opening.unionByName(escrow_openings).unionByName(creation)
closing = agents.unionByName(escrow)
checks = all_openings.join(net, "account_id", "full_outer").join(closing, "account_id", "full_outer").withColumn("net_minor", F.coalesce("net_minor", F.lit(0))).withColumn("status",
    F.when(F.col("opening_minor").isNull() | F.col("latest_minor").isNull(), "unknown").when(F.col("latest_minor") == F.col("opening_minor") + F.col("net_minor"), "pass").otherwise("fail"))
display(checks.groupBy("status").count())
display(checks.where("status != 'pass'").limit(100))
display(checks.agg(F.sum("opening_minor").alias("opening_cash_plus_escrow_minor"), F.sum("latest_minor").alias("final_cash_plus_escrow_minor"), F.sum("net_minor").alias("net_transfers_minor")))

# COMMAND ----------
bronze = spark.table("workspace.faultline_bronze.audit").where(F.col("run_id") == run_id)
receipts = spark.table("workspace.faultline_gold.import_receipts").where(F.col("run_id") == run_id)
display(receipts)
display(bronze.agg(F.count("*").alias("rows"), F.countDistinct("sequence").alias("unique_sequences"), F.min("sequence").alias("first_sequence"), F.max("sequence").alias("last_sequence")))
duplicates = transactions.groupBy("transaction_id").count().where("count > 1")
if duplicates.limit(1).count():
    raise ValueError("Duplicate transfer identities in Silver")
print("Rerun import notebook with unchanged manifest, then rerun this notebook; rows and keys must remain identical.")
