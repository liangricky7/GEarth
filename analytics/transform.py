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
               "school_seats", "enrolled_students", "planned_daily_quota", "last_week_disposable_income_minor",
               "last_week_real_income_minor", "last_week_work_days", "last_week_income_tick", "net_wealth_minor",
               "last_week_income_constrained", "last_week_operating_profit_minor",
               "last_week_operating_cash_surplus_minor", "operating_cash_surplus_average_minor",
               "operating_cash_surplus_observed_weeks", "estate_beneficiary_id", "estate_finalized",
               "deposit_interest_suspended", "part_time_student", "arrears_minor", "arrears_weeks",
               "unpaid_hungry_weeks", "insured_minor", "price_change_tick", "price_change_pct",
               "units_before_price_change", "profit_before_price_change_minor", "price_before_price_change_minor",
               "estate_unreported_payout_minor", "week_disposable_income_minor", "week_work_days",
               "war_weariness", "military_paid_tick", "military_pay_minor")
COUNTRY_FACTS = ("residents", "unemployed", "students", "graduates", "hungry_residents_last_week", "priced_out_last_week",
                 "output_lost_to_hunger_pct", "food_subsidy_pct", "food_subsidy_paid_minor", "startup_grants_paid_minor",
                 "capitation_paid_minor", "tuition_subsidy_pct", "student_stipend", "hiring_bonus_minor", "doctor_visits",
                 "public_doctor_visits", "government_cash_minor", "approval", "gini", "unmet_needs", "job_seekers", "hungry_residents",
                 "too_sick_residents", "average_health", "average_wellbeing", "hungry_days", "worker_days", "tax_basis_points",
                 "welfare_minor", "unemployment_benefit_minor", "sales_tax_basis_points", "profit_tax_basis_points",
                 "service_budget_per_resident_minor", "productivity", "last_policy_action", "last_policy_tick", "net_before_last_policy_minor",
                 "employed_residents", "labor_force", "nonparticipants", "jobless_residents", "gini_disposable_income", "gini_net_wealth",
                 "essentials_basket_minor", "median_real_disposable_income_minor", "maintenance_funded", "currency_index",
                 "trade_net_week_minor", "trade_gross_week_minor", "bank_failures", "defaulted_loans",
                 "deposit_insurance_paid_minor", "tax_changed_tick", "living_cost_at_review", "crisis_until_tick", "crisis_good",
                 "missiles", "war_weeks", "no_fit_soldier_weeks", "war_weariness", "unrest_since_tick", "tax_mark_minor",
                 "war_accounting_initialized", "government_id", "leadership_term", "next_election_tick", "wars")

AGENT_TYPES = {key: "long" for key in AGENT_FACTS}
AGENT_TYPES.update({key: "boolean" for key in ("graduated", "is_student", "seeking_better_job", "hungry", "hungry_this_week",
    "priced_out_this_week", "works_overtime", "keeps_savings_in_bank", "too_sick", "estate_finalized",
    "deposit_interest_suspended", "part_time_student")})
AGENT_TYPES.update({key: "string" for key in ("occupation", "specialty", "owner_id", "owned_business_id", "school_id", "lending_standard", "estate_beneficiary_id")})
AGENT_TYPES["needs_met"] = "double"
AGENT_TYPES["last_week_work_days"] = "double"
AGENT_TYPES["week_work_days"] = "double"
AGENT_TYPES["war_weariness"] = "double"
COUNTRY_TYPES = {key: "long" for key in COUNTRY_FACTS}
COUNTRY_TYPES.update(student_stipend="boolean", last_policy_action="string", maintenance_funded="boolean", crisis_good="string", war_accounting_initialized="boolean", government_id="string")
COUNTRY_TYPES.update({key: "double" for key in ("approval", "gini", "output_lost_to_hunger_pct", "average_health", "average_wellbeing", "productivity")})
COUNTRY_TYPES.update(gini_disposable_income="double", gini_net_wealth="double", currency_index="double", living_cost_at_review="double", war_weariness="double")


AGENT_JSON_FACTS = ("inventory", "qol_parts_points")
COUNTRY_JSON_FACTS = ("plan", "qol_parts", "needed_since", "planning_view", "last_policy_params",
    "infrastructure", "construction_materials_used", "borrowed_builders", "endowment", "resource_imports",
    "powered_sessions_left", "production_input_credits", "relationships", "military", "mission")
