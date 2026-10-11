import asyncio
from collections import Counter
from datetime import datetime, timedelta, timezone

import httpx
import pytest

from agents import worker
from agents.providers import ModelProvider, ProviderPool, load_providers


def provider(name, capacity=1):
    return ModelProvider(
        name, f"model-{name}", f"https://{name}.example/v1", capacity, f"secret-{name}"
    )


@pytest.mark.parametrize("stage", [1, 2])
@pytest.mark.parametrize(
    "model,settings",
    [
        (
            "nvidia/Qwen3.8-27B-NVFP4",
            {"chat_template_kwargs": {"enable_thinking": False}},
        ),
        (
            "Qwen/Qwen3-8B",
            {"chat_template_kwargs": {"enable_thinking": False}},
        ),
        ("openai/gpt-oss-120b", {"reasoning_effort": "low"}),
        ("other/model", {}),
    ],
)
def test_prompt_uses_backend_specific_thinking_settings(stage, model, settings):
    job = worker.DecisionJob(
        request_id="thinking-mode",
        run_id="probe",
        agent_id="resident",
        observation_tick=0,
        deadline=datetime.now(timezone.utc) + timedelta(seconds=30),
        dependencies={},
        evidence_ids=[],
        observation={"world_stage": stage},
        candidates=[{"action_type": "hold", "params": {}}],
    )
    payload = worker.build_prompt(job, model)
    assert payload["model"] == model
    actual = {
        key: payload[key]
        for key in ("chat_template_kwargs", "reasoning_effort")
        if key in payload
    }
    assert actual == settings
    assert payload["response_format"] == {"type": "json_object"}
    assert payload["max_tokens"] == 512


def test_round_robin_and_independent_capacity():
    pool = ProviderPool([provider("spark", 2), provider("amd")])
    assert [pool.acquire(0).name for _ in range(3)] == ["spark", "amd", "spark"]
    assert pool.acquire(0) is None
    pool.release(pool.providers[0], 1)
    assert pool.acquire(1).name == "spark"
    assert pool.active == {"spark": 2, "amd": 1}


def deployed_topology():
    return [
        ModelProvider("spark_a", "8B", "https://a/v1", 16, "key", "institution"),
        ModelProvider("spark_b", "8B", "https://b/v1", 16, "key", "institution"),
        ModelProvider("spark_a_citizen", "4B", "https://ac/v1", 32, "key", "citizen"),
        ModelProvider("spark_b_citizen", "4B", "https://bc/v1", 32, "key", "citizen"),
        ModelProvider("amd", "4B", "https://amd/v1", 1, "key", "citizen"),
    ]


@pytest.mark.parametrize("groups", [("institution", "citizen"), ("citizen", "institution")])
def test_interleaved_roles_do_not_starve_other_compute_sources(groups):
    pool = ProviderPool(deployed_topology())
    assigned = Counter()
    for now in range(90):
        for group in groups:
            selected = pool.acquire(now, group)
            assert selected.serves(group)
            assigned[selected.name] += 1
            pool.release(selected, now)
    assert assigned == {"spark_a": 45, "spark_b": 45,
                        "spark_a_citizen": 30, "spark_b_citizen": 30, "amd": 30}


def test_full_load_balances_configured_capacity_and_reuses_freed_slots():
    pool = ProviderPool(deployed_topology())
    for _ in range(32):
        assert pool.acquire(0, "institution") is not None
    for _ in range(65):
        assert pool.acquire(0, "citizen") is not None
    assert pool.active == {p.name: p.capacity for p in pool.providers}
    assert pool.active["spark_a"] + pool.active["spark_a_citizen"] == 48
    assert pool.active["spark_b"] + pool.active["spark_b_citizen"] == 48
    assert pool.active["amd"] == 1
    assert pool.acquire(0, "institution") is None
    assert pool.acquire(0, "citizen") is None
    # Faster providers may finish more work; their idle slots must immediately
    # be usable without exceeding a slower provider's limit or switching models.
    for selected in pool.providers:
        pool.release(selected, 1)
        assert pool.acquire(1, selected.decision_group) == selected
    assert pool.active == {p.name: p.capacity for p in pool.providers}


