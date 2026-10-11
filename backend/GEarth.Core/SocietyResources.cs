namespace GEarth.Core;

public sealed partial class Nation
{
    // A consumed energy unit powers ten actual service sessions, including across week boundaries.
    public Dictionary<string, int> PoweredSessionsLeft { get; set; } = new();
    // Fractions of an already consumed input power later output; rounding every small daily batch would
    // otherwise make low-concentration mines spend one energy per unit instead of one per ten.
    public Dictionary<string, Dictionary<string, int>> ProductionInputCredits { get; set; } = new();
    // Delivery ticks survive checkpoint/event-window trimming; government views count arrivals, not orders.
    public Dictionary<string, Dictionary<int, long>> ResourceImports { get; set; } = new();
}

public static class SocietyResources
{
    public const int FixedShippingMinor = 20;
    public const double ShippingRatePerDistance = 0.01; // ponytail: tune after resource runs
    public const int ServiceSessionsPerEnergy = 10;
    public const string Note = "Ore and fuel are industrial inputs, not household needs: energy uses fuel, tools use ore and energy, and extraction depends on each country's endowment, and raw-material founders respond to market opportunity. Shipping costs 20 plus quantity times route distance/100, discounted by ports; import tariffs are 5% (zero with an agreement), and input prices include landed costs. ";

    // Three rich, four middling and three poor countries per material; rich farmland or a rich raw material
    // in every country. Patterns cross government profiles and never leave a material entirely absent.
    private static readonly double[] Ore = [0.25, 0.3, 1.8, 2.0, 0.8, 0.4, 0.85, 0.9, 1.8, 0.9];
    private static readonly double[] Fuel = [0.8, 0.8, 0.3, 0.3, 1.8, 0.85, 0.9, 2.0, 0.35, 1.9];

    public static void Setup(World world, Nation nation)
    {
        var index = world.Society!.Nations.Keys.ToList().IndexOf(nation.Id);
        nation.Endowment["ore"] = Ore[index];
        nation.Endowment["fuel"] = Fuel[index];
    }

    // Both extraction firms hire miners; cumulative proportional allocation avoids giving a poor deposit
    // half the workforce simply because the two firms share an occupation.
    public static string MiningSector(Nation nation, int number)
    {
        var share = PhaseContracts.Endowment(nation, "ore")
            / (PhaseContracts.Endowment(nation, "ore") + PhaseContracts.Endowment(nation, "fuel"));
        return Math.Floor((number + 1) * share) > Math.Floor(number * share) ? "mine" : "well";
    }

    public static SocietyLabor.Sector SectorForGood(string good) =>
        SocietyLabor.Sectors.Single(sector => sector.Id == good || sector.Good == good);

    // One unit of the input per this many output units. Production and marginal-price estimates share it.
    public static (string Good, int OutputsPerInput)[] Inputs(string sector) => sector switch
    {
        "energy" => [("fuel", 4)],
        "tools" => [("ore", 1), ("energy", 10)],
        "medicine" => [("energy", 10), ("tools", 10)],
        "food" or "mine" or "well" or "entertainment" or "school" => [("energy", 10)],
        "clinic" => [("medicine", 1)],
        _ => []
    };

    public static long ShippingCost(World world, Agent buyer, PathPlan path, int quantity)
    {
        if (quantity < 1)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        var discount = Math.Clamp(PhaseContracts.Boost(world.Society!.Nations[buyer.CountryId], "shipping"), 0, 1);
        return checked((long)Math.Ceiling((FixedShippingMinor + quantity * ShippingRatePerDistance * path.CostMinor) * (1 - discount)));
    }

    // Per unit after the currency adjustment, freight and tariff.
    public static double LandedUnitCost(World world, Agent buyer, Agent seller, PathPlan path, int quantity)
    {
        var paid = SocietyTrade.Price(world, buyer, seller, quantity).Paid;
        return (paid + ShippingCost(world, buyer, path, quantity) + SocietyTrade.Tariff(world, buyer, seller, paid)) / (double)quantity;
    }

    // Current replacement cost: buy locally first, otherwise include freight and tariff from a reachable
    // foreign supplier. A missing supplier uses the base quote; its absence still blocks actual production.
    public static double InputUnitCost(World world, Agent firm, string good, int quantity)
    {
        var local = SocietyEconomy.Offers(world, world.Society!.Nations[firm.CountryId], good)
            .Where(offer => offer.Seller != firm).FirstOrDefault();
        return local is not null ? local.PriceMinor : ImportQuote(world, firm, good, quantity) ?? SocietyFactory.BasePrice(good);
    }

