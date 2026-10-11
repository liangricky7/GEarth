# Phase 3 review against the current economy

Reviewed October 11, 2026. Current checkout: `gearth/phase-3-war`, at `6528738`.
Historical implementation: `9ad6be5` (reverted by `da310d2`).
Updated after an adversarial subagent review and explicit agreement on E1–E13.

**Recommendation: adapt selected parts of the earlier implementation; do not restore the commit wholesale.**
The contest function, military project types, missile targeting and relations shape are reusable. Procurement,
military eligibility, exhaustion state, government observations and political transitions need work before
integration. Repair systems, tribute caps, truces and endowment floors are model choices, not all proven defects.

This is the historical review and debate record. Its original baseline and evidence below are preserved.
The subsequent implementation and current validation are recorded in [phase-3-war.md](phase-3-war.md);
the narrowed decisions are now recorded in [next-steps.md](next-steps.md#phase-3-war-unrest-relations).

## Evidence and limits

- Current baseline: **294 .NET tests passed**, **244 Python tests passed**, design validator passed;
  observer/control checks: **11 passed, 1 skipped**. The handoff's 291-test .NET count is stale.
- Read the full historical implementation, its 14 tests, its builder's report and its shared-file changes.
  References below to “old War” mean `9ad6be5:backend/GEarth.Core/SocietyWar.cs`, with original line numbers.
- Read-only Neon download of `world_20261011_102658_3098da`: checkpoint and events through day **223**,
  **78,360 events**, **1,498 deliveries**. The handoff described this same run at day 118, with 821 deliveries.
  Calculations using the earlier cutoff are explicitly identified. This run predates infrastructure and D1–D6;
  it does not establish how those changes behave under LIVE decisions.
- In-process probes used the **current compiled engine** and temporary code under
  `/private/tmp/gearth-phase3-review`. They reproduce the integration conditions; they are not a restored
  Phase 3 run or a live model experiment. Cash and goods conservation remained enabled on every advanced day.
- No Spark run, deployment, commit or push. Credentials were read privately and not printed.
- Adversarial follow-up tested the actual historical `SocietyWar` against a temporary copy of current Core,
  with only required compatibility fields and the historical border threshold added. It did not restore all
  historical shared-file hooks. Five assertions passed: cross-war exhaustion reset, missile-blocked surrender,
  third-country embargo routing, gift quantity mismatch and striking soldiers counted as strength.
- The subagent independently probed actual public payroll, treatment availability and civilian arrears
  inheritance. Machine-readable outputs and probe sources are linked in
  [the adversarial evidence record](evidence/phase-3-adversarial-review.json).

## Consensus after challenge

The subagent argued against every item; the parent defended the evidence and narrowed proposals that exceeded it.
Both explicitly accepted the following outcomes. “Narrow” retains the concrete issue while removing broader claims.

| Item | Outcome | Agreed scope |
|---|---|---|
| E1 | Uphold | Fund military orders through the current procurement path. |
| E2 | Narrow | Fix missile-blocked surrender; recovery duration is a calibration choice. |
| E3 | Narrow | Correct striker/paid eligibility; recruitment protections are policy choices. |
| E4 | Narrow | Fix cross-war exhaustion reset; no demonstrated tax-window bug. |
| E5 | Reclassify | Cheap restoration follows Phase 2; separate combat repairs need approval. |
| E6 | Narrow | Preserve the current diplomacy guard and peace access; LIVE readiness matches the old spec. |
| E7 | Narrow | Make embargo observations match actual permissions; decide route versus bilateral semantics. |
| E8 | Uphold | Show affordability, relevant shortages and action consequences. |
| E9 | Reclassify | Show fiscal exposure; tribute caps and payment priority are optional. |
| E10 | Narrow | Correct gift sourcing and quantities; zero relations gain is valid arithmetic. |
| E11 | Narrow | Address incompatible ownership transitions; clarify unrest/election semantics. |
| E12 | Withdraw defect claim | Initial endowment ranges are not permanent bounds; a floor would change G5. |
| E13 | Uphold | Render military outcomes and old/new relation representations. |

## Reviewed findings and decision options

### E1 — Military procurement can deadlock and ignores committed spending

**Evidence:** old War `Order`, lines 376–395, adds a project without paying anything when the construction
sector exists. Current `SocietyInfrastructure.Enact`, lines 78–85, advances 50%; its `CanBuild`, lines 33–41,
also reserves the unpaid cost of active projects. The old military path does neither. After advancing the
current economy to day 56, ordering a missile for an idle contractor and staffing it with two builders left it
unfinished at day 112, with `PaidMinor = 0` and contractor cash zero. The otherwise identical advance-funded
order finished at day 70. A fresh-world contractor's seeded cash can mask this: both versions completed in
earlier day-zero probes.

**Proposed fix:** use one funded project-order path for civilian and military projects, including advance,
material purchases, outstanding commitments and enactment-time checks. Preserve the special rule that missile
orders can repeat after completion and turrets have one level.

**Approach:** rewrite the military ordering adapter around current infrastructure; retain the catalog and
missile-to-stockpile conversion. Do not replace the current contract with the historical copy.

### E2 — Missiles prevent soldier-based surrender; treatment can erase wounds in one tick

**Evidence:** old War `Fit` and `Contest`, lines 45–50 and 129–134, wound soldiers to health 20 and consider
health 30 sufficient to fight. A current-engine probe recovered a soldier from 20 to **60 in the same daily
tick**. The subagent's counterprobe kept the soldier at **20 when treatment was unavailable**: instant recovery
is not unconditional. The old wounding test explicitly permits same-day treatment and checks headcount rather than sustained
incapacity. Old War line 68 resets the no-fit-soldier counter whenever a missile exists: a country with no fit
soldiers and one unlaunched missile never reaches surrender. This contradicts W9's no-fit-soldier condition.

**Agreed correction:** count fit soldiers alone for the surrender condition; keep the agreed missile contribution
to weekly contest strength. The actual historical implementation remained at war with zero fit soldiers and
one missile after three weekly checks, with the exhaustion counter still zero.

**Optional calibration:** sustained combat recovery would require a separate timer or fitness rule. W9 says
soldiers remain out until they recover, without prescribing a duration. Same-tick treatment is a balance concern,
also acknowledged by the original builder, rather than proof that existing health treatment violates W9.
Measure recovery and war duration before selecting a new rule.

**Approach:** adapt surrender eligibility; change recovery only after a model decision. Retain contest probability
and the no-agent-removal rule.

### E3 — Military participation and recruiting disagree with the labor system

**Evidence:** old War `Fit` ignores `WorkEffort == 0`; `Paid` means only `ArrearsMinor == 0`. A current-engine
voluntary striker has daily effort zero but passes both old predicates. No arrears also does not prove that
wages were paid that week: a striker can receive no wage and still be counted as paid. Old `Recruits`, lines
283–287, can take any unskilled business employee, including essential farm/power staff, unlike the contractor's
non-essential recruiting safeguard. Recruitment overwrites employment directly and carries the person's old
arrears into the government job. The order-line menu applies the autocratic suppression floor to democracies too.

**Agreed correction:** exclude voluntary strikers from fit military strength and internal-order eligibility;
use an explicit definition of actual military payment rather than infer payment from zero arrears. An independent
call to current public payroll paid a striking soldier zero while leaving arrears zero; the old `Paid` predicate
still returned true. `WorkEffort` persists until changed by a decision, so this is not merely a transient daily flag.

**Optional policy choices:** essential-worker protections and democratic recruiting floors are not specified
military requirements. Preserve unemployed-first recruitment. Former-employer arrears also survive ordinary
`SocietyLabor.Hire`/`Fire`: a civilian hire retained 500 in arrears. Handle debt provenance as a shared labor
limitation, not a newly introduced military regression.

**Approach:** adapt fit/paid predicates and payment tracking. Broader recruiting and labor-debt changes need a
separate decision; using current hiring helpers alone does not resolve debt provenance.

### E4 — Ending one war resets exhaustion for a different ongoing war

**Evidence:** old `Nation.NoFitSoldierWeeks` is shared across all opponents; `EndWar`, line 166, resets both
nations' counters when any one conflict ends. With disarmed aster fighting beryl and cinder, one remaining
weekly check until surrender, the actual historical `Day` ended the first war, left the second active and
reset exhaustion to zero. Surrender to one opponent postpones surrender to another solely because of loop order.

**Challenge accepted:** the pre-payroll tax snapshot can define a consistent seven-day window. Its position
alone does not demonstrate missing taxes, double counting or incorrect tribute. The same army fighting every
opponent is a simplifying model rule; front allocation was not signed off.

**Agreed correction:** ending one war must not clear continuing exhaustion while the nation remains in other
wars. Preserve a clearly documented tax/payroll window. Per-conflict exhaustion is one implementation option;
preserving a shared national counter while other wars continue may be sufficient.

**Optional model choice:** dividing strength among fronts requires a separate decision and is no longer a
recommended mandatory part of the initial integration.

**Approach:** adapt counter reset logic; retain the contest formula and existing weekly cadence unless another
test identifies a specific ordering defect.

### E5 — Design interaction: routine upkeep cheaply restores combat damage

**Evidence:** old War `Strike`, line 409, removes 50 condition. Current infrastructure maintenance sets
condition straight to 100 whenever funded and staffed. A probe with a road at condition 50 returned it to
100 within 14 days; its weekly maintenance bill is **310 minor units (3.10 WLD)**. A missile costs **10,000
minor units (100 WLD)**. Once a maintenance crew is present, restoration can occur at the next weekly
maintenance pass, including the strike day with the historical hook order. Turrets receive the same restoration.

**Challenge accepted:** restoring condition through funded upkeep is explicitly Phase 2's contract. The probe
establishes a cheap restoration interaction, not that missiles are always harmless or that upkeep is implemented
incorrectly. A strike still reduces output or defense until repair, and the value depends on timing and targets.

**Optional model change:** distinguish normal wear from combat damage, funding combat repairs with materials
and work. Alternatively retain short-lived disruption as the intended missile effect and state it accurately.
Measure lost production, defense and repair delay before choosing a larger repair subsystem.

**Approach:** retain targeting, defense scaling and existing upkeep unless a coordinated repair change is approved.

### E6 — Restoring the old diplomacy menu drops a current guard and permits immediate renewed war

**Evidence:** current `GovernmentOptions` and diplomacy enactment require a failed/expired diplomatic offer.
The old shared-file changes remove that prerequisite; old `OffersWar`, lines 271–274, returns true
unconditionally in LIVE mode, so readiness and trade-first gating affect only RULE menus. This LIVE/RULE
distinction follows the historical spec and is not itself a regression. Old `EndWar` leaves
relations at their wartime score, normally -100, and records no truce. Surrender can consequently be followed
by another eligible declaration as soon as a government can act. Accepted peace adds 25 relations, which still
leaves the pair below the -60 embargo threshold. The relationships menu takes four pairs, potentially hiding
a fifth war's peace options.

**Agreed integration corrections:** retain the current failed-diplomacy guard when migrating to Score, and
ensure every active war has an accessible peace response/offer. Show readiness and consequences in LIVE views
without silently imposing RULE-only filters on the model.

**Optional decisions:** a truce, and whether peace also lifts an embargo, are new model choices. If adopted,
apply the chosen restrictions in both candidate generation and policy enactment.

**Approach:** adapt the existing diplomacy implementation rather than overwrite it with the historical hunks.

### E7 — An embargo is only a blocked road, not a ban on trading with that country

**Evidence:** the old `BorderPermits` change checks relations between the endpoints of each route edge.
Current `CanImport` and `ForeignSellers` check buyer/seller war status, but not the proposed bilateral embargo.
An embargoed seller can therefore sell directly to the buyer over a path through a third country, without
even requiring resale by an intermediary. Old War `BlockedBy` nevertheless reports that seller as embargoed.
Existing war checks already stop most new belligerent-to-belligerent orders; the new embargo check is the gap.

**Executed check:** with cinder–dune relations at -100 and the historical border threshold applied, the current
trade code found `dune_to_grove → grove_to_cinder`, and `CanImport` returned true for a fuel purchase.

**Agreed correction:** the blocked-resource view and actual trade permission must agree. The specifications
are ambiguous: W6 describes refused trades/embargo, while the build spec implements it through `BorderPermits`.
If the intended rule is only route closure, report alternate routes as available. If it is a bilateral trade
ban, add a buyer/seller permission check independently of routing, including an explicit in-flight-cargo rule.

**Approach:** adapt the view/permission boundary after deciding semantics; a bilateral ban is conditional, not
a required rewrite inferred from the route-closure spec.

### E8 — Governments cannot see the actual resource constraint or compare war choices

**Evidence:** old War `Blocked`, lines 251–266, starts only when aggregate stock is exactly zero and every
foreign stock holder is blocked. It does not examine production demand, input purchasers' cash, landed prices,
exportable stock or cargo already coming. Thus “none on hand” can be ordinary transit, while severe
unaffordability or insufficient stock can be omitted. Its raw-material mission view also includes every good.
The historical military options lack D1-style effect lines; four wars can add 16 peace-term choices before
other military actions. The old run's government menu was median 16, maximum 22 through day 118; those are
historical measurements, not the current D1 menu size.

**Proposed fix:** build the resource-shortage view from current input demand, export availability, import quotes,
funding and pending deliveries. Explain why useful purchases fail. Give recruit/discharge, procurement, gifts,
war and peace options concrete one-time/weekly costs, likely gains and consequences, with a compact set of
relevant targets. Preserve D1 and its `country.problems` list.

**Approach:** rewrite the shortage detector; adapt options and views to the current government menu.

### E9 — Fiscal exposure needs visibility; tribute caps are optional

**Evidence:** each losing weekly contest charges 10% of that country's tax delta and seizes 10% of its
business-held raw stock; there is no country-level aggregate cap. Five lost contests can therefore claim 50%
of the same week's tax base, before salaries and conflict upkeep. The saved run, without Phase 3, already
shows treasury declines from day 42 to 105: **cinder 5,399.71 → 3,732.87 WLD; haven 5,528.28 → 3,469.26 WLD**.
At day 223 they hold 2,169.12 and 1,328.02 WLD respectively. The old builder's bankruptcy result is a separate
random-menu experiment and is not proof of what real model governments will do.

**Challenge accepted:** G5 prescribes tribute; stacking obligations is not by itself a code defect. Five 10%
raw-stock seizures are sequential, removing approximately **40.951%**, not 50%, before integer rounding.
Cash tribute uses the same tax base, subject to available cash, and can sum to 50%. The saved run establishes
fiscal vulnerability, not that a cap is the correct fix.

**Agreed improvement:** show combined salaries, procurement commitments, upkeep, tribute exposure and projected
treasury runway. An aggregate tribute cap or domestic-payroll priority remains an optional policy change.
Keep the agreed 10% tribute and 25% reparations unless separately changed. Treat tribute/gifts as transfers, distinct from
commercial trade balance and the currency index; no mechanical extra currency conversion is warranted by the
current single-money accounting.

**Approach:** adapt budget observations. Do not change tribute/accounting percentages based on this review alone.

### E10 — A peaceful raw-material gift confiscates private firms' inputs

**Evidence:** old `Gift` calls `MoveGoods`, which takes a proportional share from every donor business and
puts it in the recipient's extraction firm, with no purchase or compensation. That includes ore and fuel inputs
held by other firms. Floor rounding per holder can also deliver fewer units than the advertised amount,
while the event reports the requested amount. Low-value gifts can round to zero relations gain: a default
1,000-minor cash gift against 50,000 of weekly recipient spending yields `round(15 × 1000 / 50000) = 0`.
An actual historical `Gift` call with three privately owned 11-unit ore holders moved **9 units** for a
10-unit request and emitted `amount = 10`; total goods conservation still passed.

**Proposed fix:** obtain peaceful gifts from government-owned stock or purchase an exact affordable quantity
through the economy's payment path, or explicitly decide that uncompensated requisition is intended. Transfer
and report actual delivered units/value and predicted relation gain. Zero gain is valid under the agreed
formula and is not a defect; preview it rather than silently changing the formula. Keep wartime seizure
separate from peaceful procurement.

**Approach:** rewrite goods-gift sourcing/allocation; retain the spending-relative gain and +15 cap.

### E11 — Unrest can miss recovery, and revolution leaves incompatible institutions

**Evidence:** old `Unrest` chooses any different government profile and calls `ChangeGovernment`. The current
helper changes the profile, tax/welfare settings and planner appointment, but does not convert ownership.
A probe changing aster to `planned_republic` left **15 private firms and all ownership assignments unchanged**,
while appointing a planner. `SocietyPlanning.Applies` requires state ownership, so those firms do not actually
follow the plan. The helper's emitted reason remains `timeline_fork`, even for a revolution.
In addition, old War calls `Unrest` only every seventh day: a midweek recovery does not reset the purported
continuous 28-day crisis. Scheduling an early election alone also does not guarantee W3's promised leadership
replacement; the existing election keeps a leader with at least 55% approval.

**Agreed correction:** make the transition explicit and accounting-safe, including ownership, owner claims, loans,
planner roles, pending policies and event cause. If that is too large for this phase, defer transitions across
the market/planned boundary instead of selecting them randomly. Keep ordinary leadership replacement and
political change distinct from ownership conversion.
**Specification decisions:** W3 describes n ticks of low QoL, while the build spec explicitly says weekly
sampling. Daily continuous tracking is one resolution, not an already unambiguous requirement. Likewise,
explicitly decide whether crisis elections force replacement or retain the ordinary approval rule. Scheduling
an election alone is consistent with the narrower build spec; forced replacement would alter that behavior.

**Approach:** rewrite the revolution adapter; extend the transition helper only for approved transitions.

### E12 — Withdrawn as a defect: repeated cession follows the agreed rule

**Evidence:** old `EndWar`, lines 157–162, transfers 25% of concentration without a floor or cumulative bound.
A seeded 0.25 endowment becomes 0.1875 after one cession and about **0.0593 after five**, outside the phase-1
initial range. Output scales directly with concentration. Immediate renewed war under E6 makes repeated
cession particularly concerning. One cession is the signed-off rule; the evidence does not show that every
single cession necessarily destroys mine viability.

**Challenge accepted:** the phase-1 range is an initialization range, not an invariant after war. A declining
resource base is a direct consequence of G5's approved 25% cession and remains positive for finite repeated
cessions in these examples. The arithmetic does not establish a broken mine or justify a mandatory floor.

**Agreed improvement:** show production losses in peace terms and observe the resulting economy. A minimum
endowment, cumulative cession cap or truce is optional and needs a new decision. Retain the 25% rule by default.

**Approach:** retain the cession operation; improve its preview. Withdraw the original required-bounds recommendation.

### E13 — War events survive filtering but lose their meaning in the observer

**Evidence:** current `newsworthy` defaults to true for unknown events, so `war_week`, `missile_strike`,
`war_ended`, `revolution` and recruitment are **not hidden by the noise filter**. However, `describe` falls
back to the event name and optional reason: it omits winners, tribute, wounds, damaged projects and settlement
terms. The old observer change updates only one relation line; current `diplomatic_tension` formatting would
read missing `payload.tension` when the historical event instead supplies `relations`.

**Proposed fix:** add meaningful descriptions and alerts for the military/political lifecycle, peace terms,
damage and tribute; render old tension and new Score histories correctly. Include new outcome events in run
notes/analytics where relevant, without bringing routine payment noise back into the feed.

**Approach:** adapt the current observer and reporting code; do not replace its filtering changes.

## Leads that did not establish a defect

**Relations saturation:** through day 118, the busiest pairs (`dune:grove` and `dune:fjord`) each received 41
deliveries: about **2.43 per week**, not ten or more. Replaying only the old +1-per-delivery update and integer
weekly drift from seeded starting scores put those pairs at +26 and +21; no pair reached +100. At day 223,
the busiest pair had 80 deliveries, about 2.51/week, and still no pair reached +100 in that replay. This is a
diagnostic replay of delivery/drift arithmetic, excluding counterfactual wars, gifts and diplomatic events;
it is not an integrated historical Phase 3 simulation. There is no evidence here for the handoff's predicted
trade-driven saturation. Using trade value instead of shipment count remains a spec-alignment choice, since
splitting an equal-value shipment currently multiplies the relation gain.

**Unrest remaining dormant in healthy countries:** QoL 74–80 against a crisis line of 40 does not itself
justify raising the line. War weariness only scales the 15-point security component, capped at half, so it
cannot alone knock a healthy country below 40. Retain 40/28 as provisional until current infrastructure and
D1 have run under LIVE decisions; test sustained deprivation, intermittent recovery and actual leadership
replacement. The existing unrest test calls War directly on weekly samples rather than exercising continuous
daily conditions. Weekly-only sampling can miss a recovery between checks, and an early election merely
scheduled by the old code still replaces the leader only below 55% approval. Those semantics need to be
stated when calibrating N1 rather than claimed as validated.

**Population and shared files:** preserve the current count logic, D1–D6, resources, port behavior and contractor
fixes. The handoff's single “190” figure is incomplete: current `SocietyFactory.Create` still applies a 200
floor. Do not apply the historical blanket population edits as part of restoring war.

## After decisions

Record only approved decisions in the Phase 3 table in `next-steps.md`, then integrate on the existing
`gearth/phase-3-war` branch. Replace the historical stand-alone assumptions in the war tests with integrated
procurement, recovery behavior, chosen embargo semantics, peace access and any approved truce, multiple-war,
payroll, regime-transition and save/load cases. Run a rule-mode year before a human-operated Spark experiment. Baseline checks passing today
do not validate the absent Phase 3 implementation.
