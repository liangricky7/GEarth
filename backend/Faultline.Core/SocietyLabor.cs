namespace Faultline.Core;

// Labor market, business ownership and the weekly life of firms. One tick is one day; payroll,
// hiring, price reviews and closures happen weekly. Education is abstracted into the starting
// pay table and productivity, so occupations differ in pay from day one.
public static class SocietyLabor
{
    public const int DaysPerWeek = 7;
    public const int StartupWeeks = 8;
    public const int ServiceStartupWeeks = 4; // a venue needs less capital than a farm or workshop
    public const int OwnerDrawBasisPoints = 5000;
    public const int DividendBufferWeeks = 8; // firms keep this many weeks of payroll; the rest goes to the owner
    public const int DistressWeeksToClose = 4;
    public const int RelatedJobAfterDays = 14;
    public const int MaxPriceStepPct = 20;
    // Overtime adds a quarter more hours, paid at time and a half.
    public const double OvertimeOutput = 1.25;
    public const int OvertimePayEighths = 3; // 0.25 extra hours x 1.5 = 3/8 of the daily wage

    // ponytail: calibration constants; move to scenario config if experiments need to vary them
    public sealed record Sector(string Id, string Occupation, string? Good, int BasePrice, double DailyOutput, int Firms);

    // DailyOutput is per full-strength worker per day (clinic: visits per doctor; entertainment: sessions per
    // worker). Freight earns shipping fees; the bank earns interest (SocietyBank). Firms is how many compete
    // in each country at the start.
    public static readonly Sector[] Sectors =
    [
        new("food", "farmer", "food", 40, 7.5, 3),
        new("energy", "energy_worker", "energy", 25, 22, 2),
        new("tools", "factory_worker", "tools", 100, 3, 2),
        new("entertainment", "service_worker", null, 60, 5, 2),
        new("medicine", "researcher", "medicine", 120, 4.5, 1),
        new("clinic", "doctor", null, 450, 8, 1),
        new("freight", "dockworker", null, 0, 0, 1),
        new(SocietyBank.Sector, "banker", null, 0, 0, 1)
    ];

    public static Sector SectorOf(string id) => Sectors.Single(sector => sector.Id == id);

    // Services are sold by the visit or session and cannot be stocked.
    public static bool IsService(Sector sector) => sector.Good is null && sector.BasePrice > 0;

    // Weekly pay with education baked in: roughly real-world median ratios, farmer = 1,000 minor.
    public static readonly Dictionary<string, long> TableWage = new()
    {
        ["farmer"] = 1000, ["service_worker"] = 1100, ["factory_worker"] = 1300, ["soldier"] = 1400, ["dockworker"] = 1450,
        ["teacher"] = 1750, ["banker"] = 2000, ["energy_worker"] = 2100, ["researcher"] = 2500, ["doctor"] = 6000
    };

    // Unskilled jobs are open to anyone after a spell of unemployment, so skilled workers can step down;
    // doctors and researchers also move between each other. Banks train their own staff, so anyone can
    // become a banker. Teachers and soldiers are public-sector only.
    public static readonly string[] Unskilled = ["farmer", "service_worker", "energy_worker", "factory_worker", "dockworker"];

    public static bool CanSwitch(string from, string to) => from == to
        || Unskilled.Contains(to) || to == "banker"
        || (from, to) is ("doctor", "researcher") or ("researcher", "doctor");

    public static bool IsResident(Agent agent) => agent.Role is "citizen" or "entrepreneur";

    public static bool IsPlanned(Nation nation) => nation.GovernmentId == "planned_republic";

    // Owned businesses act through their entrepreneur; closed businesses never act.
    public static bool DecidesForItself(Agent agent) =>
        agent.Role != "defunct" && !(agent.Role == "business" && agent.OwnerId is not null);

    public static IEnumerable<Agent> Firms(World world, string country, string sector) =>
        world.InCountry(country).Where(agent => agent.Role == "business" && agent.Specialty == sector);

    // Staff live in the firm's country: migrating clears a resident's employer.
    public static Agent[] Staff(World world, Agent firm) =>
        world.InCountry(firm.CountryId).Where(agent => agent.EmployerId == firm.Id && IsResident(agent)).ToArray();

