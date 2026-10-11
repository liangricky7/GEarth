"""Exercise the real local API with a controlled worker; never contacts Spark or Neon."""

import argparse
import json
import os
import signal
import socket
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import httpx

from check_world import check_run

ROOT = Path(__file__).resolve().parents[1]
BASE = "http://127.0.0.1:5080"


def wait_for(predicate, process, seconds=15):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError("API exited; see artifacts/local/decision-metrics/api-check.log")
        try:
            value = httpx.get(BASE + "/api/health", timeout=2).raise_for_status().json()
            if value.get("status") == "paused":
                raise RuntimeError(value.get("error"))
            if predicate(value):
                return value
        except httpx.HTTPError:
            pass
        time.sleep(.05)
    raise RuntimeError("Timed out waiting for the API check")


def stop(process):
    if process.poll() is None:
        process.send_signal(signal.SIGINT)
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
            raise RuntimeError("API did not stop gracefully")


def run_checks():
    os.chdir(ROOT)
    for port in (5080, 8100):
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", port))  # Do not interfere with another run.
    subprocess.run(["dotnet", "build", "Faultline.sln", "--no-restore", "--nologo", "-v", "quiet"], check=True)
    output = ROOT / "artifacts/local/decision-metrics"
    output.mkdir(parents=True, exist_ok=True)
    release = threading.Event()
    state = {"calls": 0, "hang": False}
    gate = threading.Lock()

    class Worker(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_POST(self):
            if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
                raw_job = bytearray()
                while True:
                    size = int(self.rfile.readline().split(b";", 1)[0], 16)
                    if size == 0:
                        while self.rfile.readline() not in (b"\r\n", b"\n", b""):
                            pass
                        break
                    raw_job.extend(self.rfile.read(size))
                    self.rfile.read(2)
            else:
                raw_job = self.rfile.read(int(self.headers["Content-Length"]))
            job = json.loads(raw_job)
            with gate:
                index = state["calls"]
                state["calls"] += 1
                hang = state["hang"]
            if hang:
                release.wait(30)
            case = index % 7
            body = {"request_id": job["request_id"], "source": "model", "model": "controlled-test-worker",
                "latency_ms": 3, "error": None,
                "action": {"action_type": "hold", "params": {}, "reason": "Controlled check.", "evidence_ids": []}}
            status = 200
            if case == 1:
                body["action"]["action_type"] = "unavailable_test_action"
            elif case in (2, 3, 6):
                body.update(source="rule_fallback", error={2: "JSONDecodeError", 3: "TimeoutError", 6: "HTTPStatusError"}[case],
                    error_category={2: "model_invalid_response", 3: "model_timeout", 6: "model_http_error"}[case],
                    error_http_status=503 if case == 6 else None)
            elif case == 4:
                status = 429
            raw = b"{broken" if case == 5 else json.dumps(body).encode()
            time.sleep(.005)
            try:
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(raw)))
                self.end_headers()
                self.wfile.write(raw)
            except (BrokenPipeError, ConnectionResetError):
                pass

    server = ThreadingHTTPServer(("127.0.0.1", 8100), Worker)
    server.daemon_threads = True
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    process = None
    log = (output / "api-check.log").open("w")

    def start(saved_id="", ticks=80):
        environment = os.environ.copy()
        environment.update(FAULTLINE_STORAGE="local", FAULTLINE_WORLD="true", FAULTLINE_LIVE="true",
            FAULTLINE_POPULATION="100", FAULTLINE_CAPACITY="8", FAULTLINE_TICKS=str(ticks),
            FAULTLINE_INTERVAL_MS="10", FAULTLINE_SAVED_RUN_ID=saved_id,
            FAULTLINE_PORT="5080", WORKER_TOKEN="controlled-local-check",
            ASPNETCORE_CONTENTROOT=str(ROOT / "backend/Faultline.Api"))
        return subprocess.Popen(["dotnet", "backend/Faultline.Api/bin/Debug/net10.0/Faultline.Api.dll"],
            env=environment, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)

    try:
        process = start()
        health = wait_for(lambda h: h.get("run_status") == "completed", process)
        completed = check_run(BASE)
        summary = completed["decision_metrics"]
        assert summary["requests"]["reconciled"] and summary["requests"]["pending"] == 0
        assert summary["requests"]["admitted"] == summary["completed_results"]
        assert summary["valid_model_responses"] > summary["by_source"]["model"]["accepted"] > 0
        assert summary["model_timeouts"] > 0 and summary["worker_timeouts"] == 0
        assert summary["fallback_categories"]["worker_invalid_response"] > 0
        assert summary["http_failures"]["worker_http_error:429"] > 0
        assert summary["http_failures"]["model_http_error:503"] > 0
        assert summary["coordinator_duration"]["sample_count"] == summary["completed_results"]
        events = httpx.get(BASE + f"/api/runs/{health['run_id']}/events", params={"limit": 500}, timeout=10).json()
        first_id = next(e["payload"]["decision_id"] for e in events if e["event_type"] == "decision_requested")
        trace = check_run(BASE, request_id=first_id)["decision_metrics"]["request_trace"]
        assert trace["admission"] and trace["result"] and trace["cancellation"] is None
        (output / "completed.json").write_text(json.dumps(completed, indent=2) + "\n")
        stop(process)

        state["hang"] = True
        process = start(ticks=10000)
        health = wait_for(lambda h: (h.get("tick") or 0) >= 5, process)
        stop(process)
        checkpoint = json.loads((ROOT / ".data/runs" / (health["run_id"] + ".json")).read_text())
        assert checkpoint["status"] == "stopped"
        assert checkpoint["society"]["pending_decisions"] == 0
        release.set()
        process = start(saved_id=health["run_id"])
        wait_for(lambda h: h.get("run_status") == "stopped", process)
        stopped = check_run(BASE)
        requests = stopped["decision_metrics"]["requests"]
        assert requests["reconciled"] and requests["pending"] == 0
        assert requests["admitted"] == requests["canceled"] == 8
        assert requests["applied"] == 0
        assert len(set(requests["canceled_ids"])) == 8
        assert stopped["metrics"]["fallback_decision_count"] == stopped["metrics"]["model_decision_count"] == 0
        (output / "stopped.json").write_text(json.dumps(stopped, indent=2) + "\n")
        return {"passed": True, "new_spark_calls": 0,
            "completed_run": summary, "stopped_run": stopped["decision_metrics"],
            "checks": ["full API/event/storage reconciliation", "valid but engine-rejected responses",
                "model failure categories", "worker HTTP rejection", "malformed worker JSON",
                "coordinator duration", "request trace", "graceful cancellation IDs", "saved stopped-run readback"]}
    finally:
        release.set()
        try:
            if process is not None:
                stop(process)
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)
            log.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    options = parser.parse_args()
    report = run_checks()
    if options.output:
        options.output.parent.mkdir(parents=True, exist_ok=True)
        options.output.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({"passed": report["passed"], "new_spark_calls": 0,
        "completed_requests": report["completed_run"]["requests"], "stopped_requests": report["stopped_run"]["requests"]}))
