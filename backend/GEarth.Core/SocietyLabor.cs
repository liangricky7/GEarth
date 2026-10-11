namespace GEarth.Core;

// Labor market, business ownership and the weekly life of firms. One tick is one day; payroll,
// hiring, price reviews and closures happen weekly. Occupations differ in pay from day one (the pay
// table); school adds a graduate's output boost on top.
public static class SocietyLabor
{
    public const int DaysPerWeek = 7;
    public const int StartupWeeks = 8;
    public const int ServiceStartupWeeks = 4; // a venue needs less capital than a farm or workshop
    // A sector is worth entering when its average price is at least this many times a unit's cost.
    public const double EntryMarkup = 1.5; // ponytail: tune after runs
    // Every four weeks profitable firms pass this share of the rise in the cost of living into pay.
    public const int IndexationDays = 28;
    public const double IndexationShare = 0.5; // ponytail: partial indexation; tune after runs
    public const int OwnerGivesUpWeeks = 2;
    // Sectors the government keeps alive with startup grants: an empty one gets a grant after two weeks.
    public static readonly string[] Essential = ["food", "energy", "clinic", "medicine"];
    public const int GrantAfterDays = 14;
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
    // worker; school: students per teacher at once). Freight earns shipping fees; the bank earns interest
    // (SocietyBank). Firms is how many compete in each country at the start. A clinic visit is cheap because
    // the public services budget pays clinics a weekly capitation; school's price is its fixed tuition.
    public static readonly Sector[] Sectors =
    [
        new("food", "farmer", "food", 40, 7.5, 3),
        new("energy", "energy_worker", "energy", 25, 22, 2),
        new("tools", "factory_worker", "tools", 100, 3, 2),
        new("entertainment", "service_worker", null, 60, 5, 2),
        new("medicine", "researcher", "medicine", 120, 4.5, 1),
        new("clinic", "doctor", null, 200, 8, 1),
        new("school", "teacher", null, 2000, 5, 1),
        new("freight", "dockworker", null, 0, 0, 1),
        new(SocietyBank.Sector, "banker", null, 0, 0, 1)
    ];

    public const int CourseDays = 28;
    public const int PartTimeCourseDays = 56; // half days while keeping the job, at half pay
    public const double GraduateOutputBoost = 1.25; // ponytail: tunable; one course, no stacking
    public const int GraduatePayPremiumPct = 15; // employers keep the rest of the graduate's extra output

    public static Sector SectorOf(string id) => Sectors.Single(sector => sector.Id == id);

    // Services are sold by the visit or session and cannot be stocked.
    public static bool IsService(Sector sector) => sector.Good is null && sector.BasePrice > 0;

    // Weekly pay with education baked in: roughly real-world median ratios, farmer = 1,000 minor.
    public static readonly Dictionary<string, long> TableWage = new()
    {
        ["farmer"] = 1000, ["service_worker"] = 1100, ["factory_worker"] = 1300, ["soldier"] = 1400, ["dockworker"] = 1450,
        ["teacher"] = 1750, ["banker"] = 2000, ["energy_worker"] = 2100, ["researcher"] = 2500, ["planner"] = 2500,
        ["doctor"] = 6000
    };

    // Unskilled jobs are open to anyone after a spell of unemployment, so skilled workers can step down;
    // doctors and researchers also move between each other. Banks train their own staff, so anyone can
    // become a banker. Teachers are skilled; soldiers and planners are public-sector only.
    public static readonly string[] Unskilled = ["farmer", "service_worker", "energy_worker", "factory_worker", "dockworker"];

    public static bool CanSwitch(string from, string to) => from == to
        || Unskilled.Contains(to) || to == "banker"
        || (from, to) is ("doctor", "researcher") or ("researcher", "doctor");

    // Residents live, eat and work; the planned economy's planner is a resident on the public payroll.
    public static bool IsResident(Agent agent) => agent.Role is "citizen" or "entrepreneur" or "planner";

    public static bool IsPlanned(Nation nation) => nation.GovernmentId == "planned_republic";

    // The state runs schools by rule; owned businesses act through their entrepreneur; closed ones never act.
    public static bool DecidesForItself(Agent agent) =>
        agent.Role != "defunct" && !(agent.Role == "business" && (agent.OwnerId is not null || agent.Specialty == "school"));

    public static bool IsStudent(Agent agent) => agent.StudentUntilTick >= 0;

