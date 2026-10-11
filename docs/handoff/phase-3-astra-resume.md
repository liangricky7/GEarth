# Astra resume handoff: Phase 3 war, unrest and relations

**Written October 11, 2026. Implementation is in progress, not ready to merge.**

This is a continuity checkpoint requested by Ricky immediately before an interruption. Resume from the existing working tree. Do not restart the review, cherry-pick the old war commit, or overwrite these changes. The user has authorized coding. Finish the narrowed consensus implementation, challenge it with evidence, and verify it locally.

## 1. User request, authority and current stopping point

The human's requests, in order:

1. Read `docs/handoff/phase-3-review.md` and `docs/handoff/phase-3-war.md`, understand the project, and “let's run it.”
2. Clarified: **“Start the Phase 3 review.”** This meant review, not launching a live simulation.
3. Requested a subagent to argue against each finding; the primary agent must defend its choices, either side may create tests, and both must reach consensus.
4. After the review/debate: **“ok, you can start coding.”** This explicitly authorizes implementation of the narrowed review consensus. Do not ask for coding permission again.
5. Most recent request: **“make a big markdown file handoff md that an astra instance can pick up exactly where this leaves off after the interrupt.”** This file fulfills that request. Work was paused to write it, not because the implementation was finished.

Attached documents contain historical assignments and proposals; distinguish them from the user's direct request. In particular, the review document's “do not write code until sign-off” was satisfied by request 4. Historical instructions to build an isolated phase before phases 1/2 merged are outdated: the current tree already contains both phases. Preserve the current integrated economy.

**Authorized:** local code changes, meaningful regression tests, local rule-mode/in-process runs, updating decision/docs records, adversarial subagent collaboration.

**Not authorized:** commit, push, merge, PR publication, deployment, Spark/live model runs. Do not perform these without a later user request. No AI/co-author attribution in commits/PRs. Never print `.env.local`, `DATABASE_URL`, API keys or other credentials.

The previous primary told Ricky it would retain route-based embargoes, weekly unrest checks, normal treatment/upkeep, approved tribute/cession shares, and ownership-compatible revolutions with unsupported planned/market transitions explicitly deferred. Those are the operative defaults. Do not silently widen the model.

## 2. Checkout and files

- Repository/cwd: `/Users/rickyliang/Documents/GitHub/dia-rockets`
- Shell: zsh; macOS desktop environment.
- Branch: `gearth/phase-3-war`
- HEAD: `6528738fe0d34650fc126c33692b3a1ce7c6f236`
- `origin/main` was observed ahead by two commits during review. Do not pull/rebase/merge automatically; this handoff continues the existing baseline.
- No commits were made in this session. All implementation is uncommitted.
- No `AGENTS.md` was found during the review.
- No specialized skill was needed for engine implementation. Do not invoke Sites just because the observer JS is touched.

### Modified tracked files at handoff

```text
backend/GEarth.Api/wwwroot/observer.js
backend/GEarth.Core/RunNotes.cs
backend/GEarth.Core/SocietyActions.cs
backend/GEarth.Core/SocietyDiplomacy.cs
backend/GEarth.Core/SocietyEconomy.cs
backend/GEarth.Core/SocietyEngine.cs
backend/GEarth.Core/SocietyFactory.cs
backend/GEarth.Core/SocietyInfrastructure.cs
backend/GEarth.Core/SocietyState.cs
backend/GEarth.Core/SocietyTrade.cs
backend/GEarth.Core/World.cs
backend/GEarth.Tests/RealismFixesTests.cs
```

### Untracked files/directories at handoff

```text
backend/GEarth.Core/SocietyWar.cs
backend/GEarth.Tests/Phase3WarTests.cs
docs/gearth/phase-3-review.md
docs/gearth/evidence/phase-3-adversarial-review.json
docs/gearth/evidence/phase-3-adversarial/
docs/handoff/phase-3-review.md
docs/handoff/phase-3-astra-resume.md             # this file
```