    // Everyone who works at a firm: its staff plus a working owner.
    public static Agent[] Workers(World world, Agent firm) => Staff(world, firm)
        .Concat(firm.OwnerId is { } owner && world.Agents[owner].Role == "entrepreneur" ? [world.Agents[owner]] : [])
        .ToArray();

    // Output factor for one working day: a striker produces nothing; overtime adds a quarter.
    public static double DayEffort(World world, Agent worker)
    {
        if (worker.WorkEffort == 0)
            return 0;
        return WorksOvertime(world, worker) ? OvertimeOutput : 1;
    }

    // Overtime needs the employer's offer and the worker's consent (effort 2); owners decide for themselves.
    public static bool WorksOvertime(World world, Agent worker) => worker.WorkEffort == 2
        && (worker.Role == "entrepreneur" && worker.OwnedBusinessId is not null
            || worker.EmployerId is { } employer && world.Agents[employer] is { Role: "business", OvertimeOffered: true });

    public static long StartupCost(Agent founder, string sector) =>
        (sector == "entertainment" ? ServiceStartupWeeks : StartupWeeks) * TableWage.GetValueOrDefault(founder.Occupation, 1000);

    // Founders start firms in their own trade or one they could move into as skilled workers;
    // anyone can open an entertainment venue. Each country has one bank, so none can be started.
    public static string[] StartableSectors(Agent founder) => Sectors
        .Where(sector => sector.Id != SocietyBank.Sector)
        .Where(sector => sector.Occupation == founder.Occupation || sector.Id == "entertainment"
            || (!Unskilled.Contains(founder.Occupation) && !Unskilled.Contains(sector.Occupation)
                && CanSwitch(founder.Occupation, sector.Occupation)))
        .Select(sector => sector.Id).ToArray();

    // A new firm needs an edge: no incumbent, high prices, empty shelves or customers turned away.
    public static bool HasOpportunity(World world, Nation nation, string sector)
    {
        var incumbents = Firms(world, nation.Id, sector).ToArray();
        if (incumbents.Length == 0)
            return true;
        var spec = SectorOf(sector);
        if (IsService(spec))
            return incumbents.Any(firm => world.Society!.TurnedAwayThisWeek.GetValueOrDefault(firm.Id) > 0)
                || incumbents.Min(firm => firm.PriceMinor) >= spec.BasePrice * 6 / 5;
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

    // Founders pay from cash and deposits, or borrow up to the bank's limit for the rest.
    public static bool CanStart(World world, Agent founder, string sector, long loan = 0)
    {
        var nation = world.Society!.Nations[founder.CountryId];
        var cost = StartupCost(founder, sector);
        return founder.Role == "citizen" && !IsPlanned(nation) && StartableSectors(founder).Contains(sector)
            && SocietyBank.Liquid(world, founder) + loan >= cost && loan <= SocietyBank.StartupLoan(world, founder, cost)
            && HasOpportunity(world, nation, sector);
    }

    public static Agent Start(World world, Agent founder, string sector, int launchPrice, string cause)
    {
        var nation = world.Society!.Nations[founder.CountryId];
        var spec = SectorOf(sector);
        var cost = StartupCost(founder, sector);
        var firm = new Agent
        {
            Id = $"{founder.CountryId}_{sector}_firm{world.NextEntityNumber()}", Role = "business", CountryId = founder.CountryId,
            LocationId = founder.LocationId, Occupation = sector, Specialty = sector, PriceMinor = launchPrice,
            Traits = founder.Traits, WageOfferMinor = TableWage[spec.Occupation], TargetHeadcount = 1,
            OwnerId = founder.Id, InvestedMinor = cost, FoundedTick = world.Tick,
            Inventory = SocietyFactory.Goods.ToDictionary(good => good, _ => 0),
            Goal = $"Run a profitable {sector} business owned by {founder.Id}."
        };
        world.Agents.Add(firm.Id, firm);
        SocietyBank.Draw(world, founder, cost);
        SocietyEconomy.Transfer(world, founder, firm, cost, "business_founding");
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
        var firms = world.InCountry(nation.Id).Where(agent => agent.Role == "business").ToArray();
        int hires = 0, layoffs = 0, quits = 0, payCuts = 0;
        var dividends = 0L;
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
            var (cut, laidOff, dividend) = Payroll(world, nation, government, firm, planned);
            payCuts += cut;
            layoffs += laidOff;
            dividends += dividend;
        }
        nation.Unemployed = residents.Count(resident => resident.Role == "citizen" && resident.EmployerId is null);
        world.Emit("labor_market", "rule", [nation.Id], new
        {
            hires, layoffs, job_switches = quits, pay_cuts = payCuts, unemployed = nation.Unemployed,
            dividends_minor = dividends,
            overtime_offers = firms.Count(firm => firm.Role == "business" && firm.OvertimeOffered),
            active_businesses = world.InCountry(nation.Id).Count(agent => agent.Role == "business"),
            wage_offers = firms.Where(firm => firm.Role == "business").GroupBy(firm => SectorOf(firm.Specialty!).Occupation)
                .ToDictionary(group => group.Key, group => group.Max(firm => firm.WageOfferMinor))
        });
        foreach (var firm in firms)
        {
            firm.LastWeekUnitsSold = (int)society.PeriodUnitsSold.GetValueOrDefault(firm.Id);
            society.PeriodUnitsSold[firm.Id] = 0;
            society.TurnedAwayThisWeek[firm.Id] = 0;
        }
    }

