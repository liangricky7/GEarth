import argparse
import json
from pathlib import Path

import httpx


def check_api(url):
    with httpx.Client(base_url=url, timeout=30) as client:
        health = client.get("/api/health").raise_for_status().json()
        assert health["status"] == "ok"
        assert health["run_status"] in ("completed", "stopped")
        path = "/api/runs/" + health["run_id"]
        snapshot = client.get(path + "/snapshot").raise_for_status().json()
        assert snapshot["run"]["stage"] == 2
        compute = snapshot["compute"][0]
        roles = compute["by_role"]
        assert (
            sum(group["population"] for group in roles) == snapshot["run"]["population"]
        )
        assert (
            sum(group["unique_model_deciding"] for group in roles)
            == snapshot["agents_summary"]["unique_model_deciding"]
        )
        assert (
            sum(group["model_decision_count"] for group in roles)
            == snapshot["metrics"]["model_decision_count"]
        )
        assert compute["pending_decisions"] == 0
        assert compute["max_pending_observed"] <= compute["worker_capacity"]
        assert all(group["pending_decisions"] == 0 for group in roles)
        assert compute["citizen_rotation_weeks"] == 8

        country = snapshot["countries"][0]["id"]
        page = (
            client.get(
                path + "/agents",
                params={"country": country, "role": "citizen", "limit": 1},
            )
            .raise_for_status()
            .json()
        )
        assert len(page["agents"]) == 1 and page["total"] >= 1
        assert (
            page["revision"] == snapshot["run"]["revision"]
            and page["seq"] == snapshot["seq"]
        )
        agent = page["agents"][0]
        assert agent["country_id"] == country and agent["role"] == "citizen"
        assert (agent["model_decision_age_seconds"] is None) == (
            agent["last_model_decision_at"] is None
        )
        assert agent["decision_maker"]
        assert agent["next_attempt_due_tick"] is not None
        search = (
            client.get(path + "/agents", params={"q": agent["id"], "limit": 1000})
            .raise_for_status()
            .json()
        )
        assert search["total"] == 1 and search["limit"] == 100
        assert (
            client.get(path + "/agents", params={"offset": 1000000})
            .raise_for_status()
            .json()["agents"]
            == []
        )
        detail = client.get(path + "/agents/" + agent["id"]).raise_for_status().json()
        assert detail["observer_only"] and detail["seq"] == snapshot["seq"]
        assert client.get(path + "/agents/missing").status_code == 404
        assert client.get("/api/runs/missing/snapshot").status_code == 404
        assert client.post(path + "/snapshot").status_code == 405
        assert client.get("/.env.local").status_code == 404
        events = (
            client.get(path + "/events", params={"limit": 2}).raise_for_status().json()
        )
        assert len(events) == 2
        boundary = events[0]["seq"]
        bounded_events = (
            client.get(path + "/events", params={"through_seq": boundary, "limit": 500})
            .raise_for_status()
            .json()
        )
        assert [event["seq"] for event in bounded_events] == [boundary]
        assert (
            client.get(
                path + "/events",
                params={"after_seq": boundary, "through_seq": boundary},
            )
            .raise_for_status()
            .json()
            == []
        )
        notes = client.get(path + "/notes").raise_for_status().json()
        assert (
            notes["status"] == health["run_status"]
            and notes["through_sequence"] == snapshot["seq"]
        )
        assert client.get(path + "/notes.md").raise_for_status().text

    sample = {
        "run_id": health["run_id"],
        "snapshot": snapshot,
        "agent_page": page,
        "agent_detail": detail,
    }
    report = {
        "run_id": health["run_id"],
        "mode": snapshot["run"]["mode"],
        "ticks": snapshot["tick"],
        "population": snapshot["run"]["population"],
        "new_model_calls": 0,
        "max_pending": compute["max_pending_observed"],
        "notes_saved": True,
        "checks": [
            "role_metrics",
            "decision_ages",
            "country_role_filter",
            "search",
            "pagination",
            "revision_metadata",
            "request_bounds",
            "read_only",
            "missing_entities",
            "secret_file_blocked",
            "checkpoint_notes_agree",
            "event_sequence_boundary",
        ],
    }
    return report, sample


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:5080")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--sample", type=Path)
    options = parser.parse_args()
    report, sample = check_api(options.url)
    for path, value in ((options.output, report), (options.sample, sample)):
        if path:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(value, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
