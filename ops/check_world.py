import argparse
import json
import statistics
from collections import Counter
from pathlib import Path

import httpx
import jsonschema
from dotenv import dotenv_values

from decision_metrics import reconcile_decisions

ROOT = Path(__file__).resolve().parents[1]


def read_checkpoint(run_id, storage):
    if storage == "local":
        return json.loads((ROOT / ".data/runs" / f"{run_id}.json").read_text()), None
    connection = dotenv_values(ROOT / ".env.local").get("DATABASE_URL", "")
    host = httpx.URL(connection).host
    endpoint = "https://api." + host.split(".", 1)[1] + "/sql"
    headers = {
        "Neon-Connection-String": connection,
        "Neon-Raw-Text-Output": "true",
        "Neon-Array-Mode": "true",
    }
    with httpx.Client(timeout=30) as client:
        response = client.post(
            endpoint,
            headers=headers,
            json={
                "query": "SELECT state, (SELECT count(*) FROM gearth.stage1_events WHERE run_id=$1) FROM gearth.stage1_runs WHERE run_id=$1",
                "params": [run_id],
            },
        )
        if not response.is_success:
            raise RuntimeError(
                f"Persistence check failed (HTTP {response.status_code})."
            )
        row = response.json()["rows"][0]
    return json.loads(row[0]), int(row[1])


