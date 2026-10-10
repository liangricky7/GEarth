import argparse
import asyncio
import json
import math
import subprocess
import sys
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path

import httpx

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from agents.worker import DecisionJob, build_prompt, validate_action
from ops.run_stage1 import REMOTE_KEY_READER, SPARK_TARGET, SSH_OPTIONS


def sample_job(index):
    role = ["citizen", "business", "government", "bank", "media"][index % 5]
    candidates = {
        "citizen": [
            {"action_type": "hold", "params": {}},
            {"action_type": "household_policy", "params": {"target_stock": 8}},
            {"action_type": "work", "params": {"effort": 2}},
            {"action_type": "express_support", "params": {"support": False}},
        ],
        "business": [
            {"action_type": "hold", "params": {}},
            {"action_type": "set_price", "params": {"price_minor": 50}},
            {"action_type": "import_goods", "params": {"seller_id": "beryl_food", "good": "food", "quantity": 80, "objective": "cheapest"}},
            {"action_type": "request_loan", "params": {"amount_minor": 5000}},
        ],
        "government": [
            {"action_type": "hold", "params": {}},
            {"action_type": "set_tax", "params": {"tax_basis_points": 1500}},
            {"action_type": "set_welfare", "params": {"amount_minor": 80}},
            {"action_type": "propose_agreement", "params": {"country_id": "beryl"}},
        ],
        "bank": [
            {"action_type": "hold", "params": {}},
            {"action_type": "approve_loan", "params": {"borrower_id": "aster_food", "amount_minor": 5000}},
        ],
        "media": [
            {"action_type": "hold", "params": {}},
            {"action_type": "publish", "params": {"text": "Food supply covers 3 cycles. Treasury is 120000 WLD minor units."}},
        ],
    }[role]
    observation = {
        "world_stage": 2,
        "tick": 20,
        "self": {
            "id": f"aster_{role}_{index}",
            "role": role,
            "occupation": "farmer" if role == "citizen" else role,
            "goal": "Meet essential needs, maintain income and respond to your country's changing conditions.",
            "traits": {"risk_tolerance": round((index % 10) / 10, 1), "patience": 0.4, "generosity": 0.7, "trust": 0.5},
            "cash_minor": 2500,
            "inventory": {"food": 2, "energy": 2},
            "memories": ["Tick 10: Imported food supplies arrived.", "Tick 15: Income after tax was 119 minor units."],
        },
        "country": {"id": "aster", "government": "market_democracy", "tax_basis_points": 1500, "welfare_minor": 0, "approval": 0.6, "food_stock": 2000, "energy_stock": 1200, "unmet_needs_last_cycle": 0, "treasury_minor": 120000, "population": 100},
        "neighbors": [{"id": "beryl", "food_stock": 6000, "energy_stock": 500, "relations": 30, "trade_agreement": False}, {"id": "cinder", "food_stock": 1000, "energy_stock": 12000, "relations": -40, "trade_agreement": False}],
        "market": [{"seller": "beryl_food", "price_minor": 45, "stock": 6000, "shipping_minor": 200, "travel_ticks": 6}],
        "note": "Routine work, production, wages and household restocking follow standing policies between decisions. Cash and cargo are conserved. No blockchain settlement.",
    }
    return DecisionJob(request_id=f"benchmark_{index}", run_id="benchmark", agent_id=f"agent_{index}", observation_tick=20, deadline=datetime.now(timezone.utc) + timedelta(seconds=90), dependencies={}, evidence_ids=["state:benchmark"], observation=observation, candidates=candidates)


async def benchmark(api_key, concurrencies, waves, job_file):
    samples = json.loads(Path(job_file).read_text()) if job_file else None
    results = []
    async with httpx.AsyncClient(timeout=90, limits=httpx.Limits(max_connections=64)) as client:
        for concurrency in concurrencies:
            semaphore = asyncio.Semaphore(concurrency)
            records = []
            started = time.perf_counter()

            async def request(index):
                async with semaphore:
                    job = DecisionJob.model_validate(samples[index % len(samples)]) if samples else sample_job(index)
                    job.deadline = datetime.now(timezone.utc) + timedelta(seconds=90)
                    request_started = time.perf_counter()
                    try:
                        response = await client.post("http://127.0.0.1:18001/v1/chat/completions", headers={"Authorization": f"Bearer {api_key}"}, json=build_prompt(job))
                        response.raise_for_status()
                        body = response.json()
                        validate_action(json.loads(body["choices"][0]["message"]["content"]), job)
                        records.append({"valid": True, "latency_seconds": time.perf_counter() - request_started, "usage": body.get("usage", {})})
                    except (httpx.HTTPError, ValueError, KeyError, TypeError) as error:
                        records.append({"valid": False, "latency_seconds": time.perf_counter() - request_started, "error": type(error).__name__})

            await asyncio.gather(*(request(index) for index in range(concurrency * waves)))
            elapsed = time.perf_counter() - started
            latencies = sorted(row["latency_seconds"] for row in records)
            result = {"concurrency": concurrency, "attempts": len(records), "valid": sum(row["valid"] for row in records), "elapsed_seconds": elapsed, "valid_per_second": sum(row["valid"] for row in records) / elapsed, "p50_seconds": latencies[len(latencies) // 2], "p95_seconds": latencies[math.ceil(len(latencies) * 0.95) - 1], "records": records}
            results.append(result)
            print(json.dumps({key: value for key, value in result.items() if key != "records"}), flush=True)
    return results


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--concurrency", nargs="+", type=int, default=[4, 8, 16, 32])
    parser.add_argument("--waves", type=int, default=2)
    parser.add_argument("--jobs")
    parser.add_argument("--output", default="docs/faultline/evidence/world-spark-benchmark.json")
    options = parser.parse_args()
    key = subprocess.run(["ssh", *SSH_OPTIONS, SPARK_TARGET, "python3 -"], input=REMOTE_KEY_READER, text=True, capture_output=True, check=True).stdout.strip()
    if not key:
        raise RuntimeError("Existing model credentials unavailable")
    results = asyncio.run(benchmark(key, options.concurrency, options.waves, options.jobs))
    qualified = [row for row in results if row["valid"] == row["attempts"] and row["p95_seconds"] < 45]
    best = max(qualified, key=lambda row: row["valid_per_second"]) if qualified else None
    evidence = {"measured_at": datetime.now(timezone.utc).isoformat(), "model": "openai/gpt-oss-120b", "prompt_source": "world engine observations" if options.jobs else "representative world-role observations", "results": results, "selected": {key: value for key, value in best.items() if key != "records"} if best else None, "absolute_maximum_proven": False, "limitations": ["Short load sweep, not a sustained capacity guarantee.", "Population depends on required decision cadence and institutional priority.", "End-to-end persistence and scheduler overhead excluded."]}
    Path(options.output).write_text(json.dumps(evidence, indent=2) + "\n")


if __name__ == "__main__":
    main()
