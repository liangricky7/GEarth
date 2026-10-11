"""Reversible AMD endpoint cutover using its existing key, tunnel, and runtime."""

import argparse
import json
import os
import signal
import subprocess
import sys
import time
from pathlib import Path
from urllib.request import Request, urlopen


DIRECTORY = Path("/workspace/gearth-amd-batch")
KEY = Path("/workspace/faultline-amd/api-key")


def stop(pid):
    def alive():
        try:
            return Path(f"/proc/{pid}/stat").read_text().split(")", 1)[1].split()[0] != "Z"
        except FileNotFoundError:
            return False

    os.kill(pid, signal.SIGTERM)
    deadline = time.monotonic() + 45
    while alive() and time.monotonic() < deadline:
        time.sleep(0.2)
    if alive():
        os.kill(pid, signal.SIGKILL)


def ready():
    request = Request("http://127.0.0.1:8180/health",
                      headers={"Authorization": "Bearer " + KEY.read_text().strip()})
    with urlopen(request, timeout=3) as response:
        return json.load(response)


def supervise():
    configuration = json.loads((DIRECTORY / "production-launch.json").read_text())
    child = None

    def shutdown(signum, frame):
        if child and child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=45)
            except subprocess.TimeoutExpired:
                child.kill()
        raise SystemExit(0)

    signal.signal(signal.SIGTERM, shutdown)
    signal.signal(signal.SIGINT, shutdown)
    failures = 0
    while True:
        with (DIRECTORY / "production-server.log").open("a") as log:
            child = subprocess.Popen(configuration["args"], cwd=configuration["cwd"],
                                     env=configuration["env"], stdout=log, stderr=subprocess.STDOUT)
            (DIRECTORY / "production-server.pid").write_text(str(child.pid))
            started = time.monotonic()
            child.wait()
        failures = failures + 1 if time.monotonic() - started < 60 else 0
        time.sleep(min(30, 2 ** min(failures, 5)))


def cutover(capacity):
    if not 1 <= capacity <= 64:
        raise ValueError("Capacity must be between 1 and 64")
    original_pid = None
    for path in Path("/proc").iterdir():
        if not path.name.isdigit():
            continue
        try:
            args = (path / "cmdline").read_bytes().decode().split("\0")
            if "amd_model_server:app" in args and "8180" in args:
                original_pid = int(path.name)
                original = {"args": [a for a in args if a], "cwd": os.readlink(path / "cwd"),
                            "env": dict(x.split("=", 1) for x in (path / "environ").read_bytes().decode().split("\0") if x)}
                break
        except (OSError, UnicodeError):
            pass
    if original_pid is None:
        raise RuntimeError("Existing AMD endpoint process not found; no change made")
    backup = DIRECTORY / "rollback-launch.json"
    if backup.exists():
        raise RuntimeError("Existing rollback snapshot must be reviewed before another cutover")
    backup.write_text(json.dumps(original))
    backup.chmod(0o600)
    test_pid = int((DIRECTORY / "test-server.pid").read_text())
    test_args = Path(f"/proc/{test_pid}/cmdline").read_bytes().decode().split("\0")
    if "amd_batch_server:app" not in test_args or "8181" not in test_args:
        raise RuntimeError("Test process identity changed; no change made")
    stop(test_pid)
    tuning = {"batch_size": capacity, "capacity": capacity,
              "batch_tokens": max(8192, min(131072, capacity * 2048)), "wait_ms": 20,
              "utilization_target_percent": 90}
    (DIRECTORY / "batch-tuning.json").write_text(json.dumps(tuning) + "\n")
    launch = {"args": [sys.executable, "-m", "uvicorn", "amd_batch_server:app", "--host", "127.0.0.1",
                       "--port", "8180", "--no-access-log"], "cwd": str(DIRECTORY),
              "env": {**original["env"], "AMD_KEY_FILE": str(KEY), "AMD_TUNING_FILE": str(DIRECTORY / "batch-tuning.json"),
                      "HF_HUB_OFFLINE": "1", "TOKENIZERS_PARALLELISM": "false"}}
    private = DIRECTORY / "production-launch.json"
    private.write_text(json.dumps(launch))
    private.chmod(0o600)
    stop(original_pid)
    supervisor = subprocess.Popen([sys.executable, str(DIRECTORY / "amd_cloud_control.py"), "supervise"],
                                  stdout=(DIRECTORY / "supervisor.log").open("a"), stderr=subprocess.STDOUT,
                                  start_new_session=True)
    (DIRECTORY / "supervisor.pid").write_text(str(supervisor.pid))
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        try:
            status = ready()
            if status.get("ready") and status.get("serving") == "transformers_microbatch":
                print(json.dumps(status), flush=True)
                return
        except Exception:
            pass
        time.sleep(1)
    stop(supervisor.pid)
    subprocess.Popen(original["args"], cwd=original["cwd"], env=original["env"],
                     stdout=(DIRECTORY / "rollback.log").open("a"), stderr=subprocess.STDOUT,
                     start_new_session=True)
    raise RuntimeError("Batch endpoint failed readiness; original endpoint restarted")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["cutover", "supervise"])
    parser.add_argument("--capacity", type=int, default=32)
    args = parser.parse_args()
    if args.action == "supervise":
        supervise()
    else:
        cutover(args.capacity)


if __name__ == "__main__":
    main()
