namespace GEarth.Core;

// Automatic weekly job matching is this world's job-search mechanism. Full-time students and people too
// sick to work are nonparticipants; a current job or an active owned firm counts as employment.
public static class SocietyMetrics
{
    public static bool Employed(Agent person) => person.EmployerId is not null
        || person.Role == "entrepreneur" && person.OwnedBusinessId is not null || person.Role == "planner";

    public static bool SeekingWork(Agent person) => person.Role == "citizen" && person.EmployerId is null
        && !SocietyLabor.IsStudent(person) && !SocietyEconomy.TooSick(person) && person.WorkEffort > 0;

    public static void Labor(World world, Nation nation)
    {
        var residents = world.InCountry(nation.Id).Where(SocietyLabor.IsResident).ToArray();
        nation.EmployedResidents = residents.Count(Employed);
        nation.Unemployed = residents.Count(SeekingWork);
        nation.LaborForce = nation.EmployedResidents + nation.Unemployed;
        nation.Nonparticipants = residents.Length - nation.LaborForce;
        nation.JoblessResidents = residents.Count(person => !Employed(person));
        nation.Students = residents.Count(SocietyLabor.IsStudent);
    }

    public static long NetWealth(World world, Agent person)
    {
        var financial = SocietyBank.Savings(person) + person.ArrearsMinor - SocietyBank.Debt(world, person);
        // Guaranteed owner debt is already subtracted above; do not subtract it again from estate assets.
        financial += world.Agents.Values.Where(estate => estate.Role == "defunct" && estate.EstateBeneficiaryId == person.Id)
            .Sum(estate => SocietyBank.Savings(estate)
                + estate.Inventory.Sum(item => (long)item.Value * SocietyEstates.Price(item.Key))
                - SocietyBank.Debt(world, estate));
        if (person.OwnedBusinessId is not { } id || !world.Agents.TryGetValue(id, out var firm) || firm.Role != "business")
            return financial;
        var equity = SocietyBank.IsBank(firm) ? SocietyBank.Equity(world, firm)
            : SocietyBank.Savings(firm) + firm.Inventory.Sum(item => (long)item.Value * InventoryPrice(world, firm, item.Key))
                - SocietyBank.Debt(world, firm) - SocietyLabor.Staff(world, firm).Sum(worker => worker.ArrearsMinor);
        return financial + Math.Max(0, equity); // an owner's negative equity becomes personal debt on closure
    }

    private static int InventoryPrice(World world, Agent firm, string good) =>
        (int)Math.Round(SocietyLabor.Firms(world, firm.CountryId, SocietyResources.SectorForGood(good).Id)
            .Select(seller => (double)seller.PriceMinor).DefaultIfEmpty(SocietyFactory.BasePrice(good)).Min());

    public static void Distribution(World world, Nation nation)
    {
        var people = world.InCountry(nation.Id).Where(SocietyLabor.IsResident).ToArray();
        nation.GiniCash = Math.Round(SocietyEconomy.Gini(people.Select(SocietyBank.Savings).ToArray()), 4);
        var wealth = people.Select(person => NetWealth(world, person)).ToArray();
        nation.GiniNetWealth = wealth.Sum() <= 0 ? null : Math.Round(SocietyEconomy.Gini(wealth), 4);
        nation.GiniDisposableIncome = Math.Round(SocietyEconomy.Gini(people.Select(person => person.LastWeekDisposableIncomeMinor).ToArray()), 4);
        nation.EssentialsBasketMinor = (long)Math.Ceiling(SocietyLabor.LivingCost(world, nation) * (1 + nation.SalesTaxBasisPoints / 10000.0));
        var ordered = people.Select(person => person.LastWeekRealIncomeMinor).Order().ToArray();
        nation.MedianRealDisposableIncomeMinor = ordered.Length == 0 ? 0 : ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2] : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
    }

    public static object View(Nation nation) => new
    {
        nation.EmployedResidents, unemployed = nation.Unemployed, nation.LaborForce, nation.Nonparticipants,
        nation.JoblessResidents, nation.Students,
        unemployment_rate = nation.LaborForce == 0 ? (double?)null : nation.Unemployed / (double)nation.LaborForce,
        unemployment_definition = "available citizen participating in automatic job matching; excludes students and work refusals",
        liquid_assets_gini = nation.GiniCash, nation.GiniDisposableIncome, nation.GiniNetWealth,
        wealth_definition = "cash + deposits + wage arrears receivable - personal debt + nonnegative owned business equity, inventories at local quotes + unresolved estate assets at liquidation quotes; land and fixed capital unvalued",
        nation.EssentialsBasketMinor, nation.MedianRealDisposableIncomeMinor,
        real_income_definition = "weekly disposable income at base food-and-energy purchasing power; fixed 7+7 basket, including sales tax, before food subsidy"
    };
}