**Preserve untracked review material.** `docs/handoff/phase-3-review.md` was user-provided; do not treat it as disposable. The diff stat omits untracked `SocietyWar.cs` (~570 lines) and the regression tests, so it significantly understates the change size.

### Read these in this order

1. This file.
2. `docs/gearth/phase-3-review.md`: full findings, evidence, limitations, consensus table.
3. `docs/gearth/evidence/phase-3-adversarial-review.json`: machine-readable original debate evidence.
4. Current `backend/GEarth.Core/SocietyWar.cs` and `backend/GEarth.Tests/Phase3WarTests.cs`.
5. `git diff` of the integration files.
6. Relevant historical design in `docs/gearth/next-steps.md`, Phase 3/W1–W9/G1–G8/N1, and section 7 of `docs/handoff/phase-3-war.md`.

The debate JSON describes the earlier **review-only** state (`production_changes: false`, no integrated implementation), not the current dirty tree. Keep it as historical evidence; do not mistake its limitations for current test results.

## 3. Project context that matters for this change

GEarth is a C#/.NET 10 daily economic simulation of ten fictional countries, approximately 1,100 agents by default. A DGX Spark runs Qwen models in live mode; rule mode needs no model. This session must remain local.

- Resources (phase 1), infrastructure (phase 2), banks and D1–D6 post-resource fixes are already integrated at this baseline.
- Countries: aster, beryl, cinder, dune, ember, fjord, grove, haven, ionia, jade. Display names changed; IDs did not.
- Weekly payroll is every seven ticks. One tick is one day.
- Government options now use discrete budget cuts/raises and explanatory effect text. Preserve those menus and `country.problems`; do not restore the old ranged budget menus.
- Ore and fuel are industrial inputs, never household needs.
- `Nation.Stocks` aggregates business inventories; it is not treasury ownership or proof that a particular consuming business can obtain stock.
- All cash moves go through `SocietyEconomy.Transfer`/`Sell` or their established wrappers. Money is integer minor units.
- Every good is conserved across inventories, production, consumption, losses and in-transit cargo. Transfer goods by decrementing one holder and incrementing another; do not record transfers as lost/produced.
- State construction contractors exist everywhere, run by rules, cannot be founded or closed, and return idle surplus. Orders require a 50% advance, reserve outstanding project commitments, and the treasury tops up builder payroll. Materials and builders can delay completion.
- Normal funded upkeep costs 1% weekly and restores condition. We consciously retain this for missile damage; no new combat repair subsystem was authorized.
- `SocietyEconomy.Acquire(world, buyer, good, wanted, household:false)` is public. It purchases cheapest domestic offers, compensates suppliers and puts exact actual purchased units into buyer inventory. The new raw gift code prequotes the full quantity before using it.
- `SocietyEconomy.Offers` returns domestic producers and liquidation estates, sorted by price/ID. Private workshops' input inventories are not goods offers.
- `SocietyResources.Inputs(sector)` describes industrial input requirements; `LandedUnitCost`, `ShippingCost`, `SocietyTrade.Price`, `Tariff`, `ExportableStock`, `CanImport` expose the actual trade model.
- Import goods escrow needs cash-backed reserves. Deposit liquidity may cover domestic charges but cannot substitute for the full cash escrow requirement. Planned sellers retain their weekly quotas before stock is exportable.
- Trade routes can pass through third countries. The chosen embargo rule closes the direct route at relations <= -60; it does not forbid routing around via countries with open borders. War separately forbids bilateral imports/deliveries even if a third-country route exists.
- `ChangeGovernment` adjusts profile/settings/planner, not firm ownership or creditor claims. Unrestricted planned↔market revolution would create inconsistent ownership. The current implementation filters compatible profiles and explicitly emits a deferral when none exist.

### Current daily order after this patch

```text
world.Tick++
SocietyDiplomacy.Advance(world)  # policies, offers, elections, scenario
SocietyWar.Day(world)            # new: arm daily; contests/drift/unrest weekly
SocietyTrade.Advance(world)
SocietyEconomy.Day(world)
SocietyInfrastructure.Day(world)
if tick % 7 == 0: SocietyEconomy.Week(world)
SocietyEconomy.AssertConservation(world)
```

