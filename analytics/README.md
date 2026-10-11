# Finished-run analytics

This implementation imports immutable files after simulation termination. Neon is
the operational source of truth; Databricks writes analytics tables and a cached
JSON report. No notebook calls a model or writes simulation state.

## Operator workflow

Run from the repository root:

```sh
python3 ops/export_run.py RUN_ID /tmp/faultline-export-RUN_ID --api http://localhost:5080
python3 -m analytics.validate /tmp/faultline-export-RUN_ID/manifest.json
python3 -m analytics.report /tmp/faultline-export-RUN_ID/manifest.json /tmp/report.json
python3 -m analytics.build_bundle /tmp/faultline-analytics.zip
python3 -m unittest discover -s analytics/tests -v
```

Export refuses to overwrite an existing directory. An interrupted export has no
manifest and must not be imported. Optional HTTP authentication uses
`--token-env VARIABLE_NAME`; credentials never appear in exports or notebook
configuration. URL userinfo/query/fragment are rejected.
Each gzip chunk is bounded by both `--chunk-rows` (default 10,000) and
`--chunk-bytes` (default 64 MiB uncompressed). Rotation happens before a row would
exceed the byte cap. Individual records are limited to 2 MiB; a valid single row
may occupy its own chunk when an operator chooses a smaller custom byte cap.
The importer verifies optional per-chunk `uncompressed_bytes` and limits decoder
line length, so a gzip file never hides an unbounded record in the driver.

Upload every exported file to a single run directory under
`/Volumes/workspace/faultline_bronze/run_exports/`. Upload the helper ZIP to
`/Volumes/workspace/faultline_bronze/run_exports/_code/faultline-analytics.zip`.
Import the four Databricks source notebooks into the Faultline workspace folder.
Review before executing; the code creates managed Delta tables/views.

1. `01_import_finished_run.py`: parameters `export_directory` and `library_zip`.
   Validates hashes, row/sequence coverage, opening/day/final state coverage and
   duplicate semantic identities; inserts Bronze and typed Silver; writes
   `report-<first12charactersofmanifestSHA256>.json` beside the export and a Gold
   `import_receipts` row last. A receipt certifies import coverage, not that every
   economic check passed. Reconciliation failures and unknown checks remain visible.
   Before emitting report/receipt, scalar Delta aggregates verify stored Bronze
   count/distinct sequence/range and kind counts against the manifest; every Silver
   kind and derived option/edge count must match. Source hashes are cross-checked.
   `qualification-<manifestprefix>.json` records the bounded table counts for replay.
2. `02_agent_investigation.py`: parameters `run_id`, `agent_id`. Displays the
   exact original observations, ordered options, results/reasons, transfers and
   engine-linked effects. Displays explicit offered-option denominators and 1/7
   day descriptive outcome windows. The timeline preview is capped at 500
   decisions and transfer preview at 1,000; full records remain in tables.
3. `03_government_comparison.py`: all imported runs. Descriptive country facts
   include partial runs; matched comparisons require known configuration and all
   five rotations for the same seed. Shows independent run/seed sample counts.
4. `04_data_checks.py`: parameter `run_id`; optional `export_directory` and
   `library_zip`. Checks all accounts including cargo escrow, every day/end
   snapshot against a running transfer window, and row/key counts for replay.
   With the manifest directory, writes `qualification-checks-<manifestprefix>.json`
   with exact source provenance, table counts and daily reconciliation results.
   This includes available Gold receipt/view counts; only scalar aggregates enter
   the qualification file, enabling before/after replay comparisons.

Publish the resulting JSON with the operational operator report-publication CLI
and the exact source `manifest.json`. Publication validates the checkpoint and
manifest hash; website clients only read the cached report. `ops/analyze_run.py`
provides orchestration when available. Local report generation is deterministic
in facts/provenance; `generated_at` varies with execution time.
The current analysis version is `1.1.0`. A changed analysis version may replace the
derived current report alias only for identical manifest/run pins; the previous
report is preserved as `report-<manifestprefix>-<analysisversion>.json`. Conflicting
facts at the same analysis version and conflicting archives are refused. Export
chunks/manifests remain immutable. Import receipt `analysis_version` records the
first successful import; the current report records the latest analysis version.

## HTTP and file contracts

`GET /api/runs/{id}/analytics/export-metadata` returns a terminal committed
checkpoint. V1 export requires `analytics_enabled`, `audit_available`, and
`audit_from_tick_zero` to be true, `audit_schema_version=1`, nonzero audit cursor,
and status `completed`, `stopped`, or `failed`. Paused/running and historical
aggregate-only runs cannot satisfy the complete v1 audit contract.

**Temporary fork limit:** `faultline.run-export.v1` supports original run timelines
only. Neon control and changed forks preserve the parent `World.Id`, while the
current Delta keys are run-scoped. Any authoritative `timeline_forked` event
therefore rejects export before manifest finalization and rejects import/report
preflight before table writes. Inherited parent metadata does not establish a
new experimental condition. Do not rename IDs in exported records to bypass the
guard. Fork analysis requires a reviewed next-version contract with stable source
project/branch/timeline identity, parent lineage and fork point in every table key,
manifest, report and evidence reference; original operational IDs must remain intact.

