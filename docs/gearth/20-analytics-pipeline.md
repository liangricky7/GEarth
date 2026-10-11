# Finished-run analytics operations

The flow is simulation → Neon → finished-run export → Databricks → cached website report. Local storage uses the same audit contract for testing. Databricks receives files, never the database password, and does not change simulation state.

## Capture a new run

Fresh normal `--world` runs capture analytics by default; `--analytics` is still accepted and `--no-analytics` explicitly disables capture. Saved-run inspection and forks keep their original capture boundaries. Direct API deployments must set `GEARTH_ANALYTICS=true` for fresh runs. For an isolated rule baseline:

```sh
.venv/bin/python ops/run_stage1.py --world --storage local --analytics --population 200 --ticks 8 --interval 0
```

For an approved Neon run, use `--storage neon` and the existing private `DATABASE_URL` configuration. Analytics is off by default. Old runs without complete audit capture cannot be upgraded into complete historical decision/transaction evidence.

Capture preserves opening balances, daily and final states, original decision contexts and menus, accepted/rejected/fallback/canceled outcomes, individual transfer legs, events and immediate engine-linked effects. Money is integer minor units. Proposed policies and enacted policies are separate events. Unknown links and model metadata remain unknown. The launcher records the engine commit only when source is clean.

Public-services capture adds school enrollment and graduation, job seeking, hunger and sickness, quality-of-life components, government subsidy policies and payments, planner quotas and bank claim/state fields. Run metadata records the active feature versions, banking pause and relevant economy settings. Older exports retain their original payloads; unavailable new fields remain unknown. Deposit claims are not counted a second time as conserved cash. A numeric planner choice retains the original menu bounds and its resolved integer values.

New state-owned and private firms receive a separate `created_agent` opening-balance record before startup funding. Initial population counts stay separate; daily coverage and money reconciliation include the new firms from their recorded creation boundary. Older runs without an authoritative creation record or compatible creation event cannot be reconstructed by guessing.

This version supports normal runs. Neon forks keep the original run ID; combining them would mix timelines. Analytics capture/export therefore refuses forks until a separate timeline identity and lineage contract are added. Ordinary forks resume with analytics disabled, including when their parent captured analytics. They retain a frozen inherited audit prefix, and export marks their capture incomplete.

Daily states and individual transfers are still captured every simulated day. Neon saves the accumulated detail at each drained week boundary (ticks 6, 13, 20, …), plus the start, completion and graceful shutdown. Fresh runs with automatic renderer capture also save post-payroll checkpoints at ticks 7, 14, 21, etc., preserving actual weekly replay states. Local analytics saves remain daily. A hard interruption can lose the unsaved portion of the week; persisted results and the observer checkpoint advance weekly. This changes the application's write cadence, not Neon's backup schedule or restore window. Weekly buffering also needs memory measurement before a large run.

An audit revision becomes visible only with its successful checkpoint commit. Export accepts committed completed, stopped or failed runs. Stopped/failed runs retain a complete captured snapshot but remain partial runs.

## Analyze a finished run

Authenticate the official Databricks CLI using an OAuth profile named `gearth`. The workspace needs the schemas `workspace.gearth_bronze`, `workspace.gearth_silver`, `workspace.gearth_gold` and managed volume `workspace.gearth_bronze.run_exports`.

```sh
python3 ops/analyze_run.py RUN_ID /tmp/gearth-export-RUN_ID --storage neon --profile gearth
```

The observer defaults to `http://127.0.0.1:5080`; use `--api` for another address. The publication subprocess must have the same storage configuration as the observer. Local tests use `--storage local` and `GEARTH_RUNS_DIRECTORY` pointing to their isolated run directory. `--dotnet` and `--databricks` accept explicit executable paths.

The command exports into a new directory, verifies file hashes and snapshot coverage, uploads chunks then the final manifest, installs versioned notebooks, submits one serverless import, and downloads its report only after success. Delta row counts and source identities must match the manifest before a completion receipt is written. Publication checks the exact manifest digest and committed run revision. The website reads the cached report; it never runs Databricks on a page request.

