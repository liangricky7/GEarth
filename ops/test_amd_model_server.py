import asyncio
import threading

import httpx
import pytest

from ops import amd_model_server as server


def test_cloud_routes_require_authentication(monkeypatch):
    monkeypatch.setitem(server.state, "key", "private-key")

    async def request():
        async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=server.app), base_url="http://test"
        ) as client:
            for path in ["/health", "/v1/models"]:
                assert (await client.get(path)).status_code == 401
                assert (
                    await client.get(
                        path, headers={"Authorization": "Bearer private-key"}
                    )
                ).status_code == 200
            result = await client.post(
                "/v1/chat/completions",
                json={
                    "model": server.MODEL,
                    "messages": [{"role": "user", "content": "hi"}],
                },
            )
            assert result.status_code == 401

    asyncio.run(request())


def test_disconnected_request_keeps_gpu_reserved_until_generation_finishes(monkeypatch):
    started = threading.Event()
    release = threading.Event()
    monkeypatch.setitem(server.state, "active", False)

    def delayed(request):
        started.set()
        release.wait(5)
        return {"choices": []}

    monkeypatch.setattr(server, "generate", delayed)

    async def request():
        payload = server.CompletionRequest(
            model=server.MODEL, messages=[{"role": "user", "content": "hi"}]
        )
        task = asyncio.create_task(server.completions(payload))
        while not started.is_set():
            await asyncio.sleep(0.001)
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task
        assert server.state["active"]
        with pytest.raises(server.HTTPException) as exception:
            await server.completions(payload)
        assert exception.value.status_code == 429
        release.set()
        for _ in range(100):
            if not server.state["active"]:
                break
            await asyncio.sleep(0.001)
        assert not server.state["active"]

    try:
        asyncio.run(request())
    finally:
        release.set()


def test_failed_generation_releases_gpu(monkeypatch):
    monkeypatch.setitem(server.state, "active", False)

    def fail(request):
        raise RuntimeError("GPU error")

    monkeypatch.setattr(server, "generate", fail)

    async def request():
        payload = server.CompletionRequest(
            model=server.MODEL, messages=[{"role": "user", "content": "hi"}]
        )
        with pytest.raises(RuntimeError, match="GPU error"):
            await server.completions(payload)
        assert not server.state["active"]

    asyncio.run(request())
