using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GEarth.Core;

internal static class WorldFactory
{
    private static readonly string[] Roles = ["government", "business", "bank", "media", "citizen"];
    private static readonly string[] TraitNames = ["risk_tolerance", "patience", "generosity", "trust"];

    public static World Create(string scenarioJson, string runId, bool live, int citizensPerCountry)
    {
        var scenario = JsonNode.Parse(scenarioJson)!;
        var scenarioBytes = Encoding.UTF8.GetBytes(scenarioJson);
        var world = new World
        {
            Id = runId,
            ScenarioId = scenario["scenario_id"]!.GetValue<string>(),
            Seed = scenario["seed"]!.GetValue<int>(),
            Mode = live ? "LIVE" : "RULE",
            Countries = (JsonArray)scenario["countries"]!.DeepClone(),
            Locations = (JsonArray)scenario["locations"]!.DeepClone(),
            UpdatedAt = DateTimeOffset.UtcNow,
            ScenarioHash = Convert.ToHexString(SHA256.HashData(scenarioBytes)).ToLowerInvariant()
        };

        foreach (var routeData in scenario["routes"]!.AsArray())
        {
            var route = routeData!.Deserialize<Route>(Wire.Json)!;
            world.Routes.Add(route.Id, route);
        }

        foreach (var country in world.Countries)
        {
            var countryId = country!["id"]!.GetValue<string>();

            foreach (var role in Roles)
            {
                var count = role switch
                {
                    "citizen" => citizensPerCountry,
                    "business" => 2,
                    _ => 1
                };

                for (var index = 1; index <= count; index++)
                {
                    var agent = CreateAgent(scenario, world.Seed, countryId, role, index);
                    world.Agents.Add(agent.Id, agent);
                }
            }
        }

        world.InitialCashMinor = world.Agents.Values.Sum(agent => agent.CashMinor);
        world.Emit("world_initialized", "scenario", [world.Id], new
        {
            population = world.Agents.Count,
            stage = 1,
            currency = "simulated WLD",
            settlement = "not_implemented"
        });

        return world;
    }

    private static Agent CreateAgent(JsonNode scenario, int seed, string countryId, string role, int index)
    {
        var suffix = role switch
        {
            "citizen" => $"_{index:000}",
            "business" => $"_{index}",
            _ => ""
        };
        var agentId = $"{countryId}_{role}{suffix}";
        var traits = TraitNames.ToDictionary(name => name, name => SampleTrait(seed, agentId, name));
        var startingCash = scenario["currency"]!["initial_cash_overrides"]?[agentId]?.GetValue<long>()
            ?? scenario["currency"]!["initial_cash_minor_per_role"]![role]!.GetValue<long>();

        return new Agent
        {
            Id = agentId,
            Role = role,
            CountryId = countryId,
            LocationId = $"{countryId}_city",
            Traits = traits,
            CashMinor = startingCash,
            Goal = SelectGoal(agentId, role, traits),
            Inventory = new Dictionary<string, int>
            {
                ["food"] = role == "business" ? 100 : 0,
                ["energy"] = role == "business" ? 100 : 0
            }
        };
    }

    private static double SampleTrait(int seed, string agentId, string name)
    {
        var input = Encoding.UTF8.GetBytes($"{seed}:{agentId}:{name}");
        var sample = SHA256.HashData(input)[0] / 255.0;
        return Math.Round(sample, 2);
    }

    private static string SelectGoal(string agentId, string role, Dictionary<string, double> traits)
    {
        return role switch
        {
            "citizen" when traits["risk_tolerance"] > 0.5 =>
                "Keep at least 2 food and 2 energy in stock while conserving cash.",
            "citizen" => "Avoid shortages: aim for 4 food and 4 energy when affordable.",
            "business" when agentId == "beryl_business_1" =>
                "Plan an affordable medicine route. Balance speed, cost, and risk using your traits. This is planning only.",
            "business" => "Preserve inventory and cash for local buyers.",
            "government" => "Inform your country about verified border changes and shortages.",
            "media" => "Publish concise factual reports from supplied observations. Do not invent events.",
            _ => "Preserve liquidity. Lending is unavailable in Stage 1."
        };
    }
}