    // A market never stays without a farm, power plant, workshop, venue, pharma or clinic for long: the richest
    // resident who could run one and can afford it opens one (agents can also choose to start firms).
    private static void FillEmptySectors(World world, Nation nation, Agent[] residents)
    {
        foreach (var sector in Sectors.Where(sector => sector.Id is not ("freight" or SocietyBank.Sector) && !Firms(world, nation.Id, sector.Id).Any()))
        {
            var founder = residents.Where(person => CanStart(world, person, sector.Id))
                .OrderByDescending(person => person.CashMinor).ThenBy(person => person.Id).FirstOrDefault();
            if (founder is not null)
                Start(world, founder, sector.Id, LaunchPrice(world, nation, sector.Id), "");
        }
    }

    // Days of sales the shelves hold; empty shelves count as zero even when nothing could be sold.
    private static double StockDays(World world, Agent firm, string good)
    {
        if (firm.Inventory[good] == 0)
            return 0;
        var dailySales = world.Society!.PeriodUnitsSold.GetValueOrDefault(firm.Id) / (double)DaysPerWeek;
        return dailySales == 0 ? double.MaxValue : firm.Inventory[good] / dailySales;
    }

    // Weekly service capacity: sessions or visits the firm's workers can deliver.
    public static double ServiceCapacity(World world, Agent firm) =>
        Workers(world, firm).Sum(worker => SocietyEconomy.Capacity(worker) * DayEffort(world, worker))
        * SectorOf(firm.Specialty!).DailyOutput * DaysPerWeek;

