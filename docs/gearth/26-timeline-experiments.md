# Website timeline experiments

## Design and delivery process

Goal: select a saved world and a resumable checkpoint, change one agent or country
property, run an unchanged control and an edited timeline, and inspect their
development on the same simulated days. Preserve the parent world and agent IDs.

Build in this order: contracts and invariants; engine interventions; isolated
launcher; durable experiment service; website editor; comparison; verification.
Each layer must work independently before the next layer depends on it. Deliver a
complete deterministic experiment first, then qualify live inference separately.
This document is the implementation design, not evidence of a cloud deployment.

## First release

- Authenticated checkpoint preview, one edit, and a bounded forward horizon.
- Agent health and productivity; country government and income tax.
- One experiment executes at a time, with two isolated Neon timelines.
- Rule decisions for both timelines, explicitly labelled in the website. A LIVE
  parent is not presented as the unchanged future of a RULE control.
- Persisted progress, cancellation, results, and day-by-day comparison charts.
- Weekly drained checkpoints only, within Neon's actual history availability.
- Existing saved runs and Start/Stop/Reset continue to use the shared world.

Do not expose arbitrary JSON mutation. Cash grants, migration, education,
preference normalization, additional country levers, and multi-edit treatments
need their own invariant-preserving engine operations before being offered.

## Architecture

Browser -> HTTPS control service -> durable experiment queue -> Neon branches
and isolated engine processes -> timeline metric files -> comparison view.

The existing C# engine owns simulation behavior and intervention validation.
Python owns Neon provisioning, process supervision, checkpoint discovery, and
experiment metadata. JavaScript owns interaction and presentation. Keep the CLI
fork entry point working, but share its Neon helpers with the website service.

The controller's SQLite registry lives outside release directories on the VM.
It stores experiments, prepared previews, resource ownership, state transitions,
and a unique client request key. It never stores credentials in public responses.
Private branch connection strings are reconstructed from server configuration and
branch endpoint hosts. Timeline outputs and logs live in experiment directories.
Use one OS process lock for the runner, and database transactions for admission.
Controller restart marks interrupted work explicitly; it never reruns an edit
silently. The child process group must terminate with the supervisor.

### Identity

- Parent run ID: existing engine identity, preserved inside each isolated branch.
- Checkpoint: parent branch, run ID, revision, tick, LSN, and checkpoint digest.
- Preview ID: server-generated handle to a pinned, validated checkpoint branch.
- Experiment ID: server-generated public identity and directory name.
- Timeline ID: independent control/changed branch and metrics identity.

Never route experiment reads through the parent `/api/runs/{id}` endpoint. The
two branches share a run ID, but have separate experiment results and artifacts.
Use short generated IDs compatible with the existing 100-character path limit.

### Checkpoint selection and preview

