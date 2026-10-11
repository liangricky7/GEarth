import copy
import hashlib
import json
import time

import httpx
import pytest
from fastapi.testclient import TestClient

from ops.experiments import ExperimentService, ExperimentError, NeonExperimentBackend, atomic_json
from ops.gcp.control_server import Controller, create_app, password_hash


class Backend:
    def __init__(self):
        self.parent = {"id": "world_test", "revision": 2, "tick": 6, "status": "running", "mode": "LIVE",
                       "agents": {"resident": {"id": "resident", "role": "citizen", "country_id": "aster",
                                                "occupation": "farmer", "health": 90, "weights": {"productivity": 1.0}}},
                       "society": {"nations": {"aster": {"id": "aster", "name": "Aster", "government_id": "monarchy", "tax_basis_points": 2000}}}}
        self.created = []
        self.deleted = []
        self.fail_create = None
        self.fail_after_create = False

    def checkpoints(self, run_id):
        return [{"tick": 6, "revision": 2, "lsn": "0/123", "saved_at": "2026-10-11"}]

    def create(self, name, parent_id=None, lsn=None):
        if len(self.created) + 1 == self.fail_create:
            raise RuntimeError("private API error")
        resource = {"branch_id": name, "endpoint": "private.example.test"}
        self.created.append((resource, parent_id, lsn))
        if self.fail_after_create:
            raise RuntimeError("Create response was lost")
        return resource

    def load(self, resource, run_id):
        return copy.deepcopy(self.parent)

    def delete(self, resource):
        self.deleted.append(resource["branch_id"])

    def find_owned(self, name):
        return next((resource for resource, _, _ in self.created if resource["branch_id"] == name), None)


class Runner:
    def __init__(self):
        self.calls = []
        self.fail = None
        self.partial = False

    def run(self, resource, job, timeline, directory, cancelled, progress):
        self.calls.append(timeline)
        if self.fail == timeline:
            raise RuntimeError("postgres://private-password")
        directory.mkdir(parents=True, exist_ok=True)
        with (directory / "metrics.ndjson").open("w") as stream:
            for tick in range(job["fork_tick"], job["end_tick"] + 1):
                if self.partial and tick == job["end_tick"]:
                    break
                row = {"schema_version": "gearth.experiment-metrics.v1", "tick": tick,
                       "status": "completed" if tick == job["end_tick"] else "running",
                       "health": 90 if timeline == "control" else job["edit"]["value"]}
                stream.write(json.dumps(row) + "\n")
                progress(tick)


@pytest.fixture
def service(tmp_path):
    return ExperimentService(tmp_path, Backend(), Runner())


def prepared(service):
    preview = service.preview("world_test", 2)
    edit = {"scope": "agent", "target_id": "resident", "field": "health", "value": 20, "expected_value": 90}
    return preview, edit


def private_job(service, job_id):
    with service.db() as db:
        return json.loads(db.execute("SELECT payload FROM experiments WHERE id=?", (job_id,)).fetchone()[0])


def test_preview_pins_exact_historical_state_and_does_not_expose_connections(service):
    preview, _ = prepared(service)
    assert preview["tick"] == 6 and preview["parent_mode"] == "LIVE"
    assert "resource" not in preview and "endpoint" not in json.dumps(preview)
    assert service.backend.created[0][2] == "0/123"
    targets = service.targets(preview["id"], "agent")
    assert targets["targets"][0]["values"]["health"] == 90
    assert service.targets(preview["id"], "country")["targets"][0]["values"]["income_tax_percent"] == 20


def test_duplicate_submissions_return_one_job_and_reusing_key_for_different_edit_fails(service):
    preview, edit = prepared(service)
    first = service.submit("request_123", preview["id"], edit, 7)
    assert service.submit("request_123", preview["id"], edit, 7)["id"] == first["id"]
    with pytest.raises(ExperimentError, match="different experiment"):
        service.submit("request_123", preview["id"], {**edit, "value": 30}, 7)
    assert len(service.list()) == 1


@pytest.mark.parametrize("changes", [{"value": True}, {"value": 101}, {"value": 20.5}, {"value": float("nan")},
                                    {"field": "cash_minor"}, {"target_id": "missing"}, {"expected_value": 80}])
def test_invalid_or_stale_edit_is_rejected_before_child_branches(service, changes):
    preview, edit = prepared(service)
    with pytest.raises(ExperimentError):
        service.submit("request_bad", preview["id"], {**edit, **changes}, 7)
    assert len(service.backend.created) == 1 and not service.list()


