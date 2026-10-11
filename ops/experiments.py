"""Pinned Neon checkpoints and a durable, isolated RULE experiment queue.

The engine owns interventions. This module owns provisioning and supervision;
it never changes the parent world or forwards database credentials to a browser.
"""

import contextlib
import hashlib
import json
import math
import os
import re
import signal
import socket
import sqlite3
import subprocess
import threading
import time
import uuid
from pathlib import Path

import httpx

from ops.fork_run import GOVERNMENTS, ROOT, neon, sql

ACTIVE = ("queued", "provisioning", "running_control", "running_changed")
FIELDS = {
    "agent": {
        "health": {"label": "Health", "min": 0, "max": 100, "step": 1},
        "productivity": {"label": "Productivity multiplier", "min": .6, "max": 1.4, "step": .01},
    },
    "country": {
        "government": {"label": "Government", "choices": list(GOVERNMENTS)},
        "income_tax_percent": {"label": "Income tax (%)", "min": 0, "max": 60, "step": 1},
    },
}
METRICS = {
    "cash_minor": {"label": "Cash", "unit": "WLD", "scale": .01},
    "net_wealth_minor": {"label": "Net wealth", "unit": "WLD", "scale": .01},
    "health": {"label": "Health", "unit": "points", "scale": 1},
    "wellbeing": {"label": "Quality of life", "unit": "points", "scale": 1},
    "employment_rate": {"label": "Employment", "unit": "%", "scale": 100},
    "unemployment_rate": {"label": "Country unemployment", "unit": "%", "scale": 100},
    "approval": {"label": "Country approval", "unit": "%", "scale": 100},
    "treasury_minor": {"label": "Country treasury", "unit": "WLD", "scale": .01},
    "liquid_assets_gini": {"label": "Country liquid-assets inequality", "unit": "Gini", "scale": 1},
}


class ExperimentError(Exception):
    def __init__(self, message, status=400):
        super().__init__(message)
        self.status = status


class Cancelled(Exception):
    pass


def identity(prefix):
    return prefix + "_" + uuid.uuid4().hex[:20]


def engine_digest():
    digest = hashlib.sha256()
    for name in ("GEarth.Api.dll", "GEarth.Core.dll"):
        path = ROOT / "backend/GEarth.Api/bin/Debug/net10.0" / name
        if not path.exists():
            return None
        digest.update(name.encode() + b"\0" + hashlib.sha256(path.read_bytes()).digest())
    return digest.hexdigest()


def atomic_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp")
    with temporary.open("w") as stream:
        json.dump(value, stream, allow_nan=False)
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)


