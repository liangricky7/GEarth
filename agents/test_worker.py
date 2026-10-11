import asyncio
import json
from datetime import datetime, timedelta, timezone

import httpx
import jsonschema
import pytest

from agents import worker
from agents.providers import ProviderPool, load_providers


@pytest.fixture(autouse=True)
def isolated_provider_pool(monkeypatch):
    monkeypatch.setattr(worker, "provider_pool", ProviderPool(load_providers({})))
    monkeypatch.setattr(worker, "active_requests", 0)


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


async def check_failure_then_recovery(
    monkeypatch, fail, expected_error, timeout=1, deadline=None
):
    monkeypatch.setattr(worker, "TOKEN", "test-token")
    monkeypatch.setattr(worker, "CAPACITY", 1)
    monkeypatch.setattr(worker, "TIMEOUT_SECONDS", timeout)
    monkeypatch.setattr(worker, "active_requests", 0)
    calls = []

    async def model_response(request):
        calls.append(request)
        assert worker.active_requests == 1
        if len(calls) == 1:
            return await fail(request)
        return httpx.Response(
            200,
            json={"choices": [{"message": {"content": '{"choice":1,"reason":"Work."}'}}]},
        )

    async with httpx.AsyncClient(
        transport=httpx.MockTransport(model_response)
    ) as model_client:
        monkeypatch.setattr(worker.app.state, "model_client", model_client, raising=False)
        async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=worker.app), base_url="http://test"
        ) as client:
            job = make_job()
            job.observation = {"world_stage": 2}
            job.candidates.append({"action_type": "work", "params": {"effort": 2}})
            if deadline is not None:
                job.deadline = deadline

            async def submit():
                return await asyncio.wait_for(
                    client.post(
                        "/decide",
                        json=job.model_dump(mode="json"),
                        headers={"Authorization": "Bearer test-token"},
                    ),
                    timeout=2,
                )

            failed = await submit()
            assert failed.status_code == 200
            result = failed.json()
            assert result["request_id"] == job.request_id
            assert result["source"] == "rule_fallback"
            assert result["error"] == expected_error
            assert result["action"]["action_type"] == "hold"
            assert result["action"]["params"] == {}
            assert result["action"]["evidence_ids"] == []
            assert result["action"]["reason"].strip()
            assert result["latency_ms"] >= 0
            assert worker.active_requests == 0

            assert sum(worker.provider_pool.active.values()) == 0
            for name in worker.provider_pool.unavailable_until:
                worker.provider_pool.unavailable_until[name] = 0
            job.request_id = "request_after_failure"
            job.deadline = datetime.now(timezone.utc) + timedelta(seconds=15)
            recovered = await submit()
            assert recovered.status_code == 200
            result = recovered.json()
            assert result["request_id"] == job.request_id
            assert result["source"] == "model"
            assert result["error"] is None
            assert result["action"]["action_type"] == "work"
            assert result["action"]["params"] == {"effort": 2}
            assert worker.active_requests == 0
            assert len(calls) == 2


@pytest.mark.parametrize(
    "payload,error",
    [
        pytest.param(
            {"choices": [{"message": {"content": '{"choice":'}}]},
            "JSONDecodeError",
            id="broken-model-json",
        ),
        pytest.param({}, "KeyError", id="missing-choices"),
        pytest.param({"choices": []}, "IndexError", id="empty-choices"),
        pytest.param(
            {"choices": [{"message": {"content": None}}]},
            "TypeError",
            id="null-model-content",
        ),
    ],
)
def test_malformed_model_response_falls_back_and_next_request_succeeds(
    monkeypatch, payload, error
):
    async def fail(request):
        return httpx.Response(200, json=payload)

    asyncio.run(check_failure_then_recovery(monkeypatch, fail, error))


def test_model_http_timeout_falls_back_and_next_request_succeeds(monkeypatch):
    async def fail(request):
        raise httpx.ReadTimeout("Model did not answer", request=request)

    asyncio.run(check_failure_then_recovery(monkeypatch, fail, "ReadTimeout"))