    // The cheapest landed cost per unit from a reachable foreign seller with stock, or null when none can ship.
    public static double? ImportQuote(World world, Agent buyer, string good, int quantity) =>
        Quotes(world, buyer, good, 1).Select(quote => (double?)LandedUnitCost(world, buyer, quote.Seller, quote.Path,
            Math.Max(1, Math.Min(quantity, SocietyTrade.ExportableStock(world, quote.Seller, good))))).Min();

    // Reachable foreign sellers holding at least the quantity, with their routes. Route search is costly, so
    // only the few cheapest at the dock are priced. ponytail: cheapest 4 by price; widen if far sellers matter
    public static (Agent Seller, PathPlan Path)[] Quotes(World world, Agent buyer, string good, int quantity) =>
        world.Agents.Values.Where(seller => seller.Role == "business" && seller.CountryId != buyer.CountryId
                && SocietyLabor.SectorOf(seller.Specialty!).Good == good && SocietyTrade.ExportableStock(world, seller, good) >= quantity
                && !SocietyDiplomacy.Relation(world, buyer.CountryId, seller.CountryId).AtWar)
            .OrderBy(seller => SocietyTrade.ImportUnitPrice(world, buyer, seller)).ThenBy(seller => seller.Id, StringComparer.Ordinal).Take(4)
            .Select(seller => (Seller: seller, Path: SocietyTrade.Path(world, seller.LocationId, buyer.LocationId, "cheapest")))
            .Where(quote => quote.Path is not null).Select(quote => (quote.Seller, quote.Path!)).ToArray();

    public static double InputCostPerOutput(World world, Agent firm) => Inputs(firm.Specialty!).Sum(input =>
        InputUnitCost(world, firm, input.Good, Math.Max(1, firm.LastWeekUnitsSold / input.OutputsPerInput)) / input.OutputsPerInput);

    // Operating funding must include physical inputs as well as wages. Forecast only what the current
    // workers and quota could produce; this supplies money, never inputs or extra productive capacity.
    public static long InputWorkingCapital(World world, Nation nation, Agent firm)
    {
        var spec = SocietyLabor.SectorOf(firm.Specialty!);
        var quantity = spec.Good is { } good
            ? (int)Math.Ceiling(SocietyLabor.Workers(world, firm).Sum(worker => SocietyEconomy.Capacity(worker)
                * SocietyLabor.DayEffort(world, worker)) * spec.DailyOutput * SocietyLabor.DaysPerWeek
                * SocietyEconomy.OutputMultiplier(world, nation, good))
            : firm.Specialty == "school" ? SocietyLabor.Students(world, firm).Length * SocietyLabor.DaysPerWeek
                : (int)Math.Ceiling(SocietyLabor.ServiceCapacity(world, firm));
        quantity = SocietyPlanning.Cap(world, nation, firm, quantity, SocietyLabor.DaysPerWeek);
        var cost = 0.0;
        foreach (var input in Inputs(firm.Specialty!))
        {
            var needed = Math.Max(0, (quantity + input.OutputsPerInput - 1) / input.OutputsPerInput - firm.Inventory[input.Good]);
            if (needed > 0) cost += needed * InputUnitCost(world, firm, input.Good, needed);
        }
        if (spec.Good is null && !Inputs(firm.Specialty!).Any(input => input.Good == "energy"))
        {
            var needed = Math.Max(0, (quantity + ServiceSessionsPerEnergy - 1) / ServiceSessionsPerEnergy - firm.Inventory["energy"]);
            if (needed > 0) cost += needed * InputUnitCost(world, firm, "energy", needed);
        }
        return (long)Math.Ceiling(cost);
    }

