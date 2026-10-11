import asyncio
import threading
import time
from concurrent.futures import ThreadPoolExecutor

import pytest

from ops import amd_batch_server as server


def request(model="Qwen/Qwen3-4B", max_tokens=512):
    return server.base.CompletionRequest(
        model=model, max_tokens=max_tokens, messages=[{"role": "user", "content": "hi"}]
    )


def prepared(payload):
    return "hi", 100


def test_batches_requests_and_preserves_results():
    batches = []

    def generate(items):
        batches.append(len(items))
        return [{"tokens": item.request.max_tokens} for item in items]

    async def check():
        queue = server.BatchScheduler(server.Tuning(batch_size=4, capacity=4), generate, prepared)
        queue.runner = asyncio.create_task(queue.run())
        results = await asyncio.gather(*(queue.submit(request()) for _ in range(4)))
        assert results == [{"tokens": 512}] * 4
        assert batches == [4]
        assert queue.admitted == 0 and queue.completed == 4
        await queue.close()

    asyncio.run(check())


def test_token_budget_and_model_limits_split_batches():
    loop = asyncio.new_event_loop()
    try:
        queue = server.BatchScheduler(server.Tuning(batch_size=4, capacity=4, batch_tokens=8192))
        for model, tokens, output in [("a", 3500, 512), ("a", 3500, 512), ("a", 3500, 256), ("b", 100, 512)]:
            queue.pending.append(server.Pending(request(model, output), "hi", tokens, loop.create_future(), 0))
        assert len(queue.take_batch()) == 2
        assert len(queue.take_batch()) == 1
        assert len(queue.take_batch()) == 1
    finally:
        loop.close()


def test_cancellation_keeps_active_gpu_reserved_and_removes_queued_work():
    started, release = threading.Event(), threading.Event()

    def delayed(items):
        started.set()
        release.wait(5)
        return [{} for _ in items]

    async def check():
        queue = server.BatchScheduler(server.Tuning(batch_size=1, capacity=2, wait_ms=0), delayed, prepared)
        queue.runner = asyncio.create_task(queue.run())
        first = asyncio.create_task(queue.submit(request()))
        while not started.is_set():
            await asyncio.sleep(0.001)
        first.cancel()
        with pytest.raises(asyncio.CancelledError):
            await first
        assert queue.admitted == 1
        second = asyncio.create_task(queue.submit(request()))
        while not queue.pending:
            await asyncio.sleep(0.001)
        with pytest.raises(server.HTTPException) as error:
            await queue.submit(request())
        assert error.value.status_code == 429
        second.cancel()
        with pytest.raises(asyncio.CancelledError):
            await second
        assert queue.admitted == 1 and not queue.pending
        release.set()
        await queue.close()
        assert queue.admitted == 0

    try:
        asyncio.run(check())
    finally:
        release.set()


def test_failed_batch_releases_slots_and_next_batch_succeeds():
    calls = []

    def generate(items):
        calls.append(1)
        if len(calls) == 1:
            raise RuntimeError("device failure")
        return [{} for _ in items]

    async def check():
        queue = server.BatchScheduler(server.Tuning(batch_size=1, capacity=1), generate, prepared)
        queue.runner = asyncio.create_task(queue.run())
        with pytest.raises(server.HTTPException):
            await queue.submit(request())
        assert await queue.submit(request()) == {}
        assert queue.admitted == 0 and queue.failed == 1 and queue.completed == 1
        await queue.close()

    asyncio.run(check())


def test_oom_halves_batch_and_keeps_each_result(monkeypatch):
    import sys
    from types import SimpleNamespace
    calls = []
    monkeypatch.setitem(sys.modules, "torch", SimpleNamespace(cuda=SimpleNamespace(empty_cache=lambda: None)))

    def generate(items):
        calls.append(len(items))
        if len(items) > 2:
            raise RuntimeError("HIP out of memory")
        return [{"ok": True} for _ in items]

    async def check():
        queue = server.BatchScheduler(server.Tuning(batch_size=4, capacity=4), generate, prepared)
        queue.runner = asyncio.create_task(queue.run())
        assert await asyncio.gather(*(queue.submit(request()) for _ in range(4))) == [{"ok": True}] * 4
        assert calls == [4, 2, 2] and queue.tuning.batch_size == 2
        assert queue.oom_splits == 1 and queue.admitted == 0
        await queue.close()

    asyncio.run(check())


