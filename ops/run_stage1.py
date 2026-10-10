import argparse
import json
import os
import secrets
import signal
import socket
import subprocess
import sys
import time
from pathlib import Path
from urllib.request import urlopen

from dotenv import dotenv_values

ROOT = Path(__file__).resolve().parents[1]
API_URL = "http://127.0.0.1:5080"
SPARK_TARGET = "hackathon@100.70.23.23"
SSH_OPTIONS = [
    "-o",
    "BatchMode=yes",
    "-o",
    "ConnectTimeout=8",
    "-o",
    "StrictHostKeyChecking=yes",
]
REMOTE_KEY_READER = """
from pathlib import Path
import sys
for path in Path('/proc').iterdir():
    if not path.name.isdigit():
        continue
    try:
        arguments = (path / 'cmdline').read_bytes().decode(errors='replace').split(chr(0))
    except OSError:
        continue
    if not any('vllm' in argument.lower() for argument in arguments[:5]):
        continue
    for index, argument in enumerate(arguments):
        if argument == '--api-key' and index + 1 < len(arguments):
            sys.stdout.write(arguments[index + 1])
            sys.exit(0)
        if argument.startswith('--api-key='):
            sys.stdout.write(argument.split('=', 1)[1])
            sys.exit(0)
sys.exit(1)
"""


def ensure_port_available(port):
    with socket.socket() as connection:
        connection.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            connection.bind(("127.0.0.1", port))
        except OSError:
            raise RuntimeError(
                f"Port {port} is already in use. Stop the earlier Stage 1 process first."
            ) from None


def wait_for_service(url, process, seconds=30):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError("A Stage 1 process exited. Check its local log.")
        try:
            with urlopen(url, timeout=2) as response:
                return json.load(response)
        except (OSError, ValueError):
            time.sleep(0.25)
    raise RuntimeError("Service startup timed out. Check its local log.")