LEGACY_AGENT_JSON_FACTS = ("qol_parts_points",)
LEGACY_COUNTRY_JSON_FACTS = ("plan", "qol_parts", "needed_since", "planning_view", "last_policy_params")
CAPTURE_VERSION_V2 = "gearth.economy_capture.v2"
CAPTURE_VERSION = "gearth.economy_capture.v3"
REQUIRED_AGENT_V2 = ("part_time_student", "arrears_minor", "arrears_weeks", "unpaid_hungry_weeks", "insured_minor",
    "price_change_tick", "price_change_pct", "units_before_price_change", "profit_before_price_change_minor",
    "price_before_price_change_minor", "inventory")
REQUIRED_COUNTRY_V2 = ("infrastructure", "maintenance_funded", "construction_materials_used", "borrowed_builders",
    "currency_index", "trade_net_week_minor", "trade_gross_week_minor", "endowment", "resource_imports",
    "bank_failures", "defaulted_loans", "deposit_insurance_paid_minor")
REQUIRED_AGENT_V3 = (*REQUIRED_AGENT_V2, "war_weariness", "military_paid_tick", "military_pay_minor")
REQUIRED_COUNTRY_V3 = (*REQUIRED_COUNTRY_V2, "missiles", "war_weeks", "no_fit_soldier_weeks", "war_weariness",
    "unrest_since_tick", "tax_mark_minor", "war_accounting_initialized", "government_id", "leadership_term",
    "next_election_tick", "wars", "relationships", "military", "mission")
RELATIONSHIP_TYPES = {"id": "string", "first": "string", "second": "string", "score": "long",
    "trade_agreement": "boolean", "at_war": "boolean", "aggressor": "string", "proposed_by": "string",
    "proposal_kind": "string", "proposal_terms": "string", "proposal_expires_tick": "long",
    "diplomacy_failed_tick": "long", "delivered_units": "long", "delivered_value_minor": "long"}
MILITARY_TYPES = {name: "long" for name in ("soldiers", "soldiers_fit", "soldiers_paid", "strength",
    "soldiers_to_keep_order", "weekly_salary_minor", "missile_defense_pct")}
MISSION_TYPES = {"average_quality_of_life_at_least": "long", "average_quality_of_life": "double",
    "unrest_since_tick": "long", "unrest_comes_to_a_head_tick": "long"}
PROJECT_TYPES = {"id": "string", "type": "string", "level": "long", "condition": "long",
    "cost_minor": "long", "paid_minor": "long", "started_tick": "long", "ready_tick": "long", "complete": "boolean",
    "progress_days": "long", "required_days": "long", "progress_fraction": "double", "progress_basis": "string",
    "required_builders": "long"}


def json_fact(payload, key):
    # SQL null for unknown/absent, JSON literal null for explicitly captured null.
    if key in (*LEGACY_AGENT_JSON_FACTS, *LEGACY_COUNTRY_JSON_FACTS):
        # Preserve pre-migration Delta column semantics; original payload proves
        # the difference between an absent key and explicitly recorded null.
        return canonical(payload[key]).decode() if payload.get(key) is not None else None
    return canonical(payload[key]).decode() if key in payload else None


def capture_requirements(kind, capture_version=None):
    agent, country = (REQUIRED_AGENT_V3, REQUIRED_COUNTRY_V3) if capture_version == CAPTURE_VERSION else (REQUIRED_AGENT_V2, REQUIRED_COUNTRY_V2)
    return agent if kind == "agent_day" else country if kind == "nation_day" else ()


def validate_capture(record, capture_version):
    if capture_version in (CAPTURE_VERSION_V2, CAPTURE_VERSION):
        missing = [key for key in capture_requirements(record["kind"], capture_version) if key not in record["payload"]]
        if missing:
            raise ValueError(capture_version + " capture omitted fields: " + ", ".join(missing))
        if record["kind"] == "nation_day":
            for project in record["payload"].get("infrastructure") or []:
                if isinstance(project, dict) and any(key not in project for key in PROJECT_TYPES):
                    raise ValueError(capture_version + " capture omitted infrastructure project fields")


def structure_facts(payload):
    structures = {"inventory": dict, "qol_parts_points": dict, "infrastructure": list,
        "construction_materials_used": list, "borrowed_builders": dict, "endowment": dict, "resource_imports": dict,
        "powered_sessions_left": dict, "production_input_credits": dict, "relationships": list, "military": dict, "mission": dict}
    for key, kind in structures.items():
        if payload.get(key) is not None and not isinstance(payload[key], kind):
            raise ValueError("Invalid structured snapshot fact: " + key)
    for key, types in (("military", MILITARY_TYPES), ("mission", MISSION_TYPES)):
        if isinstance(payload.get(key), dict):
            fact_types(payload[key], types)
    if isinstance(payload.get("relationships"), list):
        seen = set()
        for relation in payload["relationships"]:
            if not isinstance(relation, dict):
                raise ValueError("Invalid relationship snapshot")
            fact_types(relation, RELATIONSHIP_TYPES)
            identity = relation.get("id")
            if not isinstance(identity, str) or not identity or identity in seen:
                raise ValueError("Missing or duplicate relationship ID")
            seen.add(identity)


