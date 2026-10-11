import argparse
import json
import os
import re
import signal
import subprocess
import time
from pathlib import Path

import httpx
from dotenv import dotenv_values

ROOT = Path(__file__).resolve().parents[1]
STATE = ROOT / ".data/share-site.json"
SSH_PID = ROOT / ".data/share-ssh.pid"
SUPERVISOR_PID = ROOT / ".data/share-supervisor.pid"
LOCAL_ORIGIN = "http://127.0.0.1:5080"


def process_exists(pid):
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def supervisor_running():
    if not SUPERVISOR_PID.exists():
        return False
    try:
        pid = int(SUPERVISOR_PID.read_text())
    except ValueError:
        return False
    if not process_exists(pid):
        return False
    result = subprocess.run(
        ["ps", "-p", str(pid), "-o", "command="], capture_output=True, text=True
    )
    return "share_site.py" in result.stdout


def healthy(client, origin):
    try:
        response = client.get(origin + "/api/health")
        return response.status_code == 200 and response.json().get("status") in {
            "ok", "starting", "paused"
        }
    except (httpx.HTTPError, ValueError):
        return False


def stop_tunnel():
    if not SSH_PID.exists():
        return
    pid = int(SSH_PID.read_text())
    result = subprocess.run(
        ["ps", "-p", str(pid), "-o", "command="], capture_output=True, text=True
    )
    if "nokey@localhost.run" in result.stdout:
        os.kill(pid, signal.SIGTERM)


def start_tunnel(client):
    stop_tunnel()
    log_path = ROOT / ".logs/share-ssh.log"
    log_path.parent.mkdir(parents=True, exist_ok=True)
    with log_path.open("w") as log:
        process = subprocess.Popen(
            [
                "ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                "-o", "StrictHostKeyChecking=accept-new",
                "-o", f"UserKnownHostsFile={ROOT}/.data/share-known-hosts",
                "-o", "IdentityAgent=none", "-o", "PubkeyAuthentication=no",
                "-o", "ServerAliveInterval=30", "-o", "ServerAliveCountMax=3",
                "-o", "ExitOnForwardFailure=yes", "-T", "-R",
                "80:127.0.0.1:5080", "nokey@localhost.run",
            ],
            stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
            start_new_session=True,
        )
    SSH_PID.write_text(str(process.pid))
    for _ in range(30):
        urls = re.findall(r"https://[a-zA-Z0-9.-]+\.lhr\.life", log_path.read_text())
        if urls and healthy(client, urls[-1]):
            return urls[-1], process.pid
        if process.poll() is not None:
            break
        time.sleep(1)
    raise RuntimeError("The observer tunnel could not connect")


def save_state(state):
    temporary = STATE.with_suffix(".tmp")
    temporary.write_text(json.dumps(state, indent=2) + "\n")
    temporary.replace(STATE)


def reconnect(client, url, key):
    state = json.loads(STATE.read_text()) if STATE.exists() else {}
    upstream = state.get("upstream") or state.get("url", "")
    if not upstream.endswith(".lhr.life") or not healthy(client, upstream):
        upstream, pid = start_tunnel(client)
    else:
        pid = state.get("pid")
    state = {
        "url": url, "upstream": upstream, "origin": LOCAL_ORIGIN,
        "provider": "Sites", "pid": pid, "supervisor_pid": os.getpid(),
        "connected": False,
    }
    save_state(state)
    response = client.post(
        url + "/connection", json={"origin": upstream},
        headers={"Authorization": "Bearer " + key},
    )
    response.raise_for_status()
    if not healthy(client, url):
        raise RuntimeError("The stable share address did not reach the observer")
    state["connected"] = True
    save_state(state)
    print("Observer connected: " + url, flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--once", action="store_true")
    options = parser.parse_args()
    values = dotenv_values(ROOT / ".env.local")
    url = str(values.get("GEARTH_SHARE_URL") or "").rstrip("/")
    key = values.get("GEARTH_SHARE_KEY")
    if not url.startswith("https://") or not key:
        raise SystemExit("Set GEARTH_SHARE_URL and GEARTH_SHARE_KEY in .env.local")
    if not options.once:
        if supervisor_running():
            raise SystemExit("The share supervisor is already running")
        SUPERVISOR_PID.write_text(str(os.getpid()))
    try:
        with httpx.Client(timeout=25, follow_redirects=False) as client:
            connected = False
            failures = 0
            while True:
                if healthy(client, LOCAL_ORIGIN):
                    if not connected or failures >= 2:
                        try:
                            reconnect(client, url, key)
                            connected = True
                            failures = 0
                        except (httpx.HTTPError, RuntimeError, OSError, ValueError) as error:
                            print("Share reconnect failed: " + type(error).__name__, flush=True)
                            if options.once:
                                raise SystemExit(1)
                    elif healthy(client, url):
                        failures = 0
                    else:
                        failures += 1
                if options.once:
                    if not connected:
                        raise SystemExit("Start the local observer before sharing")
                    return
                time.sleep(30)
    finally:
        if not options.once and SUPERVISOR_PID.exists():
            if SUPERVISOR_PID.read_text() == str(os.getpid()):
                SUPERVISOR_PID.unlink()


if __name__ == "__main__":
    main()