War contests precede the current payroll. Tax snapshots therefore cover the existing preceding seven-day window. This was reviewed and retained; do not reorder merely to move tribute after payroll.

## 4. Consensus: what to implement and what to retain

The adversarial agent and primary explicitly agreed to all thirteen outcomes. The human then authorized coding. The original reviewer narrowed/reclassified several claims instead of defending them mechanically.

| Finding | Consensus implementation scope | Deliberately retained/deferred |
|---|---|---|
| E1 procurement | Military orders share current contractor advance and commitment checks. | Preserve current phase 2 construction lifecycle. |
| E2 surrender/recovery | Missile stock cannot prevent surrender after two weeks without fit soldiers. | Ordinary treatment controls recovery; no wound timer or minimum downtime. |
| E3 participation | Voluntary strikers do not fight. Internal order requires evidence of actual positive recent pay, not just zero arrears. | Unemployed-first recruitment, existing democratic order floor, existing arrears provenance behavior. No extra essential-worker recruiting policy. |
| E4 simultaneous wars | Ending one war cannot erase exhaustion while another continues. Reset only after no wars remain. | Shared national exhaustion is acceptable; no front-specific allocation or forced tax-window change. |
| E5 missile repairs | Document effects accurately. | Ordinary 1% upkeep restoration remains. Separate combat repairs would be a new model choice. |
| E6 diplomacy | Preserve refused/expired-offer prerequisite and funding at enactment. Make peace/respond available across every active war, including fifth/sixth. | LIVE choices not gated by readiness; no mandatory truce or automatic embargo lifting. |
| E7 embargo | Observations reflect actual route permissions, including route-around access. | Direct-edge closure semantics; do not silently introduce a bilateral embargo. |
| E8 decisions | Show relevant raw-material consumer demand, stocks, incoming cargo, exportability, affordability/escrow/route constraints, action effects. | This part still has a confirmed implementation gap; see section 7. |
| E9 fiscal exposure | Expose salaries, upkeep, commitments and compounded multiwar tribute exposure. | Existing 10% losing-contest tax/stock tribute and 25% treasury reparations; no caps or payroll priority. |
| E10 gifts | Treasury-owned or fully purchased exact quantities, private suppliers compensated, truthful actual amount/value. | Zero rounded relation gain is valid. Show preview. |
| E11 unrest | Preserve ownership-compatible political changes, correct event reason; planned conversion deferred explicitly. | Weekly unrest cadence, regular approval-based election outcome, no forced incumbent defeat. |
| E12 cession | Show effects. | Keep 25% productive endowment transfer, no initial-range floor/cap. Original defect claim was withdrawn. |
| E13 observer/save | Meaningful war/unrest event descriptions, analytics observations, old tension JSON compatible with new scores. | Historical replay support matters; do not globally rewrite old event payloads. |

Approved numeric/default rules carried from the historical design:

- Score -100..100; war threshold -20; route embargo threshold -60.
- Gift gain capped at 15 relative to recipient weekly government spending.
- Recruit/discharge two at table soldier wage, 1,400 minor per person per week.
- Missiles add three strength; one launch consumes one missile.
- Missile project: 2 builders, 7 ticks, 10,000 minor; turret: 4 builders, 35 ticks, 40,000 minor, one level, 30% defense at full condition. Existing `PhaseContracts` already defines these; it was not changed.
- Weekly winner probability = own strength / combined strength. Tribute 10% of loser's weekly taxes and current ore/fuel stocks; wounds 25% of losing fit soldiers (ceiling); health set below work threshold.
- Surrender after two weekly checks without fit soldiers, independently of missiles.
- Reparations 25% of current treasury cash; cession 25% of one productive raw-material endowment, not physical stock.
- War weariness +0.1 per war week, max0.5, recovers0.1 per peaceful week. Support withdrawal draw 5% per war week.
- QoL mission40, unrest duration28 days, recently paid non-striking soldiers >=5% of residents suppress autocratic unrest.
- Readiness: dictatorship0.8, monarchy0.6, planned0.5, market democracy0.2, social democracy0.15; elected-versus-elected multiplier0.25. Rule declaration also requires blocked material need and superior strength.

