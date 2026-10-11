using System.Text.Json.Nodes;

namespace GEarth.Core;

// The planned economy's plan. A planner (a resident on the public payroll) sets each sector's weekly output
// quota once every four weeks, and state firms make or serve their share of it whatever is selling. Prices
// are fixed by decree, so a quota set too low shows as empty shelves and queues, and one set too high as
// unsold stock and wasted inputs: the planner's own judgement, not a built-in penalty.
public static class SocietyPlanning
{
    public const int PlanningWeeks = 4;
    public static readonly string[] PlannedSectors = ["food", "energy", "tools", "medicine", "clinic", "entertainment"];

    private static bool Applies(Nation nation, Agent firm) =>
        SocietyLabor.IsPlanned(nation) && firm.OwnerId is null && nation.Plan.ContainsKey(firm.Specialty!);

    private static int Share(World world, Nation nation, Agent firm) =>
        nation.Plan[firm.Specialty!] / Math.Max(1, SocietyLabor.Firms(world, nation.Id, firm.Specialty!).Count());

    // A state firm's daily production quota, or null where no plan applies.
    public static int? Quota(World world, Nation nation, Agent firm) =>
        Applies(nation, firm) ? (Share(world, nation, firm) + SocietyLabor.DaysPerWeek - 1) / SocietyLabor.DaysPerWeek : null;

    // Visits or sessions a state service may deliver over the given days, capped by the plan.
    public static int Cap(World world, Nation nation, Agent firm, int capacity, int days) =>
        Applies(nation, firm) ? Math.Min(capacity, (Share(world, nation, firm) * days + SocietyLabor.DaysPerWeek - 1) / SocietyLabor.DaysPerWeek) : capacity;

    // A first estimate of weekly demand: last week's sales, or what residents need before anything has sold.
    public static int Demand(World world, Nation nation, string sector)
    {
        var sold = SocietyLabor.Firms(world, nation.Id, sector).Sum(firm => firm.LastWeekUnitsSold);
        if (sold > 0)
            return sold;
        // A meal and a unit of energy a day each (energy also feeds a tenth of farm and workshop output), about three
        // household goods and five outings a week, and roughly one doctor's visit (with its medicine) per resident every two months.
        var residents = Math.Max(1, world.InCountry(nation.Id).Count(SocietyLabor.IsResident));
        return sector switch
        {
            "food" => residents * 7,
            "energy" => residents * 8,
            "tools" => residents * 3,
            "medicine" => residents / 6 + 1,
            "clinic" => residents / 8 + 1,
            _ => residents * 5
        };
    }

    // Most a sector's current workers could make or serve in a week at full health.
    private static int Capacity(World world, Nation nation, string sector)
    {
        var spec = SocietyLabor.SectorOf(sector);
        var firms = SocietyLabor.Firms(world, nation.Id, sector).ToArray();
        return spec.Good is { } good
            ? (int)(firms.Sum(firm => SocietyLabor.Workers(world, firm).Length) * spec.DailyOutput * SocietyLabor.DaysPerWeek
                * SocietyEconomy.OutputMultiplier(world, nation, good))
            : (int)firms.Sum(firm => SocietyLabor.ServiceCapacity(world, firm));
    }

    // What the planner sees for each sector: the quota, what sold, what was left or turned away, and the most
    // its workers could deliver.
    public static object View(World world, Nation nation) => PlannedSectors.ToDictionary(sector => sector, sector =>
    {
        var firms = SocietyLabor.Firms(world, nation.Id, sector).ToArray();
        var good = SocietyLabor.SectorOf(sector).Good;
        return new
        {
            quota_per_week = nation.Plan.GetValueOrDefault(sector),
            sold_last_week = firms.Sum(firm => firm.LastWeekUnitsSold),
            stock = good is null ? (int?)null : firms.Sum(firm => firm.Inventory[good]),
            turned_away_last_week = good is null ? firms.Sum(firm => firm.LastWeekTurnedAway) : (int?)null,
            workers = firms.Sum(firm => SocietyLabor.Workers(world, firm).Length),
            weekly_capacity = Capacity(world, nation, sector)
        };
    });

    // The plan is one action with a free number per sector, bounded well above anything the economy could use.
    public static Candidate Option(World world, Nation nation) => new("set_plan", new JsonObject(PlannedSectors.Select(sector =>
    {
        var current = nation.Plan.GetValueOrDefault(sector, Demand(world, nation, sector));
        var max = Math.Max(100, 4 * Math.Max(current, Math.Max(Demand(world, nation, sector), Capacity(world, nation, sector))));
        return KeyValuePair.Create(sector, (JsonNode?)SocietyActions.Range(0, max, Math.Min(max, Suggested(world, nation, sector, current))));
    })));

    // A rule-driven planner's adjustment (the default when no model plans): 10% more after shortages, 10% less
    // when stock piles up or a service sits idle, otherwise unchanged.
    private static int Suggested(World world, Nation nation, string sector, int current)
    {
        var firms = SocietyLabor.Firms(world, nation.Id, sector).ToArray();
        var sold = firms.Sum(firm => firm.LastWeekUnitsSold);
        var short_ = SocietyLabor.SectorOf(sector).Good is { } good
            ? (firms.Sum(firm => firm.Inventory[good]) * SocietyLabor.DaysPerWeek < sold * 3, firms.Sum(firm => firm.Inventory[good]) > sold * 2)
            : (firms.Sum(firm => firm.LastWeekTurnedAway) > 0, sold < current * 0.7);
        return short_.Item1 ? current * 11 / 10 + 1 : short_.Item2 ? current * 9 / 10 : current;
    }

    public static bool PlanningTurn(World world) => world.Tick / SocietyLabor.DaysPerWeek % PlanningWeeks == 0;

    public static void Apply(World world, Agent planner, JsonObject values, string cause)
    {
        var nation = world.Society!.Nations[planner.CountryId];
        foreach (var sector in PlannedSectors)
            nation.Plan[sector] = values[sector]!.GetValue<int>();
        world.Emit("plan_set", "rule", [nation.Id, planner.Id], new { plan = nation.Plan }, cause);
    }
}
