using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Faultline.Core;

public static class SocietyFactory
{
    private static readonly (string Id, string Name, double Lat, double Lon)[] Geography =
    [
        ("aster", "Aster", 40, -105), ("beryl", "Beryl", -18, -58),
        ("cinder", "Cinder", 50, 15), ("dune", "Capybarlos", 25, 42),
        ("ember", "Lebanonia", -27, 135), ("fjord", "Fjord", 64, -22),
        ("grove", "Dyland", 3, 23), ("haven", "Haven", 25, 110),
        ("ionia", "Ionia", 8, 80), ("jade", "Jade", 35, 140)
    ];
    public static readonly string[] Goods = ["food", "energy", "tools", "medicine"];
    // Government, media and every firm (the bank is the one firm in the bank sector).
    public static readonly int InstitutionsPerCountry = 2 + SocietyLabor.Sectors.Sum(sector => sector.Firms);

    // Starting job shares per 100 workers, sized so each sector's sales roughly cover its payroll.
    // One of each essential occupation comes first so small test worlds still have every sector staffed;
    // doctors come only from that list, so each country starts with one (two with a clinic owner, about
    // 20 per 1,000 people: the fewest that keep a clinic open at this population size).
    // Teachers staff the state school (about 5 in 100 workers, near the real 3-6%).
    private static readonly (string Occupation, int Weight)[] JobShares =
    [
        ("farmer", 21), ("energy_worker", 6), ("factory_worker", 25), ("service_worker", 24),
        ("dockworker", 2), ("teacher", 5), ("soldier", 6), ("unemployed", 9)
    ];

    public static string CitizenGoal(string occupation) =>
        $"Keep your own quality of life high (health, needs, comfort, security, free time): work as a {occupation.Replace('_', ' ')}, see a doctor when unwell, keep some savings and influence leadership according to your values.";

    public static string OwnerGoal(string sector) =>
        $"Keep your {sector} business profitable while keeping your own quality of life high.";

    public const string PlannerGoal = "Set each sector's weekly output quota so residents' needs are met without shortages or waste.";

    // The planned economy's planner: a resident on the public payroll who sets the monthly plan.
    public static void AppointPlanner(World world, Nation nation, Agent person)
    {
        person.Role = "planner";
        person.FormerOccupation = person.Occupation;
        person.Occupation = "planner";
        person.EmployerId = nation.Id + "_government";
        person.SalaryMinor = SocietyLabor.TableWage["planner"];
        person.UnemployedSinceTick = -1;
        person.Goal = PlannerGoal;
        foreach (var sector in SocietyPlanning.PlannedSectors)
            nation.Plan.TryAdd(sector, SocietyPlanning.Demand(world, nation, sector));
    }

    // A forked timeline's one change: the country switches regime overnight and starts from the new profile's default policies.
    public static void ChangeGovernment(World world, string countryId, string profileId)
    {
        if (world.Society?.Nations.GetValueOrDefault(countryId) is not { } nation)
            throw new ArgumentException($"Unknown country {countryId}");
        var profile = Governments.Profiles.FirstOrDefault(item => item.Id == profileId)
            ?? throw new ArgumentException($"Unknown government {profileId}");
        var previous = nation.GovernmentId;
        nation.GovernmentId = profile.Id;
        nation.TaxBasisPoints = profile.DefaultTaxBasisPoints;
        nation.WelfareMinor = profile.DefaultWelfareMinor;
        nation.UnemploymentBenefitMinor = profile.Id switch { "market_democracy" => 300, "social_democracy" => 400, _ => 0 };
        foreach (var country in world.Countries.OfType<System.Text.Json.Nodes.JsonObject>().Where(item => (string?)item["id"] == countryId))
            country["government_id"] = profile.Id;
        // A planned economy needs a planner; leaving one returns the planner to their old trade.
        var planner = world.InCountry(countryId).FirstOrDefault(agent => agent.Role == "planner");
        if (profile.Id == "planned_republic" && planner is null
            && world.InCountry(countryId).Where(agent => agent.Role == "citizen").OrderBy(agent => agent.Id).FirstOrDefault() is { } appointee)
            AppointPlanner(world, nation, appointee);
        else if (profile.Id != "planned_republic" && planner is not null)
        {
            planner.Role = "citizen";
            planner.Occupation = planner.FormerOccupation ?? "factory_worker";
            planner.FormerOccupation = null;
            planner.EmployerId = null;
            planner.SalaryMinor = 0;
            planner.UnemployedSinceTick = world.Tick;
            planner.Goal = CitizenGoal(planner.Occupation);
        }
        world.Emit("government_changed", "rule", [countryId], new { from = previous, to = profile.Id, reason = "timeline_fork" });
    }

