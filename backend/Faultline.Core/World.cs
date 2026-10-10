using System.Text.Json;
using System.Text.Json.Nodes;

namespace Faultline.Core;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };
    public static JsonNode Node(object value) => JsonSerializer.SerializeToNode(value, Json)!;
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
}

public sealed class Agent
{
    public string Id { get; set; } = "";
    public string Role { get; set; } = "";
    public string CountryId { get; set; } = "";
    public string LocationId { get; set; } = "";
    public string Goal { get; set; } = "";
    public AgentWeights Weights { get; set; } = new();
    public Dictionary<string, double> Traits { get; set; } = new();
    public long CashMinor { get; set; }
    public int Version { get; set; }
    public Dictionary<string, int> Inventory { get; set; } = new();
    public List<string> Memories { get; set; } = [];
    public string Occupation { get; set; } = "";
    public string? EmployerId { get; set; }
    public string? Specialty { get; set; }
    public int WorkEffort { get; set; } = 1;
    public int TargetStock { get; set; } = 8;
    public int Wellbeing { get; set; } = 100;
    public int Health { get; set; } = 100;
    public int HealthThreshold { get; set; } = 70;
    // Labor market: residents hold a salary with an employer (null employer = unemployed).
    public long SalaryMinor { get; set; }
    public int UnemployedSinceTick { get; set; } = -1;
    // Entrepreneurs own exactly one business; FormerOccupation is restored if it closes.
    public string? OwnedBusinessId { get; set; }
    public string? FormerOccupation { get; set; }
    // Businesses: owner (null = state-owned), wage offer, headcount target and distress tracking.
    public string? OwnerId { get; set; }
    public long WageOfferMinor { get; set; }
    public int TargetHeadcount { get; set; }
    public long InvestedMinor { get; set; }
    public int FoundedTick { get; set; }
    public int DistressWeeks { get; set; }
    // Overtime: firms offer it when short-handed; residents count the days they worked it this week.
    public bool OvertimeOffered { get; set; }
    public int LastWeekUnitsSold { get; set; }
    public int OvertimeDays { get; set; }
    // Quality of life inputs: discretionary goods and services bought last week, and last week's score.
    public int GoodsBoughtLastWeek { get; set; }
    public int ServicesBoughtLastWeek { get; set; }
    public int LastWeekWellbeing { get; set; } = 100;
    // Money in and out this week and last week, for budgets shown to decision makers.
    public long WeekInMinor { get; set; }
    public long WeekOutMinor { get; set; }
    public long LastWeekInMinor { get; set; }
    public long LastWeekOutMinor { get; set; }
    public bool SupportsLeadership { get; set; } = true;
    public int LastAttemptTick { get; set; } = -1;
    public int LastDecisionTick { get; set; } = -1;
    public long ModelDecisions { get; set; }
    public string LastRequestId { get; set; } = "";
    public int PriceMinor { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastModelDecisionAt { get; set; }
    public DecisionRecord? LastDecision { get; set; }

    // Agents keep their five most recent memories.
    public void Remember(string memory)
    {
        Memories.Add(memory);
        if (Memories.Count > 5)
            Memories.RemoveAt(0);
    }
}

public sealed class Route
{
    public string Id { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int TravelTicks { get; set; }
    public int CostMinor { get; set; }
    public double RiskScore { get; set; }
    public bool Open { get; set; }
    public string ControllerCountryId { get; set; } = "";
    public int Version { get; set; }
    public int? ExpiresTick { get; set; }
}

public record PathPlan(string[] EdgeIds, int TravelTicks, int CostMinor, string Objective);
public record ActionProposal(string ActionType, JsonObject Params, string Reason, string[] EvidenceIds);
public record Candidate(string ActionType, JsonObject Params);
public record DecisionJob(string RequestId, string RunId, string AgentId, int ObservationTick,
    DateTimeOffset Deadline, Dictionary<string, int> Dependencies, string[] EvidenceIds,
    JsonNode Observation, Candidate[] Candidates);
public record DecisionResult(string RequestId, ActionProposal Action, string Source, string Model,
    double LatencyMs, string? Error = null, string? Backend = null);
public record DecisionRecord(DecisionJob Job, DecisionResult Result, bool Accepted, string Code);
public record WorldEvent(int SchemaVersion, string EventId, string RunId, int Tick, long Seq,
    string EventType, string Source, string[] EntityIds, string[] CauseIds, JsonNode Payload);

public sealed class World
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string ScenarioId { get; set; } = "";
    public string Mode { get; set; } = "RULE";
    public string Status { get; set; } = "running";
    public int Seed { get; set; }
    public int Tick { get; set; }
    public long Revision { get; set; }
    public long Seq { get; set; }
    public int SchedulerCursor { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long InitialCashMinor { get; set; }
    public int UnmetNeeds { get; set; }
    public JsonArray Countries { get; set; } = [];
    public JsonArray Locations { get; set; } = [];
    public Dictionary<string, Agent> Agents { get; set; } = new();
    public Dictionary<string, Route> Routes { get; set; } = new();
    public Dictionary<string, DecisionRecord> Decisions { get; set; } = new();
    public List<WorldEvent> Events { get; set; } = [];
    public PathPlan? MedicineRoutePlan { get; set; }
    public string ScenarioHash { get; set; } = "";
    public SocietyState? Society { get; set; }

    // Verbose runs only (null otherwise): money moved since the last history frame, per giver, recipient and kind.
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<(string From, string To, string Kind), (long Amount, int Count)>? Flows { get; set; }

    // Agents grouped by country in insertion order, so per-country scans skip the rest of the world.
    // Not saved; rebuilt when an agent is added (agents are never removed) or after Reindex (migration).
    private Dictionary<string, Agent[]>? byCountry;
    private int indexedAgents;

    public Agent[] InCountry(string country)
    {
        if (byCountry is null || indexedAgents != Agents.Count)
        {
            byCountry = Agents.Values.GroupBy(agent => agent.CountryId).ToDictionary(group => group.Key, group => group.ToArray());
            indexedAgents = Agents.Count;
        }
        return byCountry.GetValueOrDefault(country, []);
    }

    public void Reindex() => byCountry = null;

    public WorldEvent Emit(string type, string source, string[] entities, object payload, params string[] causes)
    {
        var worldEvent = new WorldEvent(SchemaVersion, $"{Id}:e{++Seq}", Id, Tick, Seq, type,
            source, entities, causes, Wire.Node(payload));

        Events.Add(worldEvent);
        return worldEvent;
    }

    public object Snapshot() => Society is not null ? SocietyProjection.Snapshot(this) : StageOneSnapshot();

    private object StageOneSnapshot() => new
    {
        SchemaVersion,
        Seq,
        Tick,
        UpdatedAt,
        run = new
        {
            Id,
            ScenarioId,
            Mode,
            Status,
            Seed,
            population = Agents.Count,
            Revision,
            ScenarioHash,
            stage = 1
        },
        Countries,
        Locations,
        routes = Routes.Values,
        agents_summary = new
        {
            spawned = Agents.Count,
            by_role = Agents.Values.GroupBy(agent => agent.Role)
                .ToDictionary(group => group.Key, group => group.Count()),
            unique_model_deciding = Decisions.Values
                .Where(decision => decision.Accepted && decision.Result.Source == "model")
                .Select(decision => decision.Job.AgentId)
                .Distinct()
                .Count()
        },
        agents = Agents.Values,
        shipments = new[]
        {
            new
            {
                id = "shipment_medicine_001",
                status = "planning_only",
                settlement_state = "not_implemented",
                plan = MedicineRoutePlan
            }
        },
        metrics = new
        {
            model_decision_count = Decisions.Values.Count(decision => decision.Accepted && decision.Result.Source == "model"),
            fallback_decision_count = Decisions.Values.Count(decision => decision.Result.Source == "rule_fallback"),
            rejected_decision_count = Decisions.Values.Count(decision => !decision.Accepted),
            cash_minor = Agents.Values.Sum(agent => agent.CashMinor),
            InitialCashMinor,
            UnmetNeeds
        },
        compute = new[]
        {
            new { id = "spark_a", status = Mode == "LIVE" ? "configured" : "disabled" }
        }
    };
}
