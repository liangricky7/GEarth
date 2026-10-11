# Finished-run analytics operations

The flow is simulation → Neon → finished-run export → Databricks → cached website report. Local storage uses the same audit contract for testing. Databricks receives files, never the database password, and does not change simulation state.

## Capture a new run

Add `--analytics` to a new world run. For an isolated rule baseline:

```sh
.venv/bin/python ops/run_stage1.py --world --storage local --analytics --population 160 --ticks 8 --interval 0
```

For an approved Neon run, use `--storage neon` and the existing private `DATABASE_URL` configuration. Analytics is off by default. Old runs without complete audit capture cannot be upgraded into complete historical decision/transaction evidence.

Capture preserves opening balances, daily and final states, original decision contexts and menus, accepted/rejected/fallback/canceled outcomes, individual transfer legs, events and immediate engine-linked effects. Money is integer minor units. Proposed policies and enacted policies are separate events. Unknown links and model metadata remain unknown. The launcher records the engine commit only when source is clean.

This version supports normal runs. Neon forks keep the original run ID; combining them would mix timelines. Analytics capture/export therefore refuses forks until a separate timeline identity and lineage contract are added. Ordinary forks resume with analytics disabled, including when their parent captured analytics. They retain a frozen inherited audit prefix, and export marks their capture incomplete.

An audit revision becomes visible only with its successful checkpoint commit. Export accepts committed completed, stopped or failed runs. Stopped/failed runs retain a complete captured snapshot but remain partial runs.

## Analyze a finished run

Authenticate the official Databricks CLI using an OAuth profile named `faultline`. The workspace needs the schemas `workspace.faultline_bronze`, `workspace.faultline_silver`, `workspace.faultline_gold` and managed volume `workspace.faultline_bronze.run_exports`.

```sh
python3 ops/analyze_run.py RUN_ID /tmp/faultline-export-RUN_ID --storage neon --profile faultline
```

The observer defaults to `http://127.0.0.1:5080`; use `--api` for another address. The publication subprocess must have the same storage configuration as the observer. Local tests use `--storage local` and `FAULTLINE_RUNS_DIRECTORY` pointing to their isolated run directory. `--dotnet` and `--databricks` accept explicit executable paths.

The command exports into a new directory, verifies file hashes and snapshot coverage, uploads chunks then the final manifest, installs versioned notebooks, submits one serverless import, and downloads its report only after success. Delta row counts and source identities must match the manifest before a completion receipt is written. Publication checks the exact manifest digest and committed run revision. The website reads the cached report; it never runs Databricks on a page request.

The export directory records the remote job ID. If execution fails or times out, preserve it, inspect the job in Databricks, and correct the specific cause before retrying. No report is published after an unsuccessful job. Reimporting identical keys is harmless; conflicting records fail rather than overwrite evidence.

For separate export, investigation and publication steps, see [the analytics contract](../../analytics/README.md). Agent investigation uses the notebook's `run_id` and `agent_id` selectors. Government comparisons require known matching configuration, matched seeds and all five rotations; a single pilot cannot establish a government ranking.

## Limits to qualify before a large run

50,000 total agents × 365 days gives 18.25 million daily rows, plus opening/final rows, all transfers, decision contexts, effects and events. This row estimate is not a measured capacity result.

Measure a representative short run at the intended population before extending it to a year: database bytes, compressed export bytes, import time, query time and peak memory. Full original observations can dominate size. Driver validation/reconciliation uses disk-backed SQLite; this needs enough temporary disk and runtime. Databricks Free Edition quotas can limit a large import.

Neon checkpoint transport uses bounded parts, but retains historical checkpoint parts as well as the operational JSON state and audit. JSON preserves the engine's iteration order for main's deterministic forks. Scalar headers allow export paging without parsing the entire world. Retention/cleanup is a separate decision; no automatic deletion is enabled. Existing automatic run notes still read the complete event history at finalization, so their memory use also needs measurement.

The ledger reconciles every recorded daily/final account balance, including escrow, against opening balances and ordered transfers. A second comparison against the optional legacy verbose daily-flow buffer remains unavailable because its frame boundary differs from the audit boundary; the report labels that check `unknown`. Each individual transfer remains available. The upstream full-year economic calibration test remains failing and is separate from pipeline verification.

Public report reads are bounded and read-only. Report publication is an operator command with private database configuration; no unauthenticated write endpoint is exposed. Do not put credentials in notebooks, exports, Git or screenshots.

## Verification evidence

An isolated Neon → Databricks → cached-report pilot used 160 agents and eight ticks. All four notebooks executed, repeating the import preserved row counts, and daily balances reconciled. See [the recorded evidence](evidence/analytics-pipeline-20261010.json) for source pins and job links. This does not qualify the planned full-size workload.
