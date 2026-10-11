# Inspect saved simulation data

The data explorer is a read-only companion to the world observer. Runs share storage tables, separated by `run_id`. Opening the explorer does not start a simulation, export, import, or Databricks job.

## What to inspect

1. Open **Data explorer** from the observer. Select a run; new saved runs are discovered automatically without replacing the run you selected.
2. Inspect saved checkpoints. A **save marker** records that a save occurred; a **replay frame** contains a retained world state. They are different kinds of evidence. Opening tick 0, weekly frames, and the terminal state are labelled separately.
3. Choose a week and record kind to inspect original daily records. Each request reads a bounded committed page. A page with no matching records can still have more records to scan; use the next-page control.
4. Inspect the captured feature metadata and field coverage. Missing historical fields, explicit nulls, and recorded zero/false values mean different things. Fields seen on a displayed page are not proof of complete run-wide coverage.
5. For an explicitly analyzed and published finished run, inspect its report, actual Databricks column schemas and per-run field counts, and complete cached table rows. Raw JSON remains accessible when no dedicated typed column exists.

## Finished-run data publication

The existing operator workflow captures an audit, exports a finished run, imports it into Databricks, then publishes results to the observer. Analytics capture and imports remain explicit. See [analytics operations](20-analytics-pipeline.md).

The Databricks browse bundle contains actual run-filtered table/view rows, measured schemas and field coverage, and hashed gzip chunks. Its catalog is bound to the report's source export, revision and sequence. The API reads the immutable published cache; it does not query Databricks or start compute on page requests.

Set `GEARTH_ANALYTICS_BROWSE_DIRECTORY` to persistent storage on the observer host. The default is `.data/analytics-browse`. Publishing on a different computer does not populate the observer host's filesystem cache. Retain this directory across releases.

Older reports remain readable. Runs without a new browse bundle show report-only or unavailable row-cache status. Missing publication evidence does not prove that no import exists in Databricks. Existing runs need an explicit updated import/publication to obtain this new cache.

## Interpretation limits

- The operational source is the configured local store or Neon branch. Same-ID fork histories are not combined across branches.
- Weekly replay frames do not reconstruct every day's full world. Daily audit rows provide the separately captured daily detail.
- Feature metadata describes that run's capture contract. Current source code or current object defaults cannot establish what an older run recorded.
- A globally added Delta column can be null for older runs. Column existence alone is not historical capture evidence.
- The original report's agent list is a preview; use cached datasets for rows beyond that preview.
- Neither synthetic checks nor this browser qualify a 50,000-agent, 365-day workload. Bundle generation, import, disk usage and query performance require separate measurement.

Implementation and acceptance checks follow the Astra plan prepared on October 11, 2026. Production deployment and an actual Databricks browse-bundle import are separate from local verification.

## Local verification — October 11, 2026

- Full C# suite: 293 passed. Python analytics/operator suite: 77 passed, plus two subtests.
- Browser behavior tests: 15 explorer, 13 observer/control and three report tests passed; the existing live observer test remained skipped.
- Headless Chrome checks passed at desktop and mobile sizes, including old/new run selection, original JSON, weekly replay, report sections and dataset rows after row 100. The real local API also rendered successfully in Chrome.
- A local RULE simulation verified all daily records for days 1–14 across 20 bounded pages, retained weekly frames, and a separate final frame. A second local run was discovered without replacing the selected older run.
- A clearly synthetic Python-generated bundle was accepted by C# publication and row pagination, including explicit nulls. This checks format compatibility, not actual Databricks execution.

For actual finished runs, use the existing operator command with `--analytics-browse`, and publish on the observer host with its persistent browse directory configured. Verify one real import and its website datasets before declaring production readiness. A full-scale 50,000-agent run remains unmeasured.
