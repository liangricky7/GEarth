"""Run finite world experiments continuously; keep seeds and provenance across restarts."""

import argparse
import fcntl
import hashlib
import json
import os
import shutil
import signal
import subprocess
import sys
import threading
from datetime import datetime, timezone
from pathlib import Path
from urllib.error import URLError
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parents[1]


def timestamp():
    return datetime.now(timezone.utc).isoformat()


def write_json(path, data):
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("w") as handle:
        json.dump(data, handle, indent=2)
        handle.flush()
        os.fsync(handle.fileno())
    temporary.replace(path)


def reserve_seed(path, first_seed):
    state = json.loads(path.read_text()) if path.exists() else {"next_seed": first_seed}
    seed = state["next_seed"]
    if not 1 <= seed < 2147483647:
        raise RuntimeError("Collection seed range exhausted")
    write_json(path, {"next_seed": seed + 1})
    return seed


def provenance(root):
    deployment = root / ".deployment.json"
    if deployment.exists():
        return json.loads(deployment.read_text())
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    diff = subprocess.check_output(["git", "diff", "HEAD"], cwd=root)
    return {"commit": commit, "dirty": bool(diff), "diff_sha256": hashlib.sha256(diff).hexdigest()}


def health():
    try:
        with urlopen("http://127.0.0.1:5080/api/health", timeout=2) as response:
            return json.load(response)
    except (URLError, TimeoutError, OSError, ValueError):
        return None


def terminate(process):
    if process.poll() is None:
        # The launcher handles SIGTERM and stops its independently grouped children.
        process.terminate()
        try:
            process.wait(timeout=90)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()


def run_one(command, stop, read_health=health, poll_seconds=0.5):
    process = subprocess.Popen(command, cwd=ROOT)
    observed = None
    try:
        while process.poll() is None:
            current = read_health()
            if current and current.get("run_id"):
                observed = current
            if stop.wait(poll_seconds):
                terminate(process)
                break
        return {"exit_code": process.wait(), "interrupted": stop.is_set(), "health": observed}
    finally:
        terminate(process)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--storage", choices=["local", "neon"], default="neon")
    parser.add_argument("--compute", choices=["auto", "spark", "amd", "hybrid"], default="spark")
    parser.add_argument("--live", action="store_true")
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--population", type=int, default=1100)
    parser.add_argument("--ticks", type=int, default=365)
    parser.add_argument("--interval", type=float, default=2)
    parser.add_argument("--first-seed", type=int, default=20261010)
    parser.add_argument("--pause", type=float, default=10)
    parser.add_argument("--max-runs", type=int, default=0, help="0 means unlimited")
    parser.add_argument("--min-free-mib", type=int, default=1024)
    options = parser.parse_args()
    if (not 160 <= options.population <= 100000 or not 1 <= options.ticks <= 10000
            or not 0 <= options.interval <= 30 or not 1 <= options.first_seed < 2147483647
            or options.pause < 0 or options.max_runs < 0 or options.min_free_mib < 0):
        parser.error("Invalid experiment settings")

    directory = ROOT / ".data/collection"
    directory.mkdir(parents=True, exist_ok=True)
    lock = (directory / "collector.lock").open("a")
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        raise RuntimeError("Another collector is already running") from None
    source = provenance(ROOT)
    stop = threading.Event()
    for signum in (signal.SIGTERM, signal.SIGINT):
        signal.signal(signum, lambda *_: stop.set())

    if not options.skip_build:
        subprocess.run(["dotnet", "build", "Faultline.sln", "--nologo", "-v", "quiet"], cwd=ROOT, check=True)

    attempts = failures = 0
    while not stop.is_set() and (not options.max_runs or attempts < options.max_runs):
        if shutil.disk_usage(directory).free < options.min_free_mib * 1024 * 1024:
            print("Collection waiting: insufficient disk space", flush=True)
            stop.wait(60)
            continue
        seed = reserve_seed(directory / "state.json", options.first_seed)
        record = {"started_at": timestamp(), "seed": seed, "source": source, "settings": vars(options)}
        manifest = directory / f"experiment-{seed}.json"
        write_json(manifest, {**record, "status": "running"})
        command = [sys.executable, str(ROOT / "ops/run_stage1.py"), "--world", "--exit-on-complete", "--skip-build",
                   "--storage", options.storage, "--compute", options.compute, "--population", str(options.population),
                   "--ticks", str(options.ticks), "--interval", str(options.interval), "--seed", str(seed)]
        if options.live:
            command.append("--live")
        print(f"Starting experiment seed={seed}, commit={source['commit']}", flush=True)
        outcome = run_one(command, stop)
        status = "interrupted" if outcome["interrupted"] else "completed" if outcome["exit_code"] == 0 else "failed"
        write_json(manifest, {**record, **outcome, "status": status, "ended_at": timestamp()})
        attempts += 1
        failures = failures + 1 if status == "failed" else 0
        print(f"Experiment seed={seed}: {status}", flush=True)
        stop.wait(min(300, 10 * 2 ** min(failures, 5)) if failures else options.pause)


if __name__ == "__main__":
    main()
