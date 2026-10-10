# Faultline

An observable AI society built for KnightHacks 2026. Individual agents govern, work, trade, communicate, and respond to crises. Best AI is the primary target.

## Current status

**Stage 1 works:** a 50-agent C# simulation, Python inference worker, one DGX Spark running `openai/gpt-oss-120b`, Neon persistence, and a read-only browser observer. The recorded run completed 16 ticks with 63 accepted model decisions, one stale rejection, and zero fallbacks.

**World expansion is a draft:** ten countries, five government profiles, occupations, production, taxes, welfare, trade, cargo, credit, and diplomacy. The expanded core compiles and has basic accounting tests, but its coordinator, storage integration, larger-population qualification, and observer are unfinished. The default launcher still runs Stage 1.

The initial repository passes 15 C# tests and 5 Python tests. These checks do not establish that the expanded world runs end to end.

## Start locally

Requirements: .NET 10 SDK compatible with `global.json`, Python 3.12+, and Git. Spark access is only needed for live model decisions.

```bash
python3.12 -m venv .venv
.venv/bin/python -m pip install -r agents/requirements.txt
cp .env.example .env.local
.venv/bin/python ops/run_stage1.py --storage local
```

Open [localhost:5080](http://127.0.0.1:5080/). This runs the rule-based Stage 1 simulation without Spark or Neon credentials. The observer remains available after the run completes; Ctrl+C stops the launcher and its child services.

For live inference, connect to the team's Tailscale network and ensure SSH access to the configured Spark target in `ops/run_stage1.py`:

```bash
.venv/bin/python ops/run_stage1.py --live --storage local
```

For Neon, put your connection string in the ignored `.env.local` file and use `--storage neon`:

```bash
.venv/bin/python ops/run_stage1.py --live --storage neon
```

The launcher connects to the existing model service. It does not modify Spark networking or Netplan. Real credentials, virtual environments, runtime state, downloaded tools, and logs are excluded from Git.

## Verify

```bash
dotnet test Faultline.sln
.venv/bin/python -m pytest agents/test_worker.py -q
.venv/bin/python docs/faultline/validate_design.py
```

After a completed live Stage 1 run is being served:

```bash
.venv/bin/python ops/check_stage1.py --require-model
```

The reference-document validator checks the historical version 1 scenario and fixtures. It does not validate the expanded society or prove sponsor integrations.

## Design and implementation

- [Current plan and design](faultline-current-plan-and-design.md): the current one-Spark architecture, world design, benchmark results, and implementation plan.
- [Stage 1 runbook](docs/faultline/07-stage1-runbook.md): operation and verification of the working demo.
- [Earlier full design](faultline-full-design.md): detailed historical contracts, scenarios, and prior targets; the current plan takes precedence.
- [Recorded evidence](docs/faultline/evidence): Stage 1 results and one-Spark load sweeps.

| Directory | Contents |
|---|---|
| `backend/Faultline.Core` | Authoritative engine, accounting, routing, and expanded society draft |
| `backend/Faultline.Api` | Stage 1 coordinator, read API, and observer assets |
| `backend/Faultline.Tests` | Engine and persistence tests |
| `agents` | Python model worker and tests |
| `ops` | Local launcher, verification, and Spark benchmark |
| `docs/faultline` | Design documents, schemas, fixtures, and evidence |

## Scope and team

The current one-Spark target is provisionally 1,000 agents, subject to sustained integrated coverage tests. Short inference sweeps are recorded; no large-population result is claimed yet. Solana, Databricks, and AMD integrations remain planned or conditional. Currency and expanded escrow are currently simulated internally.

Jayden Hong: engine and integration. Kennedy Nguyen: visualization and Spark operations. Ahmad Hijazi: inference and evaluation. Ricky Liang: persistence and finance. Jayden and Kennedy have Spark SSH access.