Discover checkpoint records from Neon. Only day zero or `(tick + 1) % 7 == 0`
running-world checkpoints are eligible; renderer payroll frames are not proof of
resumability. Display the exact stored tick, without silently rounding a request.
Resolve the source branch from the configured database endpoint, including pooled
URLs, using the [Neon endpoint API](https://api-docs.neon.tech/reference/listprojectendpoints).
Do not substitute the project's default branch for a different configured branch.
On preview, branch at its LSN and read the actual checkpoint. Verify revision,
tick, world ID, status, and bounded chunk digest before returning editable values.
Expired LSNs receive an actionable unavailable response. A prepared preview pins
the starting state so it does not expire while the user edits or waits in queue.
Limit outstanding previews and reclaim unused ones after a documented lifetime.
Paged agent search reads this pinned state, not the parent's latest state.

### Intervention semantics

The engine validates target existence, resident eligibility, field, finite value,
range, and the expected old value before any mutation. Apply exactly once after
checkpoint load and before advancing simulated time. Emit a versioned intervention
event containing before/after values, actual tick, target, and experiment identity.

- Health: integer 0–100, resident only, immediate health shock.
- Productivity: finite multiplier 0.6–1.4, resident only, changes future output.
- Government: supported government profile, using `ChangeGovernment`. Preview
  explains that this also resets tax/welfare defaults and may appoint a planner.
- Income tax: whole percent 0–60, converted to basis points. A one-time experiment
  override updates the policy-change bookkeeping; later government decisions may
  change it again. It is not a permanently enforced policy.

Keep unchanged control interventions empty. Both children preserve seed, agent
IDs, world IDs, and order. Keep analytics v1 disabled for fork continuation; it
cannot safely append or export these timelines with the parent's dataset identity.

### Runner and comparison

Parameterize launcher API port, output directory, and intervention/metric inputs;
default values preserve existing commands. RULE experiments need no worker or GPU.
Use a bounded engine endpoint on loopback, separate pause directory, separate logs,
and stop the process after terminal commit and metric capture. Build once, not
inside every experiment.

For each queued job, branch twice from the pinned preview. Persist owned branch
IDs immediately, run control then changed to the same absolute end tick, validate
terminal status, and publish results only after both succeed. Retryable requests
return the original job; failures never appear completed. Cancellation kills only
that experiment's process group and retains committed partial output as partial.

Capture the selected agent (or residents aggregate for country treatments) and
country outcomes each day, including the fork-day baseline, in a dedicated
versioned metric stream. Track cash, net wealth, employment, health, wellbeing,
unemployment rate, approval, treasury, and inequality with units and definitions.
Store metric artifacts separately from existing verbose history and analytics.
Comparison joins by exact tick, with control/changed legends and an intervention
marker. Missing days remain missing; only complete matched pairs get final deltas.

## API and UI

All experiment endpoints use `/api/control/experiments` so the existing HTTPS
proxy and same-origin cookie policy apply. Mutations require team authentication.
Read results may follow the shared observer's public-read policy; preview details
and target search require authentication. Explicit routes precede the generic
`/api/control/{action}` route.

Endpoints: capability/status; run checkpoints; create preview; preview targets;
create/list experiments; get job; cancel job; get comparison. POST creation has a
client request key, preview ID, one validated intervention, and days forward.
Bound horizon, request size, queue length, result sizes, and preview count.

Website: saved run -> Create experiment -> exact checkpoint -> target/field ->
before/after review -> Run comparison -> queue progress -> paired charts/table.
Disable unavailable controls with explanations. Reuse the team login. Cancel
stale requests when switching run, preview, or experiment. Show government side
effects, RULE execution, partial failures, and the selected snapshot's values.
Closing the browser does not cancel a submitted experiment.

## Required verification

1. Engine: unchanged continuation matches the existing rule baseline; invalid or
   stale interventions do not mutate anything; accepted edits preserve conservation.
2. Provisioning: wrong checkpoint identity/digest and expired LSN fail before run;
   both branches start from the same pinned state; no write touches the parent.
3. Queue: duplicate submission, concurrent admission, restart, cancellation,
   partial branch creation, process failure, and resource cleanup are exercised.
4. UI: correct historical values, auth gating, validation, stale-response isolation,
   progress, partial outcomes, and paired result rendering.
5. Integration: a real local deterministic parent/control/changed continuation;
   separate Neon staging rehearsal verifies provisioning before cloud release.
6. Regressions: existing observer, controls, fork, audit, and renderer checks.

## Follow-up releases and rollout

Historical durability: archive full, digest-verified resumable world checkpoints
and their required event/audit prefixes prospectively. Import an archive into a
fresh isolated Neon branch, not a current parent containing later events. A
renderer frame alone cannot reconstruct a world. Older checkpoints already lost
from retention cannot be promised recoverable. Add source/schema compatibility
metadata; expose incompatibility rather than silently upgrading old worlds.

Live inference: add shared compute admission across collector and experiment
workers before enabling LIVE. Persist provider/model/configuration provenance;
use matched settings and repeated pairs. One live pair does not establish a
causal estimate. Never auto-pause the shared collector to free capacity.

Rollout: feature flag, additive schema/config changes, staging Neon rehearsal,
then deploy between runs. Preserve the VM's manual-run service override. Installing
the feature must not start a live run. Run the experiment supervisor independently
of the collector; back up the registry and metric artifacts before upgrades.
After publishing durable complete results, release owned transient Neon branches;
keep registry provenance and chart artifacts. Reclaim failed/cancelled resources
without deleting the parent or branches not registered to this feature.

## Implementation and operation

The first-release layers above are implemented on `feat/timeline-experiments`.
The service directly supervises a built .NET engine per timeline; it does not
start another collector, inference worker, share supervisor, or Spark tunnel.
SQLite persists the queue, idempotency keys, pinned previews, branch ownership,
and public result provenance. Results retain an engine binary digest and pinned
checkpoint digest. A queued job fails explicitly if its engine build changes.

Build the reviewed source first. To enable on the HTTPS cloud installation, supply
`GEARTH_EXPERIMENTS_ENABLED=true`, `NEON_API_KEY`, and `NEON_PROJECT_ID` in the
private deployment config alongside `DATABASE_URL`. `cloud_config` includes these
credentials only when the feature is explicitly enabled. A setting in
`/var/lib/gearth/control.env` overrides the release config, including an explicit
`GEARTH_EXPERIMENTS_ENABLED=false` for disabling new feature access.

Use a full source release. Compute-only `--base-archive` deployments cannot enable
this feature because their preserved engine does not include interventions and
metric capture. New source files must be tracked before packaging a release.
The controller uses `/var/lib/gearth/experiments` for its registry and artifacts;
keep this directory when upgrading. Existing cookie authentication and HTTPS
proxy routing already cover `/api/control/experiments`.
Checkpoint preparation can take longer than the original control proxy's
10-second timeout. The HTTPS installer now sets `proxy_read_timeout 180s` for
`/api/control/`. On an existing installation, update that directive and validate
and reload Nginx before enabling the feature; source releases preserve existing
HTTPS configuration rather than rewriting it.

Previews are reused for the same run/revision, expire after one hour, and are
limited to eight. Accepted jobs keep their preview branch pinned until execution
finishes even if their editor lease expires. The queue admits at most eight active
jobs. Each timeline has a 30-minute execution limit and a maximum horizon of 365
days. Queued cancellation performs no simulation; running cancellation terminates
only the experiment engine. Restarted in-flight jobs are marked failed rather than
silently replayed. Branch names are reserved before provisioning; lost POST
responses are reconciled by exact-name lookup instead of blindly repeating creation,
following the [Neon API retry guidance](https://api-docs.neon.tech/reference/listprojectbranches).
Cleanup retries only recorded owned resources. Complete
comparison files remain on the VM after transient child branches are released.

The built engine supports local checkpoint continuation for integration checks,
and the launcher accepts `--api-port` and `--output-directory` for isolated runs.
Normal launcher defaults remain unchanged. The website queue currently supports
Neon provisioning only; local branch fixtures are used by the integration test.

Verification commands:

```bash
dotnet build GEarth.sln --nologo -v quiet
dotnet test GEarth.sln --no-restore --nologo --filter 'FullyQualifiedName~ExperimentTests|FullyQualifiedName~ForkTests|FullyQualifiedName~CoordinatorAuditTests|FullyQualifiedName~ContractVersionTests' -v quiet
.venv/bin/python -m pytest ops/test_experiments.py ops/test_experiments_engine.py ops/gcp/test_controls.py ops/test_run_collection.py -q
npm run test:observer
```

The real-process integration check captures a running day-6 checkpoint from a
200-agent local world, submits a health intervention through authenticated HTTP,
runs both isolated .NET engines to day 14, checks exact daily coverage, and verifies
the original parent's file digest is unchanged. It uses local branch fixtures,
not a Neon API mock presented as live cloud evidence. Neon provisioning still
requires a staging rehearsal with real API/project credentials before release.

Local verification on 2026-10-11 passed the build with zero warnings/errors,
22 targeted C# checks, 36 Python checks, and 16 browser/observer checks (one
optional recorded-API check skipped). The local browser rehearsal also completed
the paired experiment and displayed its charts/table with no console errors.
See [verification evidence](evidence/timeline-experiments-20261011.json).
