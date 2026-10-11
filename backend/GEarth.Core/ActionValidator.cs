using System.Text.Json.Nodes;

namespace GEarth.Core;

internal static class ActionValidator
{
    public static string Validate(World world, DecisionJob job, DecisionResult result, DateTimeOffset now)
    {
        if (job.RunId != world.Id || result.RequestId != job.RequestId
            || !world.Agents.TryGetValue(job.AgentId, out var agent))
            return "UNKNOWN_ENTITY";
        if (result.Source is not ("model" or "rule" or "rule_fallback"))
            return "INVALID_SOURCE";
        if (job.ObservationTick > world.Tick)
            return "FUTURE_OBSERVATION";
        if (now > job.Deadline || world.Tick - job.ObservationTick > 5)
            return "EXPIRED_PROPOSAL";
        if (!job.Dependencies.ContainsKey(agent.Id))
            return "MISSING_DEPENDENCY";
        if (job.Dependencies.Any(dependency => CurrentVersion(world, dependency.Key) != dependency.Value))
            return "STALE_DEPENDENCY";

        var action = result.Action;
        if (action is null || action.Params is null || string.IsNullOrWhiteSpace(action.Reason)
            || action.Reason.Length > 240 || action.EvidenceIds is null || action.EvidenceIds.Length > 8)
            return "INVALID_SCHEMA";
        if (action.EvidenceIds.Distinct().Count() != action.EvidenceIds.Length
            || action.EvidenceIds.Any(id => !job.EvidenceIds.Contains(id)))
            return "UNKNOWN_EVIDENCE";

        try
        {
            return action.ActionType switch
            {
                "hold" => action.Params.Count == 0 ? "OK" : "INVALID_SCHEMA",
                "publish" => ValidatePublish(world, agent, job, action.Params),
                "purchase" => ValidatePurchase(world, agent, job, action.Params),
                "choose_route" => ValidateRoute(world, agent, job, action.Params),
                _ => "UNIMPLEMENTED_ACTION"
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException
            or FormatException or OverflowException)
        {
            return "INVALID_SCHEMA";
        }
    }

    private static int CurrentVersion(World world, string id)
    {
        if (world.Agents.TryGetValue(id, out var agent))
            return agent.Version;
        if (world.Routes.TryGetValue(id, out var route))
            return route.Version;

        return -1;
    }

    private static string ValidatePublish(World world, Agent agent, DecisionJob job, JsonObject parameters)
    {
        if (agent.Role is not ("media" or "government"))
            return "ROLE_FORBIDDEN";
        if (!HasKeys(parameters, "text", "claim_event_id"))
            return "INVALID_SCHEMA";
        if (parameters["text"]!.GetValue<string>().Length is < 1 or > 280)
            return "INVALID_SCHEMA";

        var claimId = parameters["claim_event_id"]?.GetValue<string>();
        if (claimId is null)
            return "OK";

        return job.EvidenceIds.Contains(claimId) && world.Events.Any(item => item.EventId == claimId)
            ? "OK"
            : "UNKNOWN_EVIDENCE";
    }

    private static string ValidatePurchase(World world, Agent buyer, DecisionJob job, JsonObject parameters)
    {
        if (buyer.Role is not ("citizen" or "business"))
            return "ROLE_FORBIDDEN";
        if (!HasKeys(parameters, "offer_id", "quantity"))
            return "INVALID_SCHEMA";

        var quantity = parameters["quantity"]!.GetValue<int>();
        if (quantity <= 0)
            return "INVALID_SCHEMA";
        if (!MatchesCandidate(job, "purchase", parameters))
            return "UNAVAILABLE_ACTION";

        var offer = parameters["offer_id"]!.GetValue<string>().Split(':');
        if (offer.Length != 2 || !world.Agents.TryGetValue(offer[0], out var seller)
            || !seller.Inventory.ContainsKey(offer[1]))
            return "UNKNOWN_ENTITY";
        if (seller.Role != "business" || seller.LocationId != buyer.LocationId
            || !job.Dependencies.ContainsKey(seller.Id))
            return "UNAVAILABLE_ACTION";
        if (buyer.CashMinor < quantity * Engine.UnitPrice(offer[1]))
            return "INSUFFICIENT_FUNDS";

        return seller.Inventory[offer[1]] < quantity ? "INSUFFICIENT_STOCK" : "OK";
    }

    private static string ValidateRoute(World world, Agent agent, DecisionJob job, JsonObject parameters)
    {
        if (agent.Id != "beryl_business_1")
            return "ROLE_FORBIDDEN";
        if (!MatchesCandidate(job, "choose_route", parameters))
            return "UNAVAILABLE_ACTION";

        var plan = RoutePlanner.FindPath(world, parameters["objective"]!.GetValue<string>());
        if (plan is null)
            return "NO_ROUTE";

        return plan.CostMinor > agent.CashMinor ? "INSUFFICIENT_FUNDS" : "OK";
    }

    private static bool HasKeys(JsonObject parameters, params string[] keys)
    {
        return parameters.Count == keys.Length && keys.All(parameters.ContainsKey);
    }

    private static bool MatchesCandidate(DecisionJob job, string actionType, JsonObject parameters)
    {
        return job.Candidates.Any(candidate => candidate.ActionType == actionType
            && JsonNode.DeepEquals(candidate.Params, parameters));
    }
}
