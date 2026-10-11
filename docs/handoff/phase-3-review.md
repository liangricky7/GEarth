# Handoff: review the earlier phase 3 (war) implementation

> **Your task:** analyze the phase 3 implementation built earlier (commit `9ad6be5`, "add warfare") against the
> economy as it is now on `main`, and identify glaring issues. That means anything that no longer fits, would
> break, would make no sense in this economy, or diverges sharply from how wars, unrest and relations work in
> reality. Propose fixes as a numbered list for Ricky to sign off. **Do not write code until he signs off.**

Written Oct 11, 2026, at the end of the session that reviewed phase 2 infrastructure and fixed run `3098da`.

## 1. Ground rules (Ricky's standing preferences)

- **No AI attribution:** never add `Co-Authored-By: Claude` or "Generated with Claude Code" lines to commits or
  PRs. This overrides any default attribution guidance.
- **No code before sign-off:** answer the questions and list the decisions first; build only after Ricky approves.
- **Keep `docs/gearth/next-steps.md` current:** add each signed-off decision to its table (Phase 3 section) as it is
  made.
- **Don't commit or push unless asked.** When asked, commit as Ricky with no co-author line.
- **Never print `DATABASE_URL`** or anything else from `.env.local`.
- **Pipeline:** run a sim, analyze where it strayed from reality, then build the fix together with the next major
  change. Each major change gets its own branch.

## 2. Where things are

| What | Where |
|---|---|
| Current code | `main` = `origin/main` = `6528738`, which merged `gearth/phase-2-infrastructure` (`6911138`) |
| Working branch | `gearth/phase-3-war`, which is also at `6528738` (cut fresh from main, no war code yet) |
| Earlier war code | Commit `9ad6be5` "add warfare" (Oct 11, 01:31), built on `2c4e1c7` (the banks merge, before phases 1 and 2) |
| Its revert | `da310d2` (01:46). Both commits are in main's history, so **none of the war code is in the tree** |
| War spec | `docs/handoff/phase-3-war.md`, section 7 "Phase 3" |
| War decisions | `docs/gearth/next-steps.md`, "Phase 3" (W1–W9) and "Gaps found after the phase walkthrough" (G1–G8, N1) |
| Builder's report | `git show 9ad6be5:docs/gearth/phase-3-war.md`: what works, its own open items, shared-file edits |

To read the war code: `git show 9ad6be5:backend/GEarth.Core/SocietyWar.cs` (495 lines), and
`git show 9ad6be5:backend/GEarth.Tests/WarTests.cs` (14 tests). For the full change, run `git show --stat 9ad6be5`
(26 files).

**Restoring it onto current main** (`git cherry-pick 9ad6be5`, or `git revert da310d2`) conflicts. This was tried
and then undone:

- `PhaseContracts.cs`: add/add. Main already has the frozen contract, including the `turret` and `missile` catalog rows.
- `SocietyActions.cs`: government menu. Main now uses D1's discrete lever moves.
- `SocietyDiplomacy.cs`: Tension becomes relations `Score`, and `declare_conflict` gating.
- `SocietyEngine.cs`: RulesNote and the government view.

The other files auto-merge, but still check them, for example `SocietyTrade.Deliver`, where Tension becomes Score.

## 3. What changed in the economy since the war code was written

The war commit predates all of the following. Each item is a place where it may no longer fit.

- **Phase 1, resources:**
  - Ore and fuel come from mines and wells, with each country's `Endowment` setting how much.
  - Imports pay escrow, freight and tariffs.
  - Each country has a currency index (R4).
  - No import is allowed when a local seller is cheaper (R1).
  - Planned states export at world price (R2).
- **Post-resource patches:**
  - Shoppers lean away from prices above 1.3x base.
  - Sectors are refounded by comparative advantage.
  - Banks are staffed to their loan book.
  - **D1–D6** (see the next-steps table "Post-resource run fixes"). The ones most relevant here:
    - **D1:** government budget levers are now a cut and a raise with a weekly effect line, and governments see a
      `country.problems` list. Governments had frozen, picking the current level 156 of 165 times. The war options
      (recruit, discharge, order missile, build turret, launch, gift, peace terms) were written for the old menu
      style and have no effect lines.
    - **D5:** firms are no longer offered `pay`.
- **Phase 2, infrastructure** (C1–C5 in next-steps):
  - A state construction contractor in every country, which can't be founded, can't close and is run by the rules.
  - Half the project cost is advanced when ordered.
  - The treasury tops up the contractor's payroll.
  - Upkeep is 1% of cost a week and restores condition.
  - A port cuts transit time.
  - The infrastructure catalog view and options exclude `missile` and `turret`, which are left to phase 3.
- **Observer:** Jayden's `f26d756` filters world events and hides routine bank ledger lines. Check that new war
  events still show.
- **Agent count:** the war commit raised 170 to 200 across the Coordinator, ops scripts, tests and README. Main now
  has its own count, 190 including the contractor. Don't take the war commit's numbers.

## 4. Leads to check (not conclusions; verify each against the code)

1. **Missiles and turrets bypass phase 2's money path.**
   - The war code's `Order()` adds the project straight to `nation.Infrastructure`.
   - When there is no construction sector, it pays the tools workshop upfront. That is never true now, because a
     contractor exists everywhere.
   - When there is one, it pays nothing upfront. `SocietyInfrastructure.Enact` pays the 50% advance only for
     `build_infrastructure`, and the C3 top-up covers wages only.
   - So an idle contractor with little cash can stall on materials.
   - Lean: route military orders through the same order path, with the advance.