@pytest.mark.parametrize("limit", ["worker_timeout", "job_deadline"])
def test_hanging_model_is_canceled_and_next_request_succeeds(monkeypatch, limit):
    canceled = []
    deadline = None

    async def fail(request):
        try:
            await asyncio.Future()
        finally:
            canceled.append(True)

    if limit == "job_deadline":
        # Freeze only the admission clock so a short job deadline cannot expire
        # before /decide begins under a slow test runner.
        fixed_now = datetime.now(timezone.utc)

        class AdmissionClock:
            @staticmethod
            def now(tz):
                return fixed_now

        monkeypatch.setattr(worker, "datetime", AdmissionClock)
        deadline = fixed_now + timedelta(milliseconds=20)

    asyncio.run(
        check_failure_then_recovery(
            monkeypatch,
            fail,
            "TimeoutError",
            # The worker limit exceeds submit's two-second guard here, so
            # ignoring the shorter job deadline fails instead of passing late.
            timeout=5 if limit == "job_deadline" else 0.02,
            deadline=deadline,
        )
    )
    assert canceled == [True]


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


def test_fixed_numeric_choice_only_accepts_exact_copies_of_menu_parameters():
    job = make_job()
    job.observation = {"world_stage": 2}
    job.candidates.append({"action_type": "household_policy", "params": {"target_stock": 2}})
    action = worker.validate_action({"choice": 1, "reason": "Build a buffer"}, job)
    assert action["params"] == {"target_stock": 2}
    assert worker.render_action(1, job.candidates[1]) == "1: household_policy target_stock=2"
    for values in ({}, {"target_stock": 2}):
        repeated = worker.validate_action(
            {"choice": 1, "reason": "Build a buffer", "values": values}, job
        )
        assert repeated == action
    for values in ({"target_stock": 999}, {"target_stock": True}, {"target_stock": "2"}, {"target_stock": 2.0}, {"cash": 999}, []):
        with pytest.raises(ValueError, match="INVALID_SCHEMA"):
            worker.validate_action(
                {"choice": 1, "reason": "Build a buffer", "values": values}, job
            )


def test_fixed_menus_do_not_invite_free_numeric_values():
    # Reproduced 8B responses invented price_minor for change_pct and energy
    # for an import quantity after seeing the unrelated planner instructions.
    job = make_job()
    job.observation = {"world_stage": 2}
    job.candidates.extend([
        {"action_type": "set_price", "params": {"business_id": "aster_food_2", "change_pct": 10}},
        {"action_type": "import_goods", "params": {"seller_id": "dune_energy", "good": "energy", "quantity": 55, "objective": "cheapest"}},
    ])
    instructions = worker.build_prompt(job)["messages"][0]["content"]
    assert 'exactly "choice" and "reason"' in instructions
    assert '"values"' not in instructions
    for choice, values in ((1, {"price_minor": 62}), (2, {"energy": 55})):
        with pytest.raises(ValueError, match="INVALID_SCHEMA"):
            worker.validate_action({"choice": choice, "reason": "Improve profit", "values": values}, job)
        assert worker.validate_action({"choice": choice, "reason": "Improve profit"}, job)["params"] == job.candidates[choice]["params"]


def test_ranged_menus_select_before_filling_only_the_selected_fields():
    job = make_job()
    job.observation = {"world_stage": 2}
    job.candidates.append({"action_type": "set_plan", "params": {"food": {"min": 0, "max": 1000, "default": 700}}})
    prompt = worker.build_prompt(job)
    assert 'separate request' in prompt["messages"][0]["content"]
    assert '"values"' not in prompt["messages"][0]["content"]
    assert json.loads(prompt["messages"][1]["content"])["available_actions"][1] == "1: set_plan food=<integer 0-1000, now 700>"
    assert worker.validate_action({"choice": 1, "reason": "Meet demand", "values": {"food": 720}}, job)["params"] == {"food": 720}


def test_failure_logs_safe_validation_code_without_private_response(caplog):
    job = make_job()
    worker.decision_result(job, {}, "rule_fallback", worker.time.perf_counter(), ValueError("INVALID_VALUES"))
    record = json.loads(caplog.records[-1].message.split("decision_failure ", 1)[1])
    assert record["code"] == "INVALID_VALUES"
    worker.decision_result(job, {}, "rule_fallback", worker.time.perf_counter(), ValueError("private model response"))
    assert "private" not in caplog.records[-1].message


def test_citizens_sample_with_some_randomness_and_institutions_do_not():
    citizen = make_job()
    citizen.observation = {"world_stage": 2, "self": {"role": "citizen"}}
    government = make_job()
    government.observation = {"world_stage": 2, "self": {"role": "government"}}
    assert worker.build_prompt(citizen)["temperature"] == worker.CITIZEN_TEMPERATURE == 0.5
    assert worker.build_prompt(government)["temperature"] == 0


