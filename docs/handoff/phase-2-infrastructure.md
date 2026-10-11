# GEarth pre-build handoff: Phase 2: Infrastructure and builders

> **YOUR ASSIGNMENT: Phase 2: Infrastructure and builders.**
> Branch: `gearth/phase-2-infrastructure` from `origin/main`. Your files: `backend/GEarth.Core/SocietyInfrastructure.cs`, `backend/GEarth.Tests/InfrastructureTests.cs`, `docs/gearth/phase-2-infrastructure.md`.
> Build only your phase. The other machines are building Phase 1: Resources and trading, Phase 3: War, unrest and relations; do not implement their parts.
> Your rows in the section 4 table are the only shared-file edits you make (plus the identical edits in rule 2).
> Your spec is section 7, "Phase 2". Sections 0-6 are the same on all three machines.

## 0. Why this file exists

We have about 9 hours to the deadline (Sun Oct 11, 10am EDT). The normal process is one change at a time:
run a sim on the Spark, analyze where it strayed from reality, then build the fix patch plus the next major
change on a new branch. Waiting for each run before starting each phase would waste the night, so three
machines **pre-build** the three remaining major changes **in parallel, now**, each on its own branch, so that
when its turn in the pipeline comes we rebase it onto the patched branch instead of starting from scratch.
"Something ready" beats "perfect": get your phase working end to end with tests, then refine.

All three machines get the same document; only the assignment at the top differs. Read the whole thing,
including the other two phases, because the phases touch each other through a small frozen contract (section 5).

## 1. Project and ground rules

- Repo: `dia-rockets` (GitHub). The project was just renamed **GEarth** (was Faultline): code is in
  `backend/GEarth.Core`, `backend/GEarth.Api`, `backend/GEarth.Tests`, namespace `GEarth.Core`, design doc
  `gearth-design.md`, docs in `docs/gearth/`. Country ids are unchanged (`aster`, `beryl`, `cinder`, `dune`,
  `ember`, `fjord`, `grove`, `haven`, `ionia`, `jade`); only display names changed (Jaypan, Papa New Kenny, ...).
- It is a C#/.NET 10 day-by-day economy sim of 10 countries (about 1,100 agents) where LLM agents (Qwen on a
  DGX Spark) pick from candidate menus; rule mode picks defaults with no model.
- **Branch from `origin/main`**, named as in your assignment. Do not commit to or push `main`.
- **Commit and push only when your human says so.** Never print or commit credentials (`.env.local`, API keys,
  `DATABASE_URL`, Neon keys).
- **No Spark runs** from your machine; Ricky runs the sims. Local tests and in-process rule-mode checks are fine.
- Build and test (if the system `dotnet` is the wrong SDK, use `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`):
  ```
  dotnet test GEarth.sln
  .venv/bin/python -m pytest agents ops/test_amd_model_server.py ops/test_compute_config.py ops/test_spark_qwen.py -q
  .venv/bin/python docs/gearth/validate_design.py
  ```
  Baseline on `origin/main` (1bf3630): 156 passed, 4 skipped. Keep the full-year calibration test passing;
  if your phase legitimately moves a bound, change it with a comment saying why.
- Style: match the surrounding code. Short comments that say why, no new abstractions beyond what is below,
  money in integer minor units (`...Minor`), every cash move through `SocietyEconomy.Transfer`/`Sell` and every
  goods move counted (see conservation below).

## 2. Engine primer (what you need to know before touching anything)

- **Tick loop** (`SocietyEngine.Advance`): `SocietyDiplomacy.Advance` (policies take effect) ->
  `SocietyTrade.Advance` (cargo moves) -> `SocietyEconomy.Day` -> every 7th tick `SocietyEconomy.Week` ->
  `SocietyEconomy.AssertConservation` (cash and every good are conserved every day: produced, consumed and
  lost goods are tracked in `Society.ProducedGoods/ConsumedGoods/LostGoods`; tests that delete stock must add
  it to `LostGoods`).
