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
    private static readonly string[] Occupations =
        ["farmer", "energy_worker", "factory_worker", "doctor", "teacher", "soldier", "dockworker", "researcher"];

    public static World Create(string id, bool live, int population, int seed = 20261010, int rotation = 0)
    {
        if (population < 100 || population > 100000)
            throw new ArgumentOutOfRangeException(nameof(population));

        var world = new World
        {
            SchemaVersion = 2, Id = id, ScenarioId = "earthlike_society_v2", Mode = live ? "LIVE" : "RULE",
            Seed = seed, UpdatedAt = DateTimeOffset.UtcNow,
            Society = new SocietyState { StartedAt = DateTimeOffset.UtcNow, GovernmentRotation = rotation }
        };
        var residentPopulation = population - Geography.Length * 7;

        for (var index = 0; index < Geography.Length; index++)
        {
            var citizensPerCountry = residentPopulation / Geography.Length + (index < residentPopulation % Geography.Length ? 1 : 0);
            var (countryId, name, lat, lon) = Geography[index];
            var profile = Governments.Profiles[(index + rotation) % Governments.Profiles.Length];
            var nation = new Nation
            {
                Id = countryId, Name = name, GovernmentId = profile.Id,
                Population = citizensPerCountry + 7, TaxBasisPoints = profile.DefaultTaxBasisPoints,
                WelfareMinor = profile.DefaultWelfareMinor,
                FoodMultiplier = new[] { 1.4, 1.6, 1.0, 0.55, 0.85 }[index % 5],
                EnergyMultiplier = new[] { 0.8, 0.55, 1.25, 1.6, 1.1 }[index % 5]
            };
            world.Society.Nations.Add(countryId, nation);
            world.Countries.Add(Wire.Node(new { id = countryId, name, lat, lon, government_id = profile.Id }));
            world.Locations.Add(Wire.Node(new { id = countryId + "_city", country_id = countryId, lat, lon }));

            AddInstitution(world, countryId, "government", "government", Math.Max(20000, citizensPerCountry * 2000L));
            AddInstitution(world, countryId, "bank", "bank", Math.Max(20000, citizensPerCountry * 4000L));
            AddInstitution(world, countryId, "media", "journalist", 10000);

            foreach (var good in Goods)
            {
                var business = AddInstitution(world, countryId, "business", good, Math.Max(20000, citizensPerCountry * 1000L));
                business.Specialty = good;
                business.PriceMinor = BasePrice(good);
                business.Inventory[good] = citizensPerCountry * (good is "food" or "energy" ? 12 : 4);
                business.Inventory["energy"] += good == "energy" ? 0 : citizensPerCountry * 2;
                business.Inventory["tools"] += good == "medicine" ? citizensPerCountry : 0;
            }

            for (var citizen = 1; citizen <= citizensPerCountry; citizen++)
            {
                var occupation = Occupations[(citizen - 1) % Occupations.Length];
                var agent = CreateAgent(world, $"{countryId}_citizen_{citizen:00000}", countryId,
                    "citizen", occupation, 2000);
                agent.Weights = AgentEndowments.Create(seed, $"citizen_{citizen:00000}");
                agent.Inventory["food"] = 4;
                agent.Inventory["energy"] = 4;
                agent.SupportsLeadership = Sample(seed, agent.Id, "initial_support") > 0.2;
                agent.EmployerId = occupation switch
                {
                    "farmer" => countryId + "_food",
                    "energy_worker" => countryId + "_energy",
                    "factory_worker" or "dockworker" => countryId + "_tools",
                    "doctor" or "researcher" => countryId + "_medicine",
                    _ => countryId + "_government"
                };
                world.Agents.Add(agent.Id, agent);
            }
            AgentEndowments.AllocateCash(world.Agents.Values
                .Where(agent => agent.Role == "citizen" && agent.CountryId == countryId).ToArray());
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
            $"earthlike_v2:{world.Agents.Count}:{seed}:{rotation}:economy5:election100:crisis40:{AgentEndowments.Version}"))).ToLowerInvariant();
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

    private static Agent AddInstitution(World world, string country, string role, string occupation, long cash)
    {
        var agent = CreateAgent(world, $"{country}_{occupation}", country, role, occupation, cash);
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

    public static double Sample(int seed, string id, string trait) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{id}:{trait}"))[0] / 255.0;

    public static int BasePrice(string good) => good switch
    {
        "food" => 50, "energy" => 20, "tools" => 100, "medicine" => 150,
        _ => throw new ArgumentException("Unknown good")
    };

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