## 5. What was changed so far

### `SocietyWar.cs` (new, adapted historical code)

The primary restored only this file from historical `9ad6be5` as a starting point, then substantially adapted it. No cherry-pick/revert was performed.

Current features:

- `Fit`: positive WorkEffort, not too sick, not on arrears strike.
- `Paid(world, soldier)`: positive WorkEffort, zero arrears, positive `MilitaryPayMinor`, nonnegative `MilitaryPaidTick`, paid tick >= current tick -7.
- Actual military payments through `PayWage` update these markers. Conflict payments use that same path and count as actual funded pay. Challenger questioned this and accepted the primary's reasoning: consensus requires proof of payment, not full regular salary only. At a pre-payroll weekly check, payment exactly seven days ago remains valid; at eight days it expires.
- Initial legacy tax snapshot avoids first contest seizing a share of an old checkpoint's cumulative historical taxes.
- Daily conversion of completed missile projects into stockpile missiles, removing the project from the list. Construction itself remains phase 2's job.
- Weekly contests, tribute, health wounds, surrender, condemnation, relation drift, weariness, unrest.
- No-fit counter ignores missile stock.
- Ending a war resets each nation's exhaustion only if it has no other active wars.
- `PeaceTerms` validates none/reparations/cede_ore/cede_fuel; `EndWar` rejects unsupported terms.
- `Unrest` uses weekly checks. Compatible government profile candidates only; emits `revolution_deferred` for unsupported ownership transitions. Successful revolution passes reason `revolution` into `ChangeGovernment` and removes pending old-leadership policies with explicit blocked events.
- `ResourceNeeds` predicts staffed next-week ore/fuel input needs, considers inventory/incoming cargo, probes exportable foreign sellers and one-unit quotes, classifies war/closed_route/cash_escrow/unaffordable/shipment_limit.
- `Blocked` maps inaccessible foreign holders for the rule war heuristic.
- Military action effects, gift previews, `PeaceEffect` helper, fiscal exposure in `View`.
- Raw gifts prequote full missing treasury quantity, acquire it at domestic offers, transfer exact quantity to recipient extraction firm (or recipient government fallback). No private input confiscation.
- Enactment rechecks recruitment funding and exact two recruits; discharge rechecks threat/order floor.
- Removed obsolete standalone construction fallback; integrated current-economy code only.

### `SocietyInfrastructure.cs`

- Exposes shared `CanOrder(world,nation,spec,level)` and `Order(world,nation,type,level,cause)`.
- Military types can use the shared procurement implementation, with 50% advance and all active unpaid commitments reserved.
- Civilian `build_infrastructure` still rejects missile/turret so there is no action bypass.
- Civilian menu still excludes military types.
- Missile project matching uses an active-project lookup; multiple historical completed projects do not force `SingleOrDefault` failures in the shared `Order` path.
- Existing builder/material/payroll/upkeep logic otherwise retained.

### `SocietyDiplomacy.cs`

- Direct-route permissions require Score>-60 and no war.
- New peace offers carry validated `ProposalTerms`; default none for older params.
- Offer type/state checked (peace only during war, trade only outside war).
- Accepted peace calls `SocietyWar.EndWar` with proposer as terms recipient and responder as payer/ceder.
- Acceptance +25 Score, refusal -5 Score and records `DiplomacyFailedTick`; expiration also records failure and clears proposal terms.
- Declaration retains funding, Score<=-20, not-at-war, and failed-offer prerequisites at enactment; sets Aggressor/Score-100, clears trade agreement, condemns aggressor in third countries.
- Military policies fall through to `SocietyWar.Enact` after existing infrastructure/economic policy handling.
- Scripted dispute now decreases Score rather than increasing Tension; historical event type retained.

### `SocietyActions.cs`