class NeonExperimentBackend:
    def __init__(self, config):
        self.url = config["DATABASE_URL"]
        self.key = config["NEON_API_KEY"]
        self.project = config["NEON_PROJECT_ID"]

    def connection(self, resource):
        host = resource["endpoint"]
        original = httpx.URL(self.url).host
        if "-pooler." in original and "-pooler." not in host:
            host = host.replace(".", "-pooler.", 1)
        return self.url.replace(original, host, 1)

    def checkpoints(self, run_id):
        rows = sql(self.url, """SELECT tick, revision, lsn::text, saved_at::text
            FROM gearth.checkpoints WHERE run_id=$1
            AND (tick=0 OR (tick+1)%7=0) ORDER BY tick DESC, revision DESC LIMIT 1000""", (run_id,))
        return [{"tick": int(row[0]), "revision": int(row[1]), "lsn": row[2], "saved_at": row[3]}
                for row in rows]

    def create(self, name, parent_id=None, lsn=None):
        if parent_id is None:
            host = httpx.URL(self.url).host.replace("-pooler.", ".", 1).lower()
            endpoints = neon("GET", f"/projects/{self.project}/endpoints", self.key)["endpoints"]
            matches = [item for item in endpoints if item["host"].lower() == host]
            if len(matches) != 1:
                raise ExperimentError("The website database endpoint does not match this Neon project.", 503)
            parent_id = matches[0]["branch_id"]
        branch = {"parent_id": parent_id, "name": name}
        if lsn:
            branch["parent_lsn"] = lsn
        result = neon("POST", f"/projects/{self.project}/branches", self.key,
                      json={"branch": branch, "endpoints": [{"type": "read_write"}]})
        # Return ownership immediately. Readiness is checked separately so failures can be cleaned up.
        return {"branch_id": result["branch"]["id"], "endpoint": result["endpoints"][0]["host"],
                "parent_branch_id": parent_id}

    def load(self, resource, run_id):
        url = self.connection(resource)
        deadline = time.monotonic() + 60
        while True:
            try:
                rows = sql(url, "SELECT state::text, checkpoint_digest, checkpoint_part_count FROM gearth.stage1_runs WHERE run_id=$1", (run_id,))
                break
            except (httpx.HTTPError, RuntimeError):
                if time.monotonic() >= deadline:
                    raise ExperimentError("The Neon checkpoint could not be read. Check branch availability.", 409) from None
                time.sleep(1)
        if not rows:
            raise ExperimentError("The selected world is missing from this checkpoint.", 409)
        state, digest, count = rows[0]
        if digest:
            parts = []
            count = int(count)
            if not 1 <= count <= 4096:
                raise ExperimentError("Checkpoint part count is unsupported.", 409)
            for start in range(0, count, 16):
                page = sql(url, "SELECT part,content FROM gearth.checkpoint_parts WHERE run_id=$1 AND digest=$2 AND part >= $3::int ORDER BY part LIMIT 16",
                           (run_id, digest, start))
                for number, content in page:
                    if int(number) != len(parts):
                        raise ExperimentError("Checkpoint parts are incomplete.", 409)
                    parts.append(content)
                if sum(len(part) for part in parts) > 64 * 1024 * 1024:
                    raise ExperimentError("Checkpoint exceeds the experiment preview limit.", 409)
            state = "".join(parts)
            if len(parts) != count or hashlib.sha256(state.encode()).hexdigest() != digest:
                raise ExperimentError("Checkpoint digest verification failed.", 409)
        if not isinstance(state, str) or len(state) > 64 * 1024 * 1024:
            raise ExperimentError("Checkpoint exceeds the experiment preview limit.", 409)
        return json.loads(state)

    def delete(self, resource):
        response = httpx.delete(f"https://console.neon.tech/api/v2/projects/{self.project}/branches/{resource['branch_id']}",
                                headers={"Authorization": f"Bearer {self.key}"}, timeout=30)
        if not response.is_success and response.status_code != 404:
            raise RuntimeError("Owned Neon branch cleanup failed")

    def engine_environment(self, resource):
        return {"DATABASE_URL": self.connection(resource), "GEARTH_STORAGE": "neon"}

    def find_owned(self, name):
        result = neon("GET", f"/projects/{self.project}/branches", self.key, params={"search": name, "limit": 100})
        matches = [item for item in result["branches"] if item["name"] == name]
        if len(matches) > 1:
            raise RuntimeError("Ambiguous branch ownership; operator reconciliation required")
        return {"branch_id": matches[0]["id"]} if matches else None


