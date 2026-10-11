# GEarth

An observable AI society for KnightHacks 2026: ten fictional countries, five institutional profiles and 1,100 individual agents by default. Residents work, shop and own competing firms; governments, banks and newsrooms make bounded decisions. The C# engine owns accounting and simulated time, and shared model services provide individual strategic turns.

## Current state

Main combines the daily labor-market economy, entrepreneurs, quality of life, clinics, public services, trade and diplomacy with shared Spark/AMD routing, seeded endowments, bounded scheduling, observer consistency and automatic run reports. One tick is one day; payroll runs every seven ticks. Institutions and entrepreneurs decide weekly, and citizens rotate over eight weeks. Weekly decisions finish before payroll.

The [shared cloud simulation](https://8-34-212-187.sslip.io/) runs continuously and saves worlds to Neon. Both Sparks provide Qwen3-8B institution decisions and Qwen3-4B resident decisions; AMD supplements residents with Qwen3-4B. All five providers have completed live requests. The configured pool has 97 slots (16/16/32/32 on the Sparks plus one on AMD); this is not a measured throughput result. The Google Cloud VM runs the world engine, so the owner's laptop can close.

Group members can open the website and unlock controls using the shared team password provided privately by the owner. Stop finishes in-flight decisions, saves and pauses the current world; Start resumes it. Reset saves the current run and starts a fresh world, retaining history. Everyone shares the same world, and closing a browser does not stop it. See the [cloud runbook](docs/gearth/22-google-cloud-deployment.md) for operation and access details.

See the [design](gearth-design.md) for the full specification, current limitations, decisions and their reasons. Existing deployments require the [GEarth cutover](docs/gearth/24-gearth-rename.md) to preserve saved data.

The full-year calibration test covers the economic loop; no sustained live capacity maximum is established. Old cadence-based population estimates do not describe the weekly scheduler. Banks create deposits when lending, subject to simplified capital and reserve constraints. Ore, fuel, industrial inputs and freight costs constrain production in both market and planned economies. See the [post-bank follow-up](docs/gearth/post-bank-followup.md) for the model protocol, economic fixes and validation limits. Solana settlement remains unimplemented. Finished-run Databricks analytics are opt-in; see the [analytics runbook](docs/gearth/20-analytics-pipeline.md) for capture, import and cached reports. The 50,000-agent annual workload remains unqualified.

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

Use `--compute spark` or `--compute amd` for a single source, and `--storage local` to omit Neon. See [Qwen setup](docs/gearth/18-all-node-qwen-setup.md), [AMD setup](docs/gearth/12-amd-compute.md) and [the world runbook](docs/gearth/08-world-runbook.md). Real credentials, logs, environments and runtime state are ignored by Git.

World options include `--population`, `--capacity`, `--ticks`, `--interval`, `--seed`, `--government-rotation` and `--verbose` (writes the day-by-day history file for the renderer, served at `/api/runs/{id}/history`). Defaults: 1,100 agents, 360 daily ticks and two-second intervals. Live admission derives from enabled provider capacities; an explicit `--capacity` lowers the total cap. Use `--ticks 365` for a simulated year. The software population range is 200–100,000 and is not a validated capacity range. Wall-clock institution/resident cadence flags were removed.

Ctrl+C or SIGTERM stops the launcher and its child services. The observer stays available after completion unless `--exit-on-complete` is supplied. To inspect a saved checkpoint without inference:

```bash
.venv/bin/python ops/run_stage1.py --storage neon --run-id YOUR_RUN_ID
```

Saved-run inspection does not resume execution. Stage 1 remains available by omitting `--world`.

To fork a Neon world run into a what-if timeline on its own Neon branch (needs `NEON_API_KEY` and `NEON_PROJECT_ID`; see [design 12.3](gearth-design.md#123-neon-branching-timelines)):

```bash
.venv/bin/python ops/fork_run.py YOUR_RUN_ID --day 14 --country aster --government dictatorship
```

## Verify

For unattended simulation experiments while developing locally, see the [Google Cloud deployment runbook](docs/gearth/22-google-cloud-deployment.md).

```bash
dotnet test GEarth.sln
.venv/bin/python -m pytest agents ops/test_amd_model_server.py ops/test_compute_config.py ops/test_spark_qwen.py -q
npm ci
npm run test:observer
.venv/bin/python docs/gearth/validate_design.py
.venv/bin/python ops/check_scheduler_integration.py
```

Earlier consolidation results, including the previous calibration failure, remain in [historical integration evidence](docs/gearth/evidence/main-integration.json). Current rename checks passed 156 C# and 231 Python tests; see [rename verification](docs/gearth/evidence/gearth-rename-20261011.json). The lifecycle test uses synthetic inference, not a GPU benchmark. The reference validator checks historical Stage 1 documents and fixtures. Browser visual layout remains unverified.

For observer changes, `node ops/check_observer.cjs` checks desktop/mobile rendering, saved-run switching with delayed responses, agent filters and pagination using API fixtures and installed Chrome or Edge. Screenshots are saved under ignored `artifacts/local/`.

For the integrated Vulkan globe, see the [GEarth renderer](renderer/README.md). Build it once, package a completed `ops/export_renderer.py` export with `node renderer/recording.mjs EXPORT_FOLDER OUTPUT.gearth`, then open that single file with `node renderer/run.mjs --export OUTPUT.gearth`. The recording includes all-agent checkpoints and ordered events; a full year plays in one minute by default, with adjustable pacing. Folder exports and optional `--run YOUR_RUN_ID --api http://127.0.0.1:5080` connections also remain supported. Older runs without captured history show their current snapshot with an explicit history gap.

For a completed world being served, `ops/check_world.py` checks conservation, durable ordered events, checkpoint readback, pagination, request bounds and read-only assets. Add `--require-model` for real model turns and `--require-full-coverage` only when explicitly qualifying coverage; coverage uses actual decision makers rather than owner-controlled firms. `ops/check_stage2_api.py` checks the committed API without new inference.

## Observer and reports

The [HTTPS cloud observer](https://8-34-212-187.sslip.io/) stays available when the laptop closes. Saved runs can be selected from its run menu. The earlier laptop sharing supervisor is documented separately in [sharing operations](docs/gearth/09-sharing.md).

Completed, gracefully stopped and paused runs produce behavior observations, country outcomes, model/backend counts, inequalities, examples of attributed reasoning and inference errors. Reports distinguish accepted model actions, engine rules and fallbacks, with event references and limits on interpretation. Markdown/JSON reports live under ignored `.data/run-notes/` and in configured storage. The observer exposes structured notes and a Markdown download.

| Directory | Responsibility |
| --- | --- |
| `backend/GEarth.Core` | Economy, labor, accounting, world state, weekly cohorts, observations |
| `backend/GEarth.Api` | Bounded coordination, persistence, read API, observer and run notes |
| `backend/GEarth.Tests` | Engine, queue, accounting, profiles, scheduling and persistence tests |
| `agents` | Authenticated worker, model pool, compact prompts and validation |
| `analytics` | Finished-run exports, integrity checks, Databricks notebooks and cached analytics |
| `ops` | Launcher, synthetic checks, Spark installer, AMD setup and private relays |
| `docs/gearth` | Operational guides, contracts, fixtures and historical/integration evidence |

Ricky implemented the labor economy and persistence refactors; Jayden integrated compute, endowments and observer/scheduler work; Kennedy operates visualization and live runs; Ahmad works on inference and evaluation. Outcomes describe a configured fictional society, not causal conclusions about real governments.
