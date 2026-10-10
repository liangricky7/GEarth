namespace Faultline.Core;

// Labor market, business ownership and the weekly life of firms. One tick is one day; payroll,
// hiring, price reviews and closures happen weekly. Education is abstracted into the starting
// pay table and productivity, so occupations differ in pay from day one.
public static class SocietyLabor
{
    public const int DaysPerWeek = 7;
    public const int StartupWeeks = 8;
    public const int OwnerDrawBasisPoints = 5000;
    public const int DistressWeeksToClose = 4;
    public const int RelatedJobAfterDays = 14;

    // ponytail: calibration constants; move to scenario config if experiments need to vary them
    public sealed record Sector(string Id, string Occupation, string? Good, int BasePrice, double DailyOutput);

    // DailyOutput is per full-strength worker per day (clinic: visits per doctor per day). Freight earns shipping fees.
    public static readonly Sector[] Sectors =
    [
        new("food", "farmer", "food", 40, 7.5),
        new("energy", "energy_worker", "energy", 25, 22),
        new("tools", "factory_worker", "tools", 100, 3),
        new("medicine", "researcher", "medicine", 120, 4.5),
        new("clinic", "doctor", null, 450, 8),
        new("freight", "dockworker", null, 0, 0)
    ];

    public static Sector SectorOf(string id) => Sectors.Single(sector => sector.Id == id);

    // Weekly pay with education baked in: roughly real-world median ratios, farmer = 1,000 minor.
    public static readonly Dictionary<string, long> TableWage = new()
    {
        ["farmer"] = 1000, ["factory_worker"] = 1300, ["soldier"] = 1400, ["dockworker"] = 1450,
        ["teacher"] = 1750, ["energy_worker"] = 2100, ["researcher"] = 2500, ["doctor"] = 6000
    };

    // Unskilled jobs are open to any unskilled worker after a spell of unemployment; doctors and
    // researchers only move between each other. Teachers and soldiers are public-sector only.
    public static readonly string[] Unskilled = ["farmer", "energy_worker", "factory_worker", "dockworker"];

    public static bool CanSwitch(string from, string to) => from == to
        || (Unskilled.Contains(from) && Unskilled.Contains(to))
        || (from, to) is ("doctor", "researcher") or ("researcher", "doctor");

    public static bool IsResident(Agent agent) => agent.Role is "citizen" or "entrepreneur";

    public static bool IsPlanned(Nation nation) => nation.GovernmentId == "planned_republic";

    // Owned businesses act through their entrepreneur; closed businesses never act.
    public static bool DecidesForItself(Agent agent) =>
        agent.Role != "defunct" && !(agent.Role == "business" && agent.OwnerId is not null);

    public static IEnumerable<Agent> Firms(World world, string country, string sector) =>
        world.Agents.Values.Where(agent => agent.Role == "business" && agent.CountryId == country && agent.Specialty == sector);

    public static Agent[] Staff(World world, Agent firm) =>
        world.Agents.Values.Where(agent => agent.EmployerId == firm.Id && IsResident(agent)).ToArray();

    public static long StartupCost(Agent founder) => StartupWeeks * TableWage.GetValueOrDefault(founder.Occupation, 1000);

    // Founders start firms in their own trade, or in the sector they could move into as a worker.
    public static string[] StartableSectors(Agent founder) => Sectors
        .Where(sector => sector.Occupation == founder.Occupation
            || (!Unskilled.Contains(founder.Occupation) && CanSwitch(founder.Occupation, sector.Occupation)))
        .Select(sector => sector.Id).ToArray();

    // A new firm needs an edge: no incumbent, high prices, empty shelves or patients turned away.
    public static bool HasOpportunity(World world, Nation nation, string sector)
    {
        var incumbents = Firms(world, nation.Id, sector).ToArray();
        if (incumbents.Length == 0)
            return true;
        var spec = SectorOf(sector);
        if (sector == "clinic")
            return incumbents.Any(clinic => world.Society!.TurnedAwayThisWeek.GetValueOrDefault(clinic.Id) > 0)
                || incumbents.Min(clinic => clinic.PriceMinor) >= spec.BasePrice * 6 / 5;
        if (spec.Good is not { } good)
            return false;
        return incumbents.Min(firm => firm.PriceMinor) >= spec.BasePrice * 6 / 5
            || incumbents.Sum(firm => firm.Inventory[good]) < nation.Population * 3;
    }