    public static int SupplyProduction(World world, Agent firm, int quantity)
    {
        var nation = world.Society!.Nations[firm.CountryId];
        if (!nation.ProductionInputCredits.TryGetValue(firm.Id, out var credits))
            nation.ProductionInputCredits[firm.Id] = credits = new();
        var inputs = Inputs(firm.Specialty!);
        foreach (var input in inputs)
        {
            var left = credits.GetValueOrDefault(input.Good);
            var needed = Math.Max(0, (quantity - left + input.OutputsPerInput - 1) / input.OutputsPerInput);
            SocietyEconomy.AcquireInput(world, firm, input.Good, Math.Max(0, needed - firm.Inventory[input.Good]));
            quantity = (int)Math.Min(quantity, (long)firm.Inventory[input.Good] * input.OutputsPerInput + left);
        }
        foreach (var input in inputs)
        {
            var left = credits.GetValueOrDefault(input.Good);
            var used = Math.Max(0, (quantity - left + input.OutputsPerInput - 1) / input.OutputsPerInput);
            firm.Inventory[input.Good] -= used;
            world.Society.ConsumedGoods[input.Good] += used;
            credits[input.Good] = left + used * input.OutputsPerInput - quantity;
        }
        return quantity;
    }

    // Acquisition itself never consumes goods. Only a delivered service spends energy, in batches of ten.
    public static bool PowerSession(World world, Agent firm)
    {
        var nation = world.Society!.Nations[firm.CountryId];
        var left = nation.PoweredSessionsLeft.GetValueOrDefault(firm.Id);
        if (left == 0)
        {
            SocietyEconomy.AcquireInput(world, firm, "energy", Math.Max(0, 1 - firm.Inventory["energy"]));
            if (firm.Inventory["energy"] == 0)
                return false;
            firm.Inventory["energy"]--;
            world.Society.ConsumedGoods["energy"]++;
            left = ServiceSessionsPerEnergy;
        }
        nation.PoweredSessionsLeft[firm.Id] = left - 1;
        return true;
    }

    public static bool CanPowerSession(World world, Agent firm) =>
        world.Society!.Nations[firm.CountryId].PoweredSessionsLeft.GetValueOrDefault(firm.Id) > 0
        || firm.Inventory["energy"] > 0;

    public static void PrepareServices(World world, Agent firm, int capacity)
    {
        var left = world.Society!.Nations[firm.CountryId].PoweredSessionsLeft.GetValueOrDefault(firm.Id);
        SocietyEconomy.AcquireInput(world, firm, "energy",
            Math.Max(0, (capacity - left + ServiceSessionsPerEnergy - 1) / ServiceSessionsPerEnergy - firm.Inventory["energy"]));
    }

    public static void SchoolDay(World world, Agent school)
    {
        var students = SocietyLabor.Students(world, school).OrderBy(person => person.Id).ToArray();
        if (students.Length == 0)
            return;
        var capacity = SocietyLabor.Seats(world, school);
        PrepareServices(world, school, Math.Min(students.Length, capacity));
        for (var index = 0; index < students.Length; index++)
            if (index >= capacity || !PowerSession(world, school))
                students[index].StudentUntilTick++; // an unpowered or unstaffed school cannot teach that day
    }

    public static void RecordImport(World world, Cargo cargo)
    {
        if (!PhaseContracts.RawMaterials.Contains(cargo.Good))
            return;
        var nation = world.Society!.Nations[world.Agents[cargo.BuyerId].CountryId];
        if (!nation.ResourceImports.TryGetValue(cargo.Good, out var days))
            nation.ResourceImports[cargo.Good] = days = new();
        days[world.Tick] = days.GetValueOrDefault(world.Tick) + cargo.Quantity;
        foreach (var tick in days.Keys.Where(tick => tick < world.Tick - 2 * SocietyLabor.DaysPerWeek).ToArray())
            days.Remove(tick);
    }

    // The last completed calendar week, so arriving cargo does not appear as a completed import early.
    public static object View(World world, Nation nation)
    {
        var through = world.Tick / SocietyLabor.DaysPerWeek * SocietyLabor.DaysPerWeek;
        return new
        {
            endowment = nation.Endowment,
            stock = PhaseContracts.RawMaterials.ToDictionary(good => good,
                good => world.InCountry(nation.Id).Where(agent => agent.Role == "business").Sum(agent => agent.Inventory.GetValueOrDefault(good))),
            imports_last_week = PhaseContracts.RawMaterials.ToDictionary(good => good,
                good => nation.ResourceImports.GetValueOrDefault(good)?.Where(day => day.Key > through - SocietyLabor.DaysPerWeek && day.Key <= through).Sum(day => day.Value) ?? 0),
            import_week_through_tick = through
        };
    }
}
