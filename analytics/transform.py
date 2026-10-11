"""Pure typed audit projections shared by local checks and Databricks notebooks."""
from __future__ import annotations

import math

from .export import canonical, integer
from .conditions import comparison_conditions

AGENT_FACTS = ("occupation", "specialty", "owner_id", "owned_business_id", "student_until_tick", "school_id", "graduated",
               "seeking_until_tick", "is_student", "seeking_better_job", "hungry", "needs_met", "hungry_this_week",
               "priced_out_this_week", "in_kind_minor", "sick_days", "qol_target", "works_overtime", "salary_minor",
               "wage_offer_minor", "target_headcount", "price_minor", "deposit_minor", "keeps_savings_in_bank",
               "deposit_rate_pct", "loan_rate_pct", "lending_standard", "unmet_withdrawals_minor", "too_sick",
               "school_seats", "enrolled_students", "planned_daily_quota")
COUNTRY_FACTS = ("residents", "unemployed", "students", "graduates", "hungry_residents_last_week", "priced_out_last_week",
                 "output_lost_to_hunger_pct", "food_subsidy_pct", "food_subsidy_paid_minor", "startup_grants_paid_minor",
                 "capitation_paid_minor", "tuition_subsidy_pct", "student_stipend", "hiring_bonus_minor", "doctor_visits",
                 "public_doctor_visits", "government_cash_minor", "approval", "gini", "unmet_needs", "job_seekers", "hungry_residents",
                 "too_sick_residents", "average_health", "average_wellbeing", "hungry_days", "worker_days", "tax_basis_points",
                 "welfare_minor", "unemployment_benefit_minor", "sales_tax_basis_points", "profit_tax_basis_points",
                 "service_budget_per_resident_minor", "productivity", "last_policy_action", "last_policy_tick", "net_before_last_policy_minor")

AGENT_TYPES = {key: "long" for key in AGENT_FACTS}
AGENT_TYPES.update({key: "boolean" for key in ("graduated", "is_student", "seeking_better_job", "hungry", "hungry_this_week",
    "priced_out_this_week", "works_overtime", "keeps_savings_in_bank", "too_sick")})
AGENT_TYPES.update({key: "string" for key in ("occupation", "specialty", "owner_id", "owned_business_id", "school_id", "lending_standard")})
AGENT_TYPES["needs_met"] = "double"
COUNTRY_TYPES = {key: "long" for key in COUNTRY_FACTS}
COUNTRY_TYPES.update(student_stipend="boolean", last_policy_action="string")
COUNTRY_TYPES.update({key: "double" for key in ("approval", "gini", "output_lost_to_hunger_pct", "average_health", "average_wellbeing", "productivity")})


def fact_types(payload, types):
    for key, datatype in types.items():
        value = payload.get(key)
        if value is None:
            continue
        valid = (isinstance(value, bool) if datatype == "boolean" else isinstance(value, str) if datatype == "string"
                 else isinstance(value, int) and not isinstance(value, bool) if datatype == "long"
                 else isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value))
        if not valid:
            raise ValueError("Invalid typed snapshot fact: " + key)


def agent_facts(payload):
    return {**{key: payload.get(key) for key in AGENT_FACTS}, "qol_parts_points": payload.get("qol_parts_points")}

TABLES = {"opening_balance": "opening_balances", "agent_day": "agent_daily", "nation_day": "country_daily",
          "transaction": "transactions", "decision_request": "decision_requests",
          "decision_result": "decision_results", "action_effect": "action_effects", "event": "events", "run_metadata": "runs"}


