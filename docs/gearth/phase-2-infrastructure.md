# Phase 2: infrastructure and builders

Based on `origin/main` at `f730e18`; implementation branch: `gearth/phase-2-infrastructure`.

## Checkpoint 1: shared prerequisites

Copied the frozen PhaseContracts catalog and shapes, made Nation partial, and added miner/builder to unskilled occupations. New-world population limits, launchers, executable examples and tests now use 200. Historical evidence and handoff documents retain their original population figures.

Validation at this checkpoint: 189 .NET tests passed, including the full-year calibration; 96 Windows-compatible Python tests passed. The design validator also passed after providing local timezone data. Commit: `d884f1d`.

Shared-file detail: SocietyFactory's computed minimum is also bounded at 200, so direct engine callers and the API enforce the same limit. This is the population prerequisite rather than an infrastructure setup hook.

## Checkpoint 2: infrastructure end to end

Implemented one starting construction firm per country, builder wages and unskilled hiring, roughly three builders per hundred residents (including working owners), and factory headcount adjustment when workers move. Market firms use ordinary weekly hiring and layoffs. Planned firms temporarily borrow from the largest state employers, keeping at least one worker there; workers return with their previous occupation and salary when no longer needed.

Governments can order all six civilian types and upgrade each through level three. Candidates and enactment check treasury cash against both the new cost and outstanding construction commitments. There is no government borrowing for construction. Upgrades replace the old project; its benefit is unavailable during rebuilding. The government view shows costs, effects, completion dates, paid amounts, upkeep and builder shortfalls.

Daily work allocates working builders in start order. Missing builders, materials or a due treasury payment delay the ready tick. Contractors buy materials before work, consume them once with conservation accounting, and receive cumulative weekly installments with the exact remainder at completion. The same path completes missiles and turrets supplied by Phase 3; completed missiles stay in the list for Phase 3 to convert. Ore is skipped until Phase 1 is present. Save/resume retains materials consumed and borrowed-worker assignments.

Completed civilian projects and turrets lose two condition weekly. With funded upkeep, available builders (one per four projects) and enough treasury cash, a payment of 1% of build cost restores condition to 100. Unfunded projects emit a warning event when crossing below 50. Output and clinic capacity use the frozen, condition-scaled boosts. Shipping benefits remain Phase 1's responsibility.

### Validation and integration notes

- Final .NET result: 205 passed, zero failures or skips, including 18 infrastructure tests and the unchanged full-year calibration.
- The complete prescribed Python suite passes: 135 tests in an isolated, ignored Ubuntu test environment. The design validator passes all 30 JSON artifacts and existing document checks.
- Focused tests cover waiting for builders/materials/cash, exact installments, goods and cash conservation, maintenance, upgrades, policy validation, military projects, production/clinic effects, planned borrowing, and save/replay. Full-economy government decisions complete market irrigation and a planned hospital over 120 days. The existing full-year calibration bounds are unchanged.
- Beyond the assigned shared hooks and population edits, `SocietyTests` updates institution/owner counts; the clinic test in `EconomicLoopTests` uses 400 residents-and-institutions instead of 300 so it still has an employee after adding construction owners. `SocietyLabor.CanStart` prevents duplicate construction firms but allows a founder to replace a closed one.
- The handoff's baseline is older than the checked-out main: this branch starts from `f730e18`, with 187 baseline .NET tests, rather than `1bf3630` with 156. The frozen contract itself is unchanged.

### Run calibration and limits

Catalog costs, durations, staffing, 1% upkeep, two-point degradation and starting builder share retain the requested values. Tune only after runs. A local early road order stalled while trying to hire a fourth builder, and its contractor eventually closed under ordinary business rules; an irrigation order with the starting crew completed. A closed contractor is not automatically bailed out or recreated: an existing founder action can replace it, then unfinished work resumes. No new procurement or subsidy system was added.

Completed work is retained if construction later stalls; materials are not consumed again when reloaded or when a contractor is replaced. Upkeep restores damaged condition to 100 as specified, so Phase 3 should account for repair when evaluating missile damage. Old worlds without construction firms load, but do not automatically receive new institutions. Cross-phase resource/war behavior still needs integration testing after those branches merge.

No Phase 1 or Phase 3 logic, Spark runs, remote service checks, deployment, or GitHub push is included. Local test environments and generated outputs remain ignored.