def test_paired_run_has_exact_daily_coverage_and_releases_only_owned_children(service):
    preview, edit = prepared(service)
    original = copy.deepcopy(service.backend.parent)
    job = service.submit("request_pair", preview["id"], edit, 7)
    service.execute(private_job(service, job["id"]))
    assert service.get(job["id"])["state"] == "completed"
    assert service.runner.calls == ["control", "changed"]
    assert service.backend.parent == original
    assert [item[1] for item in service.backend.created[1:]] == [preview["id"], preview["id"]]
    assert preview["id"] not in service.backend.deleted and len(service.backend.deleted) == 2
    comparison = service.comparison(job["id"])
    assert comparison["complete"]
    assert [row["tick"] for row in comparison["timelines"]["control"]] == list(range(6, 14))
    assert "private.example.test" not in json.dumps(comparison)


def test_partial_results_and_engine_failures_never_look_completed(service):
    preview, edit = prepared(service)
    job = service.submit("request_fail", preview["id"], edit, 7)
    service.runner.fail = "changed"
    service.execute(private_job(service, job["id"]))
    result = service.comparison(job["id"])
    assert not result["complete"] and result["experiment"]["state"] == "failed"
    assert "private-password" not in json.dumps(result)
    assert result["timelines"]["control"] and not result["timelines"]["changed"]


def test_missing_days_fail_validation(service):
    preview, edit = prepared(service)
    job = service.submit("request_partial", preview["id"], edit, 7)
    service.runner.partial = True
    service.execute(private_job(service, job["id"]))
    assert service.get(job["id"])["state"] == "failed"


def test_partial_provisioning_failure_reclaims_the_created_child(service):
    preview, edit = prepared(service)
    job = service.submit("request_provision", preview["id"], edit, 7)
    service.backend.fail_create = 3
    service.execute(private_job(service, job["id"]))
    assert service.get(job["id"])["state"] == "failed"
    assert service.backend.deleted == [job["id"] + "_control"]
    assert not service.runner.calls


def test_lost_branch_creation_response_is_reconciled_without_another_post(service):
    service.backend.fail_after_create = True
    with pytest.raises(ExperimentError):
        prepared(service)
    assert len(service.backend.created) == 1
    assert service.backend.deleted == [service.backend.created[0][0]["branch_id"]]
    with service.db() as db:
        assert db.execute("SELECT released FROM resources").fetchone()[0] == 1


def test_cancelled_job_cannot_be_revived_by_a_racing_progress_update(service):
    preview, edit = prepared(service)
    job = service.submit("request_cancel", preview["id"], edit, 7)
    service.cancel(job["id"])
    service.update(job["id"], state="completed", tick=13)
    service.execute(private_job(service, job["id"]))
    assert service.get(job["id"])["state"] == "cancelled" and not service.runner.calls


def test_restart_marks_interrupted_work_and_expired_preview_is_reclaimed(service):
    preview, edit = prepared(service)
    job = service.submit("request_restart", preview["id"], edit, 7)
    service.update(job["id"], state="running_control")
    with service.db() as db:
        db.execute("UPDATE previews SET created=? WHERE id=?", (time.time() - 7200, preview["id"]))
    service.start()
    service.close()
    service.reap()
    assert service.get(job["id"])["state"] == "failed"
    assert preview["id"] in service.backend.deleted


def test_preview_rejects_checkpoint_mismatch_and_cleans_up(service):
    service.backend.parent["tick"] = 7
    with pytest.raises(ExperimentError, match="not a resumable"):
        prepared(service)
    assert len(service.backend.deleted) == 1


def test_older_preview_records_get_a_checkpoint_digest_when_submitted(service):
    preview, edit = prepared(service)
    with service.db() as db:
        record = json.loads(db.execute("SELECT payload FROM previews WHERE id=?", (preview["id"],)).fetchone()[0])
        del record["checkpoint_sha256"]
        db.execute("UPDATE previews SET payload=? WHERE id=?", (json.dumps(record), preview["id"]))
    job = service.submit("request_old_preview", preview["id"], edit, 7)
    assert len(job["checkpoint_sha256"]) == 64