def test_role_routing_does_not_spill_across_models_or_cooldowns():
    institution = ModelProvider(
        "leaders", "Qwen/Qwen3-8B", "https://leaders/v1", 1, "key", "institution"
    )
    citizen = ModelProvider(
        "residents", "Qwen/Qwen3-4B", "https://residents/v1", 1, "key", "citizen"
    )
    pool = ProviderPool([institution, citizen])
    assert pool.acquire(0, "citizen") == citizen
    assert pool.acquire(0, "citizen") is None
    assert pool.acquire(0, "institution") == institution
    pool.release(citizen, 1, failed=True, unavailable=True)
    pool.release(institution, 1)
    assert pool.acquire(2, "citizen") is None
    assert pool.acquire(2, "institution") == institution
    assert pool.acquire(31, "citizen") == citizen


@pytest.mark.parametrize("group", ["citizen", "institution"])
def test_unrestricted_provider_preserves_legacy_routing(group):
    pool = ProviderPool([provider("legacy")])
    assert pool.acquire(0, group).name == "legacy"


def test_dedicated_provider_preserves_shared_capacity_for_other_group():
    shared = ModelProvider("shared", "legacy", "https://shared/v1", 1, "key")
    citizen = ModelProvider(
        "citizen", "Qwen/Qwen3-4B", "https://citizen/v1", 2, "key", "citizen"
    )
    pool = ProviderPool([shared, citizen])
    assert pool.acquire(0, "citizen") == citizen
    assert pool.acquire(0, "institution") == shared
    assert pool.acquire(0, "citizen") == citizen
    assert pool.acquire(0, "institution") is None


def test_citizen_provider_requires_explicit_enablement_and_credentials():
    assert [item.name for item in load_providers({})] == ["spark_a"]
    config = {"SPARK_A_CITIZEN_ENABLED": "true"}
    with pytest.raises(ValueError, match="SPARK_A_CITIZEN_API_KEY"):
        load_providers(config)
    providers = load_providers({**config, "SPARK_A_CITIZEN_API_KEY": "private"})
    assert providers[1].decision_group == "citizen"
    assert providers[1].model == "Qwen/Qwen3-4B"


def test_invalid_decision_group_is_rejected():
    with pytest.raises(ValueError, match="SPARK_A_DECISION_GROUP"):
        load_providers({"SPARK_A_DECISION_GROUP": "government"})


@pytest.mark.parametrize(
    "observation,group",
    [
        (
            {"world_stage": 2, "self": {"role": "citizen", "occupation": "doctor"}},
            "citizen",
        ),
        ({"world_stage": 2, "self": {"role": "government"}}, "institution"),
        ({"agent": {"role": "citizen"}}, "citizen"),
        ({"agent": {"role": "business"}}, "institution"),
        ({"role": "resident"}, "citizen"),
        ({}, "all"),
        ({"self": "invalid"}, "all"),
    ],
)
def test_role_comes_from_observation_not_occupation_or_agent_name(observation, group):
    job = worker.DecisionJob(
        request_id="role",
        run_id="probe",
        agent_id="government_1",
        observation_tick=0,
        deadline=datetime.now(timezone.utc) + timedelta(seconds=30),
        dependencies={},
        evidence_ids=[],
        observation=observation,
        candidates=[],
    )
    assert worker.decision_group(job) == group


