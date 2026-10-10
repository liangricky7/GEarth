import asyncio
import json
import os
import secrets
import signal
import socket
import subprocess
import threading
import time
from collections import Counter
from pathlib import Path
from urllib.error import URLError
from urllib.request import urlopen

import uvicorn
from fastapi import FastAPI, HTTPException, Request

ROOT = Path(__file__).resolve().parents[1]
API = "http://127.0.0.1:5081"


def read_json(path):
    with urlopen(API + path, timeout=2) as response:
        return json.load(response)


def wait_until(predicate, timeout=20):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            result = predicate()
            if result:
                return result
        except (OSError, ValueError, URLError):
            pass
        time.sleep(0.02)
    raise RuntimeError("Scheduler integration condition timed out")


def main():
    for port in (5081, 8100):
        with socket.socket() as probe:
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            probe.bind(("127.0.0.1", port))
    token = secrets.token_urlsafe(24)
    gate = threading.Event()
    state = {"block": False, "active": 0, "max_active": 0, "calls": 0}
    lock = threading.Lock()

    app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)

    @app.post("/decide")
    async def decide(request: Request):
        if request.headers.get("Authorization") != f"Bearer {token}":
            raise HTTPException(401)
        job = await request.json()
        with lock:
            state["active"] += 1
            state["calls"] += 1
            number = state["calls"]
            state["max_active"] = max(state["max_active"], state["active"])
        try:
            if state["block"]:
                await asyncio.to_thread(gate.wait, 20)
            else:
                await asyncio.sleep(0.18 if number == 1 else 0.025)
            return {
                "request_id": job["request_id"],
                "action": {
                    "action_type": "hold",
                    "params": {},
                    "reason": "Synthetic scheduler fixture; no real inference.",
                    "evidence_ids": [],
                },
                "source": "model",
                "model": "scheduler-fixture",
                "backend": ("spark_a", "spark_b", "amd")[(number - 1) % 3],
                "latency_ms": 25,
            }
        finally:
            with lock:
                state["active"] -= 1

    server = uvicorn.Server(
        uvicorn.Config(
            app, host="127.0.0.1", port=8100, access_log=False, log_level="critical"
        )
    )
    thread = threading.Thread(target=server.run, daemon=True)
    thread.start()
    wait_until(lambda: server.started)
    processes = []
    runs = []
    evidence = {"kind": "synthetic integration, no GPU or capacity qualification"}
    log_directory = ROOT / ".logs"
    log_directory.mkdir(exist_ok=True)

    def start(ticks, interval, log):
        environment = os.environ.copy()
        for name in ("DATABASE_URL", "DATABASE_DIRECT_URL", "FAULTLINE_SAVED_RUN_ID"):
            environment.pop(name, None)
        environment.update(
            {
                "FAULTLINE_PORT": "5081",
                "FAULTLINE_STORAGE": "local",
                "FAULTLINE_LIVE": "true",
                "FAULTLINE_WORLD": "true",
                "FAULTLINE_POPULATION": "400",
                "FAULTLINE_CAPACITY": "3",
                "FAULTLINE_TICKS": str(ticks),
                "FAULTLINE_INTERVAL_MS": str(interval),
                "WORKER_TOKEN": token,
                "ASPNETCORE_CONTENTROOT": str(ROOT / "backend/Faultline.Api"),
                "FAULTLINE_COMPUTE_PROVIDERS": json.dumps(
                    [
                        {"name": name, "model": "scheduler-fixture", "capacity": 1}
                        for name in ("spark_a", "spark_b", "amd")
                    ]
                ),
            }
        )
        process = subprocess.Popen(
            [
                "dotnet",
                str(ROOT / "backend/Faultline.Api/bin/Debug/net10.0/Faultline.Api.dll"),
            ],
            cwd=ROOT,
            env=environment,
            stdout=log,
            stderr=subprocess.STDOUT,
        )
        processes.append(process)
        health = wait_until(
            lambda: (data if (data := read_json("/api/health")).get("run_id") else None)
        )
        runs.append(health["run_id"])
        return process, health["run_id"]

    try:
        with (log_directory / "scheduler-integration.log").open("w") as log:
            process, run = start(15, 150, log)
            wait_until(
                lambda: read_json("/api/health").get("run_status") == "completed"
            )
            snapshot = read_json(f"/api/runs/{run}/snapshot")
            notes = read_json(f"/api/runs/{run}/notes")
            events = read_json(f"/api/runs/{run}/events?limit=500")
            accepted = [
                event for event in events if event["event_type"] == "action_accepted"
            ]
            first_tick = sum(event["tick"] == 1 for event in accepted)
            assert first_tick > 3, "Freed slots must be reused within the first tick"
            assert snapshot["compute"][0]["max_pending_observed"] <= 3
            assert snapshot["compute"][0]["pending_decisions"] == 0
            assert snapshot["metrics"]["rejected_decision_count"] == 0
            assert snapshot["metrics"]["fallback_decision_count"] == 0
            assert notes["history_complete"]
            assert any(
                note["category"] == "inequality" for note in notes["observations"]
            )
            assert {event["payload"]["backend"] for event in accepted} == {
                "spark_a",
                "spark_b",
                "amd",
            }
            assert (ROOT / ".data/run-notes" / f"{run}.md").exists()
            evidence["completion"] = {
                "run_id": run,
                "first_tick_fixture_completions": first_tick,
                "max_pending": snapshot["compute"][0]["max_pending_observed"],
                "pending_at_completion": 0,
                "fixture_results_by_backend": dict(
                    Counter(event["payload"]["backend"] for event in accepted)
                ),
                "automatic_notes": True,
            }
            process.send_signal(signal.SIGTERM)
            process.wait(timeout=15)
        state["block"] = True
        with (log_directory / "scheduler-stop-integration.log").open("w") as log:
            process, run = start(100, 50, log)
            wait_until(lambda: read_json("/api/health").get("tick", 0) >= 5)
            before = read_json(f"/api/runs/{run}/snapshot")
            assert before["compute"][0]["pending_decisions"] == 3
            process.send_signal(signal.SIGTERM)
            process.wait(timeout=15)
            checkpoint = json.loads((ROOT / ".data/runs" / f"{run}.json").read_text())
            notes = json.loads((ROOT / ".data/run-notes" / f"{run}.json").read_text())
            stopped = next(
                event
                for event in checkpoint["events"]
                if event["event_type"] == "run_stopped"
            )
            assert checkpoint["status"] == "stopped"
            assert checkpoint["society"]["pending_decisions"] == 0
            assert checkpoint["society"]["model_decision_count"] == 0
            assert stopped["payload"]["canceled_pending_requests"] == 3
            assert notes["status"] == "stopped" and notes["history_complete"]
            evidence["shutdown"] = {
                "run_id": run,
                "canceled_pending": 3,
                "pending_at_stop": 0,
                "discarded_results_not_applied": True,
                "automatic_notes": True,
            }
        assert state["max_active"] <= 3
        path = ROOT / "docs/faultline/evidence/multi-compute-scheduler.json"
        path.write_text(json.dumps(evidence, indent=2) + "\n")
        print(json.dumps(evidence, indent=2))
    finally:
        gate.set()
        for process in processes:
            if process.poll() is None:
                process.send_signal(signal.SIGTERM)
                process.wait(timeout=15)
        server.should_exit = True
        thread.join(timeout=5)


if __name__ == "__main__":
    main()
