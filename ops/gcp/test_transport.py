import asyncio
import httpx
import pytest

from agents import worker
from agents.providers import ProviderPool, load_providers
from agents.test_worker import make_job


@pytest.mark.parametrize("disconnects", [1, 2])
def test_relay_disconnect_retry_is_bounded_and_releases_provider(monkeypatch, disconnects):
    monkeypatch.setattr(worker, "TOKEN", "test-token")
    monkeypatch.setattr(worker, "CAPACITY", 1)
    monkeypatch.setattr(worker, "active_requests", 0)
    monkeypatch.setattr(worker, "provider_pool", ProviderPool(load_providers({}, capacity=1)))
    calls = []

    async def model_response(request):
        calls.append(request)
        assert worker.active_requests == 1
        assert sum(worker.provider_pool.active.values()) == 1
        if len(calls) <= disconnects:
            raise httpx.RemoteProtocolError("Relay closed connection", request=request)
        return httpx.Response(200, json={"choices": [{"message": {"content": '{"choice":0,"reason":"Wait."}'}}]})

    async def check():
        async with httpx.AsyncClient(transport=httpx.MockTransport(model_response)) as client:
            monkeypatch.setattr(worker.app.state, "model_client", client, raising=False)
            job = make_job()
            job.observation = {"world_stage": 2}
            result = await worker.decide(job, "Bearer test-token")
            assert result["source"] == ("model" if disconnects == 1 else "rule_fallback")
            assert result["error"] == (None if disconnects == 1 else "RemoteProtocolError")
            assert worker.active_requests == 0
            assert sum(worker.provider_pool.active.values()) == 0
            assert bool(worker.provider_pool.unavailable_until["spark_a"]) == (disconnects == 2)
            assert len(calls) == 2

    asyncio.run(check())


def test_retry_obeys_original_timeout(monkeypatch):
    monkeypatch.setattr(worker, "TOKEN", "test-token")
    monkeypatch.setattr(worker, "CAPACITY", 1)
    monkeypatch.setattr(worker, "TIMEOUT_SECONDS", 0.02)
    monkeypatch.setattr(worker, "active_requests", 0)
    monkeypatch.setattr(worker, "provider_pool", ProviderPool(load_providers({}, capacity=1)))
    calls = []
    cancelled = []

    async def model_response(request):
        calls.append(request)
        if len(calls) == 1:
            raise httpx.RemoteProtocolError("Relay closed connection", request=request)
        try:
            await asyncio.Future()
        finally:
            cancelled.append(True)

    async def check():
        async with httpx.AsyncClient(transport=httpx.MockTransport(model_response)) as client:
            monkeypatch.setattr(worker.app.state, "model_client", client, raising=False)
            job = make_job()
            job.observation = {"world_stage": 2}
            result = await asyncio.wait_for(worker.decide(job, "Bearer test-token"), timeout=1)
            assert result["source"] == "rule_fallback"
            assert result["error"] == "TimeoutError"
            assert cancelled == [True]
            assert len(calls) == 2
            assert worker.active_requests == 0
            assert sum(worker.provider_pool.active.values()) == 0

    asyncio.run(check())