def typed_record(record):
    payload = record["payload"]
    common = {key: record[key] for key in ("run_id", "sequence", "tick", "revision")}
    common["record_sha256"] = __import__("hashlib").sha256(canonical(record)).hexdigest()
    common["payload_json"] = canonical(payload).decode()
    kind = record["kind"]
    if kind == "event":
        common.update(event_id=payload.get("event_id", payload.get("id")), event_sequence=payload.get("seq"),
                      event_type=payload.get("event_type"), source=payload.get("source"),
                      cause_ids_json=canonical(payload.get("cause_ids", [])).decode(),
                      entity_ids_json=canonical(payload.get("entity_ids", [])).decode())
    elif kind == "run_metadata":
        common.update(seed=payload.get("seed"), mode=payload.get("mode"), government_rotation=payload.get("government_rotation"),
                      tick_limit=payload.get("tick_limit"), population=payload.get("initial_population", payload.get("population")), engine_commit=payload.get("engine_commit"),
                      active_features_json=canonical(payload.get("active_features")).decode(), configuration_json=canonical(payload.get("configuration")).decode(),
                      banking_paused=payload.get("banking_paused"), capture_contract_version=payload.get("capture_contract_version"),
                      features_json=canonical(payload.get("features")).decode(), economy_configuration_json=canonical(payload.get("economy_configuration")).decode())
        conditions = comparison_conditions(payload)
        common.update(condition_signature=conditions["signature"], conditions_known=conditions["known"],
                      conditions_unknown_json=canonical(conditions["unknown"]).decode())
    elif kind == "opening_balance":
        common.update(account_id=payload["account_id"], account_type=payload.get("account_type", "unknown"),
                      balance_minor=integer(payload["balance_minor"], "balance_minor"))
    elif kind == "transaction":
        amount = integer(payload["amount_minor"], "amount_minor", 1)
        common.update(transaction_id=payload["transaction_id"], sender_account_id=payload["sender_account_id"],
                      recipient_account_id=payload["recipient_account_id"], amount_minor=amount,
                      transaction_group_id=payload.get("transaction_group_id"), transfer_kind=payload.get("kind"),
                      decision_id=payload.get("decision_id"), event_id=payload.get("event_id"), routine_id=payload.get("routine_id"),
                      causal_source=payload.get("causal_source", "unknown"), good=payload.get("good"), quantity=payload.get("quantity"),
                      unit_price_minor=payload.get("unit_price_minor"))
        common["service_category"] = {"startup_grant": "startup", "food_subsidy": "food", "tuition": "education", "tuition_subsidy": "education",
            "student_stipend": "education", "hiring_bonus": "employment", "capitation": "health", "public_healthcare": "health"}.get(payload.get("kind"))
    elif kind == "agent_day":
        fact_types(payload, AGENT_TYPES)
        common.update(agent_id=payload["agent_id"], country_id=payload.get("country_id"), role=payload.get("role"),
                      cash_minor=integer(payload["cash_minor"], "cash_minor"), health=payload.get("health"),
                      wellbeing=payload.get("wellbeing"), employer_id=payload.get("employer_id"),
                      decision_maker=payload.get("decision_maker"), closed=payload.get("closed"), frame_kind=payload.get("frame_kind", "day"))
        common.update({key: payload.get(key) for key in AGENT_FACTS})
        common["qol_parts_points_json"] = canonical(payload.get("qol_parts_points")).decode()
    elif kind == "nation_day":
        fact_types(payload, COUNTRY_TYPES)
        common.update(country_id=payload.get("country_id", payload.get("id")), frame_kind=payload.get("frame_kind", "day"))
        common.update({key: payload.get(key) for key in COUNTRY_FACTS})
        for key in ("plan", "qol_parts", "needed_since", "planning_view", "last_policy_params"):
            common[key + "_json"] = canonical(payload.get(key)).decode()
    elif kind == "decision_request":
        job = payload.get("job", {})
        common.update(decision_id=payload["decision_id"], agent_id=payload["agent_id"], role=payload.get("role"),
                      country_id=payload.get("country_id"), observation_tick=payload.get("observation_tick"),
                      observation_json=canonical(job.get("observation")).decode(), options_json=canonical(job.get("candidates")).decode(),
                      context_available=isinstance(job.get("observation"), dict), options_available=isinstance(job.get("candidates"), list),
                      prompt_contract_version=payload.get("prompt_contract_version"), menu_parameter_contract=payload.get("menu_parameter_contract"))
    elif kind == "decision_result":
        result = payload.get("result") or {}
        common.update(decision_id=payload["decision_id"], agent_id=payload["agent_id"], status=payload.get("status", "unknown"),
                      accepted=payload.get("accepted"), fallback=payload.get("fallback", False), code=payload.get("code"),
                      selected_option_index=payload.get("selected_option_index"), applied_tick=payload.get("applied_tick"),
                      action_event_id=payload.get("action_event_id"), result_json=canonical(result).decode(),
                      selected_params_json=canonical(payload.get("selected_params", (result.get("action") or {}).get("params"))).decode(),
                      selection_match_count=payload.get("selection_match_count"), selection_provenance=payload.get("selection_provenance"))
    elif kind == "action_effect":
        common.update(decision_id=payload.get("decision_id"), event_id=payload.get("event_id"), entity_id=payload.get("entity_id"),
                      entity_type=payload.get("entity_type"), link_type=payload.get("link_type", "unknown"),
                      before_json=canonical(payload.get("before")).decode(), after_json=canonical(payload.get("after")).decode())
    else:
        raise ValueError("unsupported kind")
    return TABLES[kind], common