    public static World Create(string id, bool live, int population, int seed = 20261010, int rotation = 0)
    {
        if (population < Geography.Length * (InstitutionsPerCountry + 1) || population > 100000)
            throw new ArgumentOutOfRangeException(nameof(population));

        var world = new World
        {
            SchemaVersion = 2, Id = id, ScenarioId = "earthlike_society_v3_daily", Mode = live ? "LIVE" : "RULE",
            Seed = seed, UpdatedAt = DateTimeOffset.UtcNow,
            Society = new SocietyState { StartedAt = DateTimeOffset.UtcNow, GovernmentRotation = rotation }
        };
        var residentPopulation = population - Geography.Length * InstitutionsPerCountry;

        for (var index = 0; index < Geography.Length; index++)
        {
            var citizensPerCountry = residentPopulation / Geography.Length + (index < residentPopulation % Geography.Length ? 1 : 0);
            var (countryId, name, lat, lon) = Geography[index];
            var profile = Governments.Profiles[(index + rotation) % Governments.Profiles.Length];
            var planned = profile.Id == "planned_republic";
            var nation = new Nation
            {
                Id = countryId, Name = name, GovernmentId = profile.Id,
                Population = citizensPerCountry + InstitutionsPerCountry, TaxBasisPoints = profile.DefaultTaxBasisPoints,
                WelfareMinor = profile.DefaultWelfareMinor,
                UnemploymentBenefitMinor = profile.Id switch { "market_democracy" => 300, "social_democracy" => 400, _ => 0 },
                FoodMultiplier = new[] { 1.4, 1.6, 1.0, 0.55, 0.85 }[index % 5],
                EnergyMultiplier = new[] { 0.8, 0.55, 1.25, 1.6, 1.1 }[index % 5]
            };
            world.Society.Nations.Add(countryId, nation);
            world.Countries.Add(Wire.Node(new { id = countryId, name, lat, lon, government_id = profile.Id }));
            world.Locations.Add(Wire.Node(new { id = countryId + "_city", country_id = countryId, lat, lon }));

            var government = AddInstitution(world, countryId, "government", "government", 0);
            AddInstitution(world, countryId, "media", "journalist", 10000);
            // The first firm in a sector is "<country>_<sector>", later ones "<country>_<sector>_2" and so on.
            var firms = SocietyLabor.Sectors.ToDictionary(sector => sector.Id, sector => Enumerable.Range(1, sector.Firms).Select(number =>
            {
                var firm = AddInstitution(world, countryId, "business", sector.Id, 0, number == 1 ? null : $"{countryId}_{sector.Id}_{number}");
                firm.Specialty = sector.Id;
                firm.PriceMinor = sector.BasePrice;
                firm.WageOfferMinor = SocietyLabor.TableWage[sector.Occupation];
                return firm;
            }).ToArray());
            var hiring = SocietyLabor.Sectors.ToDictionary(sector => sector.Occupation, _ => 0);
            Agent NextFirm(string occupation)
            {
                var sector = SocietyLabor.Sectors.Single(sector => sector.Occupation == occupation);
                return firms[sector.Id][hiring[occupation]++ % sector.Firms];
            }
            var number = 0;
            Agent NewResident(string occupation, long cash)
            {
                var resident = CreateAgent(world, $"{countryId}_citizen_{++number:00000}", countryId, "citizen", occupation, cash);
                resident.Weights = AgentEndowments.Create(seed, $"citizen_{number:00000}");
                resident.Inventory["food"] = 4;
                resident.Inventory["energy"] = 4;
                resident.Health = 70 + (int)(Sample(seed, resident.Id, "initial_health") * 30);
                resident.SupportsLeadership = Sample(seed, resident.Id, "initial_support") > 0.2;
                world.Agents.Add(resident.Id, resident);
                return resident;
            }
            if (!planned) // the school is state-owned everywhere
                foreach (var firm in firms.Values.SelectMany(group => group).Where(firm => firm.Specialty != "school").Take(citizensPerCountry))
                {
                    var sector = SocietyLabor.SectorOf(firm.Specialty!);
                    var owner = NewResident("entrepreneur", 2 * SocietyLabor.TableWage[sector.Occupation]);
                    owner.Role = "entrepreneur";
                    owner.FormerOccupation = sector.Occupation;
                    owner.OwnedBusinessId = firm.Id;
                    owner.Goal = OwnerGoal(sector.Id);
                    firm.OwnerId = owner.Id;
                }
            foreach (var occupation in AllocateJobs(citizensPerCountry - number, planned))
            {
                var jobless = occupation == "unemployed";
                var trade = jobless ? "factory_worker" : occupation;
                var wage = SocietyLabor.TableWage[trade];
                var citizen = NewResident(trade, 2 * wage);
                if (jobless)
                {
                    citizen.UnemployedSinceTick = 0;
                    continue;
                }
                citizen.EmployerId = occupation is "soldier" ? government.Id : NextFirm(occupation).Id;
                citizen.SalaryMinor = wage;
            }
            if (planned && world.InCountry(countryId).LastOrDefault(agent => agent.Role == "citizen" && agent.Occupation == "factory_worker") is { } appointee)
                AppointPlanner(world, nation, appointee);
            var residentsHere = world.Agents.Values.Where(agent => agent.CountryId == countryId && SocietyLabor.IsResident(agent)).ToArray();
            AgentEndowments.AllocateCash(residentsHere, budgetMinor: residentsHere.Sum(resident => resident.CashMinor));
            foreach (var resident in residentsHere)
                resident.Wellbeing = resident.LastWeekWellbeing = SocietyEconomy.QualityOfLife(resident, 2, false);
            foreach (var (sectorId, group) in firms)
            foreach (var firm in group)
            {
                var staff = residentsHere.Where(resident => resident.EmployerId == firm.Id).ToArray();
                firm.TargetHeadcount = staff.Length;
                firm.CashMinor = Math.Max(10000, staff.Sum(worker => worker.SalaryMinor) * 4);
                if (SocietyBank.IsBank(firm))
                {
                    // The bank opens with its owner's capital (the state's in planned economies) and no
                    // deposits; it hires bankers as its loan book grows.
                    firm.CashMinor = firm.InvestedMinor = Math.Max(20000, citizensPerCountry * 4000L);
                    (firm.DepositRatePct, firm.LoanRatePct, firm.LendingStandard) = (3, 10, "normal");
                    firm.Goal = "Take deposits and lend them to viable businesses and founders while keeping enough reserves for withdrawals.";
                }
                var n = citizensPerCountry / group.Length; // starting stock is shared among a sector's firms
                switch (sectorId)
                {
                    case "food": firm.Inventory["food"] = n * 10; firm.Inventory["energy"] = n * 2; break;
                    case "energy": firm.Inventory["energy"] = n * 10; break;
                    case "tools": firm.Inventory["tools"] = n * 9; firm.Inventory["energy"] = n; break;
                    case "medicine": firm.Inventory["medicine"] = n; firm.Inventory["energy"] = n / 2 + 1; firm.Inventory["tools"] = n / 2 + 1; break;
                    case "clinic": firm.Inventory["medicine"] = n / 3 + 1; break;
                }
            }
            var publicPayroll = residentsHere.Where(resident => resident.EmployerId == government.Id).Sum(worker => worker.SalaryMinor);
            government.CashMinor = Math.Max(20000, 16 * (publicPayroll
                + (long)(nation.WelfareMinor + nation.ServiceBudgetPerResidentMinor) * citizensPerCountry));
        }

        AddRoutes(world);
        foreach (var first in Geography)
            foreach (var second in Geography.Where(other => string.CompareOrdinal(other.Id, first.Id) > 0))
            {
                var idPair = SocietyDiplomacy.PairId(first.Id, second.Id);
                world.Society.Relations.Add(idPair, new Relationship
                {
                    Id = idPair, First = first.Id, Second = second.Id,
                    Tension = (int)(Sample(seed, idPair, "tension") * 80)
                });
            }

        foreach (var good in Goods)
        {
            world.Society.InitialGoods[good] = world.Agents.Values.Sum(agent => (long)agent.Inventory[good]);
            world.Society.ProducedGoods[good] = 0;
            world.Society.ConsumedGoods[good] = 0;
            world.Society.LostGoods[good] = 0;
        }
        world.InitialCashMinor = world.Agents.Values.Sum(agent => agent.CashMinor);
        world.ScenarioHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"earthlike_v3:{world.Agents.Count}:{seed}:{rotation}:daily:labor:qol:firms12:bank:election100:crisis40:{AgentEndowments.Version}"))).ToLowerInvariant();
        SocietyEconomy.UpdateMetrics(world);
        world.Emit("world_initialized", "scenario", [world.Id], new
        {
            stage = 2, population = world.Agents.Count, countries = Geography.Length,
            currency = "simulated WLD", settlement = "internal_escrow", individual_minds = true,
            agent_weights = AgentEndowments.Version,
            routine_life = "rule-driven between individual model turns"
        });
        return world;
    }

    private static Agent AddInstitution(World world, string country, string role, string occupation, long cash, string? id = null)
    {
        var agent = CreateAgent(world, id ?? $"{country}_{occupation}", country, role, occupation, cash);
        world.Agents.Add(agent.Id, agent);
        return agent;
    }

    private static Agent CreateAgent(World world, string id, string country, string role, string occupation, long cash)
    {
        var traits = new[] { "risk_tolerance", "patience", "generosity", "trust" }
            .ToDictionary(name => name, name => Math.Round(Sample(world.Seed, id, name), 2));
        return new Agent
        {
            Id = id, CountryId = country, LocationId = country + "_city", Role = role,
            Occupation = occupation, CashMinor = cash, Traits = traits,
            Inventory = Goods.ToDictionary(good => good, _ => 0),
            Goal = role switch
            {
                "citizen" => CitizenGoal(occupation),
                "business" => $"Produce and sell {occupation}, pay workers, import scarce inputs and keep the business solvent.",
                "government" => "Meet public needs with a funded budget, follow institutional rules, manage trade and avoid costly conflict unless justified.",
                _ => "Select factual public reports that help residents understand shortages, trade and government performance."
            }
        };
    }

    private static IEnumerable<string> AllocateJobs(int workers, bool planned)
    {
        var essentials = new[] { "farmer", "energy_worker", "factory_worker", "service_worker", "researcher", "doctor", "dockworker", "teacher" };
        var jobs = essentials.Take(workers).ToList();
        var rest = workers - jobs.Count;
        foreach (var (occupation, weight) in JobShares)
            jobs.AddRange(Enumerable.Repeat(planned && occupation == "unemployed" ? "factory_worker" : occupation, rest * weight / 100));
        while (jobs.Count < workers)
            jobs.Add("factory_worker");
        return jobs;
    }

    public static double Sample(int seed, string id, string trait) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{id}:{trait}"))[0] / 255.0;

    public static int BasePrice(string good) => SocietyLabor.SectorOf(good).BasePrice;

    private static void AddRoutes(World world)
    {
        var links = new HashSet<string>();
        for (var index = 0; index < Geography.Length; index++)
        {
            var first = Geography[index];
            var neighbors = Geography.Where(other => other.Id != first.Id)
                .OrderBy(other => Distance(first, other)).Take(3)
                .Append(Geography[(index + 1) % Geography.Length]);
            foreach (var second in neighbors)
            {
                var pair = SocietyDiplomacy.PairId(first.Id, second.Id);
                if (!links.Add(pair))
                    continue;
                foreach (var (from, to) in new[] { (first, second), (second, first) })
                {
                    var id = $"{from.Id}_to_{to.Id}";
                    world.Routes[id] = new Route
                    {
                        Id = id, From = from.Id + "_city", To = to.Id + "_city", Open = true,
                        TravelTicks = Math.Clamp((int)Math.Ceiling(Distance(from, to) / 35), 2, 8),
                        CostMinor = Math.Clamp((int)Distance(from, to), 20, 250),
                        RiskScore = Math.Round(Sample(world.Seed, pair, "risk") * 0.2, 2),
                        ControllerCountryId = to.Id
                    };
                }
            }
        }
    }

    private static double Distance((string Id, string Name, double Lat, double Lon) first,
        (string Id, string Name, double Lat, double Lon) second)
    {
        var longitude = Math.Abs(first.Lon - second.Lon);
        longitude = Math.Min(longitude, 360 - longitude);
        return Math.Sqrt(Math.Pow(first.Lat - second.Lat, 2) + Math.Pow(longitude, 2));
    }
}