def test_individual_json_failure_does_not_poison_other_results():
    payload = request()
    payload.response_format = {"type": "json_object"}
    with pytest.raises(server.HTTPException) as error:
        server.response_for(payload, "bad json", 1, 2, True)
    assert error.value.status_code == 502
    assert server.response_for(payload, '{"choice":0}', 1, 2, True)["usage"]["total_tokens"] == 3


def test_parallel_prompt_preparation_does_not_mutate_tokenizer_concurrently(monkeypatch):
    active = 0
    peak = 0

    class Tokenizer:
        def apply_chat_template(self, *args, **kwargs):
            return "hi"

        def __call__(self, *args, **kwargs):
            nonlocal active, peak
            active += 1
            peak = max(peak, active)
            time.sleep(0.001)
            active -= 1
            return {"input_ids": [1, 2]}

    monkeypatch.setitem(server.base.state, "models", {server.base.MODEL: {"tokenizer": Tokenizer()}})
    with ThreadPoolExecutor(max_workers=64) as executor:
        results = list(executor.map(server.prepare, [request()] * 64))
    assert results == [("hi", 2)] * 64
    assert peak == 1


def test_batch_routes_reject_unauthenticated_access(monkeypatch):
    import httpx

    monkeypatch.setitem(server.base.state, "key", "private-key")

    async def check():
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=server.app), base_url="http://test") as client:
            assert (await client.get("/health")).status_code == 401
            assert (await client.get("/v1/models")).status_code == 401
            assert (await client.post("/admin/tuning", json=server.Tuning().model_dump())).status_code == 401
            assert (await client.post("/v1/chat/completions", json=request().model_dump())).status_code == 401

    asyncio.run(check())


def test_gpu_pacing_budgets_work_and_returns_identical_logits():
    now = [0.0]
    delays = []

    def sleep(seconds):
        delays.append(seconds)
        now[0] += seconds

    pacer = server.GpuPacer(90, lambda: None, read_busy=lambda: 90,
                           clock=lambda: now[0], sleep=sleep)
    scores = object()
    for _ in range(10):
        now[0] += 0.087
        assert pacer(None, scores) is scores
    assert now[0] == pytest.approx(1)
    assert sum(delays) == pytest.approx(0.13)
    assert pacer.high_samples == 0


def test_gpu_pacing_backs_off_above_95_and_handles_missing_sensor():
    now = [0.0]
    delays = []

    def sleep(seconds):
        delays.append(seconds)
        now[0] += seconds

    pacer = server.GpuPacer(90, lambda: None, read_busy=lambda: 99,
                           clock=lambda: now[0], sleep=sleep)
    now[0] = 0.01
    pacer(None, None)
    assert delays == [0.05] and pacer.high_samples == 1
    pacer.read_busy = lambda: None
    now[0] = 1
    pacer(None, None)
    assert pacer.high_samples == 1


def test_gpu_sensor_unavailable_is_explicit(monkeypatch, tmp_path):
    monkeypatch.setitem(server.base.state, "gpu_busy_file", None)
    monkeypatch.delenv("AMD_GPU_BUSY_FILE", raising=False)
    assert server.gpu_busy_percent() is None
    sensor = tmp_path / "busy"
    monkeypatch.setenv("AMD_GPU_BUSY_FILE", str(sensor))
    sensor.write_text("91\n")
    assert server.gpu_busy_percent() == 91
    sensor.write_text("invalid\n")
    assert server.gpu_busy_percent() is None


def test_utilization_tuning_rejects_targets_over_95():
    from pydantic import ValidationError
    with pytest.raises(ValidationError):
        server.Tuning(utilization_target_percent=100)
