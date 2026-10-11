"""Stop the collector during its inter-experiment pause, after a terminal manifest."""

import json
import subprocess
import time
from pathlib import Path


def wait_for_boundary(directory, active, stop_service, pause=time.sleep):
    announced = None
    while active():
        seed = json.loads((directory / "state.json").read_text())["next_seed"] - 1
        try:
            record = json.loads((directory / f"experiment-{seed}.json").read_text())
        except FileNotFoundError:
            # The collector reserves the seed before writing its manifest.
            pause(0.5)
            continue
        if record["status"] in {"completed", "failed", "interrupted"}:
            # A new attempt may have started while its predecessor was being read.
            current = json.loads((directory / "state.json").read_text())["next_seed"] - 1
            if current == seed:
                stop_service()
                print(f"Collector stopped between experiments after seed={seed}: {record['status']}", flush=True)
                return
        if announced != seed:
            print(f"Waiting for experiment seed={seed} to finish before switching releases", flush=True)
            announced = seed
        pause(0.5)


def main():
    def active():
        return subprocess.run(["systemctl", "is-active", "--quiet", "gearth.service"]).returncode == 0

    def stop_service():
        subprocess.run(["systemctl", "stop", "gearth.service"], check=True)

    wait_for_boundary(Path("/var/lib/gearth/collection"), active, stop_service)


if __name__ == "__main__":
    main()