The export directory records the remote job ID. If execution fails or times out, preserve it, inspect the job in Databricks, and correct the specific cause before retrying. No report is published after an unsuccessful job. Reimporting identical keys is harmless; conflicting records fail rather than overwrite evidence.

For separate export, investigation and publication steps, see [the analytics contract](../../analytics/README.md). Agent investigation uses the notebook's `run_id` and `agent_id` selectors. Government comparisons require known matching configuration, matched seeds and all five rotations; a single pilot cannot establish a government ranking.

## Limits to qualify before a large run

50,000 total agents × 365 days gives 18.25 million daily rows, plus opening/final rows, all transfers, decision contexts, effects and events. This row estimate is not a measured capacity result.

Measure a representative short run at the intended population before extending it to a year: database bytes, compressed export bytes, import time, query time and peak memory. Full original observations can dominate size. Driver validation/reconciliation uses disk-backed SQLite; this needs enough temporary disk and runtime. Databricks Free Edition quotas can limit a large import.

Neon checkpoint transport uses bounded parts, but retains historical checkpoint parts as well as the operational JSON state and audit. JSON preserves the engine's iteration order for main's deterministic forks. Scalar headers allow export paging without parsing the entire world. Retention/cleanup is a separate decision; no automatic deletion is enabled. Existing automatic run notes still read the complete event history at finalization, so their memory use also needs measurement.

The ledger reconciles every recorded daily/final account balance, including escrow, against opening balances and ordered transfers. A second comparison against the optional legacy verbose daily-flow buffer remains unavailable because its frame boundary differs from the audit boundary; the report labels that check `unknown`. Each individual transfer remains available. Full-year economic calibration and live model performance are separate from pipeline verification.

Public report reads are bounded and read-only. Report publication is an operator command with private database configuration; no unauthenticated write endpoint is exposed. Do not put credentials in notebooks, exports, Git or screenshots.

## Verification evidence

An earlier isolated Neon → Databricks → cached-report pilot used 160 agents and eight ticks. All four notebooks executed, repeating the import preserved row counts, and daily balances reconciled. See [the earlier evidence](evidence/analytics-pipeline-20261010.json) for source pins and job links.

Public-services verification used actual RULE runs of 170 agents for 35 days and 360 agents for seven days. The pipeline retained 24,151 records, registered six new state firms before funding, and verified two numeric planner choices in Delta. All four notebooks and replay checks passed; 13 existing tables retained their original rows and facts. Both cached reports exactly matched their Databricks files. Backend tests passed 147 with four banking skips; Python tests passed 214 after integrating current main’s worker fixes. See [the public-services evidence](evidence/public-services-analytics-20261010.json). These pilots do not qualify 50,000 agents, a full-year import or live model performance.


## Read-only run-data explorer

`/data.html?run_id=RUN_ID` inspects committed storage without importing or
running inference. Older runs stay selectable. Saved weekly replay frames,
Neon save markers and daily audit rows are distinct; a checkpoint marker does
not imply a complete historical world can be replayed. New committed runs
appear through lightweight catalog polling.

Finished-run full-row browsing is optional and operator-published. Run
`ops/analyze_run.py ... --analytics-browse` on the observer host / shared persistent
cache volume. The import notebook qualifies actual Delta data, commits its Gold
receipt, then exports actual run-filtered rows and schemas through distributed
JSON staging and bounded gzip chunks. The catalog separates available empty
Tables from missing Tables, and original JSON presence from nullable typed
columns. An offline report alone cannot establish actual Databricks capture.

The immutable browse manifest pins run, revision, sequence and source hash.
The report's optional `dataset_catalog` pins the exact browse-manifest SHA256.
Transport downloads/validates manifest before chunks and checks row/schema/payload
coverage and gzip bounds before publication. Cached dataset endpoints never
contact Databricks. Report-only workflows remain supported. Old simulations
without a cache require explicit publication and are labelled unavailable,
not zero. A newly added nullable column does not prove older rows captured it.

See [analytics operator runbook](../../analytics/README.md#run-data-explorer-and-actual-databricks-rows)
for cache placement, field-inventory limits and live qualification limits.
