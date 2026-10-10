import asyncio
from datetime import datetime, timedelta, timezone

import httpx
import pytest

from agents import worker
from agents.providers import ModelProvider, ProviderPool, load_providers


def provider(name, capacity=1):
    return ModelProvider(
        name, f"model-{name}", f"https://{name}.example/v1", capacity, f"secret-{name}"
    )


def test_round_robin_and_independent_capacity():
    pool = ProviderPool([provider("spark", 2), provider("amd")])
    assert [pool.acquire(0).name for _ in range(3)] == ["spark", "amd", "spark"]
    assert pool.acquire(0) is None
    pool.release(pool.providers[0], 1)
    assert pool.acquire(1).name == "spark"
    assert pool.active == {"spark": 2, "amd": 1}


def test_unavailable_backend_recovers_after_cooldown():
    pool = ProviderPool([provider("amd"), provider("spark")])
    failed = pool.acquire(0)
    pool.release(failed, 1, failed=True, unavailable=True)
    healthy = pool.acquire(2)
    assert healthy.name == "spark"
    assert pool.acquire(30) is None
    assert pool.acquire(31).name == "amd"
    assert pool.failures["amd"] == 1
    assert "secret" not in str(pool.snapshot(2))


@pytest.mark.parametrize(
    "url",
    [
        "http://remote.example/v1",
        "https://user:secret@remote.example/v1",
        "https://remote.example/v1?key=secret",
        "",
        "ftp://remote.example",
    ],
)
def test_remote_configuration_rejects_unsafe_urls(url):
    with pytest.raises(ValueError):
        load_providers(
            {
                "SPARK_A_ENABLED": "false",
                "AMD_ENABLED": "true",
                "AMD_BASE_URL": url,
                "AMD_API_KEY": "key",
            }
        )


def test_amd_configuration_requires_credentials_and_honors_limit():
    environment = {
        "SPARK_A_ENABLED": "false",
        "AMD_ENABLED": "true",
        "AMD_BASE_URL": "https://amd.example/v1",
    }
    with pytest.raises(ValueError, match="AMD_API_KEY"):
        load_providers(environment)
    providers = load_providers(
        {**environment, "AMD_API_KEY": "private", "AMD_CAPACITY": "3"}, capacity=2
    )
    assert providers[0].capacity == 2
    assert "private" not in repr(providers[0])


def test_three_backends_receive_matching_models_and_keys(monkeypatch):
    names = ["spark_a", "spark_b", "amd"]
    pool = ProviderPool([provider(name) for name in names])
    monkeypatch.setattr(worker, "provider_pool", pool)
    monkeypatch.setattr(worker, "TOKEN", "worker-secret")
    calls = []

    def model_response(request):
        import json

        calls.append(
            (
                request.url.host,
                request.headers["authorization"],
                json.loads(request.content),
            )
        )
        return httpx.Response(
            200,
            json={
                "choices": [{"message": {"content": '{"choice":0,"reason":"Wait."}'}}]
            },
        )

    async def request():
        async with httpx.AsyncClient(
            transport=httpx.MockTransport(model_response)
        ) as model_client:
            monkeypatch.setattr(
                worker.app.state, "model_client", model_client, raising=False
            )
            async with httpx.AsyncClient(
                transport=httpx.ASGITransport(app=worker.app), base_url="http://test"
            ) as client:
                for name in names:
                    job = {
                        "request_id": name,
                        "run_id": "probe",
                        "agent_id": name,
                        "observation_tick": 0,
                        "deadline": (
                            datetime.now(timezone.utc) + timedelta(seconds=30)
                        ).isoformat(),
                        "dependencies": {},
                        "evidence_ids": [],
                        "observation": {"world_stage": 2},
                        "candidates": [{"action_type": "hold", "params": {}}],
                    }
                    result = await client.post(
                        "/decide",
                        json=job,
                        headers={"Authorization": "Bearer worker-secret"},
                    )
                    assert result.json()["backend"] == name
                    assert result.json()["model"] == f"model-{name}"
                    assert result.json()["source"] == "model"
        assert worker.active_requests == 0
        assert pool.active == {name: 0 for name in names}
        assert pool.completed == {name: 1 for name in names}

    asyncio.run(request())
    for name, (host, authorization, payload) in zip(names, calls):
        assert host == f"{name}.example"
        assert authorization == f"Bearer secret-{name}"
        assert payload["model"] == f"model-{name}"
        assert "reasoning_effort" not in payload


def test_cancelled_model_request_releases_both_limits(monkeypatch):
    pool = ProviderPool([provider("amd")])
    monkeypatch.setattr(worker, "provider_pool", pool)
    monkeypatch.setattr(worker, "TOKEN", "worker-secret")

    async def request():
        started = asyncio.Event()

        async def delayed(request):
            started.set()
            await asyncio.Event().wait()

        async with httpx.AsyncClient(
            transport=httpx.MockTransport(delayed)
        ) as model_client:
            monkeypatch.setattr(
                worker.app.state, "model_client", model_client, raising=False
            )
            job = worker.DecisionJob(
                request_id="cancel",
                run_id="probe",
                agent_id="resident",
                observation_tick=0,
                deadline=datetime.now(timezone.utc) + timedelta(seconds=30),
                dependencies={},
                evidence_ids=[],
                observation={},
                candidates=[],
            )
            task = asyncio.create_task(worker.decide(job, "Bearer worker-secret"))
            await started.wait()
            task.cancel()
            with pytest.raises(asyncio.CancelledError):
                await task
        assert worker.active_requests == 0
        assert pool.active["amd"] == 0
        assert pool.completed["amd"] == 0

    asyncio.run(request())
