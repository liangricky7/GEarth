"""Private team session and serialized controls for the one shared cloud world."""

import asyncio
import hashlib
import hmac
import json
import secrets
import subprocess
import time
from contextlib import asynccontextmanager
from collections import OrderedDict
from pathlib import Path
from urllib.request import urlopen

from dotenv import dotenv_values
from fastapi import FastAPI, HTTPException, Request, Response
from pydantic import BaseModel, Field, ConfigDict
from typing import Any, Literal


COOKIE = "__Host-gearth_team"
SESSION_SECONDS = 8 * 60 * 60


def password_hash(password, salt=None):
    salt = salt or secrets.token_hex(16)
    return salt + ":" + hashlib.pbkdf2_hmac("sha256", password.encode(), bytes.fromhex(salt), 300000).hex()


class Login(BaseModel):
    password: str = Field(min_length=1, max_length=256)


class PreviewRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    run_id: str = Field(min_length=1, max_length=100)
    revision: int = Field(ge=1, strict=True)


class EditRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    scope: Literal["agent", "country"]
    target_id: str = Field(min_length=1, max_length=128)
    field: str = Field(min_length=1, max_length=32)
    value: Any
    expected_value: Any


class ExperimentRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    request_key: str = Field(min_length=8, max_length=64)
    preview_id: str = Field(min_length=1, max_length=64)
    edit: EditRequest
    days: int = Field(ge=1, le=365, strict=True)


class Controller:
    def __init__(self, directory, origin, password_digest, session_key, command=None, health=None):
        if not origin.startswith("https://") or origin.endswith("/"):
            raise ValueError("Team controls require an exact HTTPS origin")
        self.directory = Path(directory)
        self.directory.mkdir(parents=True, exist_ok=True)
        self.origin = origin
        self.password_digest = password_digest
        self.session_key = session_key.encode()
        self.command = command or self.systemctl
        self.health = health or self.read_health
        self.lock = asyncio.Lock()
        self.operation = None
        self.task = None
        self.error = None
        self.attempts = OrderedDict()

    @staticmethod
    def systemctl(action):
        result = subprocess.run(["sudo", "-n", "/usr/bin/systemctl", action, "gearth.service"],
                                timeout=150, capture_output=True)
        if result.returncode:
            raise RuntimeError("Collector command failed")

    @staticmethod
    def read_health():
        try:
            with urlopen("http://127.0.0.1:5080/api/health", timeout=3) as response:
                return json.load(response)
        except (OSError, ValueError):
            return None

    def valid_password(self, password):
        return hmac.compare_digest(password_hash(password, self.password_digest.split(":")[0]), self.password_digest)

    def issue_session(self, now=None):
        value = f"{int(now if now is not None else time.time())}.{secrets.token_hex(16)}"
        return value + "." + hmac.new(self.session_key, value.encode(), hashlib.sha256).hexdigest()

    def authenticated(self, request):
        token = request.cookies.get(COOKIE, "")
        try:
            issued, nonce, signature = token.split(".")
            age = time.time() - int(issued)
            expected = hmac.new(self.session_key, f"{issued}.{nonce}".encode(), hashlib.sha256).hexdigest()
            return 0 <= age < SESSION_SECONDS and hmac.compare_digest(signature, expected)
        except (ValueError, TypeError):
            return False

    def check_attempt(self, address):
        now = time.monotonic()
        recent = [stamp for stamp in self.attempts.pop(address, []) if stamp > now - 600]
        self.attempts[address] = recent
        while len(self.attempts) > 1024:
            self.attempts.popitem(last=False)
        if len(recent) >= 10:
            raise HTTPException(429, "Too many sign-in attempts. Try again in ten minutes.")
        recent.append(now)

    async def status(self, request):
        health = await asyncio.to_thread(self.health)
        pause = (self.directory / "pause").exists()
        acknowledged = (self.directory / "paused").exists()
        state = "paused" if pause and acknowledged else "pausing" if pause else "running" if health and health.get("run_status") == "running" else "starting" if self.operation else "stopped"
        if self.error or health and health.get("error"):
            state = "error"
        return {"authenticated": self.authenticated(request), "state": state,
                "operation": self.operation, "error": self.error,
                "run_id": health.get("run_id") if health else None,
                "tick": health.get("tick") if health else None}

    async def execute(self, action):
        async with self.lock:
            try:
                if action == "reset":
                    # Keep admission paused until the old engine has saved and exited.
                    (self.directory / "pause").touch(mode=0o600)
                    await asyncio.to_thread(self.command, "stop")
                if action == "stop":
                    (self.directory / "pause").touch(mode=0o600)
                else:
                    (self.directory / "pause").unlink(missing_ok=True)
                    (self.directory / "paused").unlink(missing_ok=True)
                    await asyncio.to_thread(self.command, "start")
                (self.directory / "last-action.json").write_text(json.dumps({"action": action, "at": time.time()}))
                self.error = None
            except Exception:
                self.error = "The operation failed. The saved runs are retained; check the VM service logs."
            finally:
                self.operation = None

    def begin(self, action):
        if self.operation is not None:
            raise HTTPException(409, "Another team operation is still finishing.")
        self.operation = action
        self.error = None
        self.task = asyncio.create_task(self.execute(action))


