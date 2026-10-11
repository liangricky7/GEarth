using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Faultline.Core;

public static class SocietyFactory
{
    private static readonly (string Id, string Name, double Lat, double Lon)[] Geography =
    [
        ("aster", "Aster", 40, -105), ("beryl", "Beryl", -18, -58),
        ("cinder", "Cinder", 50, 15), ("dune", "Dune", 25, 42),
        ("ember", "Ember", -27, 135), ("fjord", "Fjord", 64, -22),
        ("grove", "Grove", 3, 23), ("haven", "Haven", 25, 110),
        ("ionia", "Ionia", 8, 80), ("jade", "Jade", 35, 140)
    ];
    public static readonly string[] Goods = ["food", "energy", "tools", "medicine"];
    // Government, bank, media and every firm.
    public static readonly int InstitutionsPerCountry = 3 + SocietyLabor.Sectors.Sum(sector => sector.Firms);

    // Starting job shares per 100 workers, sized so each sector's sales roughly cover its payroll.
    // One of each essential occupation comes first so small test worlds still have every sector staffed;
    // doctors come only from that list, so each country starts with one (two with a clinic owner, about
    // 20 per 1,000 people: the fewest that keep a clinic open at this population size).
    private static readonly (string Occupation, int Weight)[] JobShares =
    [
        ("farmer", 21), ("energy_worker", 6), ("factory_worker", 25), ("service_worker", 20),
        ("dockworker", 2), ("teacher", 9), ("soldier", 6), ("unemployed", 9)
    ];

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
            AddInstitution(world, countryId, "bank", "bank", Math.Max(20000, citizensPerCountry * 4000L));
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
            if (!planned)
                foreach (var firm in firms.Values.SelectMany(group => group).Take(citizensPerCountry))
                {
                    var sector = SocietyLabor.SectorOf(firm.Specialty!);
                    var owner = NewResident("entrepreneur", 2 * SocietyLabor.TableWage[sector.Occupation]);
                    owner.Role = "entrepreneur";
                    owner.FormerOccupation = sector.Occupation;
                    owner.OwnedBusinessId = firm.Id;
                    owner.Goal = $"Keep your {sector.Id} business profitable while meeting your own household needs.";
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
                citizen.EmployerId = occupation is "teacher" or "soldier" ? government.Id : NextFirm(occupation).Id;
                citizen.SalaryMinor = wage;
            }
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
            government.CashMinor = Math.Max(20000, 16 * (publicPayroll + (long)nation.WelfareMinor * citizensPerCountry));
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
            $"earthlike_v3:{world.Agents.Count}:{seed}:{rotation}:daily:labor:qol:firms12:election100:crisis40:{AgentEndowments.Version}"))).ToLowerInvariant();
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
                "citizen" => $"Work as a {occupation.Replace('_', ' ')}, meet household needs, preserve savings and influence leadership according to your values.",
                "business" => $"Produce and sell {occupation}, pay workers, import scarce inputs and keep the business solvent.",
                "government" => "Meet public needs with a funded budget, follow institutional rules, manage trade and avoid costly conflict unless justified.",
                "bank" => "Fund viable requested business loans while preserving liquidity and managing default risk.",
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
