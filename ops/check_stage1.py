import argparse
import json
import statistics
from collections import Counter
from pathlib import Path

import httpx
import jsonschema
from dotenv import dotenv_values

ROOT = Path(__file__).resolve().parents[1]


def check_run(base_url, require_model):
    with httpx.Client(base_url=base_url, timeout=25) as client:
        health = client.get("/api/health").raise_for_status().json()
        if health["status"] != "ok" or health["run_status"] != "completed":
            raise RuntimeError("Wait for the run to complete before checking it.")

        run_id = health["run_id"]
        snapshot = client.get(f"/api/runs/{run_id}/snapshot").raise_for_status().json()
        events = []
        while True:
            after_sequence = events[-1]["seq"] if events else 0
            page = (
                client.get(
                    f"/api/runs/{run_id}/events",
                    params={"after_seq": after_sequence, "limit": 500},
                )
                .raise_for_status()
                .json()
            )
            events.extend(page)
            if len(page) < 500:
                break

        event_schema = json.loads(
            (ROOT / "docs/faultline/contracts/event.schema.json").read_text()
        )
        event_validator = jsonschema.Draft202012Validator(event_schema)
        known_ids = set()

        for sequence, event in enumerate(events, start=1):
            event_validator.validate(event)
            assert event["seq"] == sequence
            assert event["run_id"] == run_id
            assert set(event["cause_ids"]).issubset(known_ids)
            known_ids.add(event["event_id"])

        assert len(events) == snapshot["seq"]
        assert (
            sum(agent["cash_minor"] for agent in snapshot["agents"])
            == snapshot["metrics"]["initial_cash_minor"]
        )
        assert all(agent["cash_minor"] >= 0 for agent in snapshot["agents"])
        assert all(
            quantity >= 0
            for agent in snapshot["agents"]
            for quantity in agent["inventory"].values()
        )

        decisions = [
            event
            for event in events
            if event["event_type"] in ("action_accepted", "action_rejected")
        ]
        if require_model:
            assert snapshot["run"]["mode"] == "LIVE"
            assert snapshot["metrics"]["model_decision_count"] > 0

        sample_agent = snapshot["agents"][0]["id"]
        assert (
            client.get(f"/api/runs/{run_id}/agents/{sample_agent}")
            .raise_for_status()
            .json()["observer_only"]
        )
        assert client.post(f"/api/runs/{run_id}/snapshot").status_code == 405
        assert (
            client.get("/")
            .raise_for_status()
            .headers["content-type"]
            .startswith("text/html")
        )
        assert client.get("/observer.js").status_code == 200
        assert client.get("/style.css").status_code == 200

        runs = client.get("/api/runs").raise_for_status().json()["run_ids"]
        older_runs = [identifier for identifier in runs if identifier != run_id]
        if older_runs:
            restored = (
                client.get(f"/api/runs/{older_runs[0]}/snapshot")
                .raise_for_status()
                .json()
            )
            assert restored["run"]["id"] == older_runs[0]

        persistence = (
            verify_neon(run_id, snapshot, events)
            if health["storage"] == "neon_https"
            else "local"
        )
        latencies = [
            event["payload"]["latency_ms"]
            for event in decisions
            if event["source"] == "model"
        ]
        latencies.sort()

        return {
            "run_id": run_id,
            "mode": snapshot["run"]["mode"],
            "ticks": snapshot["tick"],
            "population": snapshot["agents_summary"],
            "metrics": snapshot["metrics"],
            "decision_attempts": len(decisions),
            "actions": dict(
                Counter(
                    event["payload"]["action"]["action_type"] for event in decisions
                )
            ),
            "rejection_codes": dict(
                Counter(
                    event["payload"]["code"]
                    for event in decisions
                    if event["event_type"] == "action_rejected"
                )
            ),
            "latency_ms_median": round(statistics.median(latencies), 2)
            if latencies
            else None,
            "latency_ms_p95": latencies[
                min(len(latencies) - 1, int(len(latencies) * 0.95))
            ]
            if latencies
            else None,
            "event_count": len(events),
            "persistence": persistence,
            "route_plan": snapshot["shipments"][0]["plan"],
            "checks": [
                "event_schema",
                "ordered_sequences",
                "cause_references",
                "cash_conservation",
                "nonnegative_resources",
                "read_only_api",
                "persisted_run_read",
                "static_assets",
            ],
            "visual_inspection": "unverified: browser permission denied",
            "limitations": [
                "single Spark",
                "synchronous batches of four",
                "route planning only",
                "no Solana or loans",
                "no scale benchmark or behavioral validation",
            ],
        }


def verify_neon(run_id, snapshot, events):
    connection = dotenv_values(ROOT / ".env.local").get("DATABASE_URL", "")
    host = httpx.URL(connection).host
    endpoint = "https://api." + host.split(".", 1)[1] + "/sql"
    headers = {
        "Neon-Connection-String": connection,
        "Neon-Raw-Text-Output": "true",
        "Neon-Array-Mode": "true",
    }
    query = {
        "query": "SELECT state, (SELECT count(*) FROM faultline.stage1_events WHERE run_id=$1) FROM faultline.stage1_runs WHERE run_id=$1",
        "params": [run_id],
    }

    with httpx.Client(timeout=25) as client:
        response = client.post(endpoint, headers=headers, json=query)
        if not response.is_success:
            raise RuntimeError(
                f"Neon verification failed (HTTP {response.status_code})."
            )
        row = response.json()["rows"][0]

    persisted = json.loads(row[0])
    assert persisted["tick"] == snapshot["tick"]
    assert persisted["seq"] == snapshot["seq"]
    assert int(row[1]) == len(events)
    assert len(persisted["decisions"]) == sum(
        1
        for event in events
        if event["event_type"] in ("action_accepted", "action_rejected")
    )
    assert persisted["events"] == events

    return "Neon checkpoint and event table verified"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:5080")
    parser.add_argument("--require-model", action="store_true")
    parser.add_argument("--output", type=Path)
    options = parser.parse_args()
    summary = check_run(options.url, options.require_model)
    serialized = json.dumps(summary, indent=2)
    if options.output:
        options.output.parent.mkdir(parents=True, exist_ok=True)
        options.output.write_text(serialized + "\n")
    print(serialized)


if __name__ == "__main__":
    main()
