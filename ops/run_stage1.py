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
from share_site import supervisor_running

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
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


def clean_engine_commit():
    """Only identify reproducible source; uncommitted source remains unknown."""
    try:
        dirty = subprocess.run(["git", "status", "--porcelain"], cwd=ROOT,
                               capture_output=True, text=True, check=True).stdout
        commit = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT,
                                capture_output=True, text=True, check=True).stdout.strip()
        return commit if not dirty and len(commit) == 40 and all(c in "0123456789abcdef" for c in commit) else None
    except (OSError, subprocess.CalledProcessError):
        return None


def stop_process(process):
    if process.poll() is not None:
        return
    os.killpg(process.pid, signal.SIGTERM)
    try:
        process.wait(timeout=30)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=5)


def handle_stop(signum, frame):
    raise KeyboardInterrupt


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--live", action="store_true")
    parser.add_argument(
        "--compute", choices=("auto", "spark", "amd", "hybrid"), default="auto"
    )
    parser.add_argument("--storage", choices=("neon", "local"), default="neon")
    parser.add_argument("--world", action="store_true")
    parser.add_argument("--run-id")
    parser.add_argument("--population", type=int, default=1100)
    parser.add_argument("--capacity", type=int)
    parser.add_argument("--government-rotation", type=int, default=0)
    parser.add_argument("--seed", type=int, default=20261010)
    parser.add_argument("--ticks", type=int)
    parser.add_argument("--interval", type=float)
    parser.add_argument("--exit-on-complete", action="store_true")
    parser.add_argument(
        "--verbose",
        action="store_true",
        help="With --world: also write the day-by-day history file for the renderer.",
    )
    parser.add_argument(
        "--analytics",
        action="store_true",
        help="With --world: preserve daily states, full decisions, and individual transfers for finished-run analysis.",
    )
    # Set by ops/fork_run.py: continue a run on its Neon branch from a week-boundary checkpoint.
    parser.add_argument("--resume", help=argparse.SUPPRESS)
    parser.add_argument("--fork-government", help=argparse.SUPPRESS)
    parser.add_argument("--timeline", help=argparse.SUPPRESS)
    parser.add_argument("--skip-build", action="store_true", help="Use an already built API (deployment/collection only)")
    options = parser.parse_args()
    signal.signal(signal.SIGTERM, handle_stop)

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
    minimum_population = 160 if options.world else 100
    if not minimum_population <= options.population <= 100000 or (
        options.capacity is not None and not 1 <= options.capacity <= 384
    ):
        parser.error(
            f"Use {minimum_population}-100000 population and 1-384 concurrent requests."
        )
    if not 0 <= options.government_rotation <= 4 or options.seed < 1:
        parser.error("Use rotation 0-4 and a positive seed.")

    if options.verbose and (not options.world or options.run_id):
        parser.error("--verbose records a new --world run.")
    if options.analytics and (not options.world or options.run_id):
        parser.error("--analytics records a new --world run.")
    if options.analytics and options.resume:
        parser.error("Analytics currently supports normal runs; fork analytics needs separate timeline identities.")
    if options.resume and (not options.world or options.run_id or options.storage != "neon"):
        parser.error("--resume continues a --world run on Neon; start forks with ops/fork_run.py.")
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
        for key, value in dotenv_values(ROOT / ".env.local", interpolate=False).items()
        if value is not None
    }
    # A fork runs against its own Neon branch; ops/fork_run.py passes that branch's connection string.
    if os.environ.get("FAULTLINE_BRANCH_DATABASE_URL"):
        config["DATABASE_URL"] = os.environ["FAULTLINE_BRANCH_DATABASE_URL"]
    if options.storage == "neon" and not config.get("DATABASE_URL"):
        raise RuntimeError("Save DATABASE_URL in .env.local, or use --storage local.")

    compute = options.compute
    live_capacity = options.capacity or (64 if options.world else 4)
    provider_environment = {}
    providers = []
    managed_spark_tunnel = False
    managed_forwards = []
    if options.live:
        from ops.compute_config import compute_environment, spark_a_forwards

        compute, provider_environment, providers, live_capacity = compute_environment(
            config, compute, options.capacity, options.world
        )
        managed_spark_tunnel = any(
            provider.name == "spark_a"
            and provider.base_url == "http://127.0.0.1:18001/v1"
            for provider in providers
        )
        managed_forwards = spark_a_forwards(providers)

    ensure_port_available(5080)
    if options.live:
        ensure_port_available(8100)
        for local_port, _ in managed_forwards:
            ensure_port_available(local_port)

    if not options.skip_build:
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
            api_key = ""
            if managed_spark_tunnel:
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

            if managed_forwards:
                forwarding_arguments = [
                    argument
                    for local_port, remote_port in managed_forwards
                    for argument in (
                        "-L",
                        f"127.0.0.1:{local_port}:127.0.0.1:{remote_port}",
                    )
                ]
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
                        *forwarding_arguments,
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
            worker_environment.pop("FAULTLINE_BRANCH_DATABASE_URL", None)
            worker_environment.update(provider_environment)
            if managed_spark_tunnel:
                worker_environment["SPARK_A_API_KEY"] = api_key
            worker_environment.update(
                {
                    "WORKER_TOKEN": token,
                    "WORKER_CAPACITY": str(live_capacity),
                    "WORKER_TIMEOUT_SECONDS": str(60 if options.world else 13),
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
                "FAULTLINE_CAPACITY": str(live_capacity),
                "FAULTLINE_COMPUTE_PROVIDERS": json.dumps(
                    [
                        {
                            "name": provider.name,
                            "model": provider.model,
                            "capacity": provider.capacity,
                            "decision_group": provider.decision_group,
                        }
                        for provider in providers
                    ]
                ),
                "FAULTLINE_GOVERNMENT_ROTATION": str(options.government_rotation),
                "FAULTLINE_SEED": str(options.seed),
                "FAULTLINE_VERBOSE": str(options.verbose).lower(),
                "FAULTLINE_ANALYTICS": str(options.analytics).lower(),
                "FAULTLINE_RESUME_RUN_ID": options.resume or "",
                "FAULTLINE_FORK_GOVERNMENT": options.fork_government or "",
                "FAULTLINE_TIMELINE": options.timeline or "",
                "FAULTLINE_TICKS": str(options.ticks),
                "FAULTLINE_INTERVAL_MS": str(round(options.interval * 1000)),
                "WORKER_TOKEN": token,
                "ASPNETCORE_CONTENTROOT": str(ROOT / "backend/Faultline.Api"),
            }
        )

        if options.analytics:
            commit = clean_engine_commit()
            if commit:
                api_environment["FAULTLINE_ENGINE_COMMIT"] = commit
            else:
                api_environment.pop("FAULTLINE_ENGINE_COMMIT", None)

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
        if config.get("FAULTLINE_SHARE_URL") and config.get("FAULTLINE_SHARE_KEY"):
            if not supervisor_running():
                start(
                    [sys.executable, str(ROOT / "ops/share_site.py")],
                    "share-supervisor.log",
                    os.environ.copy(),
                )
            print(f"Share address: {config['FAULTLINE_SHARE_URL']}", flush=True)
        mode = (
            "SAVED (no new inference)"
            if options.run_id
            else f"LIVE {compute}"
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
    except (ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