- **Sectors** (`SocietyLabor.Sectors`, record `Sector(Id, Occupation, Good, BasePrice, DailyOutput, Firms)`):
  food, energy, tools, entertainment, medicine, clinic, school, freight, bank. Goods are
  `SocietyFactory.Goods = [food, energy, tools, medicine]`. `Firms` is how many start per country;
  `InstitutionsPerCountry = 2 + sum(Firms)` and the minimum population is `10 * (InstitutionsPerCountry + 1)`
  (170 today). `TableWage` holds weekly wages per occupation; `Unskilled` lists jobs anyone can take after 14
  days unemployed; `SocietyFactory.JobShares` sets the starting mix per 100 workers.
- **Production** (`SocietyEconomy.Produce`): output = sum of worker effort x health x `sector.DailyOutput` x
  `OutputMultiplier(world, nation, good)`; inputs today: every good uses 1 energy per 10 units, medicine also
  1 tools per 10, energy uses nothing; inputs are bought with `AcquireInput` (domestic first, then automatic
  import via `SocietyTrade.AutoImport`). Clinics use medicine. Country productivity differs by
  `FoodMultiplier`/`EnergyMultiplier` (5-country pattern).
- **Households** use 1 food and 1 energy a day (energy = electricity/power, not personal stamina), buy tools
  and entertainment weekly for comfort, see clinics when health falls. QoL parts: needs, health, comfort,
  security, free time.
- **Trade** (`SocietyTrade`): routes between nearby countries have `TravelTicks` (2-8) and `CostMinor`
  (20-250, by distance); shipping is currently a **flat fee per shipment**, regardless of quantity; tariff is
  5% of value (0 with a trade agreement) paid by the importer to its own government; a domestic freight firm
  earns the fee. `SocietyDiplomacy.BorderPermits` gates routes (closed in war).
- **Prices** (`SocietyLabor.ReviewPrice`, weekly): move with shortages and gluts; the cost floor uses only the
  **wage** per unit made, not input costs. Sectors with `BasePrice == 0` (freight, bank) and school are skipped.
- **Government decisions**: `SocietyActions.GovernmentOptions` lists candidates (`Make(action, params)`;
  ranged numbers via `Range(min,max,default)`); the model picks one; `SocietyDiplomacy.Propose` queues it and
  `Enact` applies it after the government type's delay. Rule mode uses `SocietyActions.Resolve(candidate)`.
- **Views**: `SocietyEngine.Observe` builds each agent's observation; governments get a `country` object.
  `SocietyEngine.RulesNote` is the one-paragraph rules text every agent sees.
- **Planned economy** (`planned_republic`): state firms follow a planner's weekly quotas
  (`SocietyPlanning.PlannedSectors`, `Demand`, `Quota`); no private founders.
- **Existing diplomacy**: `Relationship { Tension 0-100, TradeAgreement, AtWar, ProposedBy, ... }`;
  `declare_conflict` needs tension >= 60, costs 5,000 then 1,000 a week, closes borders; peace and trade offers.
  Soldiers are citizens employed by the government (6 per 100 workers), paid from the treasury.
- Events: `world.Emit(type, "rule", entityIds, payload, cause)`; the observer and analytics read them.

## 3. Pipeline and merge plan

| Step | Branch | Contains |
|---|---|---|
| 0 | `main` | Public services batch, banks paused (run 0 on the Spark) |
| 1 | `ricky/banks-on` | Run 0 fixes + banks back on |
| 2 | `gearth/phase-1-resources` | (pre-built now) rebased onto step 1 + run 1 fixes |
| 3 | `gearth/phase-2-infrastructure` | (pre-built now) rebased onto step 2 + run 2 fixes |
| 4 | `gearth/phase-3-war` | (pre-built now) rebased onto step 3 + run 3 fixes |

Merge order is always 1 -> 2 -> 3. Your branch must build and pass tests **on its own** from `origin/main`,
because the others are not merged yet; that is what the guards in section 5 are for.

## 4. Isolation rules (so the three branches merge cleanly)