@pytest.mark.parametrize("role", ["citizen", "resident", "government", "entrepreneur"])
@pytest.mark.parametrize("ranged", [False, True])
def test_short_citizen_generation_preserves_institution_budget_and_action_validation(role, ranged):
    job = make_job()
    job.observation = {"world_stage": 2, "self": {"role": role}}
    if ranged:
        job.candidates = [{"action_type": "household_policy", "params": {
            "food": {"min": 0, "max": 20, "default": 8},
        }}]
    citizen = role in {"citizen", "resident"}
    prompt = worker.build_prompt(job, "Qwen/Qwen3-4B")
    assert prompt["max_tokens"] == (128 if citizen else 512)
    assert prompt["chat_template_kwargs"] == {"enable_thinking": False}
    assert f"at most {40 if citizen else 100} characters" in prompt["messages"][0]["content"]
    action = {"choice": 0, "reason": "Meet needs within budget"}
    if ranged:
        values = worker.build_values_prompt(job, 0, action["reason"], "Qwen/Qwen3-4B")
        assert values["max_tokens"] == (128 if citizen else 512)
        assert values["response_format"]["type"] == "json_schema"
        action["values"] = {"food": 8}
        assert worker.validate_action(action, job)["params"] == {"food": 8}
        action["values"]["food"] = 21
        with pytest.raises(ValueError, match="INVALID_VALUES"):
            worker.validate_action(action, job)
    else:
        assert worker.validate_action(action, job)["action_type"] == "hold"


def test_free_numbers_fill_ranged_fields_within_bounds():
    job = make_job()
    job.observation = {"world_stage": 2}
    plan = {"food": {"min": 0, "max": 1000, "default": 700}, "energy": {"min": 0, "max": 900, "default": 800}}
    job.candidates.append({"action_type": "set_plan", "params": plan})
    action = worker.validate_action(
        {"choice": 1, "reason": "Feed everyone.", "values": {"food": 720, "energy": 810}}, job
    )
    assert action["params"] == {"food": 720, "energy": 810}
    assert worker.render_action(1, job.candidates[1]) == (
        "1: set_plan food=<integer 0-1000, now 700> energy=<integer 0-900, now 800>"
    )
    for values in (
        {"food": 720}, {"food": 1001, "energy": 1}, {"food": "720", "energy": 1}, None,
        {"food": 720, "energy": 810, "invented": 1}, {"food": -1, "energy": 810},
        {"food": True, "energy": 810}, {"food": 720.0, "energy": 810},
    ):
        with pytest.raises(ValueError, match="INVALID_VALUES"):
            worker.validate_action({"choice": 1, "reason": "Plan.", "values": values}, job)
    with pytest.raises(ValueError, match="INVALID_VALUES"):
        worker.validate_action({"choice": 1, "reason": "Plan."}, job)
    for values in ({"food": 1},):  # fixed choices cannot invent parameters
        with pytest.raises(ValueError, match="INVALID_SCHEMA"):
            worker.validate_action({"choice": 0, "reason": "Wait.", "values": values}, job)


@pytest.mark.parametrize("invalid", [{"food": True, "energy": 810}, {"food": 1001, "energy": 810}, {"food": 1}])
def test_numeric_request_repairs_once_without_changing_choice_or_defaulting(monkeypatch, invalid):
    monkeypatch.setattr(worker, "TOKEN", "test-token")
    job = make_job()
    job.observation = {"world_stage": 2, "self": {"role": "planner"}}
    params = {"business_id": "state_firm", "food": {"min": 0, "max": 1000, "default": 700},
              "energy": {"min": 0, "max": 900, "default": 800}}
    job.candidates.append({"action_type": "set_plan", "params": params})
    calls = []

    def model_response(request):
        prompt = json.loads(request.content)
        calls.append(request)
        user = json.loads(prompt["messages"][1]["content"])
        if len(calls) == 1:
            assert user["available_actions"] == ["0: hold", "1: set_plan food=<integer 0-1000, now 700> energy=<integer 0-900, now 800>"]
            content = json.dumps({"choice": 1, "reason": "Set the weekly targets."})
        else:
            schema = prompt["response_format"]["json_schema"]["schema"]
            assert schema["properties"]["values"]["required"] == ["food", "energy"]
            assert "business_id" not in user["fields"]
            assert user["correction"] is not None if len(calls) == 3 else user["correction"] is None
            content = json.dumps({"values": invalid if len(calls) == 2 else {"food": 0, "energy": 900}})
        return httpx.Response(200, json={"choices": [{"message": {"content": content}}]})

    async def request():
        async with httpx.AsyncClient(transport=httpx.MockTransport(model_response)) as model_client:
            monkeypatch.setattr(worker.app.state, "model_client", model_client, raising=False)
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=worker.app), base_url="http://test") as client:
                for expected_source in ("model",):
                    response = await client.post("/decide", json=job.model_dump(mode="json"),
                                                 headers={"Authorization": "Bearer test-token"})
                    assert response.status_code == 200
                    result = response.json()
                    assert result["source"] == expected_source
                    assert worker.active_requests == 0
                    assert sum(worker.provider_pool.active.values()) == 0
                    if expected_source == "rule_fallback":
                        assert result["error_category"] == "model_invalid_response"
                        assert result["action"]["action_type"] == "hold"
                    else:
                        assert result["error"] is None
                        assert result["action"]["params"] == {"business_id": "state_firm", "food": 0, "energy": 900}
                        assert result["action"]["evidence_ids"] == job.evidence_ids
                        assert result["validation_retries"] == 1
                        assert len(calls) == 3
                assert job.candidates[1]["params"] == params  # Resolving numbers never changes the offered menu.

    asyncio.run(request())