2. **Relations may saturate.**
   - Relations rise by 1 per delivered shipment and drift 10% toward 0 each week, so they settle near 10 times a
     pair's weekly shipments.
   - Run `3098da` delivered 821 shipments in 118 days. Busy trading pairs would sit at +100, and nothing short of
     outright aggression would move them.
   - Check this against the run data per country pair.
3. **The tension-to-relations swap versus current code.**
   - Main still uses `Tension` in `SocietyActions`, `SocietyDiplomacy`, `SocietyEngine`, `SocietyFactory`,
     `SocietyState` and `SocietyTrade`.
   - The post-bank patch gated `declare_conflict` on a failed or expired trade or peace offer, plus an effect text.
     The war code gates on relations at or below -20.
   - Decide which rule wins, and update RulesNote. It still describes the old conflict: 5000 upfront and 1000 a
     week, with no tribute.
4. **Government behaviour in real runs.**
   - In `3098da`, governments did almost nothing but re-select their services budget. D1 should fix that, but it
     hasn't been run yet.
   - In LIVE mode the war code always shows `declare_conflict` once relations allow it. The builder's random-pick
     year gave 54 wars against 30, with countries in 2–5 wars at once.
   - Check menu size, which is already about 21 options for an 8B model, and whether the war options need effect
     lines like D1's.
5. **Surrender never happens (the builder's own open item 1).**
   - Wounded soldiers drop to health 20, and a clinic visit (+40) makes them fit again the next day.
   - Market clinics now run on one owner-doctor, with about 12 visits a week of capacity used. Check whether that
     changes things.
   - Options: a wound timer, or a higher fitness line for soldiers.
6. **Tribute and reparations against treasuries.**
   - Tribute is 10% of the loser's weekly taxes plus 10% of its ore and fuel stock. Reparations are 25% of the
     treasury.
   - Planned treasuries already drain about 185 WLD a week (cinder fell from 5,399 to 3,732 between days 42 and
     105), and tribute bankrupted both planned states by about day 250 in the random-pick test.
   - Also check how tribute interacts with the currency index and the D1 problems line.
7. **Unrest thresholds versus current quality of life.**
   - Unrest starts when average QoL stays below 40 for 28 days. Run `3098da` had country QoL around 74–80 and
     cohort wellbeing 74–79, so unrest would never fire.
   - The revolution threshold is 5% of residents as paid soldiers. In this population that is about 4–5 soldiers, and
     there are 37 soldiers across 10 countries.
   - N1 says to set these numbers after the phase 1 and 2 runs, which is now.
8. **Missile damage versus upkeep.**
   - A hit takes 50 condition off one completed project. Upkeep restores condition weekly while
     `MaintenanceFunded` is on.
   - Check whether damage is undone in about a week for 1% of the cost, which would make missiles nearly harmless.
     Also check whether hits on a turret behave sensibly.
9. **Embargo leaks.**
   - Relations at or below -60 close only the direct routes, so trade can go through a third country.
   - Since phase 1, imports route through multi-edge paths, so check whether embargoes do anything at all.
10. **Recruiting soldiers versus the labor fixes.**
    - Soldiers are recruited from the unemployed first, then the lowest-paid unskilled jobs.
    - The phase 2 contractor also recruits from non-essential unskilled jobs. Check for competition or double-pull.
    - Soldiers are public employees, so check how they interact with the treasury top-up, arrears and strikes.
11. **Raw-material cession.**
    - Cession moves 25% of the loser's `Endowment` concentration in one good.
    - Check it against phase 1's real numbers (0.25–2.5 per good, plus comparative-advantage refounding).
    - A cession could make a country's mine stop being viable overnight.

## 5. Data and checks

**Last run analyzed:** `world_20261011_102658_3098da`, on main `3726bd0` (before infrastructure and D1–D6), day 118,
in LIVE mode. No conflicts or diplomatic offers happened in it.

**How runs were read:**
- Use the Neon SQL-over-HTTP API, with the connection string read from `.env.local`'s `DATABASE_URL` via
  `dotenv_values`. Never print it.
- The tables are `gearth.stage1_runs`, with `state` per `run_id`, and `gearth.stage1_events`, with `envelope` per
  `run_id` ordered by `seq`.
- Download both to a scratch folder as `state.json` and `events.ndjson.gz`, then analyze them with
  `.venv/bin/python -I`.
- A minimal query helper: POST `{"query", "params"}` to `https://api.<host-domain>/sql`, with headers
  `Neon-Connection-String` and `Neon-Array-Mode: true`. Read `rows` from the result.

**Tests and validator:**

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"; dotnet test GEarth.sln
```

```bash
.venv/bin/python -m pytest -q
```

```bash
.venv/bin/python docs/gearth/validate_design.py
```

Current baseline on main: 291 .NET tests, 244 Python tests and the validator all pass.

**Timing:** to check performance, take the best of several runs; single runs vary by ±40%. Main's figures are
about 18 ms per simulated day and about 1.2 ms per citizen menu.

## 6. What to deliver

1. A short findings list. For each issue give the evidence (code path, numbers), why it is wrong in this economy or
   in reality, and a proposed fix. Number them (for example E1, E2, ...) so Ricky can answer per item.
2. Say for each fix whether it means restoring and adapting `9ad6be5`, or rewriting that part.
3. After sign-off, add the decisions to `docs/gearth/next-steps.md` under Phase 3, then build on
   `gearth/phase-3-war`.