1. **Your logic lives in your own new file**: phase 1 `SocietyResources.cs`, phase 2 `SocietyInfrastructure.cs`,
   phase 3 `SocietyWar.cs` (all `public static class`, namespace `GEarth.Core`). Your extra `Nation` fields go
   in a `public sealed partial class Nation { ... }` block **in your own file**. Your tests go in your own test
   file (`ResourcesTests.cs`, `InfrastructureTests.cs`, `WarTests.cs`). Your design notes go in your own doc
   (`docs/gearth/phase-1-resources.md`, etc.), not in `gearth-design.md` (folded in at integration).
2. **Byte-identical shared edits** (all three branches make exactly these, so git sees the same change):
   - Add `backend/GEarth.Core/PhaseContracts.cs` exactly as in section 5 (copy it, do not reformat).
   - In `SocietyState.cs` change `public sealed class Nation` to `public sealed partial class Nation`.
   - In `SocietyLabor.cs` change the `Unskilled` line to
     `    public static readonly string[] Unskilled = ["farmer", "service_worker", "energy_worker", "factory_worker", "dockworker", "miner", "builder"];`
   - Population minimum: everywhere the minimum population is 170 (`git grep -n "\b170\b"`: Coordinator
     `ReadNumber("GEARTH_POPULATION", 1100, 170, ...)`, ops scripts, tests, README, analytics docs), change it
     to **200** (the total after all three phases). Phase 1 needs 190 alone; 200 covers everyone.
3. **Shared files: only your assigned hunks, at your assigned anchors** (table below). Insert one-line calls into
   your own file wherever possible; do not reformat, rename or "tidy" anything else in shared files.
4. **Known conflicts the integrator resolves by keeping all sides**: the single-line `RulesNote` string (each
   phase inserts `{SocietyX.Note}` at a different spot in it, so the line conflicts; keep all three) and
   anything you were forced to touch outside the table (say so in your report).

### Shared-file hunks by phase (anchor = existing line on `origin/main`)

| File | Phase 1 (resources) | Phase 2 (infrastructure) | Phase 3 (war) |
|---|---|---|---|
| `SocietyEngine.Advance` | none | insert `SocietyInfrastructure.Day(world);` after `SocietyEconomy.Day(world);` | insert `SocietyWar.Day(world);` after `SocietyDiplomacy.Advance(world);` |
| `SocietyEngine.Observe`, government `country` object | insert `resources = SocietyResources.View(world, nation),` after `essential_sectors_without_workers = ...,` | insert `infrastructure = SocietyInfrastructure.View(world, nation),` after `nation.FoodSubsidyPct, nation.TuitionSubsidyPct, ...HiringBonusMinor,` | insert `military = SocietyWar.View(world, nation),` after `nation.Productivity, nation.SalesTaxBasisPoints, nation.ProfitTaxBasisPoints,`; owns the `neighbors` block at the top of `Observe` |
| `SocietyEngine.Observe`, import offers | `shipping_minor = path.CostMinor` becomes the per-unit cost | none | none |
| `SocietyEngine.RulesNote` | `{SocietyResources.Note}` after "...lands cheaper than their price. " | `{SocietyInfrastructure.Note}` after "...a hiring bonus for graduates. " | replace "Conflict costs 5000 upfront plus 1000 per week. " with `{SocietyWar.Note}` |
| `SocietyActions.GovernmentOptions` | none (tariff stays fixed) | insert `SocietyInfrastructure.AddOptions(world, nation, agent, options);` after the `invest` option line | owns the relationships loop; insert `SocietyWar.AddOptions(world, nation, agent, options);` before the `SocietyPayments.AddOptions(world, agent, options);` line at the end of `GovernmentOptions` (the line also appears elsewhere in the file) |
| `SocietyDiplomacy.Enact` | none | insert `if (SocietyInfrastructure.Enact(world, nation, policy) is { } built) return built;` before `switch (policy.ActionType)` (return `bool?`, null when not yours) | owns the rest of the file; `default:` returns `SocietyWar.Enact(world, nation, policy)` |
| `SocietyEconomy.OutputMultiplier` | edit the switch arms (energy no longer uses `EnergyMultiplier`; ore and fuel use endowment) | insert `multiplier *= 1 + PhaseContracts.Boost(nation, good);` after `multiplier *= 0.35;` | none |
| `SocietyEconomy.Produce` | owns the `inputs` table | none | none |
| `SocietyEconomy` clinic capacity in `Day` | none | multiply clinic capacity by `1 + PhaseContracts.Boost(nation, "clinic_capacity")` | none |
| `SocietyEconomy.QolParts` | none | none | security part reduced by war weariness |
| `SocietyLabor.Sectors` | insert `mine` and `well` rows after the `tools` row | insert the `construction` row after the `freight` row | none |
| `SocietyLabor.TableWage` | insert a new line `["miner"] = 1000,` after the `["farmer"] = 1000, ...` line | insert a new line `["builder"] = 1300,` after the `["teacher"] = 1750, ...` line | none |
| `SocietyLabor` other | `ReviewPrice` cost floor (T1); `QolPartOf`/`HasGap` for mines | `Autopilot` construction headcount; `FillEmptySectors` excludes construction | none |
| `SocietyFactory` | `JobShares` (service 24 -> 16, add miner 8); insert `SocietyResources.Setup(world, nation);` after `world.Society.Nations.Add(countryId, nation);` | insert `SocietyInfrastructure.Setup(world);` before `AddRoutes(world);` | insert `SocietyWar.Setup(world);` after `AddRoutes(world);` |
| `SocietyTrade` | owns shipping cost (per unit, port discount via contract) | none | none (embargo goes through `SocietyDiplomacy.BorderPermits`, which phase 3 owns) |
| `SocietyPlanning.PlannedSectors` | add `mine`, `well` | none | none |
| `SocietyState.Relationship` | none | none | owns it (relations score) |

