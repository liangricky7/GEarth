"""Fork a saved world run into its own Neon branch and replay it forward with one change.

  .venv/bin/python ops/fork_run.py RUN_ID --day 14 --country aster --government dictatorship [run_stage1 options]
  .venv/bin/python ops/fork_run.py --list

The branch is created at the LSN where the run's last week-boundary checkpoint at or before --day was saved
(Neon point-in-time branching), so it holds that run exactly as it was then, with full event history,
copied on write. Leave out --country/--government for a control fork. Unknown options such as --verbose,
--live, --interval and --exit-on-complete go to ops/run_stage1.py.
"""

import argparse
import json
import os
import secrets
import subprocess
import sys
import time
from pathlib import Path

import httpx
from dotenv import dotenv_values

ROOT = Path(__file__).resolve().parents[1]
API = "https://console.neon.tech/api/v2"
GOVERNMENTS = ("market_democracy", "social_democracy", "planned_republic", "dictatorship", "monarchy")


def sql(url, query, params=()):
    host = httpx.URL(url).host
    response = httpx.post(
        "https://api." + host.split(".", 1)[1] + "/sql",
        headers={"Neon-Connection-String": url, "Neon-Array-Mode": "true"},
        json={"query": query, "params": list(params)},
        timeout=30,
    )
    if not response.is_success:
        raise RuntimeError(f"Neon query failed (HTTP {response.status_code}): {response.json().get('message', '')}")
    return response.json()["rows"]


def neon(method, path, key, **kwargs):
    response = httpx.request(method, API + path, headers={"Authorization": f"Bearer {key}"}, timeout=30, **kwargs)
    if not response.is_success:
        raise RuntimeError(f"Neon API {method} {path} failed (HTTP {response.status_code}): {response.json().get('message', '')}")
    return response.json()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("run_id", nargs="?")
    parser.add_argument("--day", type=int, default=0, help="Fork at the last week boundary at or before this day.")
    parser.add_argument("--country")
    parser.add_argument("--government", choices=GOVERNMENTS)
    parser.add_argument("--ticks", type=int, default=360, help="Day the forked run ends on (default 360).")
    parser.add_argument("--list", action="store_true", help="List registered timelines.")
    options, passthrough = parser.parse_known_args()

    config = dotenv_values(ROOT / ".env.local")
    database_url = (config.get("DATABASE_URL") or "").strip()
    key = (config.get("NEON_API_KEY") or "").strip()
    project = (config.get("NEON_PROJECT_ID") or "").strip()
    if not (database_url and key and project):
        raise RuntimeError("Save DATABASE_URL, NEON_API_KEY and NEON_PROJECT_ID in .env.local.")

    sql(database_url, """CREATE TABLE IF NOT EXISTS gearth.timelines (
        timeline text PRIMARY KEY, branch_id text NOT NULL, parent_run_id text NOT NULL,
        fork_tick bigint NOT NULL, fork_lsn pg_lsn NOT NULL, change jsonb,
        created_at timestamptz NOT NULL DEFAULT now())""")
    if options.list:
        for row in sql(database_url, "SELECT timeline, branch_id, parent_run_id, fork_tick, change, created_at FROM gearth.timelines ORDER BY created_at"):
            print(" | ".join(str(value) for value in row))
        return
    if not options.run_id:
        parser.error("Give a RUN_ID, or --list.")
    if (options.country is None) != (options.government is None):
        parser.error("Give both --country and --government, or neither for a control fork.")
    if options.ticks <= options.day:
        parser.error("--ticks must be after --day.")

    # B2: only week-boundary checkpoints (and day 0) have no decision in flight.
    rows = sql(database_url, """SELECT tick, lsn::text FROM gearth.checkpoints
        WHERE run_id=$1 AND tick<=$2 AND (tick=0 OR (tick+1)%7=0) ORDER BY tick DESC, revision LIMIT 1""",
        (options.run_id, options.day))
    if not rows:
        raise RuntimeError("No week-boundary checkpoint for that run at or before --day. Forks need a world run recorded on Neon with this version.")
    tick, lsn = int(rows[0][0]), rows[0][1]

    change = {"country": options.country, "government": options.government} if options.country else None
    label = f"{options.country}-{options.government}" if change else "control"
    timeline = f"fork-{options.run_id}-d{tick}-{label}-{secrets.token_hex(2)}"

    branches = neon("GET", f"/projects/{project}/branches", key)["branches"]
    parent = next(branch for branch in branches if branch.get("default"))
    created = neon("POST", f"/projects/{project}/branches", key, json={
        "branch": {"parent_id": parent["id"], "parent_lsn": lsn, "name": timeline},
        "endpoints": [{"type": "read_write"}],
    })
    for operation in created.get("operations", []):
        while neon("GET", f"/projects/{project}/operations/{operation['id']}", key)["operation"]["status"] != "finished":
            time.sleep(1)
    # The branch inherits roles and passwords, so only the host changes (pooled endpoint, like DATABASE_URL).
    endpoint = created["endpoints"][0]["host"]
    pooled = endpoint.replace(".", "-pooler.", 1) if "-pooler." in httpx.URL(database_url).host else endpoint
    branch_url = database_url.replace(httpx.URL(database_url).host, pooled, 1)

    sql(database_url, "INSERT INTO gearth.timelines VALUES($1,$2,$3,$4,$5::pg_lsn,$6::jsonb)",
        (timeline, created["branch"]["id"], options.run_id, tick, lsn, json.dumps(change)))
    print(f"Timeline {timeline}: Neon branch {created['branch']['id']} at LSN {lsn} (day {tick}); change: {change or 'none (control)'}", flush=True)

    environment = {**os.environ, "GEARTH_BRANCH_DATABASE_URL": branch_url}
    command = [sys.executable, str(ROOT / "ops/run_stage1.py"), "--world", "--resume", options.run_id,
               "--timeline", timeline, "--ticks", str(options.ticks), *passthrough]
    if change:
        command += ["--fork-government", f"{options.country}={options.government}"]
    sys.exit(subprocess.call(command, env=environment, cwd=ROOT))


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, httpx.HTTPError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