def infrastructure_rows(record):
    """Normalize recorded projects without inventing rows for old or empty lists.

    Derived progress is supplied by the engine with its named basis, never guessed
    from balances or dates here. Keep the full nation payload and project payload.
    """
    payload = record["payload"]
    projects = payload.get("infrastructure")
    if projects is None:
        return []
    if not isinstance(projects, list):
        raise ValueError("Invalid infrastructure project list")
    country = payload.get("country_id", payload.get("id"))
    seen = set()
    rows = []
    for project in projects:
        if not isinstance(project, dict):
            raise ValueError("Invalid infrastructure project")
        fact_types(project, PROJECT_TYPES)
        identity = project.get("id")
        if not isinstance(identity, str) or not identity or identity in seen:
            raise ValueError("Missing or duplicate infrastructure project ID")
        seen.add(identity)
        if not isinstance(country, str) or not country:
            raise ValueError("Infrastructure project missing country ID")
        row = {key: record[key] for key in ("run_id", "sequence", "tick", "revision")}
        row.update(country_id=country, frame_kind=payload.get("frame_kind", "day"), project_id=identity,
            record_sha256=__import__("hashlib").sha256(canonical(record)).hexdigest(),
            payload_json=canonical(payload).decode(), project_json=canonical(project).decode())
        row.update({key: project.get(key) for key in PROJECT_TYPES if key != "id"})
        row["status"] = "complete" if project.get("complete") is True else "under_construction" if project.get("complete") is False else None
        rows.append(row)
    return rows


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
    return {**{key: payload.get(key) for key in AGENT_FACTS}, **{key: payload.get(key) for key in AGENT_JSON_FACTS}}

TABLES = {"opening_balance": "opening_balances", "agent_day": "agent_daily", "nation_day": "country_daily",
          "transaction": "transactions", "bank_ledger": "bank_ledger", "decision_request": "decision_requests",
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
                      balance_minor=integer(payload["balance_minor"], "balance_minor"), creation_tick=payload.get("creation_tick"))
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
    elif kind == "bank_ledger":
        common.update(bank_id=payload["bank_id"], sender_account_id=payload.get("from"), recipient_account_id=payload.get("to"),
                      transfer_kind=payload["kind"], amount_minor=integer(payload["amount_minor"], "amount_minor"),
                      credit_id=payload.get("credit_id"), event_id=payload.get("event_id"),
                      transaction_group_id=payload.get("transaction_group_id"), decision_id=payload.get("decision_id"),
                      reserves_minor=integer(payload["reserves_minor"], "reserves_minor"),
                      deposits_minor=integer(payload["deposits_minor"], "deposits_minor"),
                      loans_minor=integer(payload["loans_minor"], "loans_minor"), equity_minor=payload["equity_minor"])
        if type(payload["equity_minor"]) is not int:
            raise ValueError("equity_minor must be an integer")
    elif kind == "agent_day":
        fact_types(payload, AGENT_TYPES)
        structure_facts(payload)
        common.update(agent_id=payload["agent_id"], country_id=payload.get("country_id"), role=payload.get("role"),
                      cash_minor=integer(payload["cash_minor"], "cash_minor"), health=payload.get("health"),
                      wellbeing=payload.get("wellbeing"), employer_id=payload.get("employer_id"),
                      decision_maker=payload.get("decision_maker"), closed=payload.get("closed"), frame_kind=payload.get("frame_kind", "day"))
        common.update({key: payload.get(key) for key in AGENT_FACTS})
        for key in AGENT_JSON_FACTS:
            common[key + "_json"] = json_fact(payload, key)
    elif kind == "nation_day":
        fact_types(payload, COUNTRY_TYPES)
        structure_facts(payload)
        common.update(country_id=payload.get("country_id", payload.get("id")), frame_kind=payload.get("frame_kind", "day"))
        common.update({key: payload.get(key) for key in COUNTRY_FACTS})
        for key in COUNTRY_JSON_FACTS:
            common[key + "_json"] = json_fact(payload, key)
        infrastructure_rows(record)  # Validate before Spark can cast malformed structures to null.
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
