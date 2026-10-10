import copy

import pytest

from ops.decision_metrics import reconcile_decisions


def event(events, kind, identifier=None, source="rule", **payload):
    if identifier is not None:
        payload["decision_id"] = identifier
    item = {"seq": len(events) + 1, "event_type": kind, "source": source,
        "entity_ids": ["agent"], "payload": payload}
    events.append(item)
    return item


def result(events, identifier, source="model", accepted=True, **payload):
    return event(events, "action_accepted" if accepted else "action_rejected", identifier, source,
        code="OK" if accepted else "EXPIRED_PROPOSAL", latency_ms=payload.pop("latency_ms", 7), **payload)


def mixed_archive():
    events = []
    event(events, "decision_accounting_started", version=1)
    cases = [
        ("model_ok", "model", True, {}),
        ("valid_but_expired", "model", False, {}),
        ("rule_ok", "rule", True, {"latency_ms": 0}),
        ("model_timeout", "rule_fallback", True, {"error": "TimeoutError", "error_category": "model_timeout"}),
        ("worker_rejected", "rule_fallback", False, {"error": "HttpRequestException", "error_category": "worker_http_error", "error_http_status": 408}),
        ("invalid_json", "rule_fallback", False, {"error": "JSONDecodeError", "error_category": "model_invalid_response"}),
        ("worker_timeout", "rule_fallback", True, {"error": "TaskCanceledException", "error_category": "worker_timeout", "latency_ms": 0}),
    ]
    for identifier, source, accepted, extra in cases:
        event(events, "decision_requested", identifier)
        result(events, identifier, source, accepted, coordinator_latency_ms=12 if source != "rule" else None, **extra)
    event(events, "decision_requested", "canceled")
    event(events, "decision_canceled", "canceled", reason="host_shutdown")
    event(events, "decision_requested", "pending")
    return events


def test_all_sources_outcomes_and_failure_subsets_reconcile_with_one_pending_request():
    report = reconcile_decisions(mixed_archive(), {
        "model_decision_count": 1, "rule_decision_count": 1,
        "fallback_decision_count": 4, "rejected_decision_count": 3,
    }, "running", 1, "valid_but_expired")
    assert report["completed_results"] == 7
    assert report["valid_model_responses"] == 2
    assert report["by_source"] == {
        "model": {"accepted": 1, "rejected": 1},
        "rule": {"accepted": 1, "rejected": 0},
        "rule_fallback": {"accepted": 2, "rejected": 2},
    }
    assert report["model_timeouts"] == report["worker_timeouts"] == 1
    assert report["http_failures"] == {"worker_http_error:408": 1}
    assert report["engine_rejection_codes"] == {"EXPIRED_PROPOSAL": 3}
    requests = report["requests"]
    assert requests["admitted"] == requests["applied"] + requests["pending"] + requests["canceled"] == 9
    assert requests["reconciled"] and requests["canceled_ids"] == ["canceled"]
    trace = report["request_trace"]
    assert trace["admission"]["payload"]["decision_id"] == trace["result"]["payload"]["decision_id"]
    assert trace["result"]["source"] == "model"
    assert trace["result"]["event_type"] == "action_rejected"
    assert trace["cancellation"] is None


def test_legacy_run_has_known_applied_totals_but_unknown_admissions_and_cancellations():
    events = []
    result(events, "accepted")
    result(events, "expired", accepted=False)
    result(events, "timeout", "rule_fallback", error="TimeoutError")
    result(events, "ambiguous", "rule_fallback", error="TaskCanceledException")
    report = reconcile_decisions(events, run_status="completed")
    assert report["requests"]["admitted"] is None
    assert report["requests"]["canceled"] is None
    assert not report["requests"]["reconciled"]
    assert report["valid_model_responses"] == 2
    assert report["model_timeouts"] == 1 and report["worker_timeouts"] == 0
    assert report["fallback_categories"]["uncategorized"] == 1
    assert report["engine_rejection_codes"]["EXPIRED_PROPOSAL"] == 1


@pytest.mark.parametrize("fault,match", [
    ("duplicate_admission", "Repeated or late admission"),
    ("duplicate_result", "Multiple terminal outcomes"),
    ("applied_then_canceled", "Multiple terminal outcomes"),
    ("missing_admission", "Outcome without admission"),
    ("different_agent", "Agent changed"),
    ("missing_marker", "accounting-start event"),
    ("late_marker", "before decision events"),
    ("invalid_duration", "Invalid worker duration"),
    ("invalid_coordinator_duration", "Invalid coordinator duration"),
])
def test_corrupt_or_incomplete_accounting_fails_instead_of_producing_plausible_totals(fault, match):
    events = mixed_archive()
    if fault == "duplicate_admission":
        event(events, "decision_requested", "model_ok")
    elif fault == "duplicate_result":
        result(events, "model_ok")
    elif fault == "applied_then_canceled":
        event(events, "decision_canceled", "model_ok")
    elif fault == "missing_admission":
        result(events, "unadmitted")
    elif fault == "different_agent":
        events[2]["entity_ids"] = ["another_agent"]
    elif fault == "missing_marker":
        events.pop(0)
    elif fault == "late_marker":
        events.insert(3, events.pop(0))
    elif fault == "invalid_duration":
        events[2]["payload"]["latency_ms"] = float("nan")
    else:
        events[2]["payload"]["coordinator_latency_ms"] = -1
    with pytest.raises(ValueError, match=match):
        reconcile_decisions(events)


@pytest.mark.parametrize("status", ["completed", "stopped"])
def test_finished_run_cannot_hide_an_unaccounted_request(status):
    with pytest.raises(ValueError, match="without terminal outcomes"):
        reconcile_decisions(mixed_archive(), run_status=status)


def test_snapshot_pending_and_lifetime_counters_must_agree_with_archive():
    with pytest.raises(ValueError, match="pending queue count"):
        reconcile_decisions(mixed_archive(), pending_count=0)
    with pytest.raises(ValueError, match="model_decision_count"):
        reconcile_decisions(mixed_archive(), {"model_decision_count": 2})


def test_latency_windows_keep_model_round_trips_and_decision_ages_distinct():
    events = []
    for i in range(201):
        result(events, str(i), latency_ms=10, coordinator_latency_ms=1000)
    result(events, "fallback", "rule_fallback", error="ValueError", latency_ms=200)
    metrics = {"model_decision_count": 201, "rule_decision_count": 0,
        "fallback_decision_count": 1, "rejected_decision_count": 0,
        "latency_sample_scope": "last_200_positive_worker_durations_all_sources",
        "latency_sample_count": 200, "latency_p95_ms": 10,
        "model_decision_age_p95_seconds": 600}
    report = reconcile_decisions(events, metrics)
    assert report["worker_duration"]["sample_count"] == 201
    assert report["worker_duration"]["p95_ms"] == 10
    assert report["coordinator_duration"]["p95_ms"] == 1000
    wrong = copy.deepcopy(metrics)
    wrong["latency_p95_ms"] = 1000
    with pytest.raises(ValueError, match="latency window"):
        reconcile_decisions(events, wrong)


def test_canceled_request_trace_has_no_applied_result():
    report = reconcile_decisions(mixed_archive(), request_id="canceled")
    assert report["request_trace"]["cancellation"]["payload"]["reason"] == "host_shutdown"
    assert report["request_trace"]["result"] is None
    with pytest.raises(ValueError, match="Request not found"):
        reconcile_decisions(mixed_archive(), request_id="unknown")
