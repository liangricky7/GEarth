import asyncio
import json
from datetime import datetime, timedelta, timezone

import httpx
import jsonschema
import pytest

from agents import worker


def make_job():
    return worker.DecisionJob(
        request_id="request_1",
        run_id="test_run",
        agent_id="citizen_1",
        observation_tick=0,
        deadline=datetime.now(timezone.utc) + timedelta(seconds=15),
        dependencies={"citizen_1": 0},
        evidence_ids=["state:citizen_1:v0"],
        observation={"cash_minor": 100},
        candidates=[{"action_type": "hold", "params": {}}],
    )


def test_action_rejects_unknown_fields():
    action = {
        "action_type": "hold",
        "params": {},
        "reason": "Wait.",
        "evidence_ids": [],
        "cash": 999,
    }
    with pytest.raises(jsonschema.ValidationError):
        worker.validate_action(action, make_job())


def test_action_rejects_invented_evidence():
    action = {
        "action_type": "hold",
        "params": {},
        "reason": "Wait.",
        "evidence_ids": ["invented"],
    }
    with pytest.raises(ValueError, match="UNKNOWN_EVIDENCE"):
        worker.validate_action(action, make_job())


def test_action_rejects_unavailable_purchase():
    action = {
        "action_type": "purchase",
        "params": {"offer_id": "invented", "quantity": 2},
        "reason": "Buy.",
        "evidence_ids": [],
    }
    with pytest.raises(ValueError, match="UNAVAILABLE_ACTION"):
        worker.validate_action(action, make_job())


def test_unauthorized_request_never_reaches_model(monkeypatch):
    monkeypatch.setattr(worker, "TOKEN", "test-token")

    async def request():
        async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=worker.app), base_url="http://test"
        ) as client:
            response = await client.post(
                "/decide", json=make_job().model_dump(mode="json")
            )
            assert response.status_code == 401
            assert worker.active_requests == 0

    asyncio.run(request())


def test_saturated_worker_rejects_more_jobs(monkeypatch):
    monkeypatch.setattr(worker, "TOKEN", "test-token")
    monkeypatch.setattr(worker, "active_requests", 4)

    async def request():
        async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=worker.app), base_url="http://test"
        ) as client:
            response = await client.post(
                "/decide",
                json=make_job().model_dump(mode="json"),
                headers={"Authorization": "Bearer test-token"},
            )
            assert response.status_code == 429

    asyncio.run(request())


def test_world_choice_maps_to_exact_candidate():
    job = make_job()
    job.observation = {"world_stage": 2}
    job.candidates.append({"action_type": "work", "params": {"effort": 2}})
    action = worker.validate_action({"choice": 1, "reason": "Earn more."}, job)
    assert action["action_type"] == "work"
    assert action["params"] == {"effort": 2}
    assert action["evidence_ids"] == job.evidence_ids


def test_world_rules_form_a_shared_prefix_and_menus_stay_compact():
    job = make_job()
    job.observation = {"world_stage": 2, "tick": 3, "note": "One tick is one day."}
    job.candidates.append(
        {"action_type": "set_price", "params": {"business_id": "aster_food", "change_pct": 10}}
    )
    other = make_job()
    other.observation = {"world_stage": 2, "tick": 9, "note": "One tick is one day."}

    prompt = worker.build_prompt(job)
    system, user = prompt["messages"][0]["content"], json.loads(prompt["messages"][1]["content"])

    assert system.endswith("World rules: One tick is one day.")
    assert system == worker.build_prompt(other)["messages"][0]["content"]  # cacheable across agents
    assert "note" not in user["observation"]
    assert user["available_actions"] == ["0: hold", "1: set_price change_pct=10"]


@pytest.mark.parametrize("choice", [True, -1, 1, "0", None])
def test_world_choice_rejects_invalid_indices(choice):
    job = make_job()
    job.observation = {"world_stage": 2}
    with pytest.raises(ValueError, match="UNAVAILABLE_ACTION"):
        worker.validate_action({"choice": choice, "reason": "Wait."}, job)


def test_world_choice_cannot_inject_custom_parameters():
    job = make_job()
    job.observation = {"world_stage": 2}
    with pytest.raises(ValueError, match="INVALID_SCHEMA"):
        worker.validate_action(
            {"choice": 0, "reason": "Wait.", "params": {"cash": 999}}, job
        )


@pytest.mark.parametrize(
    "content,source",
    [
        ('{"choice":0,"reason":"Wait for a better option."}', "model"),
        ('{"choice":999,"reason":"Invalid."}', "rule_fallback"),
    ],
)
def test_world_request_uses_model_and_releases_capacity(monkeypatch, content, source):
    monkeypatch.setattr(worker, "TOKEN", "test-token")
    job = make_job()
    job.observation = {"world_stage": 2}
    calls = []

    def model_response(request):
        calls.append(request)
        return httpx.Response(
            200, json={"choices": [{"message": {"content": content}}]}
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
                response = await client.post(
                    "/decide",
                    json=job.model_dump(mode="json"),
                    headers={"Authorization": "Bearer test-token"},
                )
                assert response.status_code == 200
                assert response.json()["source"] == source
                assert worker.active_requests == 0
                assert len(calls) == 1

    asyncio.run(request())