@pytest.mark.parametrize(
    "role,model",
    [
        ("citizen", "Qwen/Qwen3-4B"),
        ("government", "Qwen/Qwen3-8B"),
        ("business", "Qwen/Qwen3-8B"),
    ],
)
def test_worker_routes_roles_to_their_assigned_model(monkeypatch, role, model):
    pool = ProviderPool(
        [
            ModelProvider(
                "leaders",
                "Qwen/Qwen3-8B",
                "https://leaders/v1",
                1,
                "key",
                "institution",
            ),
            ModelProvider(
                "residents",
                "Qwen/Qwen3-4B",
                "https://residents/v1",
                1,
                "key",
                "citizen",
            ),
        ]
    )
    monkeypatch.setattr(worker, "provider_pool", pool)
    monkeypatch.setattr(worker, "TOKEN", "worker-secret")
    calls = []

    def model_response(request):
        import json

        payload = json.loads(request.content)
        calls.append(payload)
        assert payload["model"] == model
        assert payload["chat_template_kwargs"] == {"enable_thinking": False}
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
            job = worker.DecisionJob(
                request_id="routed",
                run_id="probe",
                agent_id="resident",
                observation_tick=0,
                deadline=datetime.now(timezone.utc) + timedelta(seconds=30),
                dependencies={},
                evidence_ids=[],
                observation={"world_stage": 2, "self": {"role": role}},
                candidates=[{"action_type": "hold", "params": {}}],
            )
            result = await worker.decide(job, "Bearer worker-secret")
            assert result["model"] == model
            assert result["source"] == "model"
            assert sum(pool.active.values()) == 0

    asyncio.run(request())
    assert len(calls) == 1


@pytest.mark.parametrize(
    "observation,status", [({}, 422), ({"self": {"role": "government"}}, 503)]
)
def test_scoped_pool_rejects_missing_roles_or_unconfigured_groups(
    monkeypatch, observation, status
):
    from fastapi import HTTPException

    pool = ProviderPool(
        [
            ModelProvider(
                "residents",
                "Qwen/Qwen3-4B",
                "https://residents/v1",
                1,
                "key",
                "citizen",
            )
        ]
    )
    monkeypatch.setattr(worker, "provider_pool", pool)
    monkeypatch.setattr(worker, "TOKEN", "worker-secret")
    job = worker.DecisionJob(
        request_id="unroutable",
        run_id="probe",
        agent_id="resident",
        observation_tick=0,
        deadline=datetime.now(timezone.utc) + timedelta(seconds=30),
        dependencies={},
        evidence_ids=[],
        observation=observation,
        candidates=[],
    )
    with pytest.raises(HTTPException) as error:
        asyncio.run(worker.decide(job, "Bearer worker-secret"))
    assert error.value.status_code == status
    assert sum(pool.active.values()) == 0


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


@pytest.mark.parametrize(
    "url,pinned_host,allowed",
    [
        ("http://100.119.198.81:18002/v1", "100.119.198.81", True),
        ("http://100.119.198.81:18002/v1", "", False),
        ("http://100.119.198.82:18002/v1", "100.119.198.81", False),
        ("http://192.168.1.10:18002/v1", "192.168.1.10", False),
        ("http://public.example/v1", "public.example", False),
        ("http://100.119.198.81:18002/v1?key=private", "100.119.198.81", False),
        ("http://user:private@100.119.198.81:18002/v1", "100.119.198.81", False),
    ],
)
def test_tailscale_http_requires_exact_numeric_host_pin(url, pinned_host, allowed):
    environment = {
        "SPARK_A_ENABLED": "false",
        "SPARK_B_ENABLED": "true",
        "SPARK_B_BASE_URL": url,
        "SPARK_B_TAILSCALE_HTTP_HOST": pinned_host,
        "SPARK_B_API_KEY": "private",
    }
    if allowed:
        assert load_providers(environment)[0].base_url == url
    else:
        with pytest.raises(ValueError):
            load_providers(environment)


def test_remote_first_spark_requires_its_own_api_key():
    with pytest.raises(ValueError, match="SPARK_A_API_KEY"):
        load_providers({"SPARK_A_BASE_URL": "https://spark.example/v1"})
