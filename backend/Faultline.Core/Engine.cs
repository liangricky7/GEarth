using System.Text.Json.Nodes;

namespace Faultline.Core;

public static class Engine
{
    private static readonly string[] ConsumableGoods = ["food", "energy"];
    private static readonly string[] RouteObjectives = ["fastest", "cheapest", "cautious"];

    public static World Create(string scenarioJson, string id, bool live, int citizensPerCountry = 5)
    {
        return WorldFactory.Create(scenarioJson, id, live, citizensPerCountry);
    }

    public static PathPlan? FindPath(World world, string objective)
    {
        return RoutePlanner.FindPath(world, objective);
    }

    public static long UnitPrice(string good)
    {
        return good == "food" ? 50L : 20L;
    }

    public static void Advance(World world)
    {
        world.Tick++;
        UpdateBorder(world);

        if (world.Tick % 5 == 0)
            ConsumeSupplies(world);
    }

    private static void UpdateBorder(World world)
    {
        var route = world.Routes["r_direct"];
        if (world.Tick != 8 && route.ExpiresTick != world.Tick)
            return;

        route.Open = world.Tick != 8;
        route.ExpiresTick = world.Tick == 8 ? 60 : null;
        route.Version++;

        var borderEvent = world.Emit("border_changed", "scenario", [route.Id], new
        {
            edge_id = route.Id,
            route.Open,
            route.ExpiresTick
        });

        if (!route.Open && world.MedicineRoutePlan?.EdgeIds.Contains(route.Id) == true)
        {
            world.MedicineRoutePlan = null;
            world.Emit("route_unavailable", "rule", ["shipment_medicine_001"], new
            {
                reason = "PLANNED_EDGE_CLOSED",
                planning_only = true
            }, borderEvent.EventId);
        }
    }

    private static void ConsumeSupplies(World world)
    {
        var citizens = world.Agents.Values.Where(agent => agent.Role == "citizen").ToArray();

        foreach (var citizen in citizens)
        {
            foreach (var good in ConsumableGoods)
            {
                if (citizen.Inventory[good] > 0)
                    citizen.Inventory[good]--;
                else
                    world.UnmetNeeds++;
            }

            citizen.Version++;
        }

        world.Emit("inventory_changed", "rule", citizens.Select(agent => agent.Id).ToArray(), new
        {
            reason = "periodic_consumption",
            world.UnmetNeeds
        });
    }

    public static DecisionJob Observe(World world, string agentId, DateTimeOffset now)
    {
        var agent = world.Agents[agentId];
        var evidence = new List<string> { $"state:{agent.Id}:v{agent.Version}" };
        var dependencies = new Dictionary<string, int> { [agent.Id] = agent.Version };
        var candidates = new List<Candidate> { new("hold", new JsonObject()) };
        var offers = new List<object>();
        var plans = new List<PathPlan>();

        if (agent.Role == "citizen")
            AddPurchaseOptions(world, agent, dependencies, evidence, candidates, offers);
        if (agent.Id == "beryl_business_1")
            AddRouteOptions(world, dependencies, evidence, candidates, plans);
        if (agent.Role is "media" or "government")
        {
            candidates.Add(new Candidate("publish", new JsonObject
            {
                ["text"] = "<your factual report, at most 280 characters>",
                ["claim_event_id"] = null
            }));
        }

        var publicEvents = world.Events.Where(item => item.EventType == "border_changed").TakeLast(3).ToArray();
        evidence.AddRange(publicEvents.Select(item => item.EventId));

        var observation = Wire.Node(new
        {
            agent,
            tick = world.Tick,
            offers,
            route_options = plans,
            public_events = publicEvents,
            note = "Stage 1: purchases use simulated currency; route selection is planning only. Loans, dispatch, and blockchain settlement are unavailable."
        });

        return new DecisionJob(Guid.NewGuid().ToString("N"), world.Id, agent.Id, world.Tick,
            now.AddSeconds(15), dependencies, evidence.ToArray(), observation, candidates.ToArray());
    }