    // Between owner turns, firms follow simple rules: when short they offer overtime and hire, when stock
    // piles up or a service sits idle they shrink, and unfilled openings raise wage offers.
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
        bool shortHanded, idle;
        if (spec.Good is { } good)
        {
            var stockDays = StockDays(world, firm, good);
            (shortHanded, idle) = (stockDays < 7, stockDays > 21);
        }
        else if (IsService(spec))
        {
            var served = society.PeriodUnitsSold.GetValueOrDefault(firm.Id);
            var capacity = ServiceCapacity(world, firm);
            (shortHanded, idle) = (society.TurnedAwayThisWeek.GetValueOrDefault(firm.Id) > 0, capacity > 0 && served < capacity * 0.4);
        }
        else if (SocietyBank.IsBank(firm))
        {
            // A bank hires a banker when its loan book outgrows its staff and interest covers another salary.
            var book = SocietyBank.LoanBook(world, firm);
            var net = revenue - society.PeriodCostMinor.GetValueOrDefault(firm.Id);
            (shortHanded, idle) = (book >= SocietyBank.LoanCapacity(world, firm) && net >= payroll + firm.WageOfferMinor,
                staff > 0 && book <= SocietyBank.LoanCapacity(world, firm) - SocietyBank.LoansPerBanker);
        }
        else
            (shortHanded, idle) = (false, revenue < payroll);
        // A profitable firm that cannot keep up first asks its staff for overtime, then hires.
        firm.OvertimeOffered = shortHanded && profitable && !SocietyBank.IsBank(firm);
        var change = idle ? -1 : shortHanded && profitable ? 1 : 0;
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
                    && person.Occupation == occupation
                    && person.SalaryMinor * 11 / 10 < firm.WageOfferMinor)
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

    private static (int PayCuts, int Layoffs, long Dividend) Payroll(World world, Nation nation, Agent government, Agent firm, bool planned)
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
        {
            // Overtime worked this week is paid at time and a half on top of the salary.
            var overtime = worker.SalaryMinor * worker.OvertimeDays * OvertimePayEighths / (8 * DaysPerWeek);
            worker.OvertimeDays = 0;
            if (worker.WorkEffort > 0) // strikers forfeit the week's pay
                paid += SocietyEconomy.PayWage(world, nation, government, firm, worker, Math.Min(worker.SalaryMinor + overtime, firm.CashMinor));
        }
        var profit = revenue - society.PeriodCostMinor.GetValueOrDefault(firm.Id) - paid;
        var profitTax = Math.Min(firm.CashMinor, Math.Max(0, profit) * nation.ProfitTaxBasisPoints / 10000);
        SocietyEconomy.Transfer(world, firm, government, profitTax, "profit_tax");
        nation.ProfitTaxCollectedMinor += profitTax;
        nation.TaxesCollectedMinor += profitTax;
        var dividend = 0L;
        if (firm.OwnerId is { } ownerId && world.Agents[ownerId] is { Role: "entrepreneur" } owner)
        {
            owner.OvertimeDays = 0;
            // Owners are paid from profit: up to their former trade's wage first, then half of the rest.
            // A losing business pays its owner nothing, which keeps it alive longer.
            if (profit > profitTax)
            {
                var afterTax = profit - profitTax;
                var wage = TableWage.GetValueOrDefault(owner.FormerOccupation ?? "", 0);
                var draw = Math.Min(afterTax, wage) + Math.Max(0, afterTax - wage) * OwnerDrawBasisPoints / 10000;
                SocietyEconomy.Transfer(world, firm, owner, Math.Min(firm.CashMinor, draw), "owner_draw");
            }
            // Dividends: cash beyond a buffer of several weeks' payroll goes to the owner instead of idling.
            var buffer = Math.Max(10000, DividendBufferWeeks * Staff(world, firm).Sum(worker => worker.SalaryMinor));
            dividend = SocietyBank.IsBank(firm) ? SocietyBank.Dividend(world, firm) : Math.Max(0, firm.CashMinor - buffer);
            SocietyEconomy.Transfer(world, firm, owner, dividend, "dividend");
            nation.DividendsPaidMinor += dividend;
        }
        society.PeriodRevenueMinor[firm.Id] = 0;
        society.PeriodCostMinor[firm.Id] = 0;
        // A bank never closes for want of cash: its reserves belong to depositors, and insolvency means a takeover.
        if (!planned && firm.DistressWeeks >= DistressWeeksToClose && !SocietyBank.IsBank(firm))
            Close(world, nation, firm, "unable to cover costs for four weeks");
        return (cuts, layoffs, dividend);
    }

    // Prices move in proportion to the imbalance, up to 20% a week: empty shelves or customers turned
    // away push them up, gluts and idle capacity push them down, and a loss in balance adds 5% cost push.
    private static void ReviewPrice(World world, Agent firm)
    {
        var society = world.Society!;
        var spec = SectorOf(firm.Specialty!);
        if (spec.BasePrice == 0)
            return;
        var sold = society.PeriodUnitsSold.GetValueOrDefault(firm.Id);
        // Cost push only lifts a price that is near or below marginal cost (a worker's pay per unit made,
        // plus energy and tools inputs); a firm losing money on weak sales sheds staff instead.
        var nation = society.Nations[firm.CountryId];
        var marginalCost = firm.WageOfferMinor / (spec.DailyOutput * DaysPerWeek * (spec.Good is { } made ? SocietyEconomy.OutputMultiplier(world, nation, made) : 1))
            + (spec.Good is null ? 0 : 0.1 * SocietyFactory.BasePrice("energy")) + (spec.Id == "medicine" ? 0.1 * SocietyFactory.BasePrice("tools") : 0);
        var losing = society.PeriodCostMinor.GetValueOrDefault(firm.Id) + Staff(world, firm).Sum(worker => worker.SalaryMinor)
            > society.PeriodRevenueMinor.GetValueOrDefault(firm.Id) && firm.PriceMinor < 1.25 * marginalCost;
        double step;
        if (spec.Good is { } good)
        {
            var stockDays = StockDays(world, firm, good);
            step = stockDays < 7 ? MaxPriceStepPct * (7 - stockDays) / 7
                : stockDays > 21 ? -Math.Min(MaxPriceStepPct, MaxPriceStepPct * (stockDays - 21) / 42)
                : losing ? 5 : 0;
        }
        else
        {
            var turnedAway = society.TurnedAwayThisWeek.GetValueOrDefault(firm.Id);
            var utilization = sold / Math.Max(1, ServiceCapacity(world, firm));
            var unserved = turnedAway / (double)Math.Max(1, sold + turnedAway);
            // Visits and sessions are needed or not regardless of price, so a losing service sheds staff
            // instead of cutting its price; only a profitable one discounts idle capacity.
            step = unserved > 0.1 ? Math.Min(MaxPriceStepPct, 100 * unserved)
                : losing ? 5
                : turnedAway == 0 && utilization < 0.6 ? -MaxPriceStepPct * (0.6 - utilization) / 0.6 : 0;
        }
        var price = (int)Math.Round(firm.PriceMinor * (100 + step) / 100);
        firm.PriceMinor = Math.Clamp(price, spec.BasePrice / 2, spec.BasePrice * 3);
    }

    // Closing repays the firm's bank loans, then returns its cash to its owner; a competitor buys stock at
    // half price, and whatever is not bought goes to the owner. Nothing is created or destroyed.
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
            var buyer = Firms(world, firm.CountryId, firm.Specialty!).Where(agent => agent != firm)
                .OrderByDescending(agent => agent.CashMinor).FirstOrDefault();
            var price = Math.Max(1, SocietyFactory.BasePrice(good) / 2);
            var sold = buyer is null ? 0 : (int)Math.Min(units, buyer.CashMinor / price);
            if (sold > 0)
            {
                SocietyEconomy.Transfer(world, buyer!, firm, (long)sold * price, "liquidation_sale");
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
        SocietyBank.Recover(world, nation, firm);
        if (owner is not null)
        {
            recovered = firm.CashMinor;
            SocietyEconomy.Transfer(world, firm, owner, firm.CashMinor, "closure_payout");
            ReleaseOwner(world, owner);
        }
        else
            SocietyEconomy.Transfer(world, firm, world.Agents[nation.Id + "_government"], firm.CashMinor, "closure_payout");
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

    // A former owner returns to the labor market in their old trade.
    public static void ReleaseOwner(World world, Agent owner)
    {
        owner.Role = "citizen";
        owner.Occupation = owner.FormerOccupation ?? "factory_worker";
        owner.OwnedBusinessId = null;
        owner.EmployerId = null;
        owner.UnemployedSinceTick = world.Tick;
        owner.Goal = $"Find work as a {owner.Occupation.Replace('_', ' ')}, meet household needs and rebuild savings.";
    }

    // Skilled workers who step down into unskilled work keep their qualification, so a matching
    // opening (at 10% more pay) pulls them back.
    public static void Hire(World world, Agent firm, Agent person, string occupation)
    {
        person.EmployerId = firm.Id;
        if (!(Unskilled.Contains(occupation) && !Unskilled.Contains(person.Occupation)))
            person.Occupation = occupation;
        person.SalaryMinor = firm.WageOfferMinor;
        person.UnemployedSinceTick = -1;
        person.OvertimeDays = 0;
    }

    public static void Fire(World world, Agent person)
    {
        person.EmployerId = null;
        person.SalaryMinor = 0;
        person.UnemployedSinceTick = world.Tick;
        person.OvertimeDays = 0;
    }

    private static void Quit(Agent person)
    {
        person.EmployerId = null;
        person.SalaryMinor = 0;
    }
}