- All wars and pending relationships remain in menus, plus up to four ordinary peaceful relations. Removes old all-relationship `.Take(4)` which hid peace in fifth wars.
- Peace offers enumerate validated terms with consequences.
- Declaration keeps failed diplomacy/funding/Score guard and uses `OffersWar` rule heuristic (LIVE bypasses only readiness/need heuristic, not prerequisites).
- Calls `SocietyWar.AddOptions` in government options.
- Existing civilian payment condition and D1 discrete menus preserved. An initial broad text replacement accidentally added the hook in the civilian branch; that was corrected before handoff and code compiled afterward.

### State, economy, engine and factory

- `Relationship.Score`, `Aggressor`, `ProposalTerms` added.
- C# `Tension` alias retained `[JsonIgnore]`, maps `40-Score` with clamping. Setter-only `LegacyTension` JSON name `tension` loads old checkpoints. New saves emit `score`, not `tension`.
- Factory still seeds via legacy Tension setter; this maps to the intended new Score.
- `Agent.WarWeariness`, `MilitaryPaidTick=-1`, `MilitaryPayMinor` added.
- Security QoL part scales by `(1-WarWeariness)`.
- `PayWage` records actual positive government soldier payments.
- Old arbitrary weekly destruction of all producer stocks removed. `ConflictCosts` now pays funded military upkeep and emits actual funded amount; tribute is handled by contests.
- New engine war hook, accurate rules text, full scored neighbor relationships, `country.military` view.
- `ChangeGovernment` optional `reason="timeline_fork"`; unrest passes `revolution`, preserving existing default callers.
- Trade delivery increments Score instead of reducing Tension.

### Observer and analytics

- Observer handles new war_week, war_ended, gifts, missiles, soldiers, unrest, early elections, revolutions and deferred revolutions.
- Handles old/new scripted dispute payload (`tension` vs `score`) and old/new conflict costs.
- Relationship display uses Score or old tension conversion fallback.
- `RunNotes` adds event counts/evidence for war, peace, missiles, gifts, unrest/elections/revolutions/deferrals.
- Existing observer tests passed after these edits, but no dedicated new-event tests were added yet.

### Tests

- Challenger wrote only `backend/GEarth.Tests/Phase3WarTests.cs` (17 cases including theory cases). No production edits by challenger.
- Primary changed `RealismFixesTests.LeversShowTheCurrentLevelAndConflictComesOnlyAfterFailedDiplomacy` to use `world.Mode="LIVE"`. Purpose: preserve the test of the failed-offer prerequisite without conflating the newly required rule-mode readiness/material-need conditions. This change was applied after the failing full suite and is not rerun yet.

## 6. Verification actually performed

Do not claim the final tree is green. Two very small test corrections are not rerun, and E8 remains open.

### Initial review baseline (before implementation)

Reported during initial review:

- 294 .NET tests passed.
- Broader Python baseline reported 244 passed.
- Design validator passed.
- Observer11 passed/1skipped.

These baseline results do not validate current code.

### Last integrated .NET build/test during implementation

Command:

```sh
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"; dotnet test GEarth.sln --no-restore
```

- Core, API and test projects **compiled successfully**.
- **311 total, 309 passed, 2 failed, 0 skipped.**
- Existing full-year tests passed within that run; no bounds were loosened.
- Failure1: `RealismFixesTests.LeversShowTheCurrentLevelAndConflictComesOnlyAfterFailedDiplomacy`, assertion line97. It was a rule-world test expecting war after failed diplomacy without supplying new rule heuristic conditions. Primary fixed test setup to LIVE. **Not rerun.**
- Failure2: `Phase3WarTests.AnIdleContractorBuildsAnOrderedMissileAndCheckpointReplayMatches`. Raw JSON string equality failed solely because a ResourceImports dictionary contained identical tick keys in different insertion order. Challenger changed both replay checks to `JsonNode.DeepEquals` and strengthened striker marker assertions. **Not rerun.**
- Challenger's own earlier targeted test run:16passed/1failed, same raw JSON ordering issue.

### Current Python subset

