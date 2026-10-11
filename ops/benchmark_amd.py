"""Measure validated citizen completions, latency, and memory across batch sizes."""

import argparse
import asyncio
import json
import math
import time
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen


def http(origin, key, path, payload=None):
    request = Request(origin.rstrip("/") + path,
                      headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"},
                      data=None if payload is None else json.dumps(payload).encode())
    with urlopen(request, timeout=60) as response:
        return json.load(response)


async def sweep(origin, key, fixtures, concurrencies, waves, output, tune=True):
    asyncio.get_running_loop().set_default_executor(ThreadPoolExecutor(max_workers=max(concurrencies) + 8))
    results = []
    for concurrency in concurrencies:
        if tune:
            await asyncio.to_thread(http, origin, key, "/admin/tuning",
                                    {"batch_size": concurrency, "capacity": concurrency,
                                     "batch_tokens": 131072, "wait_ms": 20})
        records, samples = [], []
        semaphore = asyncio.Semaphore(concurrency)
        finished = asyncio.Event()

        async def monitor():
            while not finished.is_set():
                try:
                    samples.append(await asyncio.to_thread(http, origin, key, "/health"))
                except Exception:
                    pass
                try:
                    await asyncio.wait_for(finished.wait(), timeout=0.5)
                except TimeoutError:
                    pass

        async def request(index):
            async with semaphore:
                fixture = fixtures[index % len(fixtures)]
                started = time.perf_counter()
                try:
                    response = await asyncio.to_thread(http, origin, key, "/v1/chat/completions", fixture["payload"])
                    answer = json.loads(response["choices"][0]["message"]["content"])
                    valid = (isinstance(answer, dict) and set(answer) in ({"choice", "reason"}, {"choice", "reason", "values"})
                             and type(answer["choice"]) is int
                             and 0 <= answer["choice"] < fixture["candidate_count"]
                             and isinstance(answer["reason"], str) and 1 <= len(answer["reason"].strip()) <= 240
                             and response["choices"][0]["finish_reason"] == "stop")
                    records.append({"valid": valid, "seconds": time.perf_counter() - started,
                                    "usage": response.get("usage", {})})
                except Exception as error:
                    records.append({"valid": False, "seconds": time.perf_counter() - started,
                                    "error": type(error).__name__,
                                    "status": error.code if isinstance(error, HTTPError) else None})

        monitor_task = asyncio.create_task(monitor())
        started = time.perf_counter()
        await asyncio.gather(*(request(index) for index in range(max(len(fixtures), concurrency * waves))))
        elapsed = time.perf_counter() - started
        finished.set()
        await monitor_task
        latencies = sorted(row["seconds"] for row in records)
        result = {"concurrency": concurrency, "attempts": len(records),
                  "valid": sum(row["valid"] for row in records), "elapsed_seconds": round(elapsed, 3),
                  "valid_per_second": round(sum(row["valid"] for row in records) / elapsed, 3),
                  "p50_seconds": round(latencies[len(latencies) // 2], 3),
                  "p95_seconds": round(latencies[math.ceil(len(latencies) * 0.95) - 1], 3),
                  "min_free_gib": min((s.get("memory", {}).get("free_gib", 1000) for s in samples), default=None),
                  "largest_batch": max((s.get("largest_batch", 1) for s in samples), default=1),
                  "oom_splits": max((s.get("oom_splits", 0) for s in samples), default=0),
                  "records": records, "health_samples": samples}
        results.append(result)
        summary = {k: v for k, v in result.items() if k not in {"records", "health_samples"}}
        print(json.dumps(summary), flush=True)
        Path(output).write_text(json.dumps({"measured_at": datetime.now(timezone.utc).isoformat(),
                                          "results": results, "absolute_maximum_proven": False}, indent=2) + "\n")
        if result["valid"] < result["attempts"] * 0.95 or result["p95_seconds"] > 40:
            break
    return results


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--origin", default="http://127.0.0.1:8181")
    parser.add_argument("--key-file", required=True)
    parser.add_argument("--fixtures", required=True)
    parser.add_argument("--concurrency", type=int, nargs="+", default=[1, 4, 8, 16, 32, 64])
    parser.add_argument("--waves", type=int, default=3)
    parser.add_argument("--output", default="amd-benchmark.json")
    parser.add_argument("--no-tune", action="store_true")
    args = parser.parse_args()
    asyncio.run(sweep(args.origin, Path(args.key_file).read_text().strip(),
                      json.loads(Path(args.fixtures).read_text()), args.concurrency,
                      args.waves, args.output, not args.no_tune))


if __name__ == "__main__":
    main()