## 5. The frozen contract: `backend/GEarth.Core/PhaseContracts.cs`

Compiled and tested on `origin/main` 1bf3630 together with the `partial` change (156 passed, 4 skipped).
Copy it **exactly**. It holds only what more than one phase reads or writes: the endowment map (phase 1 sets,
phase 3 cession moves), the project list and catalog (phase 2 builds, phase 3 orders missiles and turrets and
damages projects, phase 1 reads the port discount), and the `Boost` helper. Guards for building alone:
phase 2 and 3 check `PhaseContracts.HasRawMaterials` before using ore or fuel; phase 3 checks whether the
`construction` sector exists before relying on builders (see phase 3).

```csharp
namespace GEarth.Core;

// Shared shapes for the resources (phase 1), infrastructure (phase 2) and war (phase 3) branches.
// This file is byte-identical on all three branches so they merge cleanly: change it only by agreement.
// Each phase keeps its own logic and its own extra Nation fields in its own file (Nation is partial).

public sealed partial class Nation
{
    // Phase 1: raw-material concentration per good (ore, fuel), 0.25-2.5 with 1 as average. Phase 3 cession moves it.
    public Dictionary<string, double> Endowment { get; set; } = new();
    // Phase 2: projects under construction or built. Phase 3 orders missiles and turrets here and damages them.
    public List<Project> Infrastructure { get; set; } = [];
}

public sealed class Project
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public int Level { get; set; } = 1;
    public int Condition { get; set; } = 100; // 0-100; the boost scales with it
    public long CostMinor { get; set; }       // what the government pays the construction firm, in weekly installments
    public long PaidMinor { get; set; }
    public int StartedTick { get; set; }
    public int ReadyTick { get; set; }        // planned completion; Phase 2 pushes it back while builders are short
    public bool Complete { get; set; }
}

// Builders, build ticks and cost per level-1 project. Effects are read through Boost(): sector ids add output
// (0.05 = +5%), "shipping" cuts import shipping cost, "clinic_capacity" adds clinic visits, "missile_defense"
// cuts the chance an incoming missile hits. A missile is not infrastructure: when complete it becomes one
// missile in the war stockpile (Phase 3) and is removed from the list.
public sealed record ProjectSpec(string Type, int Builders, int Ticks, long CostMinor, int MaxLevel, Dictionary<string, double> Effects);

public static class PhaseContracts
{
    public static readonly string[] RawMaterials = ["ore", "fuel"];
    public const string Construction = "construction"; // Phase 2 sector id; occupation "builder"
    public const string Missile = "missile";
    public const string Turret = "turret";

    private static readonly string[] AllGoods = ["food", "energy", "tools", "medicine", "ore", "fuel"];

    public static readonly ProjectSpec[] Catalog =
    [
        new("roads", 4, 28, 31000, 3, AllGoods.ToDictionary(good => good, _ => 0.05)),
        new("irrigation", 3, 28, 24000, 3, new() { ["food"] = 0.15 }),
        new("power_grid", 4, 35, 39000, 3, new() { ["energy"] = 0.15 }),
        new("mine_rail", 4, 35, 39000, 3, new() { ["ore"] = 0.15, ["fuel"] = 0.15 }),
        new("port", 5, 42, 55000, 3, new() { ["shipping"] = 0.25 }),
        new("hospital", 6, 42, 70000, 3, new() { ["clinic_capacity"] = 0.5 }),
        new(Turret, 4, 35, 40000, 1, new() { ["missile_defense"] = 0.30 }),
        new(Missile, 2, 7, 10000, 1, new())
    ];

    // Each level adds less: level 2 gives 1.6x the level-1 effect, level 3 gives 1.9x. Upgrades cost more.
    public static readonly double[] LevelGain = [1, 1.6, 1.9];

    public static ProjectSpec Spec(string type) => Catalog.Single(spec => spec.Type == type);

    public static long CostOf(string type, int level) => Spec(type).CostMinor * (2 + level - 1) / 2;

    // Materials the construction firm uses for a project (ore, tools, energy), scaled by its cost.
    public static Dictionary<string, int> Materials(string type, int level)
    {
        var cost = CostOf(type, level);
        return new() { ["ore"] = (int)(cost / 800), ["tools"] = (int)(cost / 1600), ["energy"] = (int)(cost / 500) };
    }

    // Phase 1: a raw material's concentration in a country (1 when unset).
    public static double Endowment(Nation nation, string good) => nation.Endowment.GetValueOrDefault(good, 1);

    // Sum of a key's effect over finished projects, scaled by level and condition. 0 when nothing is built.
    public static double Boost(Nation nation, string key) => nation.Infrastructure
        .Where(project => project.Complete && Spec(project.Type).Effects.ContainsKey(key))
        .Sum(project => Spec(project.Type).Effects[key] * LevelGain[project.Level - 1] * project.Condition / 100.0);

    // Whether this branch has the phase 1 raw-material goods (phase 2 and 3 guard on it when built alone).
    public static bool HasRawMaterials => SocietyFactory.Goods.Contains("ore");
}
```