    // Owners work in their firm only in a trade they could be hired for: a farmer can own a clinic but not see patients.
    public static bool OwnerWorks(Agent owner, Agent firm) =>
        CanSwitch(owner.FormerOccupation ?? owner.Occupation, SectorOf(firm.Specialty!).Occupation);

    public static IEnumerable<Agent> Firms(World world, string country, string sector) =>
        world.InCountry(country).Where(agent => agent.Role == "business" && agent.Specialty == sector);

    // Staff live in the firm's country: migrating clears a resident's employer.
    public static Agent[] Staff(World world, Agent firm) =>
        world.InCountry(firm.CountryId).Where(agent => agent.EmployerId == firm.Id && IsResident(agent)).ToArray();

    // Everyone who works at a firm: its staff plus an owner qualified for the trade.
    public static Agent[] Workers(World world, Agent firm) => Staff(world, firm)
        .Concat(firm.OwnerId is { } owner && world.Agents[owner] is { Role: "entrepreneur" } boss && OwnerWorks(boss, firm) ? [boss] : [])
        .ToArray();

    // Output factor for one working day: a striker, someone owed two weeks of wages or someone too sick to
    // work produces nothing; overtime adds a quarter; a graduate produces a quarter more; a part-time student
    // works half days.
    public static double DayEffort(World world, Agent worker)
    {
        if (worker.WorkEffort == 0 || OnArrearsStrike(worker) || SocietyEconomy.TooSick(worker))
            return 0;
        return (WorksOvertime(world, worker) ? OvertimeOutput : 1) * (worker.Graduated ? GraduateOutputBoost : 1)
            * (worker.PartTimeStudent && IsStudent(worker) ? 0.5 : 1);
    }

    public const int ArrearsStrikeWeeks = 2;

    public static bool OnArrearsStrike(Agent worker) => worker.ArrearsWeeks >= ArrearsStrikeWeeks;

    // Overtime needs the employer's offer and the worker's consent (effort 2); owners decide for themselves.
    public static bool WorksOvertime(World world, Agent worker) => worker.WorkEffort == 2
        && (worker.Role == "entrepreneur" && worker.OwnedBusinessId is not null
            || worker.EmployerId is { } employer && world.Agents[employer] is { Role: "business", OvertimeOffered: true });

    // Capital depends on the business, not the founder: weeks of the sector's own pay (fewer for services).
    public static long StartupCost(string sector) =>
        (IsService(SectorOf(sector)) ? ServiceStartupWeeks : StartupWeeks) * TableWage[SectorOf(sector).Occupation];

    // Anyone can start any business except a bank (one per country) or a freight firm.
    public static string[] StartableSectors() => Sectors
        .Where(sector => sector.Id is not (SocietyBank.Sector or "freight")).Select(sector => sector.Id).ToArray();

    // Average shelf price, weighted by last week's sales (a plain average before anything sold).
    public static double AveragePrice(Agent[] firms)
    {
        var sold = firms.Sum(firm => (long)firm.LastWeekUnitsSold);
        return sold == 0 ? firms.Average(firm => firm.PriceMinor) : firms.Sum(firm => (double)firm.PriceMinor * firm.LastWeekUnitsSold) / sold;
    }

    // What one more unit costs a firm to make or serve: a worker's pay per unit, plus energy and (for
    // medicine) tools inputs at base price.
    public static double UnitCost(World world, Nation nation, Agent firm)
    {
        var spec = SectorOf(firm.Specialty!);
        return firm.WageOfferMinor / (spec.DailyOutput * DaysPerWeek * (spec.Good is { } made ? SocietyEconomy.OutputMultiplier(world, nation, made) : 1))
            + (spec.Good is null ? 0 : 0.1 * SocietyFactory.BasePrice("energy")) + (spec.Id == "medicine" ? 0.1 * SocietyFactory.BasePrice("tools") : 0);
    }

