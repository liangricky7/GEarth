# Phase 3 implementation and continuation

October 11, 2026. Continued from integrated `main` at `7d28121` on
`feat/phase-3-completion`. The user's current request authorizes a new branch and incremental local
commits; it supersedes the older branch/commit restrictions in the resume document. No historical
implementation was cherry-picked and the original review artifacts were preserved.

## Result

The existing Phase 3 implementation now keeps partial raw-material shortages visible, quotes actual
domestic/foreign purchasing opportunities, and prevents stale peace responses or repeated settlement
from transferring money or endowment twice. Government response options explain the terms being
accepted and bind new delayed responses to those observed terms and expiry.

The integrated military procurement, striker/pay eligibility, soldier-based surrender, multiwar
exhaustion, gift sourcing, ownership-compatible unrest, scored relations and observer descriptions
were retained and checked against the current economy. This is local functional verification, not
evidence of calibrated live-model behavior or deployment readiness.

## Changes since the resume checkpoint

- `SocietyWar.ResourceNeeds` retains every positive staffed next-week input gap after buyer inventory
  and incoming cargo. Domestic offers show stock and prices; an aggregate affordable quantity shares
  the buyer's budget across cheapest offers. Foreign holders show exportable stock, one-unit access
  reasons, affordable quantities, and full-gap escrow/funding requirements. Quotes are independent
  alternatives sharing the same funds, not quantities that can be added. Delivery within the forecast
  week is not guaranteed. `View` computes the needs once.
- The rule-war blocked-access predicate requires no domestic or foreign buying access. A shortage
  with one affordable domestic unit stays visible without becoming a new reason to offer war.
- `EndWar` is idempotent for a relationship that is already peaceful. War start/end clears offers.
  Responses require a valid offer for the current war/trade state. New response candidates carry the
  observed offer kind, terms and expiry; enactment rejects a replacement offer. Legacy queued responses
  lacking these added fields retain compatibility and still receive current-state checks.
- Peace response menus show transfer consequences for the responding country, plus relation effects.
- New tests cover shortage visibility, route-around access, bilateral wartime blocks, planned quotas,
  incoming cargo, cash/freight/shipment limits, stale settlements, offer replacement, legacy world tax
  snapshots, treasury-owned gifts, election approval, revolution policy cancellation, missile interception
  and ordinary upkeep restoration. Observer tests exercise real DOM text, historical/new relations and
  events, and hostile event text. The analytics rendering fixture now handles Windows line endings.
- A repeatable 365-day integration test uses the production deterministic rule chooser and weekly
  cohorts, seeds one war and two available military/gift policies, and checks conservation daily.
  Documentation now describes the actual implementation rather than historical inventory destruction.

## Scope and retained choices

The [historical adversarial consensus](phase-3-review.md) remains the design basis. No fresh subagent
debate occurred in this continuation; historical instructions in the attachment were treated as
context, and the current user requested branch work and commits. Review here used code inspection
and executable counterexamples, with no claim of new independent consensus.

- Direct-route embargo threshold -60; declaration threshold -20 with failed diplomacy and funding.
  Open third-country paths remain valid outside war. LIVE bypasses only readiness/need heuristics.
- Half-cost advances and unpaid project commitments use the current state contractor lifecycle.
  Missiles cost 10,000 minor, require two builders/seven working days; turrets cost 40,000 and require
  four builders/35 working days. Materials, workers and funding can delay completion.
- Actual positive recent soldier payments count for internal order, including funded conflict pay.
  The seven-day window is inclusive because war checks precede payroll. Existing arrears provenance
  and unemployed-first recruiting remain unchanged. Declaration checks 5,000 liquid minor; soldier
  payments have no sink when no soldiers exist, so actual transfer can be zero.
- A lost contest transfers 10% of the preceding tax window and per-holder raw stock. Multiwar stock
  losses compound; no new tribute cap, payroll priority or front allocation was introduced. Tax
  initialization excludes cumulative taxes from old checkpoints. Cession moves 25% of productive
  endowment, without imposing the initial endowment bounds as permanent limits.
- Wounds use normal treatment; missile damage uses ordinary funded 1% weekly upkeep. No special
  wound timer or combat-repair economy was added.
- Unrest is checked weekly; elections use normal approval. Political revolutions preserve ownership
  compatibility. Planned-to-market ownership and creditor transitions remain explicitly deferred.
- Calibration defaults remain QoL 40, 28 days of unrest, 5% recently paid soldiers, three strength per
  missile, weariness +0.1 per war week capped at 0.5, and the existing readiness table.

## Validation

The two previously unverified corrections passed in the initial 18-test targeted run. Subsequent
targeted Phase 3 tests passed, including the new political, checkpoint and missile cases.

Final suite counts and the annual scenario are recorded in
[phase-3-completion.json](evidence/phase-3-completion.json). Commands used on Windows:

| Check | Result |
|---|---|
| .NET suite excluding the separately run annual scenario | 336 passed |
| Phase 3 rule-decision year | 1 passed; combined .NET total 337, zero failed |
| Observer, analytics, controls and experiments | 20 passed, 1 skipped |
| Portable Python subset | 120 passed |
| Design validator | Passed: 35 JSON artifacts and 25 Markdown files' local links |

Local commits: `6d6c855` (raw-input observations), `71aa284` (peace/proposal settlement),
and `d9bef25` (integration, annual scenario and observer regressions). Documentation/evidence follows
as a separate commit. The checkout is a fresh clone under this chat's `work/GEarth` directory.

```powershell
dotnet test GEarth.sln --no-restore --filter FullyQualifiedName~Phase3YearTests
dotnet test GEarth.sln --no-restore --filter 'FullyQualifiedName!~Phase3YearTests'
node --test tests/observer.test.cjs tests/observer.analytics.test.cjs tests/controls.test.cjs tests/experiments.test.cjs
.\.venv\Scripts\python.exe -m pytest agents ops/test_amd_model_server.py ops/test_amd_batch_server.py ops/test_compute_config.py -q
.\.venv\Scripts\python.exe -X utf8 docs/gearth/validate_design.py
```

The annual test passed at day 365: 15,463 rule decisions, one weekly contest, one war termination,
18 gifts, two completed missiles, five revolutions, 47 early-election calls and 11 deferred revolutions.
It began with 1,100 agents; the final 1,288 agent records include newly created businesses. No model
inference was used. The fixture seeds the war and initial policies deliberately, so this is not proof
that an unmodified starting world naturally chooses war or that asynchronous live scheduling works.

The annual scenario is a conservation/lifecycle test, not a healthy-economy calibration test. Final
needs-met was zero in five countries and below 0.051 in four others; Aster was about 0.602. Those
outcomes and repeated unrest remain a calibration concern. Existing passive annual calibration tests
are separate checks; their bounds were not weakened.

Python's Spark suite could not collect on Windows because `ops/spark_qwen.py` imports Unix `pwd`.
The remaining requested subset passed 120 tests. Installed the existing pinned worker requirements
in an ignored local virtual environment, plus `tzdata` for the design validator on Windows; repository
dependency manifests were not changed. The recorded-live observer integration test is skipped because
no recorded API endpoint was supplied. Browser visual layout, GPU/model serving, cloud storage and
deployment were not exercised. No push, PR, merge, external messages or deployment was performed.

## Subsequent approved infrastructure update

The e1efff fixes supersede the original ordinary-upkeep restoration assumption above: routine upkeep prevents wear; queued repairs restore accumulated damage with builders, proportional materials, fees and time. The missile regression now asserts that routine upkeep preserves 50 condition after a hit. See the post-infrastructure entries in `next-steps.md`.