## 6. All decisions (signed off by Ricky, Oct 10-11)

### Phase 1: resources and trading
- Goods that reasonably need raw materials require them; services need only energy and their current inputs.
- Two raw materials, **ore** and **fuel**. Food stays tied to land fertility (`FoodMultiplier`).
- Inputs: energy uses fuel (replaces the flat `EnergyMultiplier`); tools use ore and energy; medicine uses
  energy and tools (unchanged); food uses energy (unchanged); clinic uses medicine (unchanged); entertainment
  and school use a little energy. Raw materials raise QoL only through the goods made from them; that is the
  source of demand.
- Extraction sectors `mine` (ore) and `well` (fuel), occupation `miner` (unskilled, so anyone can take it
  after 14 days unemployed): output = labor x the country's concentration. About 8 jobs per 100 workers, taken
  from service work (24 -> 16), split by endowment (a country poor in ore employs few ore miners).
- Endowment per raw material 0.25-2.5 (1 = average), no zeros: per material 3 rich (1.8-2.5), 4 middling
  (0.8-1.2), 3 poor (0.25-0.5), set against each other and against food so every country is rich in something.
- No depletion (deferred; a known simplification).
- Shipping = a small fixed fee per shipment + a per-unit cost x distance; tariffs stay (fixed 5%, 0 with a
  trade agreement; not a government lever).
- Firms import automatically (exists); the planned state imports for its firms; governments act through trade
  agreements and tariffs.
- **T1**: prices pass on input costs: the cost floor in `ReviewPrice` includes inputs at landed cost (price +
  shipping + tariff), not only wages (real tariffs are mostly passed on to consumers).
- Planned economies: planner quotas also cover `mine` and `well`.