```sh
.venv/bin/python -m pytest agents ops/test_amd_model_server.py ops/test_compute_config.py ops/test_spark_qwen.py -q
```

**139 passed in0.84s.** Do not confuse this narrower command's139 with the initial broader review baseline244.

### Observer suite after edits

```sh
node --test tests/observer.test.cjs tests/observer.analytics.test.cjs
```

**11 passed, 1 skipped, 0 failed.** Skip is the recorded live/stopped integration test; no recorded API env was supplied. New Phase3 rendering itself still needs assertions.

### Not yet done for current implementation

- Rerun .NET after two test corrections.
- New E8 raw-need/route/affordability regressions.
- New observer event rendering assertions.
- Current design validator (not rerun after implementation).
- Updated implementation report/decision log/design text.
- Final adversarial review and explicit consensus on the completed code.
- A deliberate local rule-mode year that exercises decisions/new phase paths (existing passive year tests passed but do not prove full model-driven war behavior).
- No Spark/model/deploy/commit/push was attempted.

## 7. Confirmed open issue and remaining review concerns

### Confirmed E8 defect in this in-progress implementation: one domestic unit hides a shortage

**This is the next substantive code change to make.**

Current `SocietyWar.ResourceNeeds` contains:

```csharp
if (SocietyEconomy.Offers(world, nation, input.Good).Any(offer => offer.Seller != buyer
    && SocietyBank.Liquid(world, buyer) >= offer.PriceMinor)) continue;
```

If a consumer needs100 ore and a domestic seller has one affordable ore unit, the code drops the entire need from the observation. Foreign one-unit access, conversely, retains the demand record and marks the holder accessible. This is asymmetric and hides insufficient-stock shortages, one of the original E8 concerns.

Challenger's recommendation, accepted as a valid concern by primary at interruption:

- Keep the forecast gap visible even if one unit can be bought.
- Expose domestic opportunities/stock/cost alongside foreign holders.
- Distinguish “cannot buy any” from “cannot fund/fill the expected gap.”
- One-unit executable foreign quotes can establish access, but label the quote basis explicitly and do not imply they resolve a week's shortage.
- `Blocked`/rule war offers should continue using a justified inaccessible-access predicate, not turn any ordinary partial shortage into a pretext for war.
- Add a regression with substantial buyer gap plus exactly one affordable domestic input unit.

No E8 regression was written yet; the challenger stopped on the user's handoff request.

### Other checks to finish, not established failures

- Route-around access test: Score<=-60 on a direct pair, third-country route open, `CanImport` true; shortage observation must not say blocked by embargo. Closing all paths must produce closed_route. War must still block bilateral imports regardless of route-around.
- Positive national raw stock but unfunded actual consumer should show shortage/affordability. Pending cargo should reduce need. Planned export quota should be respected.
- Recompute `ResourceNeeds` only once per View if practical; currently it is called twice, and each calls route searches for all holders.
- Foreign quote basis currently quantity1; explicit visibility of forecast/full funding/stock shortfall is needed rather than silently promising fillability.
- Raw gift tests cover compensation/exactness and unfunded atomicity; strengthen a check that workshops' private input stock is untouched (producer mines/estates are the actual offers), treasury-owned stock path, and zero rounded gain.
- Peace response effect text is still plain accept/reject in current menu; consider exposing pending proposal terms/consequences for the responder too. Peace offer effects already exist.
- Validate stale peace acceptance after another action ended the war; avoid charging terms twice. `EndWar` currently validates terms but does not itself guard `war.AtWar`. Current diplomacy also needs a clear stale-offer policy. This is a review target, not an observed failing test.
- Pending proposal cancellation/expiry state after surrender/war start: inspect for stale peace/trade offers, align enactment with legitimate relation state.
- Declaration funding uses existing `PaySoldiers`; if no soldiers exist, that path transfers nothing although declaration requires liquid5000. This is inherited behavior; decide whether scope requires correction or documentation, do not silently introduce new money sinks.
- Invalid target/action parameter validation follows many older methods' assumptions; assess only where externally reachable malformed policies could break a current contract. Do not widen this into unrelated hardening.
- Actual funded pay markers may be retained after discharge/recruit; assess staleness within the7-day window if immediate reenlistment occurs. Consensus accepted recent actual pay, so do not overcomplicate without a concrete failing behavior.
- Tax initialization currently occurs on first War.Day. Add a checkpoint regression to ensure no historical cumulative tax tribute. Check a world created and immediately advanced to a weekly contest preserves intended window.
- Continue to retain injury recovery and ordinary upkeep restoration. Do not label their speed as fixed defects after the debate explicitly reclassified them.
- Test turret defense/launch consumption/condition damage with current integrated infrastructure; keep its next-week restoration expected.
- Test democratic early election is scheduled by unrest and decided through existing approval logic; no mandatory incumbent loss. Test successful revolution clears pending leadership policies.
- Ensure analytics evidence counts and observer descriptions remain safe text rendering.
- Scores need old checkpoint support; tests cover legacy relationship JSON, but a complete legacy world checkpoint migration remains useful.