`GET /api/runs/{id}/analytics/audit?after=N&limit=L&through_revision=R&through_sequence=S`
returns `{run_id,through_revision,through_sequence,records,next_after,has_more}`.
Pages are bounded to at most 2,000 records; changed checkpoints return 409.

An audit envelope is `{run_id,sequence,tick,revision,kind,payload}`. Audit sequence
and event sequence are independent. Kinds are `run_metadata`, `event`,
`opening_balance`, `agent_day`, `nation_day`, `transaction`, `decision_request`,
`decision_result`, and `action_effect`. Each real nonzero transfer leg has a
unique transaction ID, sender/recipient account IDs, actual integer
`amount_minor` and known cause/group fields. All absent provenance remains null
or explicitly unknown. Original nested payloads are retained in every table.

`manifest.json` uses:

```json
{
  "schema_version": "faultline.run-export.v1",
  "record_schema_version": "faultline.audit.v1",
  "run": {
    "run_id": "...", "status": "completed", "tick": 8,
    "through_revision": 10, "through_sequence": 1234,
    "through_event_sequence": 200, "seed": 7, "mode": "RULE",
    "population": 160, "initial_population": 160, "initial_cash_minor": 10000,
    "analytics_enabled": true, "audit_available": true,
    "audit_from_tick_zero": true, "audit_schema_version": 1,
    "metadata_unknown": ["token_usage"]
  },
  "files": [{"path": "audit-000000.ndjson.gz", "sha256": "...",
    "bytes": 1000, "row_count": 1234, "first_sequence": 1, "last_sequence": 1234}],
  "record_count": 1234, "counts_by_kind": {"transaction": 200},
  "completeness": {"snapshot_complete": true, "run_complete": true,
    "capture_complete": true, "missing_data": [], "metadata_unknown": ["token_usage"]},
  "provenance": {"source": "committed operational audit",
    "cursor": "run revision and audit sequence", "synthetic": null},
  "created_at": "2026-10-10T00:00:00+00:00"
}
```

This shortened example is illustrative, not run evidence; its kind counts are
abbreviated. Real counts must sum to all rows. SHA256 hashes cover exact
compressed file bytes; report provenance hashes cover exact manifest bytes.
Safe relative gzip paths only; no traversal, symlinks, absolute paths or Windows
drive paths. Money remains integer minor units (100 minor units = 1 WLD).

Reports use `schema_version=faultline.analytics-report.v1`, `analysis_version`,
`run_id`, `through_revision`, `through_sequence`, `source_manifest_sha256`,
`generated_at`, `completeness`, `summary`, `checks`, `agents`, `countries`,
`government_comparison`, and `evidence_refs`. Checks use `pass`, `fail`, or
`unknown` plus details and evidence references. References identify the run,
audit sequence and record kind. Agent rows are a capped preview, not a replacement
for the full investigation tables. A stopped/failed snapshot can be intact while
`run_complete=false`. Fixture status can be unknown; it is never presumed real.

## Tables, keys and limitations

Bronze `workspace.faultline_bronze.audit` uses `(run_id,sequence)`. Silver in
`workspace.faultline_silver` contains `runs`, `events`, `opening_balances`,
`agent_daily`, `country_daily`, `transactions`, `decision_requests`,
`decision_options`, `decision_results`, `event_edges`, and `action_effects`. Keys are run-scoped IDs; event keys use
their independent event sequence; state keys include tick and `frame_kind`.
`day` and same-tick `end` states are retained separately. Opening state is
separate from tick-1 daily state. Gold contains import receipts, daily agent/country
views, latest agent state, and an agent timeline view. Options retain the full raw
request alongside typed fields; typed parameter maps normalize values to strings,
so the original payload supplies exact parameter types. Replays insert only absent immutable keys; conflicting duplicates
fail. Tables are unpartitioned rather than one partition per agent/day.

Local validation and reconciliation spill to temporary SQLite files. Export
retains one HTTP page; the importer reads one compressed chunk at a time. The
report retains a capped agent preview and finite country/day summaries, never
the full ledger or decision timeline in memory. Final reconciliation uses the
latest `end` agent state and authoritative escrow snapshots. Unknown account
openings/closing snapshots remain unknown; delayed outcomes are not assigned to
the nearest earlier decision. Daily aggregate flow reconciliation is explicitly
unknown because audit.v1 does not retain the optional old daily-flow buffer.
`daily_account_reconciliation` independently checks each agent/day/end and escrow
snapshot against the opening balance plus transfers already recorded at that exact
audit sequence. A correct final balance cannot conceal a corrupted daily balance.

An initial 50,000-agent, 365-day scenario produces 18.25 million daily rows plus
opening/final snapshots, decisions, effects, events and every transfer leg.
Measure a short pilot's compressed bytes, import/query time and memory before a
full-year batch. Databricks Free Edition quotas are not assumed sufficient.
Tests are labeled synthetic and do not prove a real Neon or Databricks import.