### Phase 2: infrastructure
- Governments pay for projects from the treasury only (no borrowing); a **construction firm** (sector
  `construction`, occupation `builder`, one per country, state-owned in planned economies) is contracted and
  paid **weekly as work progresses**. Builders are unskilled; between projects the firm lives on upkeep and
  idle builders are laid off like any firm. Observe in runs how many ticks projects wait for builders.
- About 3 builders per 100 workers at the start, taken from factory workers (done in `Setup`, not
  `JobShares`, to avoid a merge conflict). In planned economies (no unemployed) the state construction firm
  borrows workers from the largest state firm for the project's length.
- Catalog (in the contract): roads +5% all goods (4 builders, 28 ticks), irrigation +15% food (3, 28), power
  grid +15% energy (4, 35), mine rail +15% ore and fuel (4, 35), port -25% shipping cost (5, 42), hospital +50%
  clinic capacity (6, 42). Boosts are sized to evidence (public-capital output elasticity about 0.1-0.2).
- Materials: ore, tools and energy (`PhaseContracts.Materials`); without phase 1, skip ore.
- One of each type per country, up to level 3, each level adding less (`LevelGain` 1, 1.6, 1.9), upgrades cost
  more (`CostOf`).
- Degradation: condition 0-100 per project, -2 a week; weekly upkeep of 1% of build cost paid automatically to
  the construction firm (its builders do it) holds it at 100; the boost scales with condition; a government
  can cut upkeep (`set_maintenance`), and then the project wears down.
- Build times are agreed as is; observe and tune after runs.

### Phase 3: war, unrest, relations (stretch goal; trim from the bottom of this list if time runs out)
- **Missiles** (G1): ordered from the construction firm like a project (2 builders, 7 ticks, about 10k, ore +
  tools + energy); kept in a stockpile; launching uses one. A hit lowers one completed project's condition,
  chosen at random weighted by build cost. Only **turrets** (one level, an infrastructure type) reduce the hit
  chance, by a flat 30% (missiles must still be able to land). Soldiers do not stop missiles.
- **Soldiers** decide wars and keep internal order. Strength = soldiers fit to fight + m x missiles. Governments
  recruit or discharge soldiers in steps of 2 at the table wage (1,400), hiring the unemployed first (G3).
- **Winning** (W9): each war week is a contest, P(win the week) = own strength / (own + enemy) (the standard
  contest function). The week's winner takes tribute; the loser has some soldiers wounded (health drops below
  the too-sick line, so they cannot fight until they recover; agents are never removed). A side with no fit
  soldiers for 2 weeks surrenders and the winner's peace terms are imposed; otherwise a war ends only when a
  peace offer is accepted.
- **Tribute and cession** (G5): weekly tribute = 10% of the loser's weekly tax income + 10% of its ore and fuel
  stock. Peace terms: reparations, or ceding 25% of the loser's concentration in one raw material (moves
  `Endowment` from loser to winner). No annexation.
- **War weariness**: each war week lowers both sides' security QoL and approval.
- **Unrest** (W3): average QoL below x for n ticks -> democracies hold an early election (the leader falls);
  autocracies face a revolution if **paid** soldiers (paid that week; unpaid soldiers defect) are fewer than y,
  and the revolution changes the government type (reuse `SocietyFactory.ChangeGovernment`). Missiles do not
  count toward suppressing unrest. Dissolution into neighbors is **cut** (only ever after conquest).
- **Mission and incentive** (W4): keeping average QoL >= x is the government's mission, shown in its view; war
  is a choice, never automatic. The view shows raw materials it needs but cannot buy (refused, embargoed,
  unaffordable) and which countries hold them. Rule default: trade first, war only when trade is blocked and
  it is stronger.
- **Relations** (W6): a score -100..100 replacing tension; rises with trade volume and gifts; falls for
  **every third country** toward an aggressor, scaled by how much bigger it is than its victim (bullying);
  drifts toward 0. Low relations mean refused trades and embargoes (through `BorderPermits`).
- **Readiness for war** (W7) by government type: dictatorship highest, democracies lowest (democracies rarely
  fight each other); shown in the view and used by the rule default.
