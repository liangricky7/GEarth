"""Real HTTP -> queue -> isolated .NET engines, with local branch fixtures.

No Neon or model requests. Build GEarth.sln before running this integration check.
"""
import copy
import hashlib
import json
import os
import shutil
import signal
import socket
import subprocess
import time

import httpx
import pytest
from fastapi.testclient import TestClient

from ops.experiments import ROOT, ExperimentService, EngineRunner
from ops.gcp.control_server import Controller, create_app, password_hash

DLL = ROOT / "backend/GEarth.Api/bin/Debug/net10.0/GEarth.Api.dll"
pytestmark = pytest.mark.skipif(not DLL.exists() or shutil.which("dotnet") is None, reason="Build the .NET engine first")


class LocalBranches:
    def __init__(self, directory, checkpoint):
        self.directory = directory
        self.checkpoint = checkpoint
        self.resources = {}
        self.deleted = []

    def checkpoints(self, run_id):
        return [{"tick": self.checkpoint["tick"], "revision": self.checkpoint["revision"], "lsn": "fixture", "saved_at": "local fixture"}]

    def create(self, name, parent_id=None, lsn=None):
        directory = self.directory / name
        directory.mkdir(parents=True)
        checkpoint = self.load(self.resources[parent_id], self.checkpoint["id"]) if parent_id else self.checkpoint
        (directory / (checkpoint["id"] + ".json")).write_text(json.dumps(checkpoint))
        resource = {"branch_id": name, "directory": str(directory)}
        self.resources[name] = resource
        return resource

    def load(self, resource, run_id):
        return json.loads((self.directory / resource["branch_id"] / (run_id + ".json")).read_text())

    def delete(self, resource):
        self.deleted.append(resource["branch_id"])
        shutil.rmtree(self.directory / resource["branch_id"])

    def engine_environment(self, resource):
        return {"GEARTH_STORAGE": "local", "GEARTH_RUNS_DIRECTORY": resource["directory"], "DATABASE_URL": ""}


def stop(process):
    if process.poll() is None:
        os.killpg(process.pid, signal.SIGTERM)
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=5)


def test_http_submission_runs_two_real_engine_processes_without_modifying_parent(tmp_path):
    parent_directory = tmp_path / "parent"
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    environment = os.environ.copy()
    environment.update({"GEARTH_STORAGE": "local", "GEARTH_RUNS_DIRECTORY": str(parent_directory),
                        "GEARTH_WORLD": "true", "GEARTH_LIVE": "false", "GEARTH_PORT": str(port),
                        "GEARTH_POPULATION": "200", "GEARTH_TICKS": "14", "GEARTH_INTERVAL_MS": "150",
                        "GEARTH_ANALYTICS": "false", "GEARTH_VERBOSE": "false", "GEARTH_COMPUTE_PROVIDERS": "",
                        "GEARTH_SAVED_RUN_ID": "", "GEARTH_RESUME_RUN_ID": "", "GEARTH_FORK_GOVERNMENT": "",
                        "GEARTH_EXPERIMENT_EDIT": "", "GEARTH_EXPERIMENT_METRICS": "", "GEARTH_TIMELINE": "",
                        "GEARTH_NOTES_DIRECTORY": str(tmp_path / "parent-notes"), "GEARTH_CONTROL_DIRECTORY": str(tmp_path / "parent-pause"),
                        "ASPNETCORE_CONTENTROOT": str(ROOT / "backend/GEarth.Api")})
    with (tmp_path / "parent.log").open("w") as log:
        process = subprocess.Popen(["dotnet", str(DLL)], cwd=ROOT, env=environment,
                                   stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            deadline = time.monotonic() + 20
            checkpoint = None
            while time.monotonic() < deadline:
                for path in parent_directory.glob("*.json"):
                    world = json.loads(path.read_text())
                    if world["tick"] == 6 and world["status"] == "running":
                        checkpoint = copy.deepcopy(world)
                if checkpoint:
                    break
                assert process.poll() is None, (tmp_path / "parent.log").read_text()
                time.sleep(.02)
            assert checkpoint is not None, "The primary engine did not save a weekly checkpoint"
            while time.monotonic() < deadline:
                health = httpx.get(f"http://127.0.0.1:{port}/api/health", timeout=2).json()
                if health["run_status"] == "completed":
                    break
                time.sleep(.05)
            assert health["run_status"] == "completed"
        finally:
            stop(process)

    parent_path = parent_directory / (checkpoint["id"] + ".json")
    parent_digest = hashlib.sha256(parent_path.read_bytes()).hexdigest()
    backend = LocalBranches(tmp_path / "branches", checkpoint)
    service = ExperimentService(tmp_path / "experiments", backend, EngineRunner(backend, timeout=30))
    origin = "https://team.example.test"
    controller = Controller(tmp_path / "control", origin, password_hash("test-password"), "test-key", health=lambda: None)
    with TestClient(create_app(controller, service), base_url=origin, headers={"Origin": origin}) as client:
        client.post("/api/control/login", json={"password": "test-password"})
        preview_response = client.post("/api/control/experiments/previews", json={"run_id": checkpoint["id"], "revision": checkpoint["revision"]})
        assert preview_response.status_code == 201, preview_response.text
        preview = preview_response.json()
        target = next(agent for agent in checkpoint["agents"].values() if agent["role"] == "citizen")
        body = {"request_key": "request_real_engine", "preview_id": preview["id"], "days": 8,
                "edit": {"scope": "agent", "target_id": target["id"], "field": "health", "value": 10, "expected_value": target["health"]}}
        response = client.post("/api/control/experiments", json=body)
        assert response.status_code == 202, response.text
        job = response.json()
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            job = client.get("/api/control/experiments/" + job["id"]).json()
            if job["state"] in ("completed", "failed", "cancelled"):
                break
            time.sleep(.05)
        assert job["state"] == "completed", job
        result = client.get("/api/control/experiments/" + job["id"] + "/comparison").json()
        assert result["complete"]
        assert result["timelines"]["control"][0]["health"] == target["health"]
        assert result["timelines"]["changed"][0]["health"] == 10
        for rows in result["timelines"].values():
            assert [row["tick"] for row in rows] == list(range(6, 15))
            assert rows[-1]["status"] == "completed"
    assert hashlib.sha256(parent_path.read_bytes()).hexdigest() == parent_digest
    assert len(backend.deleted) == 2