    // A gap worth a founder's money, as in a real market: incumbents charge well above what a unit costs to
    // make, buyers go without at today's prices (priced out or turned away, shelves under a week of sales, a
    // service near capacity), and someone out of work could staff a new firm. An empty sector is always a gap.
    public static bool HasGap(World world, Nation nation, string sector)
    {
        var incumbents = Firms(world, nation.Id, sector).ToArray();
        if (incumbents.Length == 0)
            return true;
        var spec = SectorOf(sector);
        var sold = incumbents.Sum(firm => firm.LastWeekUnitsSold);
        var turnedAway = incumbents.Sum(firm => firm.LastWeekTurnedAway) > 0;
        var capacity = spec.Good is not null ? 0 : spec.Id == "school" ? incumbents.Sum(firm => Seats(world, firm))
            : incumbents.Sum(firm => ServiceCapacity(world, firm));
        var utilization = spec.Id == "school" ? incumbents.Sum(firm => Students(world, firm).Length) : sold;
        var demand = spec.Good is { } good
            ? turnedAway || good == "food" && nation.PricedOutLastWeek > 0
                || incumbents.Sum(firm => firm.Inventory[good]) == 0 || incumbents.Sum(firm => firm.Inventory[good]) < sold
            : turnedAway || capacity > 0 && utilization >= 0.8 * capacity;
        // School tuition is a fixed fee, so only demand counts there.
        var margin = spec.Id == "school"
            || AveragePrice(incumbents) >= EntryMarkup * incumbents.Average(firm => UnitCost(world, nation, firm));
        var idle = world.InCountry(nation.Id).Any(person => person.Role == "citizen" && person.EmployerId is null && !IsStudent(person)
            && (person.Occupation == spec.Occupation || CanSwitch(person.Occupation, spec.Occupation)
                && world.Tick - person.UnemployedSinceTick >= RelatedJobAfterDays));
        return margin && demand && idle;
    }

    // A skilled business needs someone qualified to do the work: the founder or a resident free to hire.
    public static bool CanStaff(World world, Agent founder, string sector)
    {
        var occupation = SectorOf(sector).Occupation;
        return Unskilled.Contains(occupation) || CanSwitch(founder.Occupation, occupation)
            || world.InCountry(founder.CountryId).Any(person => person != founder && person.Role == "citizen"
                && person.Occupation == occupation && (person.EmployerId is null || world.Agents[person.EmployerId].Specialty != SectorOf(sector).Id));
    }

    // Newcomers launch 10% under the cheapest incumbent.
    public static int LaunchPrice(World world, Nation nation, string sector)
    {
        var incumbents = Firms(world, nation.Id, sector).ToArray();
        var basePrice = SectorOf(sector).BasePrice;
        return incumbents.Length == 0 ? basePrice : Math.Max(1, incumbents.Min(firm => firm.PriceMinor) * 9 / 10);
    }

    // Founders pay from cash and deposits, or borrow up to the bank's limit for the rest, where the market shows a gap.
    public static bool CanStart(World world, Agent founder, string sector, long loan = 0)
    {
        var nation = world.Society!.Nations[founder.CountryId];
        var cost = StartupCost(sector);
        return founder.Role == "citizen" && !IsStudent(founder) && !IsPlanned(nation) && StartableSectors().Contains(sector)
            && SocietyBank.Liquid(world, founder) + loan >= cost && loan <= SocietyBank.StartupLoan(world, founder, cost)
            && (!Firms(world, nation.Id, sector).Any() || HasGap(world, nation, sector)) && CanStaff(world, founder, sector);
    }

    // A startup grant: the treasury pays the whole startup cost (in the planned economy the state opens the firm).
    public static Agent? Grant(World world, Nation nation, string sector, string cause)
    {
        var government = world.Agents[nation.Id + "_government"];
        var cost = StartupCost(sector);
        if (government.CashMinor < cost)
            return null;
        var occupation = SectorOf(sector).Occupation;
        var residents = world.InCountry(nation.Id).Where(person => person.Role == "citizen" && !IsStudent(person)).ToArray();
        // The founder is someone in the trade (the unemployed first), or for unskilled work anyone out of a job.
        var founder = residents.Where(person => person.Occupation == occupation)
                .OrderBy(person => person.EmployerId is null ? 0 : 1).ThenBy(person => person.Id).FirstOrDefault()
            ?? (Unskilled.Contains(occupation) ? residents.Where(person => person.EmployerId is null).OrderBy(person => person.Id).FirstOrDefault() : null);
        Agent firm;
        if (IsPlanned(nation))
        {
            firm = OpenStateFirm(world, nation, sector);
            SocietyEconomy.Transfer(world, government, firm, cost, "startup_grant");
            if (founder is not null)
                Hire(world, firm, founder, occupation);
        }
        else
        {
            if (founder is null)
                return null;
            SocietyEconomy.Transfer(world, government, founder, cost, "startup_grant");
            firm = Start(world, founder, sector, LaunchPrice(world, nation, sector), cause);
        }
        nation.StartupGrantsPaidMinor += cost;
        nation.NeededSince.Remove(sector);
        world.Emit("startup_grant", "rule", [firm.Id, nation.Id], new { sector, grant_minor = cost, founder = founder?.Id }, cause == "" ? [] : [cause]);
        return firm;
    }

