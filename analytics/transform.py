"""Pure typed audit projections shared by local checks and Databricks notebooks."""
from __future__ import annotations

from .export import canonical, integer

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
                      tick_limit=payload.get("tick_limit"), population=payload.get("initial_population", payload.get("population")), engine_commit=payload.get("engine_commit"))
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
    elif kind == "agent_day":
        common.update(agent_id=payload["agent_id"], country_id=payload.get("country_id"), role=payload.get("role"),
                      cash_minor=integer(payload["cash_minor"], "cash_minor"), health=payload.get("health"),
                      wellbeing=payload.get("wellbeing"), employer_id=payload.get("employer_id"),
                      decision_maker=payload.get("decision_maker"), closed=payload.get("closed"), frame_kind=payload.get("frame_kind", "day"))
    elif kind == "nation_day":
        common.update(country_id=payload.get("country_id", payload.get("id")), frame_kind=payload.get("frame_kind", "day"))
    elif kind == "decision_request":
        job = payload.get("job", {})
        common.update(decision_id=payload["decision_id"], agent_id=payload["agent_id"], role=payload.get("role"),
                      country_id=payload.get("country_id"), observation_tick=payload.get("observation_tick"),
                      observation_json=canonical(job.get("observation")).decode(), options_json=canonical(job.get("candidates")).decode(),
                      context_available=isinstance(job.get("observation"), dict), options_available=isinstance(job.get("candidates"), list),
                      prompt_contract_version=payload.get("prompt_contract_version"))
    elif kind == "decision_result":
        result = payload.get("result") or {}
        common.update(decision_id=payload["decision_id"], agent_id=payload["agent_id"], status=payload.get("status", "unknown"),
                      accepted=payload.get("accepted"), fallback=payload.get("fallback", False), code=payload.get("code"),
                      selected_option_index=payload.get("selected_option_index"), applied_tick=payload.get("applied_tick"),
                      action_event_id=payload.get("action_event_id"), result_json=canonical(result).decode())
    elif kind == "action_effect":
        common.update(decision_id=payload.get("decision_id"), event_id=payload.get("event_id"), entity_id=payload.get("entity_id"),
                      entity_type=payload.get("entity_type"), link_type=payload.get("link_type", "unknown"),
                      before_json=canonical(payload.get("before")).decode(), after_json=canonical(payload.get("after")).decode())
    else:
        raise ValueError("unsupported kind")
    return TABLES[kind], common
