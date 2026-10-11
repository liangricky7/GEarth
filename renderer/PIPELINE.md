# GEarth renderer pipeline contract

The native viewer and launcher are now included in this repository; see
[README.md](README.md) for build, live and offline commands. Remote servers need
the renderer endpoint release deployed before they can supply these APIs.

The native launcher imports `client.mjs` directly; browser clients can reuse it. The versioned contract is
`gearth.renderer.v1`; `frame.schema.json` describes each frame. The client has no
dependencies and uses browser or Node fetch. Neon credentials stay on the backend.

For single-file delivery, `node renderer/recording.mjs EXPORT_FOLDER OUTPUT.gearth`
packages a validated export. The container starts with the eight bytes `GEARTH1\n`,
a four-byte little-endian manifest length, the UTF-8 export manifest, then each
file's original bytes in manifest order. Existing SHA-256 hashes and schemas are
unchanged. The launcher checks lengths, unique safe names and every hash; the
native reader seeks directly to records and validates frame/event coverage.
Limits are 8 MiB for the manifest, 256 MiB per entry, 10,000 entries and 2 GiB total.
Packaging refuses to replace an existing file and publishes only a complete one.

```js
import { createReplayClient } from "./client.mjs";

const abort = new AbortController();
const replay = createReplayClient({
  baseUrl: "https://8-34-212-187.sslip.io",
  runId: selectedRunId,
  signal: abort.signal,
  onEvents: events => renderer.queueEvents(events),
  onFrame: frame => renderer.showWeek(frame.snapshot, {
    week: frame.week,
    tick: frame.tick,
    partial: frame.frame_kind === "terminal" && !frame.complete_week,
  }),
});
const metadata = await replay.manifest();
// Display a history gap when metadata.captured_from_opening is false.
await replay.sync({ throughRevision: metadata.checkpoint_revision });
// Or await replay.follow({ onError: error => renderer.showConnectionError(error) });
// abort.abort() stops polling; it does not stop the simulation.
```

The native launcher's `stream.mjs` callbacks implement this handoff. Event pages arrive
before the corresponding frame; queue them for animation between authoritative
states. Callbacks are awaited, so the renderer controls backpressure. Persist both
values from `replay.cursor()` to resume. Failed callbacks do not advance their
cursor; deduplicate event IDs if a callback partially applies a page before throwing.
`sync` delivers all recorded frames through the revision pin. For a timeline slider,
fetch a frame directly from its descriptor; playback speed belongs to the renderer.
`replay.current()` can display an old run's latest checkpoint without implying replay.

## Read-only API

All paths start with `/api/runs/{run_id}/renderer`. Cross-origin GETs work without
cookies. These endpoints never mutate simulation state.

| Path | Result |
|---|---|
| `/manifest` | Version, seed, status, capture coverage and current checkpoint revision |
| `/frames?after_revision=0&limit=20` | Ordered descriptors, `next_after_revision`, `has_more`; max 50 descriptors |
| `/frames/{revision}` | One immutable frame; unknown/uncommitted revisions return 404 |
| `/events?after_seq=0&through_seq=N&limit=500` | Ordered event pages pinned to the frame's event sequence; max 500 events |
| `/current` | Latest committed state with all agents; historical coverage is not implied |

Descriptors contain `run_id`, `revision`, `tick`, `week`, `frame_kind`,
`through_event_sequence`, `checkpoint_digest` and `part_count`. Revision is a cursor,
not a week number. Frame and event sequences differ. Events carry their own ticks.
Reject unknown major versions and mixed run identities; accept additive fields.
Replace entity maps from complete frames using stable IDs, not array positions.

## Fields to draw