def stop_process(process):
    if process.poll() is not None:
        return
    os.killpg(process.pid, signal.SIGTERM)
    try:
        process.wait(timeout=30)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--live", action="store_true")
    parser.add_argument("--storage", choices=("neon", "local"), default="neon")
    parser.add_argument("--world", action="store_true")
    parser.add_argument("--run-id")
    parser.add_argument("--population", type=int, default=1100)
    parser.add_argument("--capacity", type=int, default=64)
    parser.add_argument("--government-rotation", type=int, default=0)
    parser.add_argument("--seed", type=int, default=20261010)
    parser.add_argument("--ticks", type=int)
    parser.add_argument("--interval", type=float)
    parser.add_argument("--exit-on-complete", action="store_true")
    options = parser.parse_args()

    options.ticks = (
        options.ticks if options.ticks is not None else (360 if options.world else 16)
    )
    options.interval = (
        options.interval
        if options.interval is not None
        else (2 if options.world else 3)
    )
    if (
        not 1 <= options.ticks <= (10000 if options.world else 120)
        or not 0 <= options.interval <= 30
    ):
        parser.error("Invalid tick count or interval; interval must be 0-30 seconds.")
    if not 100 <= options.population <= 100000 or not 1 <= options.capacity <= 128:
        parser.error("Use 100-100000 population and 1-128 concurrent requests.")
    if not 0 <= options.government_rotation <= 4 or options.seed < 1:
        parser.error("Use rotation 0-4 and a positive seed.")

    if options.run_id:
        if options.live:
            parser.error("Saved-run inspection does not need --live.")
        if len(options.run_id) > 100 or not all(
            character.isascii() and (character.isalnum() or character in "-_")
            for character in options.run_id
        ):
            parser.error("Invalid saved run ID.")

    os.chdir(ROOT)
    config = {
        key: value
        for key, value in dotenv_values(ROOT / ".env.local").items()
        if value is not None
    }
    if options.storage == "neon" and not config.get("DATABASE_URL"):
        raise RuntimeError("Save DATABASE_URL in .env.local, or use --storage local.")

    ensure_port_available(5080)
    if options.live:
        ensure_port_available(8100)
        ensure_port_available(18001)

    subprocess.run(
        ["dotnet", "build", "Faultline.sln", "--nologo", "-v", "quiet"],
        check=True,
        cwd=ROOT,
    )

    logs = ROOT / ".logs"
    logs.mkdir(exist_ok=True)
    processes = []
    handles = []
    token = secrets.token_urlsafe(32)

    def start(command, filename, environment):
        handle = (logs / filename).open("w")
        handles.append(handle)
        process = subprocess.Popen(
            command,
            cwd=ROOT,
            env=environment,
            stdout=handle,
            stderr=subprocess.STDOUT,
            start_new_session=True,
        )
        processes.append(process)
        return process

    try:
        if options.live:
            api_key = config.get("SPARK_A_API_KEY")
            if not api_key:
                result = subprocess.run(
                    ["ssh", *SSH_OPTIONS, SPARK_TARGET, "python3 -"],
                    input=REMOTE_KEY_READER,
                    text=True,
                    capture_output=True,
                    timeout=15,
                )
                if result.returncode != 0 or not result.stdout.strip():
                    raise RuntimeError(
                        "Could not read the existing Spark API key over SSH. No server settings changed."
                    )
                api_key = result.stdout.strip()

            tunnel = start(
                [
                    "ssh",
                    *SSH_OPTIONS,
                    "-o",
                    "ExitOnForwardFailure=yes",
                    "-o",
                    "ServerAliveInterval=30",
                    "-o",
                    "ServerAliveCountMax=3",
                    "-N",
                    "-L",
                    "127.0.0.1:18001:127.0.0.1:8000",
                    SPARK_TARGET,
                ],
                "tunnel.log",
                os.environ.copy(),
            )

            time.sleep(0.5)
            if tunnel.poll() is not None:
                raise RuntimeError("SSH tunnel failed. Check .logs/tunnel.log.")

            worker_environment = os.environ.copy()
            worker_environment.pop("DATABASE_URL", None)
            worker_environment.pop("DATABASE_DIRECT_URL", None)
            worker_environment.update(
                {
                    "WORKER_TOKEN": token,
                    "WORKER_CAPACITY": str(options.capacity if options.world else 4),
                    "WORKER_TIMEOUT_SECONDS": str(60 if options.world else 13),
                    "SPARK_A_API_KEY": api_key,
                    "SPARK_A_MODEL": config.get("SPARK_A_MODEL", "openai/gpt-oss-120b"),
                    "SPARK_A_BASE_URL": "http://127.0.0.1:18001/v1",
                }
            )

            worker = start(
                [
                    sys.executable,
                    "-m",
                    "uvicorn",
                    "agents.worker:app",
                    "--host",
                    "127.0.0.1",
                    "--port",
                    "8100",
                    "--no-access-log",
                ],
                "worker.log",
                worker_environment,
            )
            wait_for_service("http://127.0.0.1:8100/health", worker)

        api_environment = os.environ.copy()
        api_environment.update(
            {
                "DATABASE_URL": config.get("DATABASE_URL", ""),
                "FAULTLINE_STORAGE": options.storage,
                "FAULTLINE_LIVE": str(options.live).lower(),
                "FAULTLINE_WORLD": str(options.world).lower(),
                "FAULTLINE_SAVED_RUN_ID": options.run_id or "",
                "FAULTLINE_POPULATION": str(options.population),
                "FAULTLINE_CAPACITY": str(options.capacity),
                "FAULTLINE_GOVERNMENT_ROTATION": str(options.government_rotation),
                "FAULTLINE_SEED": str(options.seed),
                "FAULTLINE_TICKS": str(options.ticks),
                "FAULTLINE_INTERVAL_MS": str(round(options.interval * 1000)),
                "WORKER_TOKEN": token,
                "ASPNETCORE_CONTENTROOT": str(ROOT / "backend/Faultline.Api"),
            }
        )

        api = start(
            ["dotnet", "backend/Faultline.Api/bin/Debug/net10.0/Faultline.Api.dll"],
            "api.log",
            api_environment,
        )
        if sys.platform == "darwin":
            start(
                ["caffeinate", "-i", "-w", str(api.pid)], "awake.log", os.environ.copy()
            )
        wait_for_service(f"{API_URL}/api/health", api)
        print(f"Observer: {API_URL}", flush=True)
        mode = (
            "SAVED (no new inference)"
            if options.run_id
            else "LIVE Spark"
            if options.live
            else "RULE (no inference)"
        )
        print(
            f"Mode: {mode}; storage: {options.storage}",
            flush=True,
        )
        if options.run_id:
            print(
                f"Inspecting saved run {options.run_id}; no new simulation or inference.",
                flush=True,
            )
        print("Press Ctrl+C to stop.", flush=True)

        completed = False
        pause_reported = False
        while api.poll() is None:
            health = wait_for_service(f"{API_URL}/api/health", api, seconds=5)
            if health["status"] == "paused":
                if options.exit_on_complete:
                    raise RuntimeError(health.get("error") or "Simulation paused")
                if not pause_reported:
                    print(
                        f"Simulation paused: {health.get('error')}. The last committed state remains readable.",
                        flush=True,
                    )
                    pause_reported = True
            if health.get("run_status") == "completed" and not completed:
                completed = True
                print(
                    f"Completed run {health['run_id']} at tick {health['tick']}.",
                    flush=True,
                )
                if options.exit_on_complete:
                    return
            time.sleep(1)

        raise RuntimeError("API process exited unexpectedly.")

    finally:
        for process in reversed(processes):
            stop_process(process)
        for handle in handles:
            handle.close()


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("Stage 1 stopped.")
    except (RuntimeError, subprocess.SubprocessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
