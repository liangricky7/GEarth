"""Replace only the drained worker while preserving the live world and its token."""

import argparse
import json
import os
import signal
import subprocess
import time
from pathlib import Path
from urllib.request import Request, urlopen


def health():
    with urlopen("http://127.0.0.1:8100/health", timeout=4) as response:
        return json.load(response)


def worker_pid():
    for path in Path("/proc").iterdir():
        if not path.name.isdigit():
            continue
        try:
            if "agents.worker:app" in (path / "cmdline").read_bytes().decode().split("\0"):
                return int(path.name)
        except (OSError, UnicodeError):
            pass
    raise RuntimeError("Running worker not found")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("capacity", type=int)
    args = parser.parse_args()
    if not 1 <= args.capacity <= 128:
        parser.error("AMD capacity must be between 1 and 128")
    root = Path("/opt/gearth/current").resolve()
    control = Path("/var/lib/gearth/control")
    pause = control / "pause"
    paused = control / "paused"
    owned_pause = not pause.exists()
    stopped = False
    restored = False
    if owned_pause:
        pause.touch(mode=0o600)
    try:
        deadline = time.monotonic() + 120
        while not paused.exists() or health()["active_requests"]:
            if time.monotonic() >= deadline:
                raise RuntimeError("World did not drain; worker was left unchanged")
            time.sleep(1)
        pid = worker_pid()
        old = dict(entry.split("=", 1) for entry in Path(f"/proc/{pid}/environ").read_bytes().decode().split("\0") if entry)
        if old.get("AMD_ENABLED", "false") != "true":
            raise RuntimeError("AMD provider is not enabled")
        origin = old["AMD_BASE_URL"].rstrip("/").removesuffix("/v1")
        request = Request(origin + "/health", headers={"Authorization": "Bearer " + old["AMD_API_KEY"]})
        with urlopen(request, timeout=15) as response:
            remote = json.load(response)
        if not remote.get("ready") or remote.get("capacity", 0) < args.capacity:
            raise RuntimeError("AMD server capacity has not been verified")
        updated = {**old, "AMD_CAPACITY": str(args.capacity)}
        updated["WORKER_CAPACITY"] = str(int(old["WORKER_CAPACITY"]) - int(old["AMD_CAPACITY"]) + args.capacity)
        directory = Path("/var/lib/gearth/amd-optimization")
        directory.mkdir(mode=0o700, exist_ok=True)
        backup = directory / "worker-before.json"
        backup.write_text(json.dumps(old))
        backup.chmod(0o600)
        private = directory / "worker-env.json"
        private.write_text(json.dumps(updated))
        private.chmod(0o600)
        runner = directory / "run_worker.py"
        runner.write_text("import os,json\nfrom pathlib import Path\ne=json.loads(Path(" + repr(str(private)) + ").read_text())\nos.chdir(" + repr(str(root)) + ")\nos.execve(" + repr(str(root / ".venv/bin/python")) + ", [" + repr(str(root / ".venv/bin/python")) + ", '-m', 'uvicorn', 'agents.worker:app', '--host', '127.0.0.1', '--port', '8100', '--no-access-log'], e)\n")
        for path in [directory, private, runner]:
            subprocess.run(["chown", "gearth:gearth", str(path)], check=True)
        config = Path("/var/lib/gearth/config.env")
        original = config.read_text()
        (directory / "config-before.env").write_text(original)
        (directory / "config-before.env").chmod(0o600)
        from dotenv import set_key
        set_key(config, "AMD_CAPACITY", str(args.capacity))
        os.kill(pid, signal.SIGTERM)
        stopped = True
        deadline = time.monotonic() + 30
        while Path(f"/proc/{pid}").exists() and time.monotonic() < deadline:
            time.sleep(0.2)
        subprocess.run(["systemd-run", "--unit=gearth-worker-amd", "--uid=gearth",
                        "--property=BindsTo=gearth.service", "--property=After=gearth.service",
                        "--property=Restart=on-failure", "--property=RestartSec=2",
                        str(root / ".venv/bin/python"), str(runner)], check=True)
        deadline = time.monotonic() + 20
        while True:
            try:
                ready = health()
                amd = next(p for p in ready["providers"] if p["name"] == "amd")
                if amd["capacity"] == args.capacity:
                    break
            except Exception:
                pass
            if time.monotonic() >= deadline:
                private.write_text(json.dumps(old))
                config.write_text(original)
                subprocess.run(["systemctl", "restart", "gearth-worker-amd.service"], check=True)
                restored = True
                raise RuntimeError("Worker readiness failed; original configuration restored")
            time.sleep(0.5)
        print(json.dumps({"worker_capacity": ready["capacity"], "amd_capacity": amd["capacity"],
                          "live_world_preserved": True, "snapshot_provider_metadata_updates_next_run": True}))
    except Exception:
        if stopped and not restored:
            private.write_text(json.dumps(old))
            config.write_text(original)
            subprocess.run(["systemctl", "reset-failed", "gearth-worker-amd.service"], check=False,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            subprocess.run(["systemd-run", "--unit=gearth-worker-amd-rollback", "--uid=gearth",
                            "--property=BindsTo=gearth.service", "--property=After=gearth.service",
                            str(root / ".venv/bin/python"), str(runner)], check=True)
        raise
    finally:
        if owned_pause:
            pause.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