- **Defenseless countries** (W8) are protected by relations, not special rules: third countries with high
  relations can join the war or embargo the aggressor.
- **Gifts** (G4): cash or raw materials; the relations gain scales with the gift's value against the
  recipient's weekly spending, capped at +15 a gift.
- Numbers x (unrest QoL), n (unrest ticks), y (soldiers to stop a revolution), m (soldiers per missile) are
  set after phase 1 and 2 runs. Pre-build with constants marked `// ponytail: tune after runs` (suggested:
  x = 40, n = 28, y = 5% of residents, m = 3).

## 7. Phase build specs (the one in your assignment is yours; the others are for context)

### Phase 1: `SocietyResources.cs`
1. Goods: add `"ore", "fuel"` to `SocietyFactory.Goods` and check everything that iterates `Goods` (inventories,
   stocks, household buying, `Demand`) so households never buy or eat ore or fuel.
2. Sectors rows after tools: `new("mine", "miner", "ore", 30, 6, 1)`, `new("well", "miner", "fuel", 30, 6, 1)`
   (price and output are first guesses; tune so a mine roughly covers its payroll at endowment 1).
3. `Setup(world, nation)`: deterministic endowment per country from the R3 pattern (by country index or
   seed), opposed between ore and fuel and against `FoodMultiplier`, every country rich in something. Split
   the miners between the mine and the well by endowment.
4. `OutputMultiplier` arms: ore and fuel x `PhaseContracts.Endowment`; energy no longer uses `EnergyMultiplier`
   (fuel scarcity now does that job; leave the field for saved runs).
5. `Produce` inputs, suggested ratios: energy 1 fuel per 4; tools 1 ore per unit + 1 energy per 10; medicine
   unchanged; food unchanged; ore and fuel 1 energy per 10. Entertainment and school draw 1 energy per 10
   sessions where they are delivered.
6. Shipping: one function `SocietyResources.ShippingCost(world, buyer, path, quantity)` = fixed fee (about 20)
   + quantity x per-unit rate x path distance (derive from `path.CostMinor`), x `(1 - PhaseContracts.Boost(buyer
   nation, "shipping"))`; use it everywhere `path.CostMinor` is charged or estimated (`CanImport`,
   `ImportFood` landed cost, `Affordable`, `Dispatch`, the Observe offers).
7. T1 in `ReviewPrice`: marginal cost = wage per unit + inputs per unit at landed cost.
8. Planned: `PlannedSectors` adds `mine`, `well`; `Demand` defaults for them.
9. Mines in `QolPartOf`/`HasGap`: raw materials serve no QoL part directly; let founders open a mine on market
   opportunity alone (shortage, high price, empty shelves).
10. `View`: endowment, ore and fuel stock, last week's imports of each. `Note`: one or two sentences for RulesNote.
11. Tests: endowment set and in range; tools cannot be made without ore; a poor country imports ore; shipping
    scales with quantity and distance; price rises with input cost; conservation holds; full-year rule run
    keeps every country fed.

### Phase 2: `SocietyInfrastructure.cs`
1. Sector row after freight: `new(PhaseContracts.Construction, "builder", null, 0, 0, 1)` (BasePrice 0, so
   `ReviewPrice` skips it like freight); `["builder"] = 1300` wage line; `FillEmptySectors` excludes it.
2. `Setup(world)`: move about 3 per 100 factory workers to the construction firm as builders; state-owned in
   planned economies.
3. Own partial `Nation` fields: `MaintenanceFunded` (default true) and anything else you need.
4. Options (`AddOptions`): `build_infrastructure {type, level}` for each catalog type except missile and turret
   (phase 3 offers those), when the treasury can cover the cost, it is not at max level, and nothing of that
   type is in progress; `set_maintenance {on}`. Show cost, builders, ticks and the effect in the candidate or view.