    // Newcomers launch 10% under the cheapest incumbent.
    public static int LaunchPrice(World world, Nation nation, string sector)
    {
        var incumbents = Firms(world, nation.Id, sector).ToArray();
        var basePrice = SectorOf(sector).BasePrice;
        return incumbents.Length == 0 ? basePrice : Math.Max(1, incumbents.Min(firm => firm.PriceMinor) * 9 / 10);
    }

    public static bool CanStart(World world, Agent founder, string sector)
    {
        var nation = world.Society!.Nations[founder.CountryId];
        return founder.Role == "citizen" && !IsPlanned(nation) && StartableSectors(founder).Contains(sector)
            && founder.CashMinor >= StartupCost(founder) && HasOpportunity(world, nation, sector);
    }

    public static Agent Start(World world, Agent founder, string sector, int launchPrice, string cause)
    {
        var nation = world.Society!.Nations[founder.CountryId];
        var spec = SectorOf(sector);
        var cost = StartupCost(founder);
        var firm = new Agent
        {
            Id = $"{founder.CountryId}_{sector}_{world.Seq}", Role = "business", CountryId = founder.CountryId,
            LocationId = founder.LocationId, Occupation = sector, Specialty = sector, PriceMinor = launchPrice,
            Traits = founder.Traits, WageOfferMinor = TableWage[spec.Occupation], TargetHeadcount = 1,
            OwnerId = founder.Id, InvestedMinor = cost, FoundedTick = world.Tick,
            Inventory = SocietyFactory.Goods.ToDictionary(good => good, _ => 0),
            Goal = $"Run a profitable {sector} business owned by {founder.Id}."
        };
        world.Agents.Add(firm.Id, firm);
        SocietyEconomy.Transfer(founder, firm, cost);
        Quit(founder);
        founder.FormerOccupation = founder.Occupation;
        founder.Occupation = "entrepreneur";
        founder.Role = "entrepreneur";
        founder.OwnedBusinessId = firm.Id;
        founder.Goal = $"Keep your {sector} business profitable while meeting your own household needs.";
        nation.Population++;
        nation.BusinessesStarted++;
        world.Emit("business_started", "rule", [firm.Id, founder.Id, nation.Id], new
        {
            sector, owner = founder.Id, invested_minor = cost, launch_price_minor = launchPrice,
            edge = "market opportunity; launched below the cheapest incumbent",
            chosen_by = cause == "" ? "rule: sector had no firm" : "agent decision"
        }, cause == "" ? [] : [cause]);
        return firm;
    }

    // Weekly: autopilot adjusts headcount and wage offers, then trims staff, matches workers to
    // openings, pays wages (cutting pay or laying off when short), taxes profit and pays the owner.
    public static void Week(World world, Nation nation, Agent government, Agent[] residents)
    {
        var society = world.Society!;
        var planned = IsPlanned(nation);
        var firms = world.Agents.Values.Where(agent => agent.Role == "business" && agent.CountryId == nation.Id).ToArray();
        int hires = 0, layoffs = 0, quits = 0, payCuts = 0;
        foreach (var firm in firms.Where(_ => !planned))
            Autopilot(world, nation, firm);
        foreach (var firm in firms.Where(_ => !planned))
            foreach (var worker in Staff(world, firm).OrderBy(SocietyEconomy.Capacity).ThenByDescending(worker => worker.SalaryMinor)
                         .Take(Math.Max(0, Staff(world, firm).Length - firm.TargetHeadcount)).ToArray())
            {
                Fire(world, worker);
                layoffs++;
            }
        if (!planned)
            FillEmptySectors(world, nation, residents);
        (hires, quits) = Match(world, nation, firms, residents, planned);
        if (!planned)
            foreach (var firm in firms)
                ReviewPrice(world, firm);
        foreach (var firm in firms)
        {
            var (cut, laidOff) = Payroll(world, nation, government, firm, planned);
            payCuts += cut;
            layoffs += laidOff;
        }
        nation.Unemployed = residents.Count(resident => resident.Role == "citizen" && resident.EmployerId is null);
        world.Emit("labor_market", "rule", [nation.Id], new
        {
            hires, layoffs, job_switches = quits, pay_cuts = payCuts, unemployed = nation.Unemployed,
            active_businesses = world.Agents.Values.Count(agent => agent.Role == "business" && agent.CountryId == nation.Id),
            wage_offers = firms.Where(firm => firm.Role == "business").GroupBy(firm => SectorOf(firm.Specialty!).Occupation)
                .ToDictionary(group => group.Key, group => group.Max(firm => firm.WageOfferMinor))
        });
        foreach (var firm in firms)
        {
            society.PeriodUnitsSold[firm.Id] = 0;
            society.TurnedAwayThisWeek[firm.Id] = 0;
        }
    }