    private static void AddPurchaseOptions(World world, Agent buyer, Dictionary<string, int> dependencies,
        List<string> evidence, List<Candidate> candidates, List<object> offers)
    {
        var sellers = world.Agents.Values.Where(agent => agent.Role == "business"
            && agent.LocationId == buyer.LocationId);

        foreach (var seller in sellers)
        {
            dependencies[seller.Id] = seller.Version;

            foreach (var good in ConsumableGoods)
            {
                var offerId = $"{seller.Id}:{good}";
                evidence.Add(offerId);
                offers.Add(new
                {
                    id = offerId,
                    good,
                    unit_price_minor = UnitPrice(good),
                    stock = seller.Inventory[good]
                });

                foreach (var quantity in new[] { 2, 4 })
                {
                    candidates.Add(new Candidate("purchase", new JsonObject
                    {
                        ["offer_id"] = offerId,
                        ["quantity"] = quantity
                    }));
                }
            }
        }
    }

    private static void AddRouteOptions(World world, Dictionary<string, int> dependencies,
        List<string> evidence, List<Candidate> candidates, List<PathPlan> plans)
    {
        foreach (var route in world.Routes.Values)
            dependencies[route.Id] = route.Version;

        evidence.Add("shipment_medicine_001");

        foreach (var objective in RouteObjectives)
        {
            var plan = FindPath(world, objective);
            if (plan is null)
                continue;

            plans.Add(plan);
            candidates.Add(new Candidate("choose_route", new JsonObject
            {
                ["shipment_id"] = "shipment_medicine_001",
                ["objective"] = objective
            }));
        }
    }

    public static DecisionRecord Apply(World world, DecisionJob job, DecisionResult result, DateTimeOffset now)
    {
        if (world.Decisions.TryGetValue(job.RequestId, out var existing))
            return existing;

        var code = ActionValidator.Validate(world, job, result, now);
        var accepted = code == "OK";
        var record = new DecisionRecord(job, result, accepted, code);
        world.Decisions.Add(job.RequestId, record);

        var source = result.Source is "model" or "rule" or "rule_fallback" ? result.Source : "rule";
        var decisionEvent = world.Emit(accepted ? "action_accepted" : "action_rejected", source,
            [job.AgentId], new
            {
                decision_id = job.RequestId,
                code,
                action = result.Action,
                result.Model,
                result.Backend,
                result.LatencyMs,
                result.Error,
                observation_tick = job.ObservationTick
            });

        if (!accepted)
            return record;

        var agent = world.Agents[job.AgentId];

        if (result.Action.ActionType == "purchase")
            ApplyPurchase(world, agent, result, decisionEvent);
        if (result.Action.ActionType == "choose_route")
        {
            world.MedicineRoutePlan = FindPath(world, result.Action.Params["objective"]!.GetValue<string>());
            world.Emit("route_changed", source, ["shipment_medicine_001"], new
            {
                plan = world.MedicineRoutePlan,
                planning_only = true
            }, decisionEvent.EventId);
        }

        agent.Version++;
        agent.Memories.Add($"Tick {world.Tick}: {result.Action.ActionType}: {result.Action.Reason}");
        if (agent.Memories.Count > 8)
            agent.Memories.RemoveAt(0);

        return record;
    }

    private static void ApplyPurchase(World world, Agent buyer, DecisionResult result, WorldEvent decisionEvent)
    {
        var offer = result.Action.Params["offer_id"]!.GetValue<string>().Split(':');
        var seller = world.Agents[offer[0]];
        var good = offer[1];
        var quantity = result.Action.Params["quantity"]!.GetValue<int>();
        var amount = quantity * UnitPrice(good);

        buyer.CashMinor -= amount;
        seller.CashMinor += amount;
        buyer.Inventory[good] += quantity;
        seller.Inventory[good] -= quantity;
        seller.Version++;

        world.Emit("money_transferred", result.Source, [buyer.Id, seller.Id], new
        {
            amount_minor = amount,
            good,
            quantity
        }, decisionEvent.EventId);

        world.Emit("inventory_changed", result.Source, [buyer.Id, seller.Id], new
        {
            good,
            quantity,
            from = seller.Id,
            to = buyer.Id
        }, decisionEvent.EventId);
    }
}
