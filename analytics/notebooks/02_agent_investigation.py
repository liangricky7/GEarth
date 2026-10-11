# Databricks notebook source
# MAGIC %md
# MAGIC # Individual agent investigation
# MAGIC Select one run and agent. Context and menus are recorded facts; reasons are
# MAGIC the agent's stated reasons; effects require engine links. Later state changes
# MAGIC are descriptive outcomes, with intervening decisions shown explicitly.

# COMMAND ----------
dbutils.widgets.text("run_id", "")
dbutils.widgets.text("agent_id", "")
from pyspark.sql import functions as F
run_id, agent_id = dbutils.widgets.get("run_id"), dbutils.widgets.get("agent_id")
if not run_id or not agent_id:
    raise ValueError("Set run_id and agent_id")

requests = spark.table("workspace.faultline_silver.decision_requests")
results = spark.table("workspace.faultline_silver.decision_results")
timeline = requests.alias("q").join(results.alias("r"), ["run_id", "decision_id"], "full_outer").select(
    "run_id", "decision_id", F.coalesce(F.col("q.agent_id"), F.col("r.agent_id")).alias("agent_id"),
    F.col("q.country_id"), F.col("q.role"), F.col("q.observation_tick"), F.col("r.applied_tick"),
    F.col("q.sequence").alias("request_sequence"), F.col("r.sequence").alias("result_sequence"),
    F.col("q.observation_json"), F.col("q.options_json"), F.col("r.result_json"),
    F.col("r.status"), F.col("r.accepted"), F.col("r.fallback"), F.col("r.code"),
    F.col("r.selected_option_index"), F.col("r.action_event_id"),
    F.col("q.menu_parameter_contract"), F.col("r.selected_params_json"), F.col("r.selection_match_count"), F.col("r.selection_provenance"),
    F.when(F.col("q.observation_json").isNull(), "missing_context").otherwise("recorded_fact").alias("context_status"))
spark.sql("""CREATE OR REPLACE VIEW workspace.faultline_gold.agent_timeline AS
SELECT coalesce(q.run_id,r.run_id) AS run_id, coalesce(q.decision_id,r.decision_id) AS decision_id,
coalesce(q.agent_id,r.agent_id) AS agent_id, q.country_id,q.role,q.observation_tick,r.applied_tick,
q.sequence AS request_sequence,r.sequence AS result_sequence,q.observation_json,q.options_json,
r.result_json,r.status,r.accepted,r.fallback,r.code,r.selected_option_index,r.action_event_id,
q.menu_parameter_contract,r.selected_params_json,r.selection_match_count,r.selection_provenance,
CASE WHEN q.observation_json IS NULL THEN 'missing_context' ELSE 'recorded_fact' END AS context_status
FROM workspace.faultline_silver.decision_requests q FULL OUTER JOIN workspace.faultline_silver.decision_results r
ON q.run_id=r.run_id AND q.decision_id=r.decision_id""")
selected = timeline.where((F.col("run_id") == run_id) & (F.col("agent_id") == agent_id)).orderBy("observation_tick", "request_sequence")
display(selected.limit(500))
print("Display limited to 500 decisions; full context/menu/results remain in Gold and Silver.")