    // A market never stays without a farm, power plant, workshop, pharma or clinic for long: the richest
    // resident who could run one and can afford it opens one (agents can also choose to start firms).
    private static void FillEmptySectors(World world, Nation nation, Agent[] residents)
    {
        foreach (var sector in Sectors.Where(sector => sector.Id != "freight" && !Firms(world, nation.Id, sector.Id).Any()))
        {
            var founder = residents.Where(person => CanStart(world, person, sector.Id))
                .OrderByDescending(person => person.CashMinor).ThenBy(person => person.Id).FirstOrDefault();
            if (founder is not null)
                Start(world, founder, sector.Id, LaunchPrice(world, nation, sector.Id), "");
        }
    }

    // Between owner turns, firms follow simple rules: hire when shelves empty, shrink when stock
    // piles up or a service is idle, raise wage offers when openings stay unfilled.
    private static void Autopilot(World world, Nation nation, Agent firm)
    {
        var society = world.Society!;
        var spec = SectorOf(firm.Specialty!);
        var staff = Staff(world, firm).Length;
        var revenue = society.PeriodRevenueMinor.GetValueOrDefault(firm.Id);
        var payroll = Staff(world, firm).Sum(worker => worker.SalaryMinor);
        var profitable = revenue - society.PeriodCostMinor.GetValueOrDefault(firm.Id) > payroll;
        // Only a profitable firm bids up wages for unfilled openings; a loss-making one lets offers cool.
        var table = TableWage[spec.Occupation];
        if (staff < firm.TargetHeadcount && profitable)
            firm.WageOfferMinor = Math.Min(table * 3 / 2, firm.WageOfferMinor * 105 / 100);
        else if (!profitable && firm.WageOfferMinor > table)
            firm.WageOfferMinor = Math.Max(table, firm.WageOfferMinor * 95 / 100);
        var change = 0;
        if (spec.Good is { } good)
        {
            var dailySales = society.PeriodUnitsSold.GetValueOrDefault(firm.Id) / (double)DaysPerWeek;
            var stockDays = dailySales == 0 ? double.MaxValue : firm.Inventory[good] / dailySales;
            change = stockDays > 21 ? -1 : stockDays < 7 && profitable ? 1 : 0;
        }
        else if (spec.Id == "clinic")
        {
            var served = society.PeriodUnitsSold.GetValueOrDefault(firm.Id);
            var capacity = (staff + (firm.OwnerId is null ? 0 : 1)) * spec.DailyOutput * DaysPerWeek;
            change = society.TurnedAwayThisWeek.GetValueOrDefault(firm.Id) > 0 && profitable ? 1
                : capacity > 0 && served < capacity * 0.4 ? -1 : 0;
        }
        else if (revenue < payroll)
            change = -1;
        firm.TargetHeadcount = Math.Max(0, firm.TargetHeadcount + change);
    }

    private static (int Hires, int Switches) Match(World world, Nation nation, Agent[] firms, Agent[] residents, bool planned)
    {
        int hires = 0, switches = 0;
        foreach (var firm in firms.Where(firm => firm.Role == "business").OrderByDescending(firm => firm.WageOfferMinor).ThenBy(firm => firm.Id))
        {
            var occupation = SectorOf(firm.Specialty!).Occupation;
            var open = firm.TargetHeadcount - Staff(world, firm).Length;
            if (open <= 0)
                continue;
            var unemployed = residents.Where(person => person.Role == "citizen" && person.EmployerId is null
                    && (person.Occupation == occupation || (CanSwitch(person.Occupation, occupation)
                        && world.Tick - person.UnemployedSinceTick >= RelatedJobAfterDays)))
                .OrderBy(person => person.UnemployedSinceTick).ThenBy(person => person.Id);
            // Employed workers switch for at least 10% more pay; public employees stay in the public sector.
            var switchers = residents.Where(person => person.Role == "citizen" && person.EmployerId is { } current
                    && current != firm.Id && world.Agents[current].Role == "business"
                    && person.Occupation == occupation && person.SalaryMinor * 11 / 10 < firm.WageOfferMinor)
                .OrderBy(person => person.SalaryMinor).ThenBy(person => person.Id);
            foreach (var person in unemployed.Concat(switchers).Take(open).ToArray())
            {
                if (person.EmployerId is not null)
                    switches++;
                Hire(world, firm, person, occupation);
                hires++;
            }
        }
        if (!planned)
            return (hires, switches);
        // Planned economies assign every unemployed resident to a state firm in their sector.
        foreach (var person in residents.Where(person => person.Role == "citizen" && person.EmployerId is null).ToArray())
        {
            var firm = firms.FirstOrDefault(firm => firm.Role == "business" && SectorOf(firm.Specialty!).Occupation == person.Occupation)
                ?? firms.FirstOrDefault(firm => firm.Role == "business" && CanSwitch(person.Occupation, SectorOf(firm.Specialty!).Occupation));
            if (firm is null)
                continue;
            firm.TargetHeadcount++;
            Hire(world, firm, person, SectorOf(firm.Specialty!).Occupation);
            hires++;
        }
        return (hires, switches);
    }