    // A state-owned firm (no owner) in the planned economy, or the state school.
    public static Agent OpenStateFirm(World world, Nation nation, string sector)
    {
        var spec = SectorOf(sector);
        var firm = new Agent
        {
            Id = $"{nation.Id}_{sector}_firm{world.NextEntityNumber()}", Role = "business", CountryId = nation.Id,
            LocationId = nation.Id + "_city", Occupation = sector, Specialty = sector, PriceMinor = spec.BasePrice,
            WageOfferMinor = TableWage[spec.Occupation], TargetHeadcount = 1, FoundedTick = world.Tick,
            Inventory = SocietyFactory.Goods.ToDictionary(good => good, _ => 0),
            Traits = new() { ["risk_tolerance"] = 0.5, ["patience"] = 0.5, ["generosity"] = 0.5, ["trust"] = 0.5 },
            Goal = $"Produce and sell {sector}, pay workers, import scarce inputs and keep the business solvent."
        };
        world.Agents.Add(firm.Id, firm);
        RunAudit.Created(world, firm, "state_firm");
        nation.Population++;
        nation.BusinessesStarted++;
        return firm;
    }

    public static Agent Start(World world, Agent founder, string sector, int launchPrice, string cause)
    {
        var nation = world.Society!.Nations[founder.CountryId];
        var spec = SectorOf(sector);
        var cost = StartupCost(sector);
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
        RunAudit.Created(world, firm, "private_firm");
        SocietyBank.Draw(world, founder, cost);
        SocietyEconomy.Transfer(world, founder, firm, cost, "business_founding");
        Quit(founder);
        founder.FormerOccupation = founder.Occupation;
        founder.Occupation = "entrepreneur";
        founder.Role = "entrepreneur";
        founder.OwnedBusinessId = firm.Id;
        founder.Goal = SocietyFactory.OwnerGoal(sector);
        nation.Population++;
        nation.BusinessesStarted++;
        world.Emit("business_started", "rule", [firm.Id, founder.Id, nation.Id], new
        {
            sector, owner = founder.Id, invested_minor = cost, launch_price_minor = launchPrice,
            account_id = firm.Id, opening_cash_minor = 0L,
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
        var raisePct = !planned && world.Tick % IndexationDays == 0 ? IndexPay(world, nation, firms) : (double?)null;
        foreach (var firm in firms.Where(_ => !planned))
            foreach (var worker in Staff(world, firm).OrderBy(SocietyEconomy.Capacity).ThenByDescending(worker => worker.SalaryMinor)
                         .Take(Math.Max(0, Staff(world, firm).Length - firm.TargetHeadcount)).ToArray())
            {
                Fire(world, worker);
                layoffs++;
            }
        if (!planned)
            FillEmptySectors(world, nation, residents);
        KeepEssentials(world, nation);
        firms = world.InCountry(nation.Id).Where(agent => agent.Role == "business").ToArray();
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
            hires, layoffs, job_switches = quits, pay_cuts = payCuts, cost_of_living_raise_pct = raisePct, unemployed = nation.Unemployed,
            dividends_minor = dividends,
            overtime_offers = firms.Count(firm => firm.Role == "business" && firm.OvertimeOffered),
            active_businesses = world.InCountry(nation.Id).Count(agent => agent.Role == "business"),
            wage_offers = firms.Where(firm => firm.Role == "business").GroupBy(firm => SectorOf(firm.Specialty!).Occupation)
                .ToDictionary(group => group.Key, group => group.Max(firm => firm.WageOfferMinor))
        });
        foreach (var firm in firms)
        {
            firm.LastWeekUnitsSold = (int)society.PeriodUnitsSold.GetValueOrDefault(firm.Id);
            firm.LastWeekTurnedAway = society.TurnedAwayThisWeek.GetValueOrDefault(firm.Id);
            society.PeriodUnitsSold[firm.Id] = 0;
            society.TurnedAwayThisWeek[firm.Id] = 0;
        }
    }

