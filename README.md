# Faultline

An observable AI society for KnightHacks 2026: ten fictional countries, five institutional profiles and 1,100 individual agents by default. Residents work, shop and own competing firms; governments, banks and newsrooms make bounded decisions. The C# engine owns accounting and simulated time, and shared model services provide individual strategic turns.

## Current state

Main combines the daily labor-market economy, entrepreneurs, quality of life, clinics, public services, trade and diplomacy with shared Spark/AMD routing, seeded endowments, bounded scheduling, observer consistency and automatic run reports. One tick is one day; payroll runs every seven ticks. Institutions and entrepreneurs decide weekly, and citizens rotate over eight weeks. Weekly decisions finish before payroll.

Spark A's Qwen3-8B and Qwen3-4B services were verified; AMD's Qwen3-4B endpoint also completed decisions. Jayden's private configuration enables 4 institution slots, 8 citizen slots and 1 shared AMD slot. Spark B and AMD's second model are prepared but unverified. These are inference services; the laptop runs one world. The previous simulation is stopped and is not restarted by this merge.

See the [design](faultline-design.md) for the full specification, current limitations, decisions and their reasons.

The known upstream full-year calibration test remains failing and visible. No sustained live capacity maximum is established. Old cadence-based population estimates do not describe the weekly scheduler. Full banking remains deferred on `labor-market-potential-bank`; Databricks and Solana settlement remain unimplemented.

## Run locally

Requirements: .NET 10 matching `global.json`, Python 3.12+, and Git. Node is used for observer tests.

```bash
python3.12 -m venv .venv
.venv/bin/python -m pip install -r agents/requirements.txt
cp .env.example .env.local
.venv/bin/python ops/run_stage1.py --world --storage local
```

Open [localhost:5080](http://127.0.0.1:5080/). This uses deterministic rule choices and requires neither model credentials nor Neon. For an existing installation, preserve its private `.env.local` rather than copying over it.

For live inference, configure authenticated endpoints and Spark SSH access, then run:

```bash
.venv/bin/python ops/run_stage1.py --world --live --compute hybrid --storage neon
```

Use `--compute spark` or `--compute amd` for a single source, and `--storage local` to omit Neon. See [Qwen setup](docs/faultline/18-all-node-qwen-setup.md), [AMD setup](docs/faultline/12-amd-compute.md) and [the world runbook](docs/faultline/08-world-runbook.md). Real credentials, logs, environments and runtime state are ignored by Git.

World options include `--population`, `--capacity`, `--ticks`, `--interval`, `--seed`, `--government-rotation` and `--verbose` (writes the day-by-day history file for the renderer, served at `/api/runs/{id}/history`). Defaults: 1,100 agents, 360 daily ticks and two-second intervals. Live admission derives from enabled provider capacities; an explicit `--capacity` lowers the total cap. Use `--ticks 365` for a simulated year. The software population range is 160–100,000 and is not a validated capacity range. Wall-clock institution/resident cadence flags were removed.

Ctrl+C or SIGTERM stops the launcher and its child services. The observer stays available after completion unless `--exit-on-complete` is supplied. To inspect a saved checkpoint without inference:

```bash
.venv/bin/python ops/run_stage1.py --storage neon --run-id YOUR_RUN_ID
```

Saved-run inspection does not resume execution. Stage 1 remains available by omitting `--world`.

## Verify

```bash
dotnet test Faultline.sln
.venv/bin/python -m pytest agents ops/test_amd_model_server.py ops/test_compute_config.py ops/test_spark_qwen.py -q
npm ci
npm run test:observer
.venv/bin/python docs/faultline/validate_design.py
.venv/bin/python ops/check_scheduler_integration.py
```

Exact consolidation results and the known calibration failure are recorded in [integration evidence](docs/faultline/evidence/main-integration.json). The lifecycle test uses synthetic inference, not a GPU benchmark. The reference validator checks historical Stage 1 documents and fixtures. Browser visual layout remains unverified.

For a completed world being served, `ops/check_world.py` checks conservation, durable ordered events, checkpoint readback, pagination, request bounds and read-only assets. Add `--require-model` for real model turns and `--require-full-coverage` only when explicitly qualifying coverage; coverage uses actual decision makers rather than owner-controlled firms. `ops/check_stage2_api.py` checks the committed API without new inference.

## Observer and reports

[The permanent observer](https://diarockets-faultline.finebear64.chatgpt.site/) keeps its address across restarts. The existing share supervisor reconnects when the laptop app is running; the laptop must remain online and awake. See [sharing operations](docs/faultline/09-sharing.md).

Completed, gracefully stopped and paused runs produce behavior observations, country outcomes, model/backend counts, inequalities, examples of attributed reasoning and inference errors. Reports distinguish accepted model actions, engine rules and fallbacks, with event references and limits on interpretation. Markdown/JSON reports live under ignored `.data/run-notes/` and in configured storage. The observer exposes structured notes and a Markdown download.

| Directory | Responsibility |
| --- | --- |
| `backend/Faultline.Core` | Economy, labor, accounting, world state, weekly cohorts, observations |
| `backend/Faultline.Api` | Bounded coordination, persistence, read API, observer and run notes |
| `backend/Faultline.Tests` | Engine, queue, accounting, profiles, scheduling and persistence tests |
| `agents` | Authenticated worker, model pool, compact prompts and validation |
| `ops` | Launcher, synthetic checks, Spark installer, AMD setup and private relays |
| `docs/faultline` | Operational guides, contracts, fixtures and historical/integration evidence |

Ricky implemented the labor economy and persistence refactors; Jayden integrated compute, endowments and observer/scheduler work; Kennedy operates visualization and live runs; Ahmad works on inference and evaluation. Outcomes describe a configured fictional society, not causal conclusions about real governments.