    private static (int PayCuts, int Layoffs) Payroll(World world, Nation nation, Agent government, Agent firm, bool planned)
    {
        var society = world.Society!;
        var staff = Staff(world, firm);
        int cuts = 0, layoffs = 0;
        var revenue = society.PeriodRevenueMinor.GetValueOrDefault(firm.Id);
        if (!planned && staff.Where(worker => worker.WorkEffort > 0).Sum(worker => worker.SalaryMinor) > firm.CashMinor)
        {
            // Short of cash: first a 10% pay cut, then lay off the most expensive workers.
            foreach (var worker in staff)
                worker.SalaryMinor = worker.SalaryMinor * 9 / 10;
            cuts = staff.Length;
            while (staff.Length > 0 && staff.Sum(worker => worker.SalaryMinor) > firm.CashMinor)
            {
                var expensive = staff.OrderByDescending(worker => worker.SalaryMinor).First();
                Fire(world, expensive);
                staff = staff.Where(worker => worker != expensive).ToArray();
                layoffs++;
            }
            firm.TargetHeadcount = Math.Min(firm.TargetHeadcount, staff.Length);
            firm.DistressWeeks++;
        }
        else if (!planned)
            firm.DistressWeeks = revenue == 0 && firm.CashMinor < TableWage[SectorOf(firm.Specialty!).Occupation]
                ? firm.DistressWeeks + 1 : 0;
        var paid = 0L;
        foreach (var worker in staff)
            if (worker.WorkEffort > 0) // strikers forfeit the week's pay
                paid += SocietyEconomy.PayWage(nation, government, firm, worker, Math.Min(worker.SalaryMinor, firm.CashMinor));
        var profit = revenue - society.PeriodCostMinor.GetValueOrDefault(firm.Id) - paid;
        var profitTax = Math.Min(firm.CashMinor, Math.Max(0, profit) * nation.ProfitTaxBasisPoints / 10000);
        SocietyEconomy.Transfer(firm, government, profitTax);
        nation.ProfitTaxCollectedMinor += profitTax;
        nation.TaxesCollectedMinor += profitTax;
        // Owners are paid from profit: up to their former trade's wage first, then half of the rest.
        // A losing business pays its owner nothing, which keeps it alive longer.
        if (firm.OwnerId is { } ownerId && world.Agents[ownerId] is { Role: "entrepreneur" } owner && profit > profitTax)
        {
            var afterTax = profit - profitTax;
            var wage = TableWage.GetValueOrDefault(owner.FormerOccupation ?? "", 0);
            var draw = Math.Min(afterTax, wage) + Math.Max(0, afterTax - wage) * OwnerDrawBasisPoints / 10000;
            SocietyEconomy.Transfer(firm, owner, Math.Min(firm.CashMinor, draw));
        }
        society.PeriodRevenueMinor[firm.Id] = 0;
        society.PeriodCostMinor[firm.Id] = 0;
        if (!planned && firm.DistressWeeks >= DistressWeeksToClose)
            Close(world, nation, firm, "unable to cover costs for four weeks");
        return (cuts, layoffs);
    }