class EngineRunner:
    def __init__(self, backend, timeout=1800):
        self.backend = backend
        self.timeout = timeout

    def run(self, resource, job, timeline, directory, cancelled, progress):
        directory.mkdir(parents=True, exist_ok=True)
        metric_path = directory / "metrics.ndjson"
        edit_path = directory / "edit.json"
        if timeline == "changed":
            atomic_json(edit_path, job["edit"])
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        environment = os.environ.copy()
        environment.update({
            "GEARTH_WORLD": "true", "GEARTH_LIVE": "false", "GEARTH_PORT": str(port),
            "GEARTH_TICKS": str(job["end_tick"]), "GEARTH_INTERVAL_MS": "0", "GEARTH_CAPACITY": "64",
            "GEARTH_ANALYTICS": "false", "GEARTH_VERBOSE": "false", "GEARTH_COMPUTE_PROVIDERS": "",
            "GEARTH_SAVED_RUN_ID": "", "GEARTH_RESUME_RUN_ID": job["run_id"], "GEARTH_FORK_GOVERNMENT": "",
            "GEARTH_TIMELINE": job["id"] + "_" + timeline, "GEARTH_EXPERIMENT_ID": job["id"],
            "GEARTH_EXPERIMENT_EDIT": str(edit_path) if timeline == "changed" else "",
            "GEARTH_EXPERIMENT_SCOPE": job["edit"]["scope"], "GEARTH_EXPERIMENT_TARGET": job["edit"]["target_id"],
            "GEARTH_EXPERIMENT_METRICS": str(metric_path), "GEARTH_NOTES_DIRECTORY": str(directory / "notes"),
            "GEARTH_CONTROL_DIRECTORY": str(directory / "control"),
            "ASPNETCORE_CONTENTROOT": str(ROOT / "backend/GEarth.Api"),
        })
        environment.update(self.backend.engine_environment(resource))
        dll = ROOT / "backend/GEarth.Api/bin/Debug/net10.0/GEarth.Api.dll"
        if not dll.exists():
            raise ExperimentError("Build the experiment engine before starting the service.", 503)
        if job.get("engine_binary_sha256") and engine_digest() != job["engine_binary_sha256"]:
            raise ExperimentError("The engine changed after submission. Create a new experiment with the current build.", 409)
        with (directory / "engine.log").open("w") as log:
            process = subprocess.Popen(["dotnet", str(dll)], cwd=ROOT, env=environment,
                                       stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            deadline = time.monotonic() + self.timeout
            try:
                while process.poll() is None:
                    if cancelled():
                        raise Cancelled()
                    if time.monotonic() > deadline:
                        raise ExperimentError("The timeline exceeded its execution time limit.", 409)
                    try:
                        health = httpx.get(f"http://127.0.0.1:{port}/api/health", timeout=2).json()
                    except (httpx.HTTPError, ValueError):
                        time.sleep(.2)
                        continue
                    if health.get("error"):
                        raise ExperimentError("The timeline engine failed. Its private log is retained.", 409)
                    progress(health.get("tick"))
                    if health.get("run_status") == "completed":
                        # Commit/metrics can finish just after publication of the current world.
                        rows = read_metrics(metric_path)
                        if rows and rows[-1]["status"] == "completed" and rows[-1]["tick"] == job["end_tick"]:
                            return
                    time.sleep(.2)
                raise ExperimentError("The timeline engine exited before completing.", 409)
            finally:
                if process.poll() is None:
                    os.killpg(process.pid, signal.SIGTERM)
                    try:
                        process.wait(timeout=35)
                    except subprocess.TimeoutExpired:
                        os.killpg(process.pid, signal.SIGKILL)
                        process.wait(timeout=5)


def read_metrics(path):
    if not path.exists():
        return []
    rows = {}
    with path.open() as stream:
        for line in stream:
            try:
                row = json.loads(line)
            except ValueError:
                break  # An in-progress partial final line is not a complete sample.
            if row.get("schema_version") != "gearth.experiment-metrics.v1":
                raise ExperimentError("Unsupported experiment metric format.", 409)
            rows[row["tick"]] = row
            if len(rows) > 366:
                raise ExperimentError("Experiment metric stream exceeds its day limit.", 409)
    return [rows[tick] for tick in sorted(rows)]


class ExperimentService:
    def __init__(self, directory, backend, runner=None):
        self.directory = Path(directory).resolve()
        self.directory.mkdir(parents=True, exist_ok=True)
        self.backend = backend
        self.runner = runner or EngineRunner(backend)
        self.stop = threading.Event()
        self.thread = None
        self.lock_file = None
        with self.db() as db:
            db.executescript("""
                CREATE TABLE IF NOT EXISTS previews(id TEXT PRIMARY KEY,run_id TEXT,tick INTEGER,
                    revision INTEGER,state TEXT,payload TEXT,created REAL);
                CREATE TABLE IF NOT EXISTS experiments(id TEXT PRIMARY KEY,request_key TEXT UNIQUE,
                    preview_id TEXT,state TEXT,payload TEXT,created REAL);
                CREATE TABLE IF NOT EXISTS resources(id TEXT PRIMARY KEY,owner TEXT,payload TEXT,released INTEGER DEFAULT 0);
            """)

    @contextlib.contextmanager
    def db(self):
        connection = sqlite3.connect(self.directory / "registry.sqlite", timeout=15)
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA journal_mode=WAL")
        try:
            with connection:
                yield connection
        finally:
            connection.close()

    def start(self):
        import fcntl
        self.lock_file = (self.directory / "runner.lock").open("w")
        try:
            fcntl.flock(self.lock_file, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            self.lock_file.close()
            raise RuntimeError("An experiment supervisor already owns this registry") from None
        with self.db() as db:
            for row in db.execute("SELECT id,payload FROM experiments WHERE state IN ('provisioning','running_control','running_changed')"):
                job = json.loads(row["payload"])
                job.update(state="failed", error="Execution was interrupted by a service restart. Submit a new experiment.")
                db.execute("UPDATE experiments SET state='failed',payload=? WHERE id=?", (json.dumps(job), row["id"]))
            db.execute("UPDATE previews SET state='failed' WHERE state='preparing'")
        self.thread = threading.Thread(target=self.loop, name="experiment-runner", daemon=True)
        self.thread.start()

    def close(self):
        self.stop.set()
        if self.thread:
            self.thread.join(timeout=45)
        if self.lock_file:
            self.lock_file.close()

    def checkpoints(self, run_id):
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,100}", run_id):
            raise ExperimentError("Invalid run ID.")
        try:
            return self.backend.checkpoints(run_id)
        except (httpx.HTTPError, RuntimeError):
            raise ExperimentError("Checkpoint discovery is unavailable. Check the Neon configuration.", 503) from None

    def create_owned(self, owner, name, **parameters):
        resource_id = identity("resource")
        reservation = {"name": name, "reserved_at": time.time()}
        with self.db() as db:
            db.execute("INSERT INTO resources(id,owner,payload) VALUES(?,?,?)", (resource_id, owner, json.dumps(reservation)))
        # Record the name before POST. A lost response can then be reconciled by exact name without repeating POST.
        resource = {**reservation, **self.backend.create(name, **parameters)}
        with self.db() as db:
            db.execute("UPDATE resources SET payload=? WHERE id=?", (json.dumps(resource), resource_id))
        return resource

    def cleanup(self, owner):
        with self.db() as db:
            rows = db.execute("SELECT id,payload FROM resources WHERE owner=? AND released=0", (owner,)).fetchall()
        for row in rows:
            try:
                resource = json.loads(row["payload"])
                if not resource.get("branch_id"):
                    found = self.backend.find_owned(resource["name"])
                    if found:
                        resource.update(found)
                        with self.db() as db:
                            db.execute("UPDATE resources SET payload=? WHERE id=?", (json.dumps(resource), row["id"]))
                    elif time.time() - resource["reserved_at"] < 300:
                        continue  # Allow a timed-out creation to become visible before concluding it never happened.
                if resource.get("branch_id"):
                    self.backend.delete(resource)
            except Exception:
                continue  # Retain ownership and retry; never delete an unregistered branch.
            with self.db() as db:
                db.execute("UPDATE resources SET released=1 WHERE id=?", (row["id"],))

    def preview(self, run_id, revision):
        checkpoint = next((item for item in self.checkpoints(run_id) if item["revision"] == revision), None)
        if checkpoint is None:
            raise ExperimentError("This checkpoint is not available.", 409)
        preview_id = identity("preview")
        with self.db() as db:
            db.execute("BEGIN IMMEDIATE")
            for existing in db.execute("SELECT payload FROM previews WHERE run_id=? AND revision=? AND state='ready'", (run_id, revision)):
                pinned = json.loads(existing[0])
                if pinned["expires_at"] > time.time():
                    return {key: value for key, value in pinned.items() if key != "resource"}
            count = db.execute("SELECT count(*) FROM previews WHERE state IN ('preparing','ready')").fetchone()[0]
            if count >= 8:
                raise ExperimentError("Too many checkpoint previews are open. Try again after one expires.", 409)
            db.execute("INSERT INTO previews VALUES(?,?,?,?,?,?,?)", (preview_id, run_id, checkpoint["tick"], revision,
                       "preparing", "{}", time.time()))
        try:
            resource = self.create_owned(preview_id, preview_id, lsn=checkpoint["lsn"])
            world = self.backend.load(resource, run_id)
            if (world.get("id") != run_id or world.get("revision") != revision
                or world.get("tick") != checkpoint["tick"] or world.get("status") != "running"
                or not world.get("society") or (world["tick"] != 0 and (world["tick"] + 1) % 7 != 0)):
                raise ExperimentError("This saved state is not a resumable weekly checkpoint.", 409)
            atomic_json(self.directory / preview_id / "world.json", world)
            payload = {"id": preview_id, "run_id": run_id, **checkpoint, "parent_mode": world.get("mode", "unknown"),
                       "resource": resource, "expires_at": time.time() + 3600,
                       "checkpoint_sha256": hashlib.sha256(json.dumps(world).encode()).hexdigest()}
            with self.db() as db:
                db.execute("UPDATE previews SET state='ready',payload=? WHERE id=?", (json.dumps(payload), preview_id))
            return {key: value for key, value in payload.items() if key != "resource"}
        except Exception as error:
            with self.db() as db:
                db.execute("UPDATE previews SET state='failed' WHERE id=?", (preview_id,))
            self.cleanup(preview_id)
            if isinstance(error, ExperimentError):
                raise
            raise ExperimentError("Neon cannot restore this checkpoint. It may be outside the history window.", 409) from None

    def get_preview(self, preview_id):
        with self.db() as db:
            row = db.execute("SELECT state,payload FROM previews WHERE id=?", (preview_id,)).fetchone()
        if not row or row["state"] != "ready":
            raise ExperimentError("Checkpoint preview is unavailable. Prepare it again.", 409)
        preview = json.loads(row["payload"])
        if preview["expires_at"] <= time.time():
            raise ExperimentError("The preview has expired. Prepare the checkpoint again.", 409)
        return preview

    def world(self, preview_id):
        self.get_preview(preview_id)
        return json.loads((self.directory / preview_id / "world.json").read_text())

    def targets(self, preview_id, scope, query="", offset=0):
        if scope not in FIELDS:
            raise ExperimentError("Choose an agent or country.")
        world = self.world(preview_id)
        if scope == "agent":
            rows = [{"id": agent["id"], "label": agent["id"] + " · " + agent.get("occupation", ""),
                     "country": agent["country_id"],
                     "values": {"health": agent["health"], "productivity": agent["weights"]["productivity"]}}
                    for agent in world["agents"].values() if agent["role"] in ("citizen", "entrepreneur", "planner")]
        else:
            rows = [{"id": nation["id"], "label": nation["name"],
                     "values": {"government": nation["government_id"], "income_tax_percent": nation["tax_basis_points"] / 100}}
                    for nation in world["society"]["nations"].values()]
        rows = sorted((row for row in rows if query.lower() in (row["id"] + " " + row["label"]).lower()), key=lambda row: row["id"])
        return {"targets": rows[offset:offset + 50], "total": len(rows), "offset": offset, "fields": FIELDS[scope]}

    def submit(self, request_key, preview_id, edit, days):
        if not re.fullmatch(r"[A-Za-z0-9_-]{8,64}", request_key):
            raise ExperimentError("Invalid request key.")
        try:
            fingerprint = json.dumps({"preview_id": preview_id, "edit": edit, "days": days}, sort_keys=True, allow_nan=False)
        except (ValueError, TypeError):
            raise ExperimentError("Experiment values must be finite JSON values.") from None
        with self.db() as db:
            existing = db.execute("SELECT payload FROM experiments WHERE request_key=?", (request_key,)).fetchone()
        if existing:
            job = json.loads(existing[0])
            if job["fingerprint"] != fingerprint:
                raise ExperimentError("This request key was already used for a different experiment.", 409)
            return self.public(job)
        preview = self.get_preview(preview_id)
        if type(days) is not int or not 1 <= days <= 365 or preview["tick"] + days > 10000:
            raise ExperimentError("Choose 1–365 days forward, ending by tick 10000.")
        scope, target_id, field = edit.get("scope"), edit.get("target_id"), edit.get("field")
        if scope not in FIELDS or field not in FIELDS[scope]:
            raise ExperimentError("Unsupported experiment field.")
        world = self.world(preview_id)
        if scope == "agent":
            target = world["agents"].get(target_id)
            if not target or target["role"] not in ("citizen", "entrepreneur", "planner"):
                raise ExperimentError("Choose a resident from the checkpoint.")
            old = target["health"] if field == "health" else target["weights"]["productivity"]
        else:
            target = world["society"]["nations"].get(target_id)
            if not target:
                raise ExperimentError("Choose a country from the checkpoint.")
            old = target["government_id"] if field == "government" else target["tax_basis_points"] / 100
        if type(edit.get("expected_value")) is bool or edit.get("expected_value") != old:
            raise ExperimentError("The original value does not match the checkpoint. Preview again.", 409)
        value, definition = edit.get("value"), FIELDS[scope][field]
        if "choices" in definition:
            valid = value in definition["choices"]
        else:
            valid = (type(value) in (int, float) and math.isfinite(value)
                     and definition["min"] <= value <= definition["max"]
                     and (definition["step"] != 1 or value == int(value)))
        if not valid:
            raise ExperimentError("Value is outside the supported field range.")
        if old == value:
            raise ExperimentError("Choose a value different from the original.")
        job = {"id": identity("experiment"), "preview_id": preview_id, "run_id": preview["run_id"],
               "fork_tick": preview["tick"], "revision": preview["revision"], "end_tick": preview["tick"] + days,
               "parent_mode": preview["parent_mode"], "mode": "RULE", "edit": edit,
               "state": "queued", "created_at": time.time(), "tick": preview["tick"], "error": None,
               "fingerprint": fingerprint, "preview_resource": preview["resource"]}
        job["source_branch_id"] = preview["resource"].get("parent_branch_id")
        job["checkpoint_lsn"] = preview["lsn"]
        job["checkpoint_sha256"] = preview.get("checkpoint_sha256") or hashlib.sha256(json.dumps(world).encode()).hexdigest()
        job["engine_binary_sha256"] = engine_digest()
        with self.db() as db:
            db.execute("BEGIN IMMEDIATE")
            if db.execute("SELECT count(*) FROM experiments WHERE state IN ('queued','provisioning','running_control','running_changed')").fetchone()[0] >= 8:
                raise ExperimentError("The experiment queue is full. Try again later.", 409)
            try:
                db.execute("INSERT INTO experiments VALUES(?,?,?,?,?,?)", (job["id"], request_key, preview_id, "queued", json.dumps(job), time.time()))
            except sqlite3.IntegrityError:
                existing = json.loads(db.execute("SELECT payload FROM experiments WHERE request_key=?", (request_key,)).fetchone()[0])
                if existing["fingerprint"] != fingerprint:
                    raise ExperimentError("This request key is already in use.", 409)
                return self.public(existing)
        return self.public(job)

    @staticmethod
    def public(job):
        return {key: value for key, value in job.items() if key not in ("fingerprint", "preview_resource")}

    def get(self, job_id):
        with self.db() as db:
            row = db.execute("SELECT payload FROM experiments WHERE id=?", (job_id,)).fetchone()
        if not row:
            raise ExperimentError("Experiment not found.", 404)
        return self.public(json.loads(row[0]))

    def list(self):
        with self.db() as db:
            return [self.public(json.loads(row[0])) for row in db.execute("SELECT payload FROM experiments ORDER BY created DESC LIMIT 50")]

    def update(self, job_id, **values):
        with self.db() as db:
            db.execute("BEGIN IMMEDIATE")
            job = json.loads(db.execute("SELECT payload FROM experiments WHERE id=?", (job_id,)).fetchone()[0])
            # Cancellation is terminal from the client's perspective; a racing progress callback cannot undo it.
            if job["state"] == "cancelled":
                return job
            job.update(values)
            db.execute("UPDATE experiments SET state=?,payload=? WHERE id=?", (job["state"], json.dumps(job), job_id))
            return job

    def cancel(self, job_id):
        job = self.get(job_id)
        if job["state"] in ACTIVE:
            job = self.update(job_id, state="cancelled", error=None)
        return self.public(job)

    def comparison(self, job_id):
        job = self.get(job_id)
        streams = {timeline: read_metrics(self.directory / job_id / timeline / "metrics.ndjson")
                   for timeline in ("control", "changed")}
        complete = job["state"] == "completed"
        return {"experiment": job, "complete": complete, "metrics": METRICS, "timelines": streams,
                "aggregation": "selected agent" if job["edit"]["scope"] == "agent" else "country resident mean",
                "note": "Both timelines use deterministic rule decisions. Country metrics describe the selected country."}

    def execute(self, job):
        job_id = job["id"]
        cancelled = lambda: self.stop.is_set() or self.get(job_id)["state"] == "cancelled"
        try:
            resources = {}
            for timeline in ("control", "changed"):
                if cancelled():
                    raise Cancelled()
                resource = self.create_owned(job_id, job_id + "_" + timeline,
                                             parent_id=job["preview_resource"]["branch_id"])
                state = self.backend.load(resource, job["run_id"])
                if (state["id"] != job["run_id"] or state["revision"] != job["revision"] or state["tick"] != job["fork_tick"]
                    or hashlib.sha256(json.dumps(state).encode()).hexdigest() != job["checkpoint_sha256"]):
                    raise ExperimentError("A timeline does not match its pinned checkpoint.", 409)
                resources[timeline] = resource
            self.update(job_id, branch_ids={name: value["branch_id"] for name, value in resources.items()})
            for timeline in ("control", "changed"):
                if cancelled():
                    raise Cancelled()
                self.update(job_id, state="running_" + timeline, tick=job["fork_tick"])
                self.runner.run(resources[timeline], job, timeline, self.directory / job_id / timeline,
                                cancelled, lambda tick: self.update(job_id, tick=tick))
            for timeline in ("control", "changed"):
                rows = read_metrics(self.directory / job_id / timeline / "metrics.ndjson")
                if ([row["tick"] for row in rows] != list(range(job["fork_tick"], job["end_tick"] + 1))
                    or rows[-1]["status"] != "completed"):
                    raise ExperimentError("The timeline's daily results are incomplete.", 409)
            self.update(job_id, state="completed", tick=job["end_tick"], finished_at=time.time())
        except Cancelled:
            self.update(job_id, state="cancelled", finished_at=time.time())
        except Exception as error:
            message = str(error) if isinstance(error, ExperimentError) else "Experiment failed. Private logs and resource records are retained."
            self.update(job_id, state="failed", error=message, finished_at=time.time())
        finally:
            self.cleanup(job_id)

    def reap(self):
        with self.db() as db:
            jobs = [row[0] for row in db.execute("SELECT id FROM experiments WHERE state IN ('completed','failed','cancelled')")]
            previews = db.execute("""SELECT id,payload,state FROM previews WHERE state='failed' OR
                (created < ? AND id NOT IN (SELECT preview_id FROM experiments WHERE state IN ('queued','provisioning','running_control','running_changed')))""",
                (time.time() - 3600,)).fetchall()
            for preview in previews:
                db.execute("UPDATE previews SET state='expired' WHERE id=?", (preview["id"],))
        for owner in jobs + [row["id"] for row in previews]:
            self.cleanup(owner)
        for row in previews:
            (self.directory / row["id"] / "world.json").unlink(missing_ok=True)

    def loop(self):
        last_reap = 0
        while not self.stop.is_set():
            try:
                with self.db() as db:
                    db.execute("BEGIN IMMEDIATE")
                    row = db.execute("SELECT id,payload FROM experiments WHERE state='queued' ORDER BY created LIMIT 1").fetchone()
                    if row:
                        job = json.loads(row["payload"])
                        job["state"] = "provisioning"
                        db.execute("UPDATE experiments SET state='provisioning',payload=? WHERE id=?", (json.dumps(job), job["id"]))
                if row:
                    self.execute(job)
                if time.monotonic() - last_reap > 60:
                    self.reap()
                    last_reap = time.monotonic()
            except Exception:
                # Keep the supervisor alive; individual jobs retain their durable status.
                pass
            self.stop.wait(.5)
