# e1efff decision investigation and approved fixes

Investigated the full 93,533-event prefix of `world_20261011_113650_e1efff`, through the observations document's cutoff. Download was read-only. Evidence summaries are in `e1efff-decision-evidence.json`; raw downloaded state/events remain in `/private/tmp/gearth-e1efff-review`. The later current state was not used to infer the day252 economy.

## Clinic funding: confirmed cycling

142 accepted `fund_services` decisions contain92 changes of direction, computed from each country's chronological selected levels starting at50. This is a decision-sequence measure, not a causal estimate of damage or a count of complete round trips. Cinder made26choices/18reversals; Haven24/20; Ember29/15. All ten countries selected the action. One reason explicitly confused it with construction; that semantic mistake does not explain all142choices.

Cinder examples:

- Day27:100, “Reduce clinic funding to align with current health quality of life.”
- Day34:150, “Boost clinic funding to improve health quality of life”.
- Day48:100, “Reduce clinic funding to lower budget deficit while maintaining health quality.”
- Day55:150, “Boost clinic funding to improve health quality of life”.

The displayed health component remained around0.95 across these decisions. The controller alternates between an obvious generic benefit (health) and an obvious cost (budget), without a useful estimate of the next increment's health benefit. Existing menu effect text states who receives the payment and budget change, but does not estimate clinic saturation, unmet treatment or the causal response to the last change. `last_policy` is generic and can be overwritten by another action. The small model's explanations suggest reactive balancing; they do not prove its internal reasoning.

Recommended next change: clearly name the action as clinic funding; show untreated illness, clinic capacity/utilization, actual fees/subsidies, existing funding and dedicated funding-change history. Distinguish a budget-funded target from actual paid capitation when treasury cash is insufficient. Offer a hold with an explicit reason to wait for outcomes, without forcing the model to hold. If cycling persists, consider a short review window with an emergency override, but do not impose it before testing the improved information.

## Diplomacy: no responses despite repeated turns

| Offer tick | From | Recipient | Expires | Recipient accepted government turns before expiry |
|---|---|---|---|---:|
|22|grove|dune|82|7|
|34|dune|ionia|94|8|
|70|jade|dune|130|7|
|77|jade|cinder|137|8|
|84|jade|fjord|144|8|
|100|aster|haven|160|8|
|175|ember|fjord|235|8|

That is54 offer-specific turn opportunities, with overlapping offers potentially sharing a turn. No `respond_agreement` choice or diplomatic-response event appeared. Recipients chose domestic policy: Ionia selected food subsidy7times and unemployment benefit once; Cinder selected clinic funding7times and construction once; Haven selected clinic funding6times, construction once, hold once. Fjord often held instead.

Historical `6528738` menu code explicitly sorts incoming offers before taking four relations; current code preserves every pending relation. With so few overlapping offers, the simple “fifth relation hidden” explanation is weak for this run. Government menus are suppressed during a pending domestic policy or loss of authority, but many observed recipient choices were substantive government policies, demonstrating that their general government menu was available at those moments. The event stream does not contain exact rendered candidate menus and does not establish the deployed commit, so visibility cannot be proven for every turn.

A concrete information problem exists: response candidates merely specify country/accept; they do not explain tariff savings, terms, deadline or the consequence of inaction. Trade tariffs go to the importing country's own government; accepting an agreement can lower its tariff revenue while helping importers, so “always accept” would impose a policy preference rather than repair a bug. The single-action weekly schedule also makes diplomacy compete with domestic actions.

Recommended next change: add a dedicated pending-offers observation with expiry/remaining turns; response effect text describing zero-tariff versus5% trade, actual bilateral import volume and lost tariff revenue; clear peace terms when relevant; and explicit expired-offer events. Log candidate availability/selected alternative for pending offers. Test an offer receiver with multiple relationships and a pending domestic policy. A separate diplomatic response slot is a later product decision, not implemented here.

## What was implemented under the user's approvals

- Cumulative nutrition debt replaces the one-meal99%output cliff; prolonged starvation still reaches near-incapacitation, without removing/replacing agents. Recovery is gradual. Sub-unit output accumulates only where daily productive effort is below one unit, avoiding repeated truncation to zero without changing normal batch rounding.
- Treasury credit is blocked while delinquent/impaired; losses are provisioned into bank equity, visible to government/bank observations. Additional draws preserve old installments. Provisioning is an accounting loss allowance, not debt forgiveness or destruction of cash/deposits. No automatic recapitalization was introduced.
- Maintenance prevents normal wear; explicit queued repairs require crew, materials, fees and time. Repairs prioritize their own builder allocation; ordinary upkeep still needs its crew. Materials are purchased from domestic offers using treasury money; unavailable materials stall repair without partial payment. Imported supply can replenish domestic offers, but repairs do not dispatch their own overseas cargo.
-1%weekly spoilage of active stored food, fractional loss carried in an integer per holder; goods losses are conserved. This adds a small arithmetic loop over an already-materialized weekly country agent array, no additional model calls, inventory age cohorts or asymptotic change to weekly processing. Runtime is not literally zero; no extra inference capacity is required. In-transit cargo and liquidation estates are excluded in this first implementation.
- Regime/geography rotations unchanged as requested. No blanket resource cut.

Nutrition21days/recovery2days, provision10percentage points per overdue week after threshold, repair10points/week/0.5%cost per point and food1%weekly are explicit simulation calibration defaults, not externally validated empirical estimates.

## Validation

Full .NET suite passed326tests, including full-year food coverage, economy circulation, money/goods conservation and new targeted tests. Initial attempts exposed annual food-coverage regressions; test thresholds were not lowered. Normal production rounding was preserved while retaining sub-unit starvation recovery, and the final tested calibration passed. Final verification after the observation additions and provision safeguard also passed: 326 .NET tests, 147 Python tests, 11 observer tests (1 integration test skipped), design validator and git diff checks.

The decision investigation is observational. No live/Spark rerun, changed model prompts, forced diplomatic response or clinic cooldown was performed.

## Decision feedback implemented after authorization

The user subsequently authorized the recommended decision improvements. Governments now see completed-week clinic visits/capacity, unmet treatment patient-days after care, actual capitation paid, current doctors/medicine/fees and the last three funding changes with a pre-change snapshot. Funding explanations explicitly describe clinic operations and their constraints. The worker gives governments concise guidance to compare outcomes and allow recent changes time to work; no forced cooldown or extra inference turn was introduced.

Incoming offers now have a dedicated observation with terms, expiry, latest response date including policy delay, availability and accept/reject consequences. Candidates explain tariff-revenue tradeoffs and peace demands. Responses too late to enact are not offered; expired offers emit an explicit event. Accepted government decisions with observed pending offers record the actual response options presented and the selected alternative, so future runs can distinguish ignored from unavailable choices. Policy/authority rules and the right to reject/ignore remain intact.