    // A market never stays without a farm, power plant, workshop, venue, pharma or clinic for long: the richest
    // resident who could run one and can afford it opens one (agents can also choose to start firms).
    private static void FillEmptySectors(World world, Nation nation, Agent[] residents)
    {
        foreach (var sector in Sectors.Where(sector => sector.Id is not ("freight" or "school" or SocietyBank.Sector) && !Firms(world, nation.Id, sector.Id).Any()))
        {
            var founder = residents.Where(person => CanStart(world, person, sector.Id))
                .OrderByDescending(person => person.CashMinor).ThenBy(person => person.Id).FirstOrDefault();
            if (founder is not null)
                Start(world, founder, sector.Id, LaunchPrice(world, nation, sector.Id), "");
        }
    }

    // Essential sectors never stay without someone working: an idle firm takes back a worker from the trade
    // (a doctor returns to the clinic, or opens one on a grant if there is none), and a sector still empty after
    // two weeks gets a startup grant from the treasury.
    private static void KeepEssentials(World world, Nation nation)
    {
        foreach (var sector in Essential)
        {
            var firms = Firms(world, nation.Id, sector).ToArray();
            if (firms.Any(firm => Workers(world, firm).Length > 0))
            {
                nation.NeededSince.Remove(sector);
                continue;
            }
            nation.NeededSince.TryAdd(sector, world.Tick);
            var occupation = SectorOf(sector).Occupation;
            var worker = world.InCountry(nation.Id).Where(person => person.Role == "citizen" && !IsStudent(person)
                    && person.Occupation == occupation && (person.EmployerId is null || world.Agents[person.EmployerId].Specialty != sector))
                .OrderBy(person => person.EmployerId is null ? 0 : 1).ThenBy(person => person.Id).FirstOrDefault();
            if (firms.Length > 0 && worker is not null)
            {
                var firm = firms.OrderByDescending(firm => firm.CashMinor).ThenBy(firm => firm.Id).First();
                firm.TargetHeadcount = Math.Max(1, firm.TargetHeadcount);
                Hire(world, firm, worker, occupation);
                nation.NeededSince.Remove(sector);
            }
            else if ((firms.Length == 0 && sector == "clinic" && worker is not null) || world.Tick - nation.NeededSince[sector] >= GrantAfterDays)
                Grant(world, nation, sector, "");
        }
    }

    // A firm that makes or serves something keeps a crew of one unless its owner does the work (freight
    // and the bank run on their owner).
    private static int MinimumStaff(World world, Agent firm) =>
        SectorOf(firm.Specialty!).BasePrice > 0 && !(firm.OwnerId is { } owner && world.Agents[owner] is { Role: "entrepreneur" } boss && OwnerWorks(boss, firm)) ? 1 : 0;

    public static Agent[] Students(World world, Agent school) =>
        world.InCountry(school.CountryId).Where(person => person.SchoolId == school.Id && IsStudent(person)).ToArray();

    // Each working teacher takes this many students at once.
    public static int Seats(World world, Agent school) =>
        Workers(world, school).Count(teacher => DayEffort(world, teacher) > 0) * (int)SectorOf("school").DailyOutput;

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
    // This week's sales cover its costs and its payroll.
    private static bool Profitable(World world, Agent firm) =>
        world.Society!.PeriodRevenueMinor.GetValueOrDefault(firm.Id) - world.Society.PeriodCostMinor.GetValueOrDefault(firm.Id)
        > Staff(world, firm).Sum(worker => worker.SalaryMinor);

    // A week of food and energy at average shelf prices.
    private static double LivingCost(World world, Nation nation) =>
        new[] { "food", "energy" }.Sum(sector => Firms(world, nation.Id, sector).ToArray() is { Length: > 0 } sellers
            ? DaysPerWeek * AveragePrice(sellers) : DaysPerWeek * SectorOf(sector).BasePrice);

    // Partial indexation: when the cost of living has risen since the last review, profitable firms raise their
    // staff's pay and their wage offers by a share of the rise. Pay never falls this way. Returns the raise in %.
    private static double IndexPay(World world, Nation nation, Agent[] firms)
    {
        var cost = LivingCost(world, nation);
        // Prices open at base, so the first review compares against base prices.
        var before = nation.LivingCostAtReview > 0 ? nation.LivingCostAtReview
            : DaysPerWeek * (SectorOf("food").BasePrice + SectorOf("energy").BasePrice);
        var rise = cost / before - 1;
        nation.LivingCostAtReview = cost;
        if (rise <= 0)
            return 0;
        var basisPoints = (long)Math.Round(rise * IndexationShare * 10000);
        foreach (var firm in firms.Where(firm => firm.Role == "business" && !SocietyBank.IsBank(firm) && Profitable(world, firm)))
        {
            var table = TableWage[SectorOf(firm.Specialty!).Occupation];
            firm.WageOfferMinor = Math.Max(firm.WageOfferMinor,
                Math.Min(table * 3, firm.WageOfferMinor * (10000 + basisPoints) / 10000));
            foreach (var worker in Staff(world, firm))
                worker.SalaryMinor = worker.SalaryMinor * (10000 + basisPoints) / 10000;
        }
        return Math.Round(basisPoints / 100.0, 2);
    }

