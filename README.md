# GEarth

Trained on human society's flaws and virtues, how well can AI simulate the world that shaped it? We built our project, GEarth, to find out: recreating governance, trade, and conflict to watch it live.

[Devpost submission](https://devpost.com/software/diarockets) · [Browser dashboard](https://8-34-212-187.sslip.io/) · [Run locally](#run-locally)

## Inspiration

As humans, we experience both the flaws and virtues of society: how we cooperate, compete, govern, and build our lives together. AI is trained on accounts of those same human experiences, the good and the bad. That made us wonder: how well could it simulate the world that shaped it?

To find out, we built GEarth. With an abundance of computing power, our project sets out to recreate the core systems of society (citizens, governments, economies, trade, diplomacy, and conflict) and watch how AI agents navigate life together.

## What it does

GEarth is a generated/simulated Earth where AI agents represent countries, institutions, and citizens. Our default world contains 1,000+ agents across ten fictional countries.

- Citizens work, buy necessities, seek healthcare, and start businesses.
- Governments, banks, and businesses make policy decisions, provide loans, and produce and trade goods.
- Countries navigate diplomacy, resource shortages, and conflict.

These decisions interact: a shortage can affect production, production can affect employment, and unemployment can affect an entire country's economy.

Through our browser dashboard, viewers can watch the simulation unfold, explore countries, and inspect individual agents' decisions and explanations. GEarth makes it possible to follow both a society's overall development and the choices of the individuals living within it.

## How we built it

- **Simulation Engine:** We built the simulation engine in C# and .NET, giving it responsibility for time, resources, transactions, and the consequences of every action. AI agents receive observations about their circumstances and choose from actions the engine allows.
- **Distributed AI Inference:** Our Python inference workers connect those agents to Qwen3-8B and Qwen3-4B, running across two NVIDIA DGX Spark systems and an AMD GPU. We use the larger model for institutional and entrepreneur decisions and the smaller model for resident decisions. Scheduling and batching help coordinate requests across the hardware.
- **Cloud, Data & Analytics:** We use Neon PostgreSQL to preserve simulation checkpoints and event history, and Google Cloud to keep the shared world running independently of our laptops. A browser dashboard makes the simulation observable, while our Databricks analytics pipeline processes completed runs to help us investigate their outcomes.

## Challenges we ran into

- **Balancing a Functioning Economy:** Making a society function was harder than giving agents personalities. Early simulations exposed businesses hoarding money, high unemployment, and declining citizen health. We had to revisit how wages, production, healthcare, public services, and spending connected to one another.
- **Coordinating Distributed Inference:** Different machines had different capacities and response times, and model requests could time out or arrive after the world had changed. We worked on bounded scheduling, batching, validation, and fallback behavior to keep the simulation progressing.
- **Enforcing Reliable Accounting:** Every decision had to respect available money, goods, and authority. Recording events and distinguishing model decisions from rule-based fallbacks helped us understand what actually happened.

## Accomplishments that we're proud of

- Building a connected AI society where individual choices have consequences beyond a single conversation, with citizens, businesses, banks, and governments participating in the same economic system.
- Bringing NVIDIA and AMD inference into one shared simulation, preserving worlds for later inspection, and building a dashboard that makes complex interactions easier to explore.
- Designing a full-year rule-based calibration test that checks economic consistency over time, giving us a repeatable foundation alongside our live AI experiments.

## What we learned

We learned that believable agent behavior depends on the world around it. A model needs meaningful choices, useful information, and consistent consequences.

Building GEarth also taught us how interconnected society's systems are. Healthcare affects people's ability to work. Employment affects spending. Spending supports businesses, and businesses help fund governments. Small implementation choices can ripple through the entire simulation.

We learned to inspect those connections carefully and separate problems in the economic rules from problems in model behavior or infrastructure.

## What's next for GEarth

We want to expand our what-if experiments so viewers can change a policy or a citizen's circumstances and compare how alternate timelines develop. Our initial experiment system uses rule-based decisions; extending it to live AI decisions is a next step.

We also want richer visual replay, more scenarios, and longer experiments with matched starting conditions. As we scale the population, we'll measure sustained inference performance and decision coverage.

Ultimately, we want GEarth to become a place where people can explore how individual decisions shape a society, and discover outcomes we didn't anticipate.

## Built with

C#, .NET, C++, Vulkan, Python, distributed computation, Qwen3-8B, Qwen3-4B, NVIDIA DGX Spark, AMD GPU, Neon PostgreSQL, Google Cloud, and Databricks.

## Knight Hacks IX

Created by Ricky Liang, Ahmad Hijazi, Jayden Hong, and Kennedy Nguyen. The [Devpost submission](https://devpost.com/software/diarockets) received:

- **BNY:** Financial Services Challenge
- **AMD:** Best Use of AMD Technologies
- **Neon:** Best Use of Neon

---

## Current state

Main combines the daily labor-market economy, entrepreneurs, quality of life, clinics, public services, infrastructure, trade, diplomacy and war with shared Spark/AMD routing, seeded endowments, bounded scheduling, observer consistency and automatic run reports. It also includes timeline experiments, weekly analytics saves, the saved-data explorer and the Vulkan renderer. One tick is one day; payroll runs every seven ticks. Institutions and entrepreneurs decide weekly, and citizens rotate over eight weeks. Weekly decisions finish before payroll.

The [shared cloud simulation](https://8-34-212-187.sslip.io/) saves worlds to Neon. The VM uses manual runs: Start launches one experiment, and completed runs remain available for inspection. Both Sparks provide Qwen3-8B institution decisions and Qwen3-4B resident decisions; AMD supplements residents with Qwen3-4B. All five providers have completed live requests. The configured worker pool has 128 slots (16/16/32/32 on the Sparks plus 32 on AMD). The [AMD batch optimization](docs/gearth/25-amd-batch-optimization.md) uses a 90% average utilization target and passed 192/192 public requests at 2.77 valid citizen choices/second; slots are admission limits, not throughput. Brief dashboard readings can still exceed 95%. The preserved running world's engine admission and provider labels retain their startup values until the next run. The Google Cloud VM runs the world engine, so the owner's laptop can close.

GitHub `main` and the deployed VM release are separate: a push does not update an active simulation. Follow the [deployment runbook](docs/gearth/22-google-cloud-deployment.md) to build, verify and activate a release. The [timeline experiment guide](docs/gearth/26-timeline-experiments.md), [saved-data explorer guide](docs/gearth/26-run-data-explorer.md) and [renderer guide](renderer/README.md) describe their individual setup requirements.

Group members can open the website and unlock controls using the shared team password provided privately by the owner. Stop finishes in-flight decisions, saves and pauses the current world; Start resumes it. Reset saves the current run and starts a fresh world, retaining history. Everyone shares the same world, and closing a browser does not stop it. See the [cloud runbook](docs/gearth/22-google-cloud-deployment.md) for operation and access details.

See the [design](gearth-design.md) for the full specification, current limitations, decisions and their reasons. Existing deployments require the [GEarth cutover](docs/gearth/24-gearth-rename.md) to preserve saved data.

The full-year calibration test covers the economic loop; no sustained live capacity maximum is established. Old cadence-based population estimates do not describe the weekly scheduler. Banks create deposits when lending, subject to simplified capital and reserve constraints. Ore, fuel, industrial inputs and freight costs constrain production in both market and planned economies. See the [post-bank follow-up](docs/gearth/post-bank-followup.md) for the model protocol, economic fixes and validation limits. Solana settlement remains unimplemented. Fresh normal world runs launched with `ops/run_stage1.py` capture daily analytics by default (`--no-analytics` opts out); Databricks import remains an operator action; see the [analytics runbook](docs/gearth/20-analytics-pipeline.md) for capture, import and cached reports. The 50,000-agent annual workload remains unqualified.

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

The website experiment feature adds checkpoint previews, one agent/country edit,
queued control/changed timelines, and comparison charts. The first release uses
deterministic rule decisions. It is disabled by default; see the
[design and operation guide](docs/gearth/26-timeline-experiments.md) for enabling
it with server-side Neon branch credentials and for the local verification commands.

## Verify

For unattended simulation experiments while developing locally, see the [Google Cloud deployment runbook](docs/gearth/22-google-cloud-deployment.md).

```bash
dotnet test GEarth.sln
.venv/bin/python -m pytest agents ops/test_amd_model_server.py ops/test_amd_batch_server.py ops/test_compute_config.py ops/test_spark_qwen.py -q
npm ci
npm run test:observer
npm run test:data
npm run test:renderer
.venv/bin/python docs/gearth/validate_design.py
.venv/bin/python ops/check_scheduler_integration.py
```

Earlier consolidation results, including the previous calibration failure, remain in [historical integration evidence](docs/gearth/evidence/main-integration.json). The rename-time checks passed 156 C# and 231 Python tests; see [rename verification](docs/gearth/evidence/gearth-rename-20261011.json). These are historical results, not counts for the current tree. Later feature checks are recorded in [complete data-capture evidence](docs/gearth/evidence/complete-data-capture-pilot-20261011.json), [timeline experiment evidence](docs/gearth/evidence/timeline-experiments-20261011.json) and [renderer validation](renderer/VALIDATION.md). The lifecycle test uses synthetic inference, not a GPU benchmark. The reference validator checks historical Stage 1 documents and fixtures.

For observer changes, `node ops/check_observer.cjs` checks desktop/mobile rendering, saved-run switching with delayed responses, agent filters and pagination using API fixtures and installed Chrome or Edge. Screenshots are saved under ignored `artifacts/local/`.

For the integrated Vulkan globe, see the [GEarth renderer](renderer/README.md). Build it once, package a completed `ops/export_renderer.py` export with `node renderer/recording.mjs EXPORT_FOLDER OUTPUT.gearth`, then open that single file with `node renderer/run.mjs --export OUTPUT.gearth`. The recording includes all-agent checkpoints and ordered events; a full year plays in one minute by default, with adjustable pacing. Folder exports and optional `--run YOUR_RUN_ID --api http://127.0.0.1:5080` connections also remain supported. Older runs without captured history show their current snapshot with an explicit history gap.

For a completed world being served, `ops/check_world.py` checks conservation, durable ordered events, checkpoint readback, pagination, request bounds and read-only assets. Add `--require-model` for real model turns and `--require-full-coverage` only when explicitly qualifying coverage; coverage uses actual decision makers rather than owner-controlled firms. `ops/check_stage2_api.py` checks the committed API without new inference.

## Observer and reports

The agent network adds a rotating abstract field of country clusters and a fullscreen presentation view. Bright small dots are sampled citizens; larger nodes are institutions. A bounded layer of up to 2,400 ambient particles and decorative mesh adds density without creating simulated agents or affecting needs totals. Fine structural links show country membership, employment and schooling, while moving signals show recorded trade, diplomacy, conflict and closed borders. Select a country, agent or interaction to inspect it, or color the whole network by citizen food and energy needs. Needs totals include every citizen; rendering uses bounded samples and follows the selected live or saved run. Foreground lighting gives the sphere depth, recorded trade and policy events ripple through affected clusters, and presentation controls fade while idle. Rotation, zoom and motion controls work without external graphics dependencies.

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
