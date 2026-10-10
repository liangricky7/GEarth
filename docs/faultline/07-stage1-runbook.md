# Faultline Stage 1

## What works

Stage 1 creates 50 agents across the five scenario countries, asks up to four agents to decide in each step, validates proposals in C#, commits the world checkpoint and events together, and publishes the committed state through a read-only API. A simple SVG observer displays routes, decision history, population coverage, and individual profiles.

The active action subset is `hold`, local `purchase`, `publish`, and `choose_route`. The medicine shipment is a planning placeholder. There is no escrow funding, dispatch, physical delivery, banking ledger, loan, or Solana transaction. Banks and most businesses currently have only `hold` available. Publications are generated claims with evidence references; their prose is not fact-checked by a separate model.

## Start on Jayden's Mac

Tailscale and SSH access to `hackathon@100.70.23.23` must be working. The launcher uses the existing vLLM service, reads its configured API key into process memory over SSH, and creates a local tunnel on port 18001. It does not change Spark configuration or Netplan. Model and worker keys are not written to files. The inference worker does not receive `DATABASE_URL`.

Store the Neon connection string on the `DATABASE_URL=` line in the ignored `.env.local` file. Do not put it in source, logs, or chat. This implementation uses Neon's SQL-over-HTTPS endpoint because the local direct PostgreSQL connection was refused.

```bash
cd "/Users/jaydenhong/Documents/ChatGPT/KnightHacks2026 2"
.venv/bin/python ops/run_stage1.py --live
```

Open http://127.0.0.1:5080. The simulation runs for 16 ticks and then remains available for observation. Ctrl+C stops the API, worker, and tunnel started by this launcher. The existing remote model server continues running. A new invocation creates a fresh run. Previously saved runs can be selected in the observer.

For a faster smoke test, use `--interval 0`. The three-second default is a pause between batches; it is not a guaranteed three-second wall-clock tick. Each step waits for its inference batch and database commit. Ports 5080, 8100, and 18001 must be available. Local logs are under `.logs/`.

## Development without a Spark

```bash
.venv/bin/python ops/run_stage1.py --storage local --interval 0
```

This uses explicitly labeled deterministic rules, records zero model coverage, and writes checkpoints under `.data/runs/`. Storage failures never silently switch a Neon run to local storage. The API and worker bind to loopback only.

On another machine, install the .NET 10 SDK and Python 3.12, then create the environment:

```bash
python3.12 -m venv .venv
.venv/bin/python -m pip install -r agents/requirements.txt
dotnet build Faultline.sln
```

The pinned SDK is 10.0.203 with patch roll-forward. To run live elsewhere, configure SSH access to the known Spark host. An API key can optionally be supplied through `SPARK_A_API_KEY` in the private environment file, but SSH tunneling is still used.

## Agent differences

`WorldFactory` derives each trait from SHA-256 of the scenario seed, agent ID, and trait name, maps the sample to 0–1, and rounds it to two decimal places. The same seed and identity produce the same profile regardless of creation order. These are synthetic preference parameters, not validated psychological measurements.

The prompt contains the agent's role, goal, traits, own cash and inventory, at most eight memories, relevant offers or route choices, and public border events. Citizens with lower risk tolerance have a larger supply target. The same model weights serve all agents; separate prompts and state provide their differences. The engine computes routes using Dijkstra with travel-time, price, or travel-plus-risk edge weights.

Risk tolerance and the role goal affect the current purchasing and route-choice context. Patience, generosity, and trust are supplied as model preferences, but their behavioral effect has not been evaluated, and there is no social learning or relationship update yet. Temperature zero helps keep the smoke test stable; it does not guarantee reproducible model decisions across serving changes.

## Validation and persistence

The Python worker checks the shared action schema, permitted candidates, and known evidence IDs. The C# engine independently checks actor identity, role, funds, stock, location, entity versions, known evidence, and deadlines. An unavailable model produces a labeled `rule_fallback` hold. Rejected requests and fallback requests remain visible in the event history.

Each observation records versions of its relevant entities. A changed seller can invalidate a purchase while an unrelated country's change does not. This versioning is conservative: a seller's route-planning decision can also invalidate a pending purchase because both use that agent's version. The scheduler does not yet resubmit rejected requests.

Neon uses `faultline.stage1_runs` for complete checkpoints and `faultline.stage1_events` for ordered events. A single SQL statement guards the expected revision and commits both together. Repeated identical saves are idempotent. Transport failures retry at most three times. A lasting storage failure pauses the coordinator before publishing uncommitted state; the observer keeps the previous checkpoint and displays the paused status.

The launcher currently creates fresh runs and allows reading saved runs. It does not resume an interrupted run or generate an animated replay. Checkpoints include the decisions, observations, and memory state needed to implement recovery later. Full checkpoints and accumulated events are suitable for the small smoke test; incremental storage and pruning are required before long or large runs.

## Checks

```bash
dotnet test Faultline.sln
.venv/bin/python -m pytest agents/test_worker.py -q
python3 docs/faultline/validate_design.py
```

After a live run completes, while the API is still running:

```bash
.venv/bin/python ops/check_stage1.py --require-model --output artifacts/local/stage1-check.json
```

This verifies event schema and ordering, cause references, nonnegative resources, cash conservation, read-only endpoints, earlier run loading, static asset responses, and the actual Neon checkpoint and event rows. It records measured latency and coverage. It does not verify visual layout or prove personality effects. Browser access was denied during implementation, so the observer's appearance has not been visually inspected.

## Recorded smoke test

The [October 10 live evidence](evidence/stage1-smoke-20261010.json) records a completed 16-tick run on the first Spark: 64 model decision attempts, 63 accepted decisions, one stale-dependency rejection, zero fallbacks, and 49 agents with an accepted model decision out of 50 spawned. All 50 agents produced a model decision; the rejected citizen's decision did not change state. Accepted actions included purchases, publications, holds, and medicine route plans.

Model-request latency was 6.20 seconds at the median and 8.03 seconds at the recorded p95. These values cover this small bounded run; they are not a population-scale throughput benchmark. Total cash stayed at 1,237,000 minor units. The closure caused the medicine plan to use the sea detour at 12 travel ticks and 1,700 minor units. The checkpoint, all 123 event rows, schema, cause references, and read API checks passed. Thirteen C# tests and five Python tests also passed.

An earlier live attempt paused after a transport failure. The completed run used the retry fix. No model service or Spark network settings were changed.

## Next stage

Prioritize actual shipment movement, banking actions and ledger entries, then Solana devnet settlement. Add asynchronous scheduling and a second worker before increasing population. Add messaging and relationship state to make trust and generosity actionable. Evaluate trait effects under matched scenarios rather than claiming distinct behavior from profile fields alone.