5. `Enact`: create the `Project` (Id via `world.NextEntityNumber()`), emit `infrastructure_started`.
6. `Day`: process unfinished projects in start order; the construction firm's working builders are allocated
   greedily; a project short of builders gets its `ReadyTick` pushed back a day; materials are bought before
   work starts (`AcquireInput`; skip ore without phase 1); the government pays weekly installments to the
   firm (`SocietyEconomy.Sell`, kind `construction`); at `ReadyTick` mark `Complete`, emit
   `infrastructure_completed`. **Any catalog type, including missile and turret, goes through this path**;
   do not remove completed missiles (phase 3 converts them).
7. `Autopilot` for the construction firm: target headcount = builders needed by active projects + a
   maintenance crew (1 per 4 completed projects); I9 borrowing in planned economies.
8. Weekly: condition -2 per completed non-missile project; if `MaintenanceFunded`, pay 1% of cost to the firm
   and restore it; emit `infrastructure_worn` when a project drops below 50.
9. Effects: the `OutputMultiplier` line and clinic capacity (shipping is phase 1's function, already reading
   the contract).
10. `View`: projects (type, level, condition, ready tick, builders short), upkeep per week, what each option
    costs and does. `Note` for RulesNote.
11. Tests: a project waits for builders and completes; installments sum to cost; the boost appears in output
    and scales with condition; unpaid upkeep wears it down; level 2 gives 1.6x; conservation holds.

### Phase 3: `SocietyWar.cs`
1. Relations: change `Relationship` to a score -100..100 (keep the JSON readable for old saves), update
   `declare_conflict` and the existing offer and response logic, the `neighbors` view and the relationships
   loop in `GovernmentOptions`. Weekly drift toward 0, + trade delivered between the pair, + gifts, - for
   every third country toward an aggressor scaled by size ratio. Very low relations close routes in
   `BorderPermits` (embargo).
2. Own partial `Nation` fields: `Missiles`, unrest start tick, wounded and war-week counters, and anything else.
3. Options (`AddOptions`): `recruit_soldiers` / `discharge_soldiers` (2 at a time), `order_missile`,
   `build_turret`, `launch_missile {country_id}` (only at war, with stock), `send_gift {country_id, kind,
   amount}`, `offer_peace {country_id, terms}` (reparations or cede ore/fuel) alongside the existing diplomacy.
4. Missiles and turrets are `Project`s in `nation.Infrastructure` (types `missile` and `turret`); phase 2
   builds them. **Built alone** (no `construction` sector): complete them after their ticks once paid from the
   treasury. Convert completed missile projects into `Missiles` and remove them from the list.
5. Strike: hit chance 1 - `PhaseContracts.Boost(target, "missile_defense")`; on a hit lower a random completed
   non-missile project's condition by 50, weighted by `CostMinor`; emit `missile_strike`.
6. Weekly war contest, tribute (guard ore and fuel on `HasRawMaterials`), wounding (health below
   `SocietyEconomy.SickBelow`), surrender after 2 weeks with no fit soldiers, imposed terms, war weariness
   (security QoL and approval). Keep the existing 1,000 a week conflict upkeep.
7. Unrest: weekly average QoL vs x; democracies: early election (`NextElectionTick`); autocracies: revolution
   if paid soldiers < y -> `ChangeGovernment` (seeded choice of a different type); emit `unrest_started`,
   `revolution`.
8. `View`: strength, missiles, turret, soldiers fit and paid, relations by country, readiness for war,
   average QoL vs the mission threshold, raw materials it needs but cannot buy and who holds them. `Note`.
9. Rule default: trade first; war only when trade is blocked and it is stronger, weighted by readiness.
10. Tests: contest odds follow strength; a defenseless country surrenders in 2 weeks; tribute and cession
    move cash, goods and endowment with conservation; a turret cuts hits by 30%; relations fall for third
    countries after aggression and embargo closes routes; unrest triggers an election in a democracy and a
    revolution in an underpaid autocracy.

## 8. When you are done (or at a good checkpoint)

- All tests pass (`dotnet test GEarth.sln`, the Python tests, the design validator).
- Your branch builds from `origin/main` alone.
- Write a short report for Ricky in your phase doc (`docs/gearth/phase-N-*.md`): what works, what is stubbed,
  constants that need tuning, any shared-file edit outside the table in section 4, and anything in this spec
  that turned out wrong.
- Ask your human before committing and pushing `gearth/phase-N-...`.