    // Prices move 5% a week: up when shelves empty, patients are turned away or costs exceed sales;
    // down when stock piles up or a clinic sits mostly idle.
    private static void ReviewPrice(World world, Agent firm)
    {
        var society = world.Society!;
        var spec = SectorOf(firm.Specialty!);
        if (spec.BasePrice == 0)
            return;
        var sold = society.PeriodUnitsSold.GetValueOrDefault(firm.Id);
        var losing = society.PeriodCostMinor.GetValueOrDefault(firm.Id) + Staff(world, firm).Sum(worker => worker.SalaryMinor)
            > society.PeriodRevenueMinor.GetValueOrDefault(firm.Id);
        int direction;
        if (spec.Good is { } good)
        {
            var dailySales = sold / (double)DaysPerWeek;
            var stockDays = dailySales == 0 ? double.MaxValue : firm.Inventory[good] / dailySales;
            direction = stockDays < 3 ? 1 : stockDays > 21 ? -1 : losing ? 1 : 0;
        }
        else
        {
            var turnedAway = society.TurnedAwayThisWeek.GetValueOrDefault(firm.Id);
            var capacity = (Staff(world, firm).Length + (firm.OwnerId is null ? 0 : 1)) * spec.DailyOutput * DaysPerWeek;
            direction = turnedAway > sold / 10 ? 1 : turnedAway == 0 && sold < capacity * 0.6 ? -1 : losing ? 1 : 0;
        }
        firm.PriceMinor = Math.Clamp(firm.PriceMinor * (100 + 5 * direction) / 100, spec.BasePrice / 2, spec.BasePrice * 3);
    }

    // Closing returns the firm's cash to its owner; a competitor buys stock at half price, and
    // whatever is not bought goes to the owner. Nothing is created or destroyed.
    public static void Close(World world, Nation nation, Agent firm, string reason)
    {
        var staff = Staff(world, firm);
        foreach (var worker in staff)
            Fire(world, worker);
        var owner = firm.OwnerId is { } ownerId ? world.Agents[ownerId] : null;
        var recovered = 0L;
        foreach (var good in SocietyFactory.Goods)
        {
            var units = firm.Inventory[good];
            if (units == 0)
                continue;
            var buyer = world.Agents.Values.Where(agent => agent.Role == "business" && agent != firm
                    && agent.CountryId == firm.CountryId && agent.Specialty == firm.Specialty)
                .OrderByDescending(agent => agent.CashMinor).FirstOrDefault();
            var price = Math.Max(1, SocietyFactory.BasePrice(good) / 2);
            var sold = buyer is null ? 0 : (int)Math.Min(units, buyer.CashMinor / price);
            if (sold > 0)
            {
                SocietyEconomy.Transfer(buyer!, firm, (long)sold * price);
                buyer!.Inventory[good] += sold;
            }
            if (owner is not null)
                owner.Inventory[good] += units - sold;
            else if (buyer is not null)
                buyer.Inventory[good] += units - sold;
            else
                continue;
            firm.Inventory[good] = 0;
        }
        if (owner is not null)
        {
            recovered = firm.CashMinor;
            SocietyEconomy.Transfer(firm, owner, firm.CashMinor);
            owner.Role = "citizen";
            owner.Occupation = owner.FormerOccupation ?? "factory_worker";
            owner.OwnedBusinessId = null;
            owner.EmployerId = null;
            owner.UnemployedSinceTick = world.Tick;
            owner.Goal = $"Find work as a {owner.Occupation.Replace('_', ' ')}, meet household needs and rebuild savings.";
        }
        else
            SocietyEconomy.Transfer(firm, world.Agents[nation.Id + "_government"], firm.CashMinor);
        firm.Role = "defunct";
        firm.TargetHeadcount = 0;
        nation.Population--;
        nation.BusinessesClosed++;
        world.Emit("business_closed", "rule", [firm.Id, nation.Id], new
        {
            sector = firm.Specialty, owner = firm.OwnerId, reason, workers_laid_off = staff.Length,
            invested_minor = firm.InvestedMinor, recovered_minor = recovered
        });
    }

    public static void Hire(World world, Agent firm, Agent person, string occupation)
    {
        person.EmployerId = firm.Id;
        person.Occupation = occupation;
        person.SalaryMinor = firm.WageOfferMinor;
        person.UnemployedSinceTick = -1;
    }

    public static void Fire(World world, Agent person)
    {
        person.EmployerId = null;
        person.SalaryMinor = 0;
        person.UnemployedSinceTick = world.Tick;
    }

    private static void Quit(Agent person)
    {
        person.EmployerId = null;
        person.SalaryMinor = 0;
    }
}
