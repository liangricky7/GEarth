# Faultline

An observable AI society built for KnightHacks 2026. Individual agents govern, work, trade, communicate, and respond to crises. Best AI is the primary target.

## Current status

The expanded world is implemented: ten fictional countries, five government profiles, 1,100 individual agents, occupations, wages, production, taxes, welfare, internal escrow, moving shipments, credit and diplomacy. The observer includes country outcomes, shipment and loan ledgers, searchable agents, and their observations and decisions.

A 1,100-agent rule-mode smoke test completed 120 ticks, delivered 11 shipments and verified cash, goods, ordered events, persistence and pagination. The first live run reached 977/1,100 model coverage and 82 delivered shipments, but a laptop sleep interrupted inference and invalidated its sustained-capacity measurement. The launcher now prevents idle sleep on macOS, the scheduler retries failed turns sooner. The repeat coverage test was stopped at the user’s request; it is not required for the next implementation stage. This population is a test target, not an absolute hardware maximum.

The implementation passes 30 C# tests and 14 Python tests. Browser layout inspection remains unverified.

## Start locally

Requirements: .NET 10 SDK compatible with `global.json`, Python 3.12+, and Git. Spark access is only needed for live model decisions.

```bash
python3.12 -m venv .venv
.venv/bin/python -m pip install -r agents/requirements.txt
cp .env.example .env.local
.venv/bin/python ops/run_stage1.py --world --storage local
```

Open [localhost:5080](http://127.0.0.1:5080/). This runs the expanded world with deterministic rule choices, without Spark or Neon credentials. The observer remains available after the run completes; Ctrl+C stops the launcher and its child services.

For live inference, connect to the team's Tailscale network and ensure SSH access to the configured Spark target in `ops/run_stage1.py`:

```bash
.venv/bin/python ops/run_stage1.py --world --live --storage local
```

For Neon, put your connection string in the ignored `.env.local` file and use `--storage neon`:

```bash
.venv/bin/python ops/run_stage1.py --world --live --storage neon --ticks 420
```

The launcher connects to the existing model service. It does not modify Spark networking or Netplan. Real credentials, virtual environments, runtime state, downloaded tools, and logs are excluded from Git.

## Verify

```bash
dotnet test Faultline.sln
.venv/bin/python -m pytest agents/test_worker.py -q
.venv/bin/python docs/faultline/validate_design.py
```

After a completed live world run is being served:

```bash
.venv/bin/python ops/check_world.py --require-model --require-full-coverage
```

The reference-document validator checks the historical version 1 scenario and fixtures. It does not validate the expanded society or prove sponsor integrations.

Keep Stage 1 available by omitting `--world`. To inspect a saved checkpoint without new inference:

```bash
.venv/bin/python ops/run_stage1.py --storage neon --run-id YOUR_RUN_ID
```

World options include `--population`, `--capacity`, `--ticks`, `--interval`, `--institution-seconds`, `--resident-seconds`, `--seed`, and `--government-rotation`. Defaults are 1,100 agents, 64 concurrent requests, two-second ticks, institution attempts every 120 seconds, and resident attempts every 600 seconds. Eligibility is a target cadence; measured queue wait and coverage determine achieved cadence. The default world run lasts 360 ticks; use 420 for the longer qualification. Routine economic activity runs every five ticks. Full events are durable; the checkpoint retains recent events, decisions and each agent's last observation.

## Share the observer

Use [the permanent Faultline link](https://diarockets-faultline.finebear64.chatgpt.site). The address stays the same after restarts and deployments. The hosted Site forwards the read-only observer to the laptop; keep the laptop awake, online and running the application.

When FAULTLINE_SHARE_URL and FAULTLINE_SHARE_KEY are configured in the ignored .env.local file, the launcher starts an automatic reconnect supervisor. See [sharing operations](docs/faultline/09-sharing.md) for configuration and manual reconnects. No Spark networking changes are needed.

## Automatic run observations

Every new run saves observations at completion, graceful stop or pause. Reports describe accepted choices by role/source, work effort, trade and reroutes, policy changes, crises, migration, diplomacy, credit outcomes, country metrics, example model explanations, and inference failures. Findings reference recorded event IDs and separate descriptive behavior from political or causal claims. Paused runs use the available checkpoint and label incomplete history.

The observer shows run notes and a Markdown download. `GET /api/runs/{id}/notes` returns structured notes; `GET /api/runs/{id}/notes.md` downloads the report. Markdown and JSON copies are written under `.data/run-notes/`, with complete reports also saved in Neon or local storage. Older runs are analyzed when inspected. Saved-run inspection uses no model inference.

## Design and implementation

- [Current plan and design](faultline-current-plan-and-design.md): the current one-Spark architecture, world design, benchmark results, and implementation plan.
- [World runbook](docs/faultline/08-world-runbook.md): launch, inspect and verify the expanded world.
- [Stage 1 runbook](docs/faultline/07-stage1-runbook.md): operation and verification of the working demo.
- [Earlier full design](faultline-full-design.md): detailed historical contracts, scenarios, and prior targets; the current plan takes precedence.
- [Recorded evidence](docs/faultline/evidence): Stage 1, expanded world results and one-Spark load sweeps.

| Directory                 | Contents                                                        |
| ------------------------- | --------------------------------------------------------------- |
| `backend/Faultline.Core`  | Authoritative engine, accounting, routing, and expanded society |
| `backend/Faultline.Api`   | Bounded coordinator, read API, and observer assets              |
| `backend/Faultline.Tests` | Engine and persistence tests                                    |
| `agents`                  | Python model worker and tests                                   |
| `ops`                     | Local launcher, verification, and Spark benchmark               |
| `docs/faultline`          | Design documents, schemas, fixtures, and evidence               |

## Scope and team

The current one-Spark test target is 1,100 agents, subject to sustained integrated coverage and cadence measurements. Solana, Databricks, and AMD integrations remain planned or conditional. Currency and expanded escrow are currently simulated internally.

Jayden Hong: engine and integration. Kennedy Nguyen: visualization and Spark operations. Ahmad Hijazi: inference and evaluation. Ricky Liang: persistence and finance. Jayden and Kennedy have Spark SSH access.