@pytest.mark.parametrize("mode", ["exhausted", "schema_unsupported"])
def test_numeric_failure_is_bounded_and_schema_fallback_remains_validated(monkeypatch, mode):
    job = make_job()
    job.observation = {"world_stage": 2, "self": {"role": "government"}}
    job.candidates.append({"action_type": "set_welfare", "params": {"amount_minor": {"min": 0, "max": 200, "default": 50}}})
    calls = []

    def respond(request):
        prompt = json.loads(request.content)
        calls.append(prompt)
        if len(calls) == 1:
            content = {"choice": 1, "reason": "Support residents."}
        elif mode == "schema_unsupported" and len(calls) == 2:
            assert prompt["response_format"]["type"] == "json_schema"
            return httpx.Response(400, json={"error": "schema unsupported"})
        else:
            if mode == "schema_unsupported":
                assert prompt["response_format"]["type"] == "json_object"
            content = {"values": {"amount_minor": 175 if mode == "schema_unsupported" else True}}
        return httpx.Response(200, json={"choices": [{"message": {"content": json.dumps(content)}}]})

    async def run():
        async with httpx.AsyncClient(transport=httpx.MockTransport(respond)) as client:
            monkeypatch.setattr(worker.app.state, "model_client", client, raising=False)
            provider = load_providers({})[0]
            if mode == "exhausted":
                with pytest.raises(worker.ActionValidationError) as caught:
                    await worker.resolve_model_action(job, provider, {})
                assert caught.value.detail == "not_integer"
                result = worker.decision_result(job, {}, "rule_fallback", worker.time.perf_counter(), caught.value)
                assert result["error_code"] == "INVALID_VALUES"
                assert result["error_field"] == "amount_minor"
            else:
                action, retries = await worker.resolve_model_action(job, provider, {})
                assert action["params"] == {"amount_minor": 175}
                assert retries == 0
            assert len(calls) == 3
            assert job.candidates[1]["params"]["amount_minor"]["default"] == 50

    asyncio.run(run())


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


@pytest.mark.parametrize(
    "fault,category,status",
    [
        ("timeout", "model_timeout", None),
        ("bad_json", "model_invalid_response", None),
        ("http", "model_http_error", 503),
        ("transport", "model_transport_error", None),
    ],
)
def test_model_failure_metadata_preserves_origin_and_status(monkeypatch, fault, category, status):
    monkeypatch.setattr(worker, "TOKEN", "test-token")
    monkeypatch.setattr(worker, "active_requests", 0)
    job = make_job()
    job.observation = {"world_stage": 2}

    def respond(request):
        if fault == "timeout":
            raise httpx.ReadTimeout("Model timed out", request=request)
        if fault == "transport":
            raise httpx.RemoteProtocolError("Model disconnected", request=request)
        if fault == "http":
            return httpx.Response(503, text="private model response")
        return httpx.Response(200, json={"choices": [{"message": {"content": "{broken"}}]})

    async def run():
        async with httpx.AsyncClient(transport=httpx.MockTransport(respond)) as model:
            monkeypatch.setattr(worker.app.state, "model_client", model, raising=False)
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=worker.app), base_url="http://test") as client:
                result = (await client.post("/decide", json=job.model_dump(mode="json"),
                    headers={"Authorization": "Bearer test-token"})).json()
                assert result["source"] == "rule_fallback"
                assert result["request_id"] == job.request_id
                assert result["error_category"] == category
                assert result["error_http_status"] == status
                assert result["latency_ms"] >= 0
                assert "private" not in result["error"]
                assert worker.active_requests == 0

    asyncio.run(run())
