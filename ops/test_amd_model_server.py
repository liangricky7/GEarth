import asyncio
import threading

import httpx
import pytest

from ops import amd_model_server as server


@pytest.fixture(autouse=True)
def isolated_gpu_state(monkeypatch):
    monkeypatch.setitem(server.state, "active", False)
    monkeypatch.setitem(server.state, "active_models", set())
    monkeypatch.setitem(server.state, "models", {})
    monkeypatch.setattr(server, "DUAL_MODELS", False)


def test_dual_model_catalog_and_health_report_each_loaded_model(monkeypatch):
    monkeypatch.setitem(server.state, "key", "private-key")
    monkeypatch.setitem(
        server.state,
        "models",
        {"Qwen/Qwen3-8B": {"revision": "8"}, "Qwen/Qwen3-4B": {"revision": "4"}},
    )
    monkeypatch.setattr(server, "DUAL_MODELS", True)

    async def request():
        async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=server.app), base_url="http://test"
        ) as client:
            response = await client.get(
                "/v1/models", headers={"Authorization": "Bearer private-key"}
            )
            assert [entry["id"] for entry in response.json()["data"]] == [
                "Qwen/Qwen3-8B",
                "Qwen/Qwen3-4B",
            ]
            assert server.health()["capacity"] == 2
            assert [entry["revision"] for entry in server.health()["models"]] == [
                "8",
                "4",
            ]

    asyncio.run(request())


def test_dual_models_reserve_independent_slots_through_client_cancellation(monkeypatch):
    models = ["Qwen/Qwen3-8B", "Qwen/Qwen3-4B"]
    monkeypatch.setitem(server.state, "models", {model: {} for model in models})
    monkeypatch.setattr(server, "DUAL_MODELS", True)
    started = {model: threading.Event() for model in models}
    release = {model: threading.Event() for model in models}

    def delayed(request):
        started[request.model].set()
        release[request.model].wait(5)
        return {"model": request.model}

    monkeypatch.setattr(server, "generate", delayed)

    async def request():
        payloads = [
            server.CompletionRequest(
                model=model, messages=[{"role": "user", "content": "hi"}]
            )
            for model in models
        ]
        tasks = [
            asyncio.create_task(server.completions(payload)) for payload in payloads
        ]
        while not all(event.is_set() for event in started.values()):
            await asyncio.sleep(0.001)
        assert server.health()["active_requests"] == 2
        with pytest.raises(server.HTTPException) as error:
            await server.completions(payloads[0])
        assert error.value.status_code == 429
        tasks[0].cancel()
        with pytest.raises(asyncio.CancelledError):
            await tasks[0]
        assert server.health()["active_requests"] == 2
        release[models[0]].set()
        for _ in range(100):
            if models[0] not in server.state["active_models"]:
                break
            await asyncio.sleep(0.001)
        assert server.state["active_models"] == {models[1]}
        release[models[1]].set()
        assert (await tasks[1])["model"] == models[1]
        assert not server.state["active"]

    try:
        asyncio.run(request())
    finally:
        for event in release.values():
            event.set()


def test_dual_loader_uses_pinned_revisions_without_replacing_rocm_torch(monkeypatch):
    import json
    import sys
    from types import SimpleNamespace

    calls = []

    class Tokenizer:
        @staticmethod
        def from_pretrained(model, **settings):
            calls.append(("tokenizer", model, settings))
            return object()

    class Model:
        @staticmethod
        def from_pretrained(model, **settings):
            calls.append(("model", model, settings))
            return SimpleNamespace(eval=lambda: object())

    torch = SimpleNamespace(
        bfloat16="bf16",
        version=SimpleNamespace(hip="rocm"),
        cuda=SimpleNamespace(
            is_available=lambda: True,
            mem_get_info=lambda: (32 * 1024**3, 48 * 1024**3),
            get_device_name=lambda index: "AMD",
        ),
    )
    monkeypatch.setitem(sys.modules, "torch", torch)
    monkeypatch.setitem(
        sys.modules,
        "transformers",
        SimpleNamespace(AutoTokenizer=Tokenizer, AutoModelForCausalLM=Model),
    )
    monkeypatch.setattr(server, "DUAL_MODELS", True)
    server.load_model()
    manifest = json.loads(server.MODEL_MANIFEST.read_text())
    assert set(server.model_ids()) == {entry["model"] for entry in manifest.values()}
    for entry in manifest.values():
        matching = [settings for _, model, settings in calls if model == entry["model"]]
        assert all(settings["revision"] == entry["revision"] for settings in matching)
        assert matching[1]["torch_dtype"] == "bf16"


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