def test_cloud_deployment_only_includes_branch_credentials_when_explicitly_enabled(monkeypatch):
    from ops.gcp import deploy
    monkeypatch.setattr(deploy, "compute_environment", lambda *args: None)
    config = {"DATABASE_URL": "postgres://fixture", "NEON_API_KEY": "private-key", "NEON_PROJECT_ID": "project"}
    for prefix in ("SPARK_A", "SPARK_A_CITIZEN", "SPARK_B", "SPARK_B_CITIZEN"):
        config.update({prefix + "_MODEL": "fixture-model", prefix + "_API_KEY": "fixture-key"})
    assert "NEON_API_KEY" not in deploy.cloud_config(config)
    enabled = deploy.cloud_config({**config, "GEARTH_EXPERIMENTS_ENABLED": "true"})
    assert enabled["NEON_API_KEY"] == "private-key" and enabled["GEARTH_EXPERIMENTS_ENABLED"] == "true"
    with pytest.raises(ValueError, match="NEON_API_KEY"):
        deploy.cloud_config({"DATABASE_URL": "fixture", "GEARTH_EXPERIMENTS_ENABLED": "true"})


def test_chunked_checkpoint_digest_is_verified(monkeypatch):
    backend = NeonExperimentBackend({"DATABASE_URL": "postgres://user:secret@host.neon.tech/db", "NEON_API_KEY": "key", "NEON_PROJECT_ID": "project"})
    state = '{"id":"world_test"}'
    digest = hashlib.sha256(state.encode()).hexdigest()
    def query(url, query, params):
        return [[None, digest, "2"]] if "stage1_runs" in query else [["0", state[:10]], ["1", state[10:]]]
    monkeypatch.setattr("ops.experiments.sql", query)
    assert backend.load({"endpoint": "branch.neon.tech"}, "world_test")["id"] == "world_test"
    digest = "wrong"
    with pytest.raises(ExperimentError, match="digest verification"):
        backend.load({"endpoint": "branch.neon.tech"}, "world_test")


def test_neon_preview_forks_the_configured_database_branch_not_the_project_default(monkeypatch):
    backend = NeonExperimentBackend({"DATABASE_URL": "postgres://user:secret@ep-source-pooler.neon.tech/db",
                                    "NEON_API_KEY": "key", "NEON_PROJECT_ID": "project"})
    calls = []
    def request(method, path, key, **kwargs):
        calls.append((method, path, kwargs))
        if path.endswith("/endpoints"):
            return {"endpoints": [{"host": "ep-default.neon.tech", "branch_id": "br-default"},
                                  {"host": "ep-source.neon.tech", "branch_id": "br-source"}]}
        return {"branch": {"id": "br-preview"}, "endpoints": [{"host": "ep-preview.neon.tech"}]}
    monkeypatch.setattr("ops.experiments.neon", request)
    resource = backend.create("preview_test", lsn="0/123")
    assert resource["parent_branch_id"] == "br-source"
    assert calls[-1][2]["json"]["branch"] == {"parent_id": "br-source", "name": "preview_test", "parent_lsn": "0/123"}
    assert httpx.URL(backend.connection(resource)).host == "ep-preview-pooler.neon.tech"
    backend.url = "postgres://user:secret@ep-other.neon.tech/db"
    with pytest.raises(ExperimentError, match="does not match"):
        backend.create("preview_mismatch", lsn="0/123")
    assert len([call for call in calls if call[0] == "POST"]) == 1


def test_api_uses_team_auth_same_origin_and_strict_request_contract(service, tmp_path):
    origin = "https://team.example.test"
    controller = Controller(tmp_path / "control", origin, password_hash("test-password"), "test-key", health=lambda: None)
    with TestClient(create_app(controller, service), base_url=origin, headers={"Origin": origin}) as client:
        assert client.get("/api/control/experiments/capabilities").json()["enabled"]
        assert client.post("/api/control/experiments/previews", json={"run_id": "world_test", "revision": 2}).status_code == 401
        assert client.post("/api/control/login", json={"password": "test-password"}).status_code == 200
        assert client.post("/api/control/experiments/previews", json={"run_id": "world_test", "revision": 2}, headers={"Origin": "https://other.test"}).status_code == 403
        preview = client.post("/api/control/experiments/previews", json={"run_id": "world_test", "revision": 2}).json()
        body = {"request_key": "request_api", "preview_id": preview["id"], "days": 7,
                "edit": {"scope": "agent", "target_id": "resident", "field": "health", "value": 20, "expected_value": 90}}
        assert client.post("/api/control/experiments", json={**body, "days": True}).status_code == 422
        response = client.post("/api/control/experiments", json=body)
        assert response.status_code == 202 and response.json()["mode"] == "RULE"
        assert client.post("/api/control/experiments", json=body).json()["id"] == response.json()["id"]
