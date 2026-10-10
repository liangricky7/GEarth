import argparse
import asyncio
import json
import secrets
import sys
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path

import httpx
from dotenv import dotenv_values

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))


async def check(output):
    from agents import worker
    from agents.providers import ProviderPool, load_providers

    configuration = {
        **dotenv_values(ROOT / ".env.local"),
        "SPARK_A_ENABLED": "false",
        "SPARK_B_ENABLED": "false",
        "AMD_ENABLED": "true",
    }
    providers = load_providers(configuration, capacity=1)
    provider = providers[0]
    headers = {"Authorization": f"Bearer {provider.api_key}"}
    worker.provider_pool = ProviderPool(providers)
    worker.TOKEN = secrets.token_urlsafe(32)
    worker.TIMEOUT_SECONDS = 60
    started_at = time.perf_counter()
    async with httpx.AsyncClient(timeout=60, follow_redirects=False) as model_client:
        models = await model_client.get(f"{provider.base_url}/models", headers=headers)
        models.raise_for_status()
        if provider.model not in [entry["id"] for entry in models.json()["data"]]:
            raise RuntimeError("Configured AMD model is not available")
        unauthorized = await model_client.get(f"{provider.base_url}/models")
        if unauthorized.status_code != 401:
            raise RuntimeError("AMD endpoint must reject unauthenticated requests")
        worker.app.state.model_client = model_client
        job = worker.DecisionJob(
            request_id="amd_connection_probe",
            run_id="compute_probe",
            agent_id="fictional_resident",
            observation_tick=0,
            deadline=datetime.now(timezone.utc) + timedelta(seconds=60),
            dependencies={},
            evidence_ids=[],
            observation={
                "world_stage": 2,
                "role": "resident",
                "goal": "Save money for tomorrow",
                "cash_minor": 100,
            },
            candidates=[
                {"action_type": "hold", "params": {}},
                {"action_type": "work", "params": {"effort": 1}},
            ],
        )
        result = await worker.decide(job, f"Bearer {worker.TOKEN}")
        if result["source"] != "model":
            raise RuntimeError(f"AMD decision probe failed: {result['error']}")
    receipt = {
        "checked_at": datetime.now(timezone.utc).isoformat(),
        "backend": result["backend"],
        "model": result["model"],
        "unauthenticated_status": unauthorized.status_code,
        "action": result["action"],
        "latency_ms": result["latency_ms"],
        "elapsed_seconds": round(time.perf_counter() - started_at, 2),
        "observations": [
            "The configured endpoint listed its model and rejected unauthenticated access.",
            "One fictional resident decision passed the existing action validator.",
            "This is a connection smoke check, not a simulation or a capacity benchmark.",
        ],
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(receipt, indent=2) + "\n")
    output.with_suffix(".md").write_text(
        "# AMD connection observations\n\n"
        + "\n".join(f"- {observation}" for observation in receipt["observations"])
        + f"\n\nModel: {result['model']}. Decision latency: {result['latency_ms']} ms.\n"
    )
    print(json.dumps(receipt, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--output", type=Path, default=ROOT / ".data/amd-connection.json"
    )
    arguments = parser.parse_args()
    try:
        asyncio.run(check(arguments.output))
    except (ValueError, RuntimeError, httpx.HTTPError, KeyError, TypeError) as error:
        print(
            f"AMD check failed: {type(error).__name__}. Check configuration and cloud server readiness.",
            file=sys.stderr,
        )
        sys.exit(1)