def create_app(controller, experiments=None):
    @asynccontextmanager
    async def lifespan(app):
        if experiments:
            await asyncio.to_thread(experiments.start)
        try:
            yield
        finally:
            if experiments:
                await asyncio.to_thread(experiments.close)

    app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None, lifespan=lifespan)

    if experiments:
        from ops.experiments import ExperimentError

        @app.exception_handler(ExperimentError)
        async def experiment_error(request, error):
            return Response(content=json.dumps({"detail": str(error)}), status_code=error.status, media_type="application/json")

    def experiment_service(request, authenticated=False):
        if authenticated and not controller.authenticated(request):
            raise HTTPException(401, "Unlock team controls first.")
        if not experiments:
            raise HTTPException(503, "Timeline experiments are not enabled on this server.")
        return experiments

    @app.get("/api/control/experiments/capabilities")
    async def experiment_capabilities():
        return {"enabled": experiments is not None, "mode": "RULE", "max_days": 365,
                "checkpoint_note": "Weekly checkpoints within Neon's history window; previews pin the starting state."}

    @app.get("/api/control/experiments/runs/{run_id}/checkpoints")
    async def checkpoints(run_id: str, request: Request):
        service = experiment_service(request, True)
        return {"checkpoints": await asyncio.to_thread(service.checkpoints, run_id)}

    @app.post("/api/control/experiments/previews", status_code=201)
    async def preview(body: PreviewRequest, request: Request):
        return await asyncio.to_thread(experiment_service(request, True).preview, body.run_id, body.revision)

    @app.get("/api/control/experiments/previews/{preview_id}/targets")
    async def targets(preview_id: str, request: Request, scope: Literal["agent", "country"] = "agent",
                      q: str = "", offset: int = 0):
        if len(q) > 100 or not 0 <= offset <= 100000:
            raise HTTPException(400, "Invalid target search.")
        return await asyncio.to_thread(experiment_service(request, True).targets, preview_id, scope, q, offset)

    @app.post("/api/control/experiments", status_code=202)
    async def submit_experiment(body: ExperimentRequest, request: Request):
        service = experiment_service(request, True)
        return await asyncio.to_thread(service.submit, body.request_key, body.preview_id, body.edit.model_dump(), body.days)

    @app.get("/api/control/experiments")
    async def list_experiments(request: Request):
        return {"experiments": await asyncio.to_thread(experiment_service(request).list)}

    @app.get("/api/control/experiments/{experiment_id}/comparison")
    async def comparison(experiment_id: str, request: Request):
        return await asyncio.to_thread(experiment_service(request).comparison, experiment_id)

    @app.post("/api/control/experiments/{experiment_id}/cancel")
    async def cancel_experiment(experiment_id: str, request: Request):
        return await asyncio.to_thread(experiment_service(request, True).cancel, experiment_id)

    @app.get("/api/control/experiments/{experiment_id}")
    async def experiment(experiment_id: str, request: Request):
        return await asyncio.to_thread(experiment_service(request).get, experiment_id)

    @app.middleware("http")
    async def same_origin(request, call_next):
        if request.method == "POST" and (request.headers.get("origin") != controller.origin
                or request.headers.get("content-type", "").split(";")[0] != "application/json"):
            return Response(status_code=403, content='{"detail":"Use the HTTPS team website for controls."}', media_type="application/json")
        response = await call_next(request)
        response.headers["Cache-Control"] = "no-store"
        return response

    @app.get("/api/control/status")
    async def status(request: Request):
        return await controller.status(request)

    @app.post("/api/control/login")
    async def login(body: Login, request: Request, response: Response):
        address = request.headers.get("x-real-ip") or request.client.host
        controller.check_attempt(address)
        if not await asyncio.to_thread(controller.valid_password, body.password):
            raise HTTPException(401, "Incorrect team password.")
        response.set_cookie(COOKIE, controller.issue_session(), max_age=SESSION_SECONDS,
                            secure=True, httponly=True, samesite="strict", path="/")
        return {"authenticated": True}

    @app.post("/api/control/logout")
    async def logout(response: Response):
        response.delete_cookie(COOKIE, path="/", secure=True, httponly=True, samesite="strict")
        return {"authenticated": False}

    @app.post("/api/control/{action}", status_code=202)
    async def action(action: str, request: Request):
        if action not in {"start", "stop", "reset"}:
            raise HTTPException(404, "Unknown control.")
        if not controller.authenticated(request):
            raise HTTPException(401, "Unlock team controls first.")
        controller.begin(action)
        return {"operation": action}

    return app


def main():
    import uvicorn
    config = dotenv_values("/var/lib/gearth/control.env", interpolate=False)
    controller = Controller("/var/lib/gearth/control", config["CONTROL_ORIGIN"],
                            config["CONTROL_PASSWORD_HASH"], config["CONTROL_SESSION_KEY"])
    experiments = None
    private = {**dotenv_values(".env.local", interpolate=False), **config}
    if private.get("GEARTH_EXPERIMENTS_ENABLED", "false").lower() == "true":
        from ops.experiments import ExperimentService, NeonExperimentBackend

        if not all(private.get(key) for key in ("DATABASE_URL", "NEON_API_KEY", "NEON_PROJECT_ID")):
            raise RuntimeError("Timeline experiments require server-side Neon configuration")
        experiments = ExperimentService("/var/lib/gearth/experiments", NeonExperimentBackend(private))
    uvicorn.run(create_app(controller, experiments), host="127.0.0.1", port=5090, access_log=False)


if __name__ == "__main__":
    main()