## 8. Suggested exact continuation sequence

1. Inspect `git status --short`, the current diff and this handoff. Preserve the existing files and branch.
2. Rerun the targeted17 Phase3 tests and the one corrected baseline guard test. Confirm the two small test fixes really work; do not assume.
3. Fix E8's domestic-unit suppression using a clear observation structure separating forecast gap, local/foreign access and blocking reasons. Add focused integrated regressions.
4. Finish stale peace/proposal handling if the review establishes a concrete incorrect transfer/state path; add meaningful tests rather than implementation-mirroring tests.
5. Add observer assertions for core war outcomes, gifts, peaceful end, revolution deferral, and old/new relations.
6. Reuse the adversarial agent if available and ask it to challenge the completed choices/tests. User explicitly requested this pattern; preserve substantive disagreement and resolve with evidence. If the old agent cannot resume, a replacement challenger is within that direct user request. Do not create user-owned sidebar chats for this subtask.
7. Update `docs/gearth/next-steps.md` with signed-off narrowed decisions. Current table is still historical/in discussion and has no implementation entries from this turn.
8. Create `docs/gearth/phase-3-war.md` as the implementation report (currently absent). Include current scope, test evidence, retained calibration choices, planned-conversion deferral and any remaining limits.
9. Update stale `gearth-design.md` conflict section, which currently claims abstract inventory destruction/tension and war as stretch goal. Keep it consistent with actual final behavior; run design validator.
10. Run appropriate full tests once changes justify them: .NET, Python contract suites, observer, validator. Avoid repeated full suites after unchanged successful results.
11. Run/record a local rule-mode year. Existing passive annual economy tests already passed, but explicitly exercising new policy paths is separate. Report which mode/actions were tested; do not imply a live model run.
12. Finish with changed behavior, validation and actual limits. No commit/push unless requested.

## 9. Challenger status and exact evidence

Subagent: `/root/challenge_phase3_review`.

- User explicitly requested it to argue against every finding.
- Original debate finished with explicit agreement on E1–E13; see review table/evidence JSON.
- Primary later reused it for regression tests and implementation challenge.
- It edited only `backend/GEarth.Tests/Phase3WarTests.cs`.
- It is finished/stopped at this checkpoint; no further code edits are running.
- It accepted actual-payment marker including funded conflict pay and the pre-payroll7-day inclusive window.
- Its one unresolved implementation objection is E8 domestic one-unit suppression, confirmed by code inspection. No new test was added before interrupt.

The17 added cases (theories expand counts) cover:

