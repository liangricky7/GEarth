"""Reconcile the durable event archive; older runs cannot prove dispatch totals."""

import argparse
import json
import math
import statistics
from collections import Counter
from pathlib import Path


def failure_category(payload):
    if payload.get("error_category"):
        return payload["error_category"]
    error = payload.get("error")
    if error in {"TimeoutError", "ReadTimeout", "ConnectTimeout", "WriteTimeout", "PoolTimeout"}:
        return "model_timeout"
    if error in {"ValueError", "JSONDecodeError", "ValidationError", "KeyError", "IndexError", "TypeError"}:
        return "model_invalid_response"
    if error in {"RemoteProtocolError", "ConnectError", "ReadError", "WriteError"}:
        return "model_transport_error"
    # Old TaskCanceledException results do not distinguish deadline from shutdown.
    return "uncategorized"


def latency_summary(values, scope):
    return {
        "scope": scope,
        "sample_count": len(values),
        "median_ms": statistics.median(values) if values else None,
        "p95_ms": sorted(values)[min(len(values) - 1, int(len(values) * .95))] if values else None,
    }


def reconcile_decisions(events, metrics=None, run_status=None, pending_count=None, request_id=None):
    admitted, applied, canceled = {}, {}, {}
    accounting_version = None
    by_source = {source: {"accepted": 0, "rejected": 0} for source in ("model", "rule", "rule_fallback")}
    errors, categories, statuses, rejection_codes = Counter(), Counter(), Counter(), Counter()
    worker_latencies, coordinator_latencies = [], []
    recent_worker_latencies = []
    valid_model_responses = 0

    for event in events:
        kind, payload = event["event_type"], event["payload"]
        if kind == "decision_accounting_started":
            if accounting_version is not None or admitted or applied or canceled:
                raise ValueError("Accounting must start once, before decision events")
            accounting_version = payload["version"]
            if accounting_version != 1:
                raise ValueError("Unsupported decision accounting version")
            continue
        if kind not in {"decision_requested", "decision_canceled", "action_accepted", "action_rejected"}:
            continue
        identifier = payload["decision_id"]
        if not identifier:
            raise ValueError("Empty request ID")
        if kind == "decision_requested":
            if identifier in admitted or identifier in applied or identifier in canceled:
                raise ValueError(f"Repeated or late admission: {identifier}")
            admitted[identifier] = event
            continue
        if identifier in applied or identifier in canceled:
            raise ValueError(f"Multiple terminal outcomes: {identifier}")
        if accounting_version is not None:
            if identifier not in admitted:
                raise ValueError(f"Outcome without admission: {identifier}")
            if event["entity_ids"] != admitted[identifier]["entity_ids"]:
                raise ValueError(f"Agent changed for request: {identifier}")
        if kind == "decision_canceled":
            canceled[identifier] = event
            continue

        applied[identifier] = event
        source = event["source"]
        if source not in by_source:
            raise ValueError(f"Unknown result source: {source}")
        outcome = "accepted" if kind == "action_accepted" else "rejected"
        by_source[source][outcome] += 1
        if outcome == "rejected":
            rejection_codes[payload["code"]] += 1
        if source == "model" and payload.get("error") is None:
            valid_model_responses += 1
        if source == "rule_fallback":
            errors[payload.get("error") or "unspecified"] += 1
            category = failure_category(payload)
            categories[category] += 1
            if payload.get("error_http_status") is not None:
                statuses[f"{category}:{payload['error_http_status']}"] += 1
        duration = payload.get("latency_ms", 0)
        if not isinstance(duration, (int, float)) or not math.isfinite(duration) or duration < 0:
            raise ValueError(f"Invalid worker duration: {identifier}")
        if duration > 0:
            recent_worker_latencies.append(duration)
            if source == "model":
                worker_latencies.append(duration)
        duration = payload.get("coordinator_latency_ms")
        if duration is not None:
            if not isinstance(duration, (int, float)) or not math.isfinite(duration) or duration < 0:
                raise ValueError(f"Invalid coordinator duration: {identifier}")
            coordinator_latencies.append(duration)

    if accounting_version is None and (admitted or canceled):
        raise ValueError("Request records lack their accounting-start event; archive may be incomplete")
    pending = set(admitted) - set(applied) - set(canceled)
    if accounting_version is not None:
        if run_status in {"completed", "stopped"} and pending:
            raise ValueError("Finished run contains requests without terminal outcomes")
        if pending_count is not None and len(pending) != pending_count:
            raise ValueError("Request ledger disagrees with pending queue count")
    if metrics is not None:
        expected = {
            "model_decision_count": by_source["model"]["accepted"],
            "rule_decision_count": by_source["rule"]["accepted"],
            "fallback_decision_count": sum(by_source["rule_fallback"].values()),
            "rejected_decision_count": sum(values["rejected"] for values in by_source.values()),
        }
        for field, count in expected.items():
            if metrics[field] != count:
                raise ValueError(f"Event totals disagree with {field}: {count} != {metrics[field]}")
        if metrics.get("latency_sample_scope") == "last_200_positive_worker_durations_all_sources":
            window = recent_worker_latencies[-200:]
            expected_p95 = latency_summary(window, "")['p95_ms'] or 0
            if metrics["latency_sample_count"] != len(window) or not math.isclose(metrics["latency_p95_ms"], expected_p95):
                raise ValueError("Snapshot worker-latency window disagrees with event archive")

    report = {
        "completed_results": len(applied),
        "valid_model_responses": valid_model_responses,
        "by_source": by_source,
        "fallback_errors": dict(errors),
        "fallback_categories": dict(categories),
        "model_timeouts": categories["model_timeout"],
        "worker_timeouts": categories["worker_timeout"],
        "http_failures": dict(statuses),
        "engine_rejection_codes": dict(rejection_codes),
        "requests": {
            "accounting_version": accounting_version,
            "admitted": len(admitted) if accounting_version is not None else None,
            "applied": len(applied),
            "pending": len(pending) if accounting_version is not None else None,
            "canceled": len(canceled) if accounting_version is not None else None,
            "pending_ids": sorted(pending),
            "canceled_ids": sorted(canceled),
            "reconciled": accounting_version is not None,
            "scope": "full archive" if accounting_version is not None else "legacy applied results only; admissions/cancellations unknown",
        },
        "worker_duration": latency_summary(worker_latencies, "full-run positive model-source worker durations"),
        "coordinator_duration": latency_summary(coordinator_latencies, "full-run measured worker round trips; no decision ages"),
    }
    if request_id is not None:
        if request_id not in admitted and request_id not in applied and request_id not in canceled:
            raise ValueError(f"Request not found: {request_id}")
        report["request_trace"] = {
            "request_id": request_id,
            "admission": admitted.get(request_id),
            "result": applied.get(request_id),
            "cancellation": canceled.get(request_id),
        }
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--events", type=Path, required=True, help="Complete ordered event archive JSON array")
    parser.add_argument("--snapshot", type=Path, help="Optional committed snapshot for counter reconciliation")
    parser.add_argument("--request-id", help="Include the events for one request")
    parser.add_argument("--output", type=Path)
    options = parser.parse_args()
    events = json.loads(options.events.read_text())
    snapshot = json.loads(options.snapshot.read_text()) if options.snapshot else None
    if not events or any(event["seq"] != index for index, event in enumerate(events, 1)):
        raise ValueError("Use the complete event archive starting at sequence 1")
    if len({event["run_id"] for event in events}) != 1:
        raise ValueError("Archive mixes multiple runs")
    if snapshot is not None and (snapshot["seq"] != len(events)
        or snapshot["run"]["id"] != events[0]["run_id"]):
        raise ValueError("Archive does not match the snapshot boundary")
    report = reconcile_decisions(events, snapshot["metrics"] if snapshot else None,
        snapshot["run"]["status"] if snapshot else None,
        snapshot["compute"][0]["pending_decisions"] if snapshot else None, options.request_id)
    serialized = json.dumps(report, indent=2) + "\n"
    if options.output:
        options.output.parent.mkdir(parents=True, exist_ok=True)
        options.output.write_text(serialized)
    print(serialized, end="")


if __name__ == "__main__":
    main()
