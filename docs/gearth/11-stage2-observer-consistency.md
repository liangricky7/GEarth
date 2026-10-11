# Stage 2 observer and API consistency

Branch: `stage2-observer-consistency`, based on `stage2-scheduling-api` at `f60d36f`. This closes the observer/API handoff issue recorded on [Jayden's website-data task](https://trello.com/c/RiwEPBVc).

## Changes

The recorded 420-tick run failed because policy events encode `payload.action` as a string, while accepted and rejected decisions encode it as an object. The event renderer now handles both shapes and missing optional parameters. Existing events and their historical meaning are preserved.

Run changes clear all prior panels and cancel the previous requests. Each refresh captures its run and selection generation; responses from older selections cannot update the page. The committed world renders before secondary panels finish. Temporary failures retain the last successfully loaded panels and label the connection as reconnecting. Events, notes and agent pages recover independently; changing runs still clears the previous run to prevent mixing data. Requests time out after ten seconds, and a long disconnection catches up only the recent 500-event feed. Agent profiles have separate cancellation and identity checks; periodic refreshes let an unfinished profile request complete.

Agent decision age uses the committed API timestamp, so inspecting a saved run does not make its displayed age increase with the viewer's clock.

`GET /api/runs/{id}/events` now accepts optional `through_seq`. Results are bounded by that sequence and the currently committed sequence. The observer uses the snapshot sequence as its boundary, leaving newer events for the next refresh. The current feed still shows a bounded recent window, rather than a full causal-history explorer. During a running simulation, agent-page and note responses may reflect a later revision of the same run; this change does not introduce a multi-endpoint snapshot transaction.

## Verification

Nine offline DOM regression tests pass, including outage retention, independent panel recovery, hung-request timeouts and bounded event catch-up. The earlier checks cover: mixed event shapes, delayed run responses, failed-load recovery, stale profiles and committed decision ages, and search/pagination during active requests. An additional DOM integration check passes with actual API responses from the saved completed and stopped live runs, including completed → stopped → completed switching and agent inspection. These checks execute the real observer script with JSDOM; they do not verify browser layout.

The .NET build passes with no warnings or errors. The API checker passes the event boundary, coverage metrics, decision-age, search, pagination, metadata, read-only and note-consistency checks. [Recorded checks](evidence/stage2-observer-api.json) include the matching public asset hash. No simulations or model calls were started, and existing observation reports remain available.

```bash
npm ci
npm run test:observer
FAULTLINE_OBSERVER_URL=http://127.0.0.1:5080 npm run test:observer
.venv/bin/python ops/check_stage2_api.py
```

The optional API integration test reads two historical run IDs from the project's existing Neon dataset. A fresh local store can run the five offline tests without those saved runs. Node.js is only needed for these checks; the observer still uses plain JavaScript without a frontend build step.

The share address remains [Faultline observer](https://diarockets-faultline.finebear64.chatgpt.site). The laptop must stay online and running. Its temporary tunnel needed reconnection during verification; the permanent address was restored and all six checks then passed.

## Design document from main

`faultline-full-design.md` was copied exactly from `origin/main` at `0833da5`, including the economy revision committed as `da090c0`. It describes the labor-market redesign and supersedes the earlier economy plan. This branch still serves the previous saved world; pulling the document does not incorporate the newer engine code. Integrating main's changing roles, business ownership, coverage denominator and time scale remains a separate step.

This fixes the reported rendering and run-selection issue. Expanded visualization, causal navigation and the labor-market observer remain Kennedy's broader work. It does not qualify sustained Spark capacity or finish the entire team's Stage 2 review.

The live simulation continues during static observer updates. Reconnection operations now hold a file lock, preventing a manual repair and the share supervisor from replacing each other's tunnel. One focused concurrency regression test verifies this lock. The external localhost.run tunnel may still return transient errors; the page preserves loaded data and retries, while the local simulation keeps saving checkpoints.