def check_run(base_url, require_model=False, require_full_coverage=False, request_id=None):
    with httpx.Client(base_url=base_url, timeout=30) as client:
        health = client.get("/api/health").raise_for_status().json()
        if health["status"] != "ok" or health["run_status"] not in {"completed", "stopped"}:
            raise RuntimeError("Wait for the run to complete or stop before checking it.")
        run_id = health["run_id"]
        path = f"/api/runs/{run_id}"
        snapshot = client.get(path + "/snapshot").raise_for_status().json()
        assert snapshot["schema_version"] == 2
        events = []
        while len(events) < snapshot["seq"]:
            page = (
                client.get(
                    path + "/events", params={"after_seq": len(events), "limit": 500}
                )
                .raise_for_status()
                .json()
            )
            assert page
            events.extend(page)
        schema = json.loads(
            (ROOT / "docs/gearth/contracts/event.v2.schema.json").read_text()
        )
        validator = jsonschema.Draft202012Validator(schema)
        known_ids = set()
        for sequence, event in enumerate(events, 1):
            validator.validate(event)
            assert event["seq"] == sequence
            assert event["run_id"] == run_id
            assert set(event["cause_ids"]).issubset(known_ids)
            known_ids.add(event["event_id"])
        agents = []
        total = snapshot["agents_summary"]["spawned"]
        while len(agents) < total:
            page = (
                client.get(
                    path + "/agents", params={"offset": len(agents), "limit": 100}
                )
                .raise_for_status()
                .json()
            )
            assert page["total"] == total
            assert page["agents"]
            agents.extend(page["agents"])
        assert len({agent["id"] for agent in agents}) == total
        checkpoint, event_count = read_checkpoint(run_id, health["storage"])
        assert checkpoint["tick"] == snapshot["tick"]
        assert checkpoint["seq"] == len(events)
        if event_count is not None:
            assert event_count == len(events)
        assert checkpoint["events"] == events[-len(checkpoint["events"]) :]
        stored_agents = list(checkpoint["agents"].values())
        society = checkpoint["society"]
        escrow = sum(cargo["escrow_minor"] for cargo in society["cargo"])
        assert (
            sum(agent["cash_minor"] for agent in agents) + escrow
            == checkpoint["initial_cash_minor"]
        )
        assert all(agent["cash_minor"] >= 0 for agent in stored_agents)
        assert all(
            quantity >= 0
            for agent in stored_agents
            for quantity in agent["inventory"].values()
        )
        for good in society["initial_goods"]:
            held = sum(agent["inventory"][good] for agent in stored_agents) + sum(
                cargo["quantity"]
                for cargo in society["cargo"]
                if cargo["status"] == "in_transit" and cargo["good"] == good
            )
            assert (
                held
                == society["initial_goods"][good]
                + society["produced_goods"][good]
                - society["consumed_goods"][good]
                - society["lost_goods"][good]
            )
        assert society["max_pending_observed"] <= society["worker_capacity"]
        assert society["pending_decisions"] == 0
        assert len(checkpoint["decisions"]) <= 256
        model_coverage = sum(agent["model_decisions"] > 0 for agent in agents)
        assert model_coverage == snapshot["agents_summary"]["unique_model_deciding"]
        if require_model:
            assert snapshot["run"]["mode"] == "LIVE" and model_coverage > 0
        if require_full_coverage:
            # Owned and closed businesses act through their entrepreneur, so coverage counts decision makers.
            deciders = sum(agent["decision_maker"] for agent in agents)
            assert deciders == snapshot["agents_summary"]["decision_makers"]
            assert model_coverage == deciders, (
                f"Model coverage is {model_coverage}/{deciders}"
            )
        sample = next(
            (agent for agent in agents if agent["model_decisions"]), agents[0]
        )
        profile = client.get(path + "/agents/" + sample["id"]).raise_for_status().json()
        assert profile["observer_only"]
        assert client.post(path + "/snapshot").status_code == 405
        for asset in ("/", "/observer.js", "/style.css"):
            assert client.get(asset).status_code == 200
        assert client.get("/.env.local").status_code == 404
    decisions = [
        event
        for event in events
        if event["event_type"] in ("action_accepted", "action_rejected")
    ]
    latencies = sorted(
        event["payload"]["latency_ms"]
        for event in decisions
        if event["source"] == "model"
    )
    decision_metrics = reconcile_decisions(events, snapshot["metrics"], snapshot["run"]["status"],
        snapshot["compute"][0]["pending_decisions"], request_id)
    return {
        "run_id": run_id,
        "mode": snapshot["run"]["mode"],
        "ticks": snapshot["tick"],
        "population": total,
        "model_coverage": model_coverage,
        "model_coverage_by_role": dict(
            Counter(agent["role"] for agent in agents if agent["model_decisions"])
        ),
        "metrics": snapshot["metrics"],
        "compute": snapshot["compute"],
        "completed_decision_results": len(decisions),
        "decision_metrics": decision_metrics,
        "latency_sample_scope": "full-run model-source worker durations; excludes coordinator duration and decision age",
        "accepted_actions": dict(
            Counter(
                event["payload"]["action"]["action_type"]
                for event in decisions
                if event["event_type"] == "action_accepted"
            )
        ),
        "rejection_codes": dict(
            Counter(
                event["payload"]["code"]
                for event in decisions
                if event["event_type"] == "action_rejected"
            )
        ),
        "fallback_errors": dict(
            Counter(
                event["payload"].get("error")
                for event in decisions
                if event["source"] == "rule_fallback"
            )
        ),
        "event_types": dict(Counter(event["event_type"] for event in events)),
        "event_count": len(events),
        "latency_median_ms": statistics.median(latencies) if latencies else None,
        "latency_p95_ms": latencies[min(len(latencies) - 1, int(len(latencies) * 0.95))]
        if latencies
        else None,
        "persistence": health["storage"],
        "checks": [
            "event_schema",
            "ordered_sequences",
            "cause_references",
            "cash_conservation",
            "goods_conservation",
            "nonnegative_resources",
            "bounded_requests",
            "decision_counter_reconciliation",
            "checkpoint_readback",
            "event_archive",
            "agent_pagination",
            "read_only_api",
            "static_assets",
        ],
        "visual_inspection": "unverified: browser permission denied",
        "limitations": [
            "configured shared model services; backend/model assignment may affect outcomes",
            "sparse model turns between rule-driven life cycles",
            "synthetic institutional assumptions",
            "internal currency and escrow",
            "no absolute capacity claim",
        ],
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:5080")
    parser.add_argument("--require-model", action="store_true")
    parser.add_argument("--require-full-coverage", action="store_true")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--request-id", help="Include the events for one request")
    options = parser.parse_args()
    report = check_run(
        options.url, options.require_model, options.require_full_coverage, options.request_id
    )
    serialized = json.dumps(report, indent=2)
    if options.output:
        options.output.parent.mkdir(parents=True, exist_ok=True)
        options.output.write_text(serialized + "\n")
    print(serialized)


if __name__ == "__main__":
    main()