| Field | Meaning |
|---|---|
| `snapshot.countries`, `.locations`, `.routes` | Layout and route topology; the renderer supplies any needed visual placement |
| `snapshot.agents` | Every agent, including citizens: employment, ownership, needs, health, cash, deposits, resource inventories and estate fields |
| `snapshot.nations` | Government, employment, budgets, quotas and country outcomes |
| `snapshot.banks[].state` | Reserves, deposits, equity, loans, continuation and interest suspension |
| `snapshot.shipments` | Cargo status, route plan, edge index and remaining edge travel time |
| `snapshot.loans` | Borrower, lender, principal, status and credit creation flag |
| `snapshot.metrics` | Production, consumption, goods loss, base cash and escrow |
| `events[].event_type`, `.entity_ids`, `.cause_ids`, `.payload` | Recorded activity and causal links |

One tick is one day; seven days form a week. Money is integer minor units: 100 per
WLD. Deposits are claims distinct from base cash and cargo escrow. Null means unknown.
Closed firms remain identities (`role="defunct"`, `closed=true`); hungry residents
become nearly ineffective and can recover, rather than disappearing. Walking and
individual payments without matching events are illustrative, not recorded facts.
Shipments have recorded routes. No interpolated visual state should be used as an
economic measurement or fed back into the engine.

## Capture and offline export

Fresh normal society runs automatically capture tick 0, post-payroll ticks 7, 14,
21, etc., and terminal states even when the last week is partial. Capture works
without `--verbose` or `--analytics`. A post-payroll checkpoint is added each week;
existing pre-payroll fork checkpoints remain. Neon stages bounded, hash-verified
checkpoint parts and registers the renderer frame in the same successful checkpoint
transaction. Failed/uncommitted saves are not exposed. Local storage has the same
visibility rule. Capture changes persistence cadence, not economic rules.

```sh
.venv/bin/python ops/export_renderer.py RUN_ID /tmp/gearth-renderer-RUN_ID \
  --api https://8-34-212-187.sslip.io
```

The folder contains frame JSON files, bounded event-page JSON files and a manifest
written last with hashes. Live exports pin the initial checkpoint revision. Existing
folders and incomplete event coverage are rejected. Consumers verify hashes, accept
safe relative paths and apply files in manifest order: events precede their frame.
The launcher loads this folder or follows the HTTP callbacks. `fixtures/week-1.json` is a small synthetic parser example,
not a real simulation or a complete population.

For offline playback, `playExport` verifies the files and uses the same callbacks:

```js
import { playExport } from "./client.mjs";
await playExport({
  manifest: downloadedManifest,
  loadFile: path => uploadedFiles.get(path).arrayBuffer(),
  onEvents: events => renderer.queueEvents(events),
  onFrame: frame => renderer.showWeek(frame.snapshot, { week: frame.week }),
});
```

Here `uploadedFiles` is the renderer's map of selected file names to browser
`File` objects. Alternatively load bytes from a hosted export folder or Node files.
Browser hash verification uses Web Crypto on HTTPS or localhost.

## Existing runs and release status

Older runs have no automatic weekly archive. `/current` and events remain usable,
but absent week states are not fabricated. `world_20261011_092034_dabda2` started
on the preceding release without verbose or analytics capture. This feature does
not interrupt or upgrade that run; fresh runs after deployment capture weeks.
Forks share their parent's operational IDs, so appending to parent replays is disabled.
Separate fork identities remain future work. The default 1,100-agent payload is the
immediate target; 50,000-agent frame transport/memory still needs qualification.

## Verification, 2026-10-11

- `dotnet test GEarth.sln --no-restore`: 255 passed, including archive visibility,
  failed-save competing writers, Neon transport retries, hash verification,
  all-agent frames and cross-origin read-only HTTP endpoints.
- Agent/analytics/operations Python subset: 205 passed. Export tests cover pinned
  revisions, hashes, missing events, output-directory collisions and fixture schema.
- Five renderer-client tests passed, including offline integrity and cursor recovery.
  Existing observer/control tests passed, with the existing live-browser test skipped.
- A real local RULE run of 200 agents for 15 days delivered ticks `[0,7,14,15]`
  and 1,377 ordered events through both HTTP callbacks and a verified offline export.
  All four frame files passed the published schema. This is a parser/integration
  check, not model-behavior or large-population performance evidence.

Neon persistence is covered by protocol tests; this feature has not been deployed
or tested against production Neon. The active model simulation remains untouched.
