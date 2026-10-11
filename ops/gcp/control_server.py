"""Private team session and serialized controls for the one shared cloud world."""

import asyncio
import hashlib
import hmac
import json
import secrets
import subprocess
import time
from collections import OrderedDict
from pathlib import Path
from urllib.request import urlopen

from dotenv import dotenv_values
from fastapi import FastAPI, HTTPException, Request, Response
from pydantic import BaseModel, Field

COOKIE = "__Host-faultline_team"
SESSION_SECONDS = 8 * 60 * 60


def password_hash(password, salt=None):
    salt = salt or secrets.token_hex(16)
    return salt + ":" + hashlib.pbkdf2_hmac("sha256", password.encode(), bytes.fromhex(salt), 300000).hex()


class Login(BaseModel):
    password: str = Field(min_length=1, max_length=256)


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
        result = subprocess.run(["sudo", "-n", "/usr/bin/systemctl", action, "faultline.service"],
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


def create_app(controller):
    app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)

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
    config = dotenv_values("/var/lib/faultline/control.env", interpolate=False)
    controller = Controller("/var/lib/faultline/control", config["CONTROL_ORIGIN"],
                            config["CONTROL_PASSWORD_HASH"], config["CONTROL_SESSION_KEY"])
    uvicorn.run(create_app(controller), host="127.0.0.1", port=5090, access_log=False)


if __name__ == "__main__":
    main()
