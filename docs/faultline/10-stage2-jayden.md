# Jayden's Stage 2 scheduling and API review

Branch: `stage2-scheduling-api`, based on `world-simulation-run-observations` at `6da1096`. The expanded-world dependency is not merged into main. The branch also includes the preceding permanent observer sharing changes.

## Assigned work

| Trello task | Result |
| --- | --- |
| [Schedule agent decisions without growing queues](https://trello.com/c/N9Fgq3Wm) | Existing scheduling reviewed; explicit admission and cancellation added; focused tests pass. |
| [Expose expanded world data to the website](https://trello.com/c/RiwEPBVc) | Existing read API reviewed; committed revision metadata, decision ages and role metrics added. |
| [Run the expanded world with real AI decisions](https://trello.com/c/xv83H2Dc) | Reused the recorded live run and checked it through the updated API. No new model calls or repeat coverage qualification. Ahmad's worker review and Ricky's storage review remain separate tasks. |

## Scheduling behavior

`backend/Faultline.Api/DecisionQueue.cs` owns the bounded in-flight set. It rejects duplicate agent IDs, excess requests and submissions after cancellation before calling the worker. Completing one request frees its slot without waiting for another request. There is no additional unbounded backlog of request objects: due agents remain in world state until admitted.

The coordinator applies completed requests on each tick. At the tick limit it stops submitting and drains pending results. Host shutdown interrupts that drain, cancels and awaits pending tasks, discards their results, and commits a stopped checkpoint and `run_stopped` event. Worker HTTP requests retain their existing 65-second client timeout. Cancellation depends on the worker task honoring its token; the production HTTP client does so.

Default ticks target two real seconds, with an economic cycle every five ticks. This is an abstract simulation clock, not a calibrated mapping to an Earth day or year. Storage delays and host interruptions can lengthen actual tick spacing. Institution eligibility defaults to 120 real seconds after an attempt, residents to 600 seconds. Failed or rejected live turns retry after 30 seconds. Eligibility does not promise a completed turn at that frequency.

The existing 3/10 institution and 7/10 resident slot allocation remains. Tests verify every resident gets its initial turn despite a continuing institution backlog. Request high-water counts are now sampled after submission and before immediate results are removed.

## Website data

`GET /api/runs/{id}/snapshot` already contains countries, government profiles, policy proposals, relationships, shipments, loans, accounting and rejection/fallback counts.

Its `compute[0]` now also contains:

| Field | Meaning |
| --- | --- |
| `by_role` | Population, unique model coverage, accepted model decision count, never-attempted count, eligible count, pending count and model decision age p95/max for each role. |
| `pending_by_role` | In-flight requests by role at the checkpoint. Old checkpoints with pending work may lack this breakdown; their role-level pending counts are null. |
| `oldest_eligible_wait_seconds` | Longest time since an agent became eligible at the committed timestamp. Eligibility can overlap an in-flight request; this is not model latency. |

Agent list responses include `revision`, `seq`, `updated_at`, `model_decision_age_seconds`, and `next_attempt_due_at`. Details include the same revision metadata and decision age. An agent with no accepted model decision has a null age. A fallback does not refresh that age or increase model coverage.

Age is measured from the last accepted model decision to the world's committed update time. It stays fixed when viewing a completed saved run. It measures decision freshness, rather than the duration of a model request. `latency_p95_ms` remains the recent request-latency metric, and `average_model_decisions_per_second` remains the average accepted rate since run start, including interruptions.

Agent pages remain searchable by ID or occupation, filterable by current country and role, and limited to 100 results per page. Migration changes country populations, so clients should use returned counts. Responses can span different revisions during a live run; clients can use the revision metadata to identify that difference. All endpoints remain read-only.

Kennedy can use [the recorded API sample](fixtures/stage2-api-sample.json) to build panels without Spark access. It comes from a 100-agent rule run; its zero model counts are intentional. The existing real model request example remains available in agent details for the saved live run.

## Verification and observations

36 C# tests pass, including request limits, duplicate exclusion, independent completion, final-drain cancellation, resident fairness, pending telemetry and committed-time age semantics. The existing Python worker was not changed. JavaScript syntax and Python lint checks pass.

[API and lifecycle evidence](evidence/stage2-jayden-review.json) records a 100-agent, 40-tick rule run with a request high-water count of 8/8, followed by a separate graceful-stop check at tick 10. Both saved automatic observation notes, and stopped state had zero pending requests. These checks exercise integration without measuring Spark capacity.

The prior live run `world_20261010_125750_6d3812` still supplies the AI integration evidence: 1,881 accepted model decisions, 977/1,100 identities covered, 82 delivered shipments, and conserved cash and goods. Its API now passes the Stage 2 read checks. Host sleep interrupted inference, so its sustained capacity and full coverage remain unqualified. See [the existing live evidence](evidence/world-live-1100.json) and its saved observation report in the observer.

To repeat the read-only contract check against a completed or stopped run:

```bash
.venv/bin/python ops/check_stage2_api.py
```

For a small rule integration check:

```bash
.venv/bin/python ops/run_stage1.py --world --storage local --population 100 --capacity 8 --ticks 40 --interval 0.02
```

Stop an existing launcher before using its port. Direct API launches may set `FAULTLINE_PORT=5081` for isolated checks; the default remains 5080. The permanent public URL is unchanged: [Open Faultline](https://diarockets-faultline.finebear64.chatgpt.site).

Browser layout inspection and sustained live cadence remain unverified. This review does not complete teammates' worker/storage tasks or sponsor integrations.
