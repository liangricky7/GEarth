# Phase 3: war, unrest and relations (pre-build report)

Branch `gearth/phase-3-war`, cut from `origin/main` at 2c4e1c7 (banks merged), not from 1bf3630 as in the handoff.
Logic lives in `backend/GEarth.Core/SocietyWar.cs`; tests are in `backend/GEarth.Tests/WarTests.cs` (14 tests).
`dotnet test GEarth.sln`: 185 passed, 0 skipped. The Python suites, analytics tests and `validate_design.py` pass.

## What works

- **Relations** (`Relationship.Score`, -100..100) replace tension. The starting draw and old saves map tension t
  to relations 40 - t through a write-only `Tension` setter, so the old war line of tension 60 is now relations
  -20 (`WarBelow`). Relations rise by 1 per delivered shipment and with gifts. They drift 10% toward 0 each week
  (not during a war). On a declaration every third country loses 10 x size ratio (population, clamped 0.5-3) x
  friendship with the victim (1-3) toward the aggressor, then 2 x the same each war week. At -60 or below
  (`EmbargoBelow`), `BorderPermits` closes the routes between the pair.
- **Options**: `recruit_soldiers` and `discharge_soldiers` (2 at a time, unemployed first, then the lowest-paid
  unskilled jobs), `order_missile`, `build_turret`, `launch_missile {country_id}` (only at war and with stock),
  `send_gift {country_id, kind, amount}` (cash, or ore and fuel when phase 1 is present), and
  `offer_peace {country_id, terms}`. The terms are none, reparations (25% of the treasury) or `cede_ore`/`cede_fuel`
  (phase 1 only), and they name what the offering side demands. `declare_conflict` now needs relations <= -20.
- **Missiles and turrets** are `Project`s in `nation.Infrastructure`. When built alone (no `construction` sector),
  the country's tools workshop is paid the full cost upfront and the project completes after its build ticks. With
  phase 2, its construction path builds them. Completed missiles move into `Nation.Missiles`.
- **Strikes** hit with chance 1 - `Boost(target, "missile_defense")`. A hit halves (-50 condition) one completed
  non-missile project, picked at random weighted by cost. A turret can itself be hit, which lowers the defense.
- **Weekly contest** per war: strength = fit soldiers (not too sick, not on an arrears strike) + 3 x missiles, and
  P(win) = own / (own + enemy). The winner takes 10% of the loser's taxes that week, plus 10% of its ore and fuel
  stock when phase 1 is present. A quarter of the loser's fit soldiers drop to health 20. A side with no fit soldiers
  and no missiles for 2 weeks surrenders: the winner takes the loser's richest raw material when phase 1 is present,
  and reparations otherwise. The existing 1,000-a-week upkeep and inventory losses are unchanged.
- **War weariness**: +0.1 per war week, up to 0.5, and -0.1 per week of peace. It scales down the security part of
  every resident's QoL. Each war week, 5% of supporters also withdraw their support.
- **Unrest**: when average QoL stays below 40 for 28 days, a democracy holds an election the next tick, which
  replaces the leader if approval is under 0.55. An autocracy with fewer paid soldiers than 5% of residents has a
  revolution and becomes a seeded different government type (`ChangeGovernment`). Otherwise the unrest is
  suppressed. Events: `unrest_started`, `early_election_called`, `revolution`, `unrest_suppressed`.
- **View** (`country.military`): strength, soldiers (fit, paid and needed to keep order), missiles and orders,
  missile defense, readiness, weariness, wars with win chances, relations with every country, the QoL mission and
  goods it has none of whose holders are all blocked (by war, embargo or no route).
- **Rule default**: in RULE mode, `declare_conflict` appears only when the target holds a good the country cannot
  buy, the country is stronger, and a draw weighted by readiness passes. A model (LIVE) always sees the option.
  Military purchases are offered only when threatened (at war, or relations <= -20). Recruiting is also offered
  below the order line, and discharging only above it.

## Checks run

A year in-process at 1,100 agents where every government picks a random menu option every third day:
- **RULE mode**: no wars, armies steady at 5-6, and every country above 75 QoL except dune. dune collapses the same
  way on `origin/main` with this harness. Here it then had a revolution.
- **LIVE menus with random picks** (a worst case): 54 wars against 30 on `origin/main`. The increase comes from
  third countries turning on aggressors. Countries fought 2-5 wars at once. Tribute and reparations bankrupted both
  planned states (cinder, haven) by around tick 250. A real model should fight far less, but watch for this.

## Needs a decision or tuning after runs

1. **Wounded soldiers recover in a day.** The handoff says wounded soldiers are too sick to fight until they recover.
   In practice, a clinic visit (+40) makes a soldier at health 20 fit again the next day when there is capacity.
   As a result, surrender almost never happens unless one side starts with no soldiers. Options: a wound timer
   (`WoundedUntilTick`), or a fitness line above 30 for soldiers.
2. Constants marked `// ponytail: tune after runs` in `SocietyWar.cs`:
   - x = 40 and n = 28: the QoL line and how long it must hold before unrest comes to a head.
   - y = 5% of residents: paid soldiers an autocracy needs to stop a revolution. Today's armies sit just at this line.
   - m = 3: strength per missile.
   - Relations: 40 - tension at start, a -20 war line, a -60 embargo line and 10% weekly drift.
   - Readiness: dictatorship 0.8, monarchy 0.6, planned 0.5, market democracy 0.2, social democracy 0.15; x0.25
     when two democracies face each other.
3. Embargoes close the direct routes only. Trade can still route through a third country.
4. "Unaffordable" is not yet one of the reasons in the view's `needs_but_cannot_buy`.
5. When built alone, missiles and turrets use no materials. With phase 2, its construction path buys them.

## Shared-file edits outside the section 4 table

- `SocietyTrade.Deliver`: the line `relation.Tension = Math.Max(0, relation.Tension - 1)` became
  `relation.Score = Math.Min(100, relation.Score + 1)` (trade raises relations). Phase 1 owns shipping in this file,
  so expect a possible nearby hunk.
- `World.cs` `Agent`: added `WarWeariness`, because the `QolParts` hunk only receives the resident.
- `GEarth.Api/wwwroot/observer.js`: the relation line shows `relations ${relation.score}` instead of tension.
- `SocietyWar.Setup` was not added, because nothing needed it. `Relations` are created after `AddRoutes`, so Setup
  could not seed them anyway.
- 170 -> 200: changed in the Coordinator, ops scripts, tests, the README and the analytics doc command. Two things
  were left alone: the analytics doc sentence that records a historical run of 170 agents, and the two lines in
  `gearth-design.md` (folded in at integration).

## Spec notes

- `origin/main` has moved past 1bf3630 (the banks merge). The contract still compiles unchanged.
- Rule mode chooses a random candidate, so "rule default" is implemented as which options appear in the menu.
