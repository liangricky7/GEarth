import asyncio
import time
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from ops.gcp.control_server import COOKIE, Controller, create_app, password_hash
from ops.gcp.control_patch import pause_hooks, control_panel

ORIGIN = "https://team.example.test"


@pytest.fixture
def control(tmp_path):
    commands = []
    controller = Controller(tmp_path, ORIGIN, password_hash("test-team-password"), "test-session-signing-key",
                            command=commands.append, health=lambda: {"run_id": "world_old", "tick": 15, "run_status": "running"})
    with TestClient(create_app(controller), base_url=ORIGIN, headers={"Origin": ORIGIN}) as client:
        yield controller, client, commands


def login(client):
    response = client.post("/api/control/login", json={"password": "test-team-password"})
    assert response.status_code == 200
    cookie = response.headers["set-cookie"]
    assert "HttpOnly" in cookie and "Secure" in cookie and "SameSite=strict" in cookie
    assert "test-team-password" not in cookie


def finished(client):
    for _ in range(100):
        response = client.get("/api/control/status").json()
        if response["operation"] is None:
            return response
        time.sleep(.01)
    raise AssertionError("Operation did not finish")


def test_only_authenticated_same_origin_requests_can_change_shared_world(control):
    controller, client, commands = control
    assert client.post("/api/control/reset", json={}).status_code == 401
    assert client.post("/api/control/login", json={"password": "wrong"}).status_code == 401
    login(client)
    assert client.post("/api/control/reset", json={}, headers={"Origin": "https://other.example"}).status_code == 403
    assert client.post("/api/control/reset", content="{}").status_code == 403
    assert not commands and not (controller.directory / "pause").exists()
    client.post("/api/control/logout", json={})
    assert client.post("/api/control/start", json={}).status_code == 401


def test_stop_requests_drained_pause_and_start_resumes_same_world(control):
    controller, client, commands = control
    login(client)
    assert client.post("/api/control/stop", json={}).status_code == 202
    assert finished(client)["state"] == "pausing"
    (controller.directory / "paused").touch()
    assert client.get("/api/control/status").json()["state"] == "paused"
    assert not commands  # Stop does not kill the engine or its readable API.
    assert client.post("/api/control/start", json={}).status_code == 202
    result = finished(client)
    assert result["state"] == "running" and result["run_id"] == "world_old"
    assert commands == ["start"] and not (controller.directory / "pause").exists()


def test_reset_stops_and_saves_before_launching_and_keeps_prior_data(control):
    controller, client, commands = control
    saved = controller.directory / "history.json"
    saved.write_text("prior-run")
    phases = []
    def command(action):
        phases.append((action, (controller.directory / "pause").exists()))
    controller.command = command
    login(client)
    assert client.post("/api/control/reset", json={}).status_code == 202
    finished(client)
    assert phases == [("stop", True), ("start", False)]
    assert saved.read_text() == "prior-run"


def test_control_operations_are_serialized_and_failure_is_visible(control):
    controller, client, commands = control
    login(client)
    controller.operation = "reset"
    assert client.post("/api/control/start", json={}).status_code == 409
    controller.operation = None
    def failure(action):
        raise RuntimeError("private remote response")
    controller.command = failure
    client.post("/api/control/start", json={})
    result = finished(client)
    assert result["error"] and "private remote response" not in result["error"]


def test_expired_and_tampered_sessions_are_rejected_and_login_is_rate_limited(control):
    controller, client, commands = control
    client.cookies.set(COOKIE, controller.issue_session(time.time()-9*3600))
    assert client.post("/api/control/start", json={}).status_code == 401
    client.cookies.set(COOKIE, controller.issue_session()+"changed")
    assert client.post("/api/control/start", json={}).status_code == 401
    for _ in range(10):
        assert client.post("/api/control/login", json={"password": "wrong"}).status_code == 401
    assert client.post("/api/control/login", json={"password": "test-team-password"}).status_code == 429


def test_control_hooks_are_idempotent_and_refuse_unrecognized_engine_layouts():
    root = Path(__file__).resolve().parents[2]
    coordinator = (root / "backend/Faultline.Api/Coordinator.cs").read_text()
    assert pause_hooks(coordinator) == coordinator
    html = (root / "backend/Faultline.Api/wwwroot/index.html").read_text()
    assert control_panel(html) == html
    with pytest.raises(ValueError):
        pause_hooks("unrecognized collecting engine")
    with pytest.raises(ValueError):
        control_panel("unrecognized observer")