# COMMAND ----------
# Denominator is decisions at which this action type was offered, not all decisions.
options = requests.select("run_id", "decision_id", "agent_id", "options_json", F.posexplode(F.from_json("options_json", "array<struct<action_type:string>>" )).alias("option_index", "option"))
options = options.withColumn("offered_params_json", F.expr("get_json_object(options_json,concat('$[',cast(option_index as string),'].params'))"))
display(options.where((F.col("run_id") == run_id) & (F.col("agent_id") == agent_id)).select("decision_id", "option_index", F.col("option.action_type").alias("action_type"), "offered_params_json").limit(1000))
offered_types = options.select("run_id", "decision_id", "agent_id", F.col("option.action_type").alias("action_type")).dropDuplicates(["run_id", "decision_id", "action_type"])
choices = results.select("run_id", "decision_id", "selected_option_index", "accepted", "fallback", "status", F.get_json_object("result_json", "$.action.action_type").alias("selected_action_type"))
frequency = offered_types.join(choices, ["run_id", "decision_id"], "left").where((F.col("run_id") == run_id) & (F.col("agent_id") == agent_id)).groupBy("action_type").agg(
    F.count("*").alias("offered_decisions"),
    F.sum(F.when((F.col("selected_action_type") == F.col("action_type")) & F.col("selected_option_index").isNotNull(), 1).otherwise(0)).alias("selected_decisions"),
    F.sum(F.when(F.col("fallback"), 1).otherwise(0)).alias("fallback_decisions"),
    F.sum(F.when(F.col("status") == "rejected", 1).otherwise(0)).alias("rejected_decisions"),
    F.sum(F.when(F.col("status").isNull(), 1).otherwise(0)).alias("unfinished_decisions"))
display(frequency)

# COMMAND ----------
daily = spark.table("workspace.faultline_silver.agent_daily").where((F.col("run_id") == run_id) & (F.col("agent_id") == agent_id))
display(daily.orderBy("tick", "sequence"))
transfers = spark.table("workspace.faultline_silver.transactions").where((F.col("run_id") == run_id) & ((F.col("sender_account_id") == agent_id) | (F.col("recipient_account_id") == agent_id)))
display(transfers.orderBy("sequence").limit(1000))
events = spark.table("workspace.faultline_silver.events").where((F.col("run_id") == run_id) & F.array_contains(F.from_json("entity_ids_json", "array<string>"), agent_id))
display(events.where(F.col("event_type").isin("enrolled", "graduated", "business_started", "startup_grant", "policy_proposed", "policy_enacted", "policy_blocked")).orderBy("sequence").limit(500))
print("School, hunger, health and recomputed QOL parts are snapshot facts; wellbeing is the smoothed engine score. Money legs do not prove an intervention caused a later outcome.")
if spark.catalog.tableExists("workspace.faultline_silver.action_effects"):
    effects = spark.table("workspace.faultline_silver.action_effects").where((F.col("run_id") == run_id) & (F.col("entity_id") == agent_id) & (F.col("link_type") == "engine_execution"))
    display(effects.orderBy("sequence").limit(500))

# COMMAND ----------
# Exactly observed +1/+7 day states. Missing days are incomplete outcome windows.
day_states = daily.where(F.col("frame_kind") == "day")
outcomes = []
for window in (1, 7):
    q = selected.alias("q")
    baseline = day_states.alias("b")
    future = day_states.alias("f")
    outcome = q.join(baseline, F.col("b.tick") == F.col("q.applied_tick"), "left").join(future, F.col("f.tick") == F.col("q.applied_tick") + window, "left").select(
        F.col("q.decision_id"), F.col("q.applied_tick"), F.lit(window).alias("outcome_window_days"),
        F.col("b.sequence").alias("baseline_sequence"), F.col("f.sequence").alias("outcome_sequence"),
        (F.col("f.cash_minor") - F.col("b.cash_minor")).alias("cash_change_minor"),
        (F.col("f.health") - F.col("b.health")).alias("health_change"),
        (F.col("f.wellbeing") - F.col("b.wellbeing")).alias("wellbeing_change"),
        (F.col("b.sequence").isNotNull() & F.col("f.sequence").isNotNull()).alias("window_complete"))
    interventions = selected.alias("x").join(selected.alias("y"), (F.col("y.applied_tick") > F.col("x.applied_tick")) & (F.col("y.applied_tick") <= F.col("x.applied_tick") + window), "left").groupBy(F.col("x.decision_id").alias("decision_id")).agg(F.count(F.col("y.decision_id")).alias("intervening_decisions"))
    outcomes.append(outcome.join(interventions, "decision_id", "left"))
display(outcomes[0].unionByName(outcomes[1]).orderBy("applied_tick", "outcome_window_days"))
print("Outcome changes describe observed windows; they are not causal estimates. Baseline is end-of-day, after the action.")