1. Integrated daily engine surrender with one unused missile.
2. Both simultaneous wars end when a shared soldierless nation is exhausted.
3. Accepted peace leaves exhaustion against another opponent.
4. Voluntary striker contributes neither combat strength nor paid order; direct marker checks.
5. Actual payroll required; valid through day14 from day7, expired day15.
6. Military order half advance and existing commitment rejection.
7. Idle contractor after day56 builds missile, pays full cost and replays checkpoint deterministically by semantic state.
8. Exact ten-unit raw gift compensates private suppliers and conserves.
9. Unfunded raw gift changes no inventory.
10. Six-war menus include all peace options and incoming response.
11. Delayed declaration rechecks failed diplomacy.
12–14. Legacy tensions0/60/100 map to Scores40/-20/-60; new save preserves Score-95/Aggressor/war and omits tension.
15. Active war checkpoint retains missile/exhaustion and deterministic surrender.
16–17. Market autocracy changes only compatible government; planned autocracy explicitly defers, preserving owners/planner compatibility.

### Historical scratch artifacts (do not depend on these if missing)

- `/private/tmp/gearth-phase3-review/SocietyWar-9ad6be5.cs`
- `/private/tmp/gearth-phase3-review/WarTests-9ad6be5.cs`
- `/private/tmp/gearth-phase3-review/probe/Program.cs`
- `/private/tmp/gearth-phase3-debate/` copied current Core plus historical War adapters and targeted counterexamples.
- `/private/tmp/gearth-phase3-challenge/` independent probes.
- `/private/tmp/gearth-phase3-review/state.json` and `events.ndjson.gz`: earlier Neon checkpoint, day223, approximately78,360events/1,498deliveries. Credentials are not in this handoff; don't fetch again unnecessarily.
- `/private/tmp/phase3-integrate.py`, `/private/tmp/phase3-war-edit.py`, `/private/tmp/phase3-options.py`: one-off text-edit scripts already applied. **Do not rerun them**; they are not idempotent and describe an earlier file state.

Source copies of debate probes are preserved in `docs/gearth/evidence/phase-3-adversarial/`.

## 10. Tools, commands and sandbox facts

Use the installed .NET10 SDK under `$HOME/.dotnet`:

```sh
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"; dotnet test GEarth.sln --no-restore
```

MSBuild's named pipes fail in the sandbox with `SocketException(13): Permission denied`. This happened twice before escalation. Do not diagnose it as a source-code failure or try to bypass the sandbox. Use `sandbox_permissions:"require_escalated"` and the tool's justification/approved rule when required. Existing saved approved command prefixes include the exact full-suite shell command, and a targeted Phase3 test command:

```sh
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"; dotnet test GEarth.sln --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~Phase3WarTests
```

Single-project `dotnet run ... -p:UseSharedCompilation=false -m:1` worked inside the sandbox in earlier probe work. If a command fails due to sandboxing/network restrictions, follow the tool's escalation requirements; don't invent a workaround to evade approval.

Other relevant commands:

```sh
.venv/bin/python -m pytest agents ops/test_amd_model_server.py ops/test_compute_config.py ops/test_spark_qwen.py -q
.venv/bin/python docs/gearth/validate_design.py
node --test tests/observer.test.cjs tests/observer.analytics.test.cjs
```

Use `rg` for searches. Read independent files in parallel when useful. Keep approvals/dependent edits sequential. Do not chain noisy separator commands or print environment secrets. Existing Python environment and dependencies are present; no installs needed so far.

The agent and primary share the same working directory; changes are immediately visible. Never revert someone else's edit merely because it appears in the diff.

For communication: lead with outcome, keep progress updates concise and at least every~60seconds, do not ask for permission already given. Final response should be self-contained and link the result/report with absolute paths. The current user specifically requested this handoff file; deliver it, with a brief truthful note that coding is unfinished and E8 remains.

## 11. Ready-to-use resume prompt

> Read `/Users/rickyliang/Documents/GitHub/dia-rockets/docs/handoff/phase-3-astra-resume.md` and continue the authorized Phase3 implementation from the current dirty tree on `gearth/phase-3-war`. Preserve all existing changes and review artifacts. Coding is approved; commits/pushes/Spark runs are not. First verify the two unrerun test fixes, then correct the confirmed E8 domestic-unit shortage visibility defect, finish targeted integration/observer tests, and get evidence-based agreement from an adversarial subagent as the user requested. Update decision and implementation docs, run the local required validation/year, and report the actual limits. Do not restart the historical review or cherry-pick the old implementation.
