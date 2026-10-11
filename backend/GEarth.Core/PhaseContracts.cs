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
// (0.05 = +5%), "shipping" cuts import transit time, "clinic_capacity" adds clinic visits, "missile_defense"
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