    private static void Autopilot(World world, Nation nation, Agent firm)
    {
        var society = world.Society!;
        var spec = SectorOf(firm.Specialty!);
        var staff = Staff(world, firm).Length;
        var revenue = society.PeriodRevenueMinor.GetValueOrDefault(firm.Id);
        var payroll = Staff(world, firm).Sum(worker => worker.SalaryMinor);
        var profitable = Profitable(world, firm);
        bool shortHanded, idle;
        if (spec.Good is { } good)
        {
            var stockDays = StockDays(world, firm, good);
            (shortHanded, idle) = (stockDays < 7, stockDays > 21);
        }
        else if (spec.Id == "school")
        {
            var seats = Seats(world, firm);
            var enrolled = Students(world, firm).Length;
            // Public teaching jobs are stable: the state school grows when classes fill but does not shed teachers.
            (shortHanded, idle) = (enrolled >= seats * 0.8, firm.OwnerId is not null && enrolled < seats * 0.4);
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
        // Only a profitable firm bids up wages for unfilled openings; a loss-making one lets offers cool. In a
        // shortage an essential firm raises its offer as fast as prices may rise (to twice the table wage).
        var table = TableWage[spec.Occupation];
        if (staff < firm.TargetHeadcount && profitable)
            firm.WageOfferMinor = Essential.Contains(spec.Id) && shortHanded
                ? Math.Min(table * 2, firm.WageOfferMinor * (100 + MaxPriceStepPct) / 100)
                : Math.Max(firm.WageOfferMinor, Math.Min(table * 3 / 2, firm.WageOfferMinor * 105 / 100));
        else if (!profitable && firm.WageOfferMinor > table)
            firm.WageOfferMinor = Math.Max(table, firm.WageOfferMinor * 95 / 100);
        // A profitable firm that cannot keep up first asks its staff for overtime, then hires.
        firm.OvertimeOffered = shortHanded && profitable && !SocietyBank.IsBank(firm) && spec.Id != "school";
        var change = idle ? -1 : shortHanded && profitable ? 1 : 0;
        firm.TargetHeadcount = Math.Max(MinimumStaff(world, firm), firm.TargetHeadcount + change);
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
            // Graduates are hired first at the same offer; students are in class.
            var unemployed = residents.Where(person => person.Role == "citizen" && person.EmployerId is null && !IsStudent(person)
                    && (person.Occupation == occupation || (CanSwitch(person.Occupation, occupation)
                        && world.Tick - person.UnemployedSinceTick >= RelatedJobAfterDays)))
                .OrderByDescending(person => person.Graduated).ThenBy(person => person.UnemployedSinceTick).ThenBy(person => person.Id);
            // Employed workers switch for at least 10% more pay (any raise while looking for a better job);
            // public employees stay in the public sector.
            var switchers = residents.Where(person => person.Role == "citizen" && person.EmployerId is { } current
                    && current != firm.Id && world.Agents[current].Role == "business"
                    && person.Occupation == occupation
                    && person.SalaryMinor * (person.SeekingUntilTick >= world.Tick ? 10 : 11) / 10 < firm.WageOfferMinor)
                .OrderByDescending(person => person.Graduated).ThenBy(person => person.SalaryMinor).ThenBy(person => person.Id);
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
        foreach (var person in residents.Where(person => person.Role == "citizen" && person.EmployerId is null && !IsStudent(person)).ToArray())
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
        // A banking firm draws on its deposit for the week's pay; a state-run employer owes what it cannot pay.
        var stateRun = firm.OwnerId is null && !SocietyBank.IsBank(firm);
        if (SocietyBank.Banks(firm))
            SocietyBank.Draw(world, firm, staff.Sum(worker => worker.SalaryMinor)
                + TableWage.GetValueOrDefault(firm.OwnerId is { } id ? world.Agents[id].FormerOccupation ?? "" : "", 0));
        if (!planned && !stateRun && staff.Where(worker => worker.WorkEffort > 0).Sum(worker => worker.SalaryMinor) > firm.CashMinor)
        {
            // Short of cash: lay off the most expensive workers down to a minimum crew first (employers rarely cut
            // pay), and cut the crew's pay 10% only when that is still not enough.
            while (staff.Length > MinimumStaff(world, firm) && staff.Sum(worker => worker.SalaryMinor) > firm.CashMinor)
            {
                var expensive = staff.OrderByDescending(worker => worker.SalaryMinor).First();
                Fire(world, expensive);
                staff = staff.Where(worker => worker != expensive).ToArray();
                layoffs++;
            }
            if (staff.Sum(worker => worker.SalaryMinor) > firm.CashMinor)
            {
                foreach (var worker in staff)
                    worker.SalaryMinor = worker.SalaryMinor * 9 / 10;
                cuts = staff.Length;
            }
            firm.TargetHeadcount = Math.Min(firm.TargetHeadcount, staff.Length);
            firm.DistressWeeks++;
        }
        else if (!planned)
            firm.DistressWeeks = revenue == 0 && firm.CashMinor < TableWage[SectorOf(firm.Specialty!).Occupation]
                ? firm.DistressWeeks + 1 : 0;
        var paid = 0L;
        var dues = new List<(Agent Worker, long Wage)>();
        foreach (var worker in staff)
        {
            // Overtime worked this week is paid at time and a half on top of the salary.
            var overtime = worker.SalaryMinor * worker.OvertimeDays * OvertimePayEighths / (8 * DaysPerWeek);
            worker.OvertimeDays = 0;
            if (worker.WorkEffort == 0) // strikers forfeit the week's pay
                continue;
            var wage = OnArrearsStrike(worker) ? 0 : SocietyEconomy.EarnedWage(worker, worker.SalaryMinor) + overtime;
            if (stateRun)
                dues.Add((worker, wage));
            else
                paid += SocietyEconomy.PayWage(world, nation, government, firm, worker, Math.Min(wage, firm.CashMinor));
        }
        paid += SocietyEconomy.PayOwed(world, nation, government, firm, dues);
        // A graduate on staff earns the firm the government's hiring bonus, if one is offered.
        var bonus = Math.Min(government.CashMinor, (long)nation.HiringBonusMinor * staff.Count(worker => worker.Graduated));
        if (bonus > 0 && firm.OwnerId is not null)
            SocietyEconomy.Transfer(world, government, firm, bonus, "hiring_bonus");
        var profit = revenue - society.PeriodCostMinor.GetValueOrDefault(firm.Id) - paid;
        var profitTax = Math.Min(firm.CashMinor, Math.Max(0, profit) * nation.ProfitTaxBasisPoints / 10000);
        SocietyEconomy.Transfer(world, firm, government, profitTax, "profit_tax");
        nation.ProfitTaxCollectedMinor += profitTax;
        nation.TaxesCollectedMinor += profitTax;
        var dividend = 0L;
        Agent? unpaidOwner = null;
        if (firm.OwnerId is { } ownerId && world.Agents[ownerId] is { Role: "entrepreneur" } owner)
        {
            var draw = 0L;
            owner.OvertimeDays = 0;
            // Owners are paid from profit: up to their former trade's wage first, then half of the rest.
            // A losing business pays its owner nothing, which keeps it alive longer.
            if (profit > profitTax)
            {
                var afterTax = profit - profitTax;
                var wage = TableWage.GetValueOrDefault(owner.FormerOccupation ?? "", 0);
                draw = Math.Min(firm.CashMinor, Math.Min(afterTax, wage) + Math.Max(0, afterTax - wage) * OwnerDrawBasisPoints / 10000);
                SocietyEconomy.Transfer(world, firm, owner, draw, "owner_draw");
            }
            // Dividends: cash and deposits beyond a buffer of several weeks' payroll go to the owner instead of idling.
            var buffer = Math.Max(10000, DividendBufferWeeks * Staff(world, firm).Sum(worker => worker.SalaryMinor));
            dividend = SocietyBank.IsBank(firm) ? SocietyBank.Dividend(world, firm) : Math.Max(0, firm.CashMinor + firm.DepositMinor - buffer);
            SocietyBank.Draw(world, firm, dividend);
            dividend = Math.Min(dividend, firm.CashMinor);
            SocietyEconomy.Transfer(world, firm, owner, dividend, "dividend");
            nation.DividendsPaidMinor += dividend;
            // A small venture's owner gives up after two hungry, unpaid weeks when the firm cannot fund
            // another week of its crew's pay and the owner's former trade's wage. Larger firms use distress.
            var staffNow = Staff(world, firm);
            var failing = staffNow.Length <= 1 && firm.CashMinor + firm.DepositMinor
                < staffNow.Sum(worker => worker.SalaryMinor) + TableWage.GetValueOrDefault(owner.FormerOccupation ?? "", 0);
            owner.UnpaidHungryWeeks = owner.HungryThisWeek && draw + dividend == 0 && failing ? owner.UnpaidHungryWeeks + 1 : 0;
            if (owner.UnpaidHungryWeeks >= OwnerGivesUpWeeks)
                unpaidOwner = owner;
        }
        society.PeriodRevenueMinor[firm.Id] = 0;
        society.PeriodCostMinor[firm.Id] = 0;
        // A bank never closes for want of cash: its reserves belong to depositors, and insolvency means a takeover.
        if (!planned && firm.DistressWeeks >= DistressWeeksToClose && !SocietyBank.IsBank(firm))
            Close(world, nation, firm, "unable to cover costs for four weeks");
        else if (!planned && unpaidOwner is not null && !SocietyBank.IsBank(firm))
        {
            unpaidOwner.UnpaidHungryWeeks = 0;
            Close(world, nation, firm, "its owner went hungry and unpaid for two weeks");
        }
        return (cuts, layoffs, dividend);
    }

    // Prices move in proportion to the imbalance, up to 20% a week: empty shelves or customers turned
    // away push them up, gluts and idle capacity push them down, and a loss in balance adds 5% cost push.
    private static void ReviewPrice(World world, Agent firm)
    {
        var society = world.Society!;
        var spec = SectorOf(firm.Specialty!);
        if (spec.BasePrice == 0 || spec.Id == "school") // tuition is a fixed fee
            return;
        var sold = society.PeriodUnitsSold.GetValueOrDefault(firm.Id);
        // Cost push only lifts a price that is near or below marginal cost (a worker's pay per unit made,
        // plus energy and tools inputs); a firm losing money on weak sales sheds staff instead.
        var nation = society.Nations[firm.CountryId];
        var marginalCost = UnitCost(world, nation, firm);
        var losing = society.PeriodCostMinor.GetValueOrDefault(firm.Id) + Staff(world, firm).Sum(worker => worker.SalaryMinor)
            > society.PeriodRevenueMinor.GetValueOrDefault(firm.Id) && firm.PriceMinor < 1.25 * marginalCost;
        double step;
        if (spec.Good is { } good)
        {
            // Shoppers who wanted the good but could not afford this seller are lost sales: when more than a
            // tenth of custom walks away, the price comes down (never below marginal cost) before anything else.
            var stockDays = StockDays(world, firm, good);
            var lost = society.TurnedAwayThisWeek.GetValueOrDefault(firm.Id);
            var lostShare = lost / (double)Math.Max(1, sold + lost);
            step = lostShare > 0.1 && firm.PriceMinor > marginalCost ? -Math.Min(MaxPriceStepPct, 50 * lostShare)
                : stockDays < 7 ? MaxPriceStepPct * (7 - stockDays) / 7
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
        firm.PriceMinor = ClampPrice(spec, (int)Math.Round(firm.PriceMinor * (100 + step) / 100));
    }

    // Prices stay between half and three times the sector's base price.
    public static int ClampPrice(Sector spec, int price) => Math.Clamp(price, spec.BasePrice / 2, spec.BasePrice * 3);

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
        SocietyBank.Withdraw(world, firm, firm.DepositMinor);
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
        owner.UnpaidHungryWeeks = 0;
        owner.EmployerId = null;
        owner.UnemployedSinceTick = world.Tick;
        owner.Goal = SocietyFactory.CitizenGoal(owner.Occupation);
    }

    // Skilled workers who step down into unskilled work keep their qualification, so a matching
    // opening (at 10% more pay) pulls them back.
    public static void Hire(World world, Agent firm, Agent person, string occupation)
    {
        person.EmployerId = firm.Id;
        if (!(Unskilled.Contains(occupation) && !Unskilled.Contains(person.Occupation)))
            person.Occupation = occupation;
        person.SalaryMinor = firm.WageOfferMinor * (100 + (person.Graduated ? GraduatePayPremiumPct : 0)) / 100;
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
