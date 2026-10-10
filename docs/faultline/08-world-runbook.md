# Faultline world runbook

Run the world application on Jayden's laptop. Spark and AMD supply configured inference services; the application, worker, observer and persistence client run locally. Netplan is outside this workflow.

## Start without Spark access

Install the requirements from [README](../../README.md). Then run:

```bash
.venv/bin/python ops/run_stage1.py --world --storage local --population 1100 --ticks 120 --interval 0
```

This is a fast rule-mode smoke test. It uses deterministic choices and does not measure AI capacity. For real-time rule observation, omit `--interval 0`. Open [localhost:5080](http://127.0.0.1:5080/). The observer stays available when the run completes.

## Start live inference

Connect to Tailscale and verify noninteractive SSH access to `hackathon@100.70.23.23`. Save the Neon connection string in the ignored `.env.local`; keep credentials out of chat, source and exported evidence.

```bash
.venv/bin/python ops/run_stage1.py --world --live --compute hybrid --storage neon --population 1100 --ticks 365
```

Use `--storage local` to avoid Neon. The launcher reads the existing model API key over SSH when it is absent from local configuration. It does not restart or reconfigure the model service. Only launcher-owned local child processes are stopped on exit.

Defaults are two-second ticks (one simulated day each), a weekly economy (payroll, hiring, price reviews and discretionary spending every seven ticks) and one pending request per agent. Decisions follow the weekly rhythm: every government, entrepreneur, bank, newsroom and state firm gets one turn a week, and citizens rotate so each decides once every eight weeks (about 12% a week). Before each weekly review the run waits until that week's requests have returned, so a slow model stretches wall time instead of acting on stale views; any proposal made on a view more than seven days old is rejected as expired. Model calls time out at 60 seconds and proposals expire at 65 seconds. Admission derives from enabled model capacities. Jayden's current private configuration totals 13 slots (4 institutions, 8 citizens, 1 shared AMD); these are concurrency settings, not throughput guarantees. The macOS launcher holds a `caffeinate -i` assertion while the API runs; keep the lid open because lid-closed sleep is not prevented. The observer publishes committed state every five ticks and at completion.

A spawned agent is an individual identity with state and observations. All identities share the model weights. Routine purchases, wages, production and consumption follow engine rules between model turns.

## Inspect a recorded run

Stop the current launcher with Ctrl+C before restarting on the same ports.

```bash
.venv/bin/python ops/run_stage1.py --storage neon --run-id YOUR_RUN_ID
```

This restores the committed checkpoint for inspection without inference. It does not resume interrupted pending jobs or animate historical states. Use `--storage local` for locally saved runs. The run selector can also load older Neon runs, including Stage 1.

## Verify a completed world

```bash
dotnet test Faultline.sln
.venv/bin/python -m pytest agents/test_worker.py -q
.venv/bin/python ops/check_world.py --require-model --require-full-coverage --output docs/faultline/evidence/world-live-smoke.json
```

Omit the two model flags for rule mode. The checker reconciles cash and goods from the saved checkpoint, retrieves full ordered event history with causal references, checks model coverage and request bounds, compares persistence with the API, and verifies agent pagination and read-only endpoints. It does not perform browser visual inspection or validate real-world political claims.

## Automatic run notes

Completed and gracefully stopped runs automatically export `.data/run-notes/{run_id}.md` and `.json`, and save structured notes with the run’s storage. Paused runs export a checkpoint report with an explicit partial-history label. A forced process kill cannot run finalization; inspect its saved checkpoint to generate notes for the recorded portion.

Notes summarize role-specific choices, work effort, trade, policy changes, crises, migration, elections, diplomacy, credit, country outcomes and inference failures. Examples quote recorded model explanations without treating them as proven motives. Event references make findings traceable. Download the Markdown report from the observer; older runs are analyzed when inspected without using the Spark.

## Read API

- `GET /api/runs/{id}/notes`: structured run observations.
- `GET /api/runs/{id}/notes.md`: downloadable Markdown report.
- `GET /api/health`: storage, current run and any paused status.
- `GET /api/runs`: saved IDs and current ID.
- `GET /api/runs/{id}/snapshot`: committed world and metrics.
- `GET /api/runs/{id}/events?after_seq=0&limit=500`: ordered durable event pages.
- `GET /api/runs/{id}/agents?country=aster&role=citizen&q=farmer&offset=0&limit=50`: filtered agent summaries; limit is capped at 100.
- `GET /api/runs/{id}/agents/{agentId}`: individual state, latest decision and observation, and retained recent decisions.

The model worker remains on loopback port 8100 and requires an ephemeral bearer token. Browser clients never receive that token or the Spark model key. The observer is read-only.

Agent pages and details include committed revision, event sequence and update time. Model decision ages and role metrics are documented with sample responses in [Jayden's Stage 2 review](10-stage2-jayden.md). Run `ops/check_stage2_api.py` against a completed or stopped world to check these API contracts without new inference.

## Sharing and failure handling

Use [the permanent Faultline address](https://diarockets-faultline.finebear64.chatgpt.site). Its temporary upstream tunnel reconnects automatically while the public address stays the same. The laptop must remain awake and online. Groupmates can inspect that observer; they can run rule mode locally without Spark access. They do not need direct model access to view Jayden's integrated runs. See [sharing operations](09-sharing.md).

If health reports `paused`, the last committed state remains inspectable. Check `.logs/api.log`, `.logs/worker.log` and `.logs/tunnel.log` locally. Inference failures become explicit fallback holds; fallbacks do not count toward accepted model coverage. A storage failure pauses the run after bounded retries. Restarting normally creates a new run; inspect the old checkpoint with `--run-id`.

Cash and goods are simulated. Cargo escrow and credit use internal accounting. AMD inference is implemented and verified; Solana and Databricks remain future integrations. The population is a test target, not a measured maximum. The known full-year calibration failure and old hybrid HTTP-error fallbacks remain recorded in the [maintained design](../../faultline-current-plan-and-design.md).
