using System.Text.Json.Nodes;

namespace Faultline.Core;

// Independent of the bounded UI event/decision windows. Revision is assigned by the committing store.
public sealed record AuditRecord(string RunId, long Sequence, int Tick, long Revision, string Kind, JsonObject Payload);

public sealed record RunAuditHead(string RunId, long Revision, long AuditSequence, string Status, bool AnalyticsEnabled);

public interface IRunAuditStore
{
    bool SupportsAudit { get; }
    Task<RunAuditHead?> ReadAuditHead(string runId, CancellationToken cancellationToken);
    Task<AuditRecord[]> ReadAudit(string runId, long afterSequence, int limit, CancellationToken cancellationToken);
}

public interface IAnalyticsReportStore
{
    Task<JsonObject?> ReadAnalyticsReport(string runId, CancellationToken cancellationToken);
    Task SaveAnalyticsReport(string runId, JsonObject report, CancellationToken cancellationToken);
}

public sealed record AuditContext(string? DecisionId = null, string? EventId = null, string? RoutineId = null,
    string? GroupId = null, string? Good = null, int? Quantity = null, long? UnitPriceMinor = null);

public sealed class AnalyticsTimelineException() : InvalidOperationException(
    "Analytics capture for resumed/forked timelines requires a distinct dataset identity and is not supported by audit v1; start a fresh analytics run or resume with analytics disabled.");

public static class RunAudit
{
    public const int SchemaVersion = 1;

    public static void PrepareResume(World world, bool enableAnalytics)
    {
        if (enableAnalytics)
            throw new AnalyticsTimelineException();
        if (world.PendingAudit.Count > 0)
            throw new InvalidOperationException("Resume requires a committed checkpoint without pending audit records");
        // A branch inherits its parent's audit prefix, but cannot append using the same dataset identity.
        // Keep the cursor and coverage boundary intact so exporters can identify the incomplete capture.
        world.AnalyticsEnabled = false;
    }

    public static void Enable(World world, object? metadata = null)
    {
        if (world.AnalyticsEnabled)
            return;
        world.AnalyticsEnabled = true;
        world.AuditSchemaVersion = SchemaVersion;
        world.AuditStartTick = world.Tick;
        world.AuditStartedAt = DateTimeOffset.UtcNow;
        Append(world, "run_metadata", metadata ?? new
        {
            world.Seed, world.Mode, initial_population = world.Agents.Count,
            capture_started_at = world.AuditStartedAt, capture_start_tick = world.Tick,
            schema_version = SchemaVersion, engine_commit = (string?)null
        });
        foreach (var item in world.Events)
            Append(world, "event", item);
        foreach (var agent in world.Agents.Values)
            Append(world, "opening_balance", new { account_id = agent.Id, account_type = "agent",
                balance_minor = agent.CashMinor, agent = AgentState(agent) });
        if (world.Society is { } society)
            foreach (var cargo in society.Cargo)
                Append(world, "opening_balance", new { account_id = cargo.Id, account_type = "escrow",
                    balance_minor = cargo.EscrowMinor });
        world.AuditOpeningBalancesCaptured = true;
    }

    public static void Append(World world, string kind, object payload)
    {
        if (!world.AnalyticsEnabled)
            return;
        world.PendingAudit.Add(new AuditRecord(world.Id, ++world.AuditSequence, world.Tick, 0, kind,
            Wire.Node(payload).AsObject()));
    }

    public static object AgentState(Agent agent) => new
    {
        id = agent.Id, agent_id = agent.Id, agent.Role, country = agent.CountryId, country_id = agent.CountryId,
        location_id = agent.LocationId, agent.Occupation, employer_id = agent.EmployerId,
        owner_id = agent.OwnerId, owned_business_id = agent.OwnedBusinessId,
        decision_maker = SocietyLabor.DecidesForItself(agent), agent.CashMinor, agent.Health, agent.Wellbeing,
        agent.Inventory, agent.Specialty, agent.PriceMinor, agent.WageOfferMinor, agent.TargetHeadcount,
        closed = agent.Role == "defunct", agent.SalaryMinor, agent.UnemployedSinceTick, agent.FoundedTick,
        agent.HealthThreshold, agent.TargetStock, agent.WorkEffort, agent.SupportsLeadership,
        agent.OvertimeOffered, agent.ModelDecisions, agent.LastDecisionTick, agent.LastAttemptTick
    };

    // Exactly one row per agent per simulated day. Call at the day's boundary, after late results.
    // Opening balances contain tick-zero state; final checkpoint contains terminal state.
    public static void Day(World world, bool terminal = false)
    {
        if (!world.AnalyticsEnabled || !terminal && (world.Tick == 0 || world.Tick <= world.AuditDailyThroughTick))
            return;
        foreach (var agent in world.Agents.Values)
        {
            var payload = Wire.Node(AgentState(agent)).AsObject();
            payload["frame_kind"] = terminal ? "end" : "day";
            Append(world, "agent_day", payload);
        }
        if (world.Society is { } society)
            foreach (var nation in society.Nations.Values)
            {
                var people = world.InCountry(nation.Id);
                Append(world, "nation_day", new
                {
                    frame_kind = terminal ? "end" : "day",
                    nation_id = nation.Id, id = nation.Id, government = nation.GovernmentId,
                    escrow_accounts = society.Cargo.Where(cargo => world.Agents[cargo.BuyerId].CountryId == nation.Id)
                        .Select(cargo => new { account_id = cargo.Id, balance_minor = cargo.EscrowMinor }).ToArray(),
                    residents = people.Count(SocietyLabor.IsResident), nation.Unemployed, nation.Approval,
                    government_cash_minor = world.Agents[nation.Id + "_government"].CashMinor,
                    gini = nation.GiniCash, unmet_needs = nation.UnmetNeedsLastCycle,
                    wars = society.Relations.Values.Count(relation => relation.AtWar
                        && (relation.First == nation.Id || relation.Second == nation.Id)),
                    stock = SocietyFactory.Goods.ToDictionary(good => good,
                        good => people.Sum(agent => (long)agent.Inventory.GetValueOrDefault(good))),
                    shipments = society.Cargo.Where(cargo => cargo.Status == "in_transit"
                        && world.Agents[cargo.BuyerId].CountryId == nation.Id).Select(cargo => new
                        {
                            cargo.Id, cargo.Good, cargo.Quantity, cargo.BuyerId, cargo.SellerId,
                            cargo.EscrowMinor, cargo.Status,
                            edge = cargo.Plan?.EdgeIds.ElementAtOrDefault(cargo.EdgeIndex), cargo.EdgeRemainingTicks
                        }).ToArray()
                });
            }
        if (!terminal)
            world.AuditDailyThroughTick = world.Tick;
    }

    public static void Request(World world, DecisionJob job, DateTimeOffset dispatchedAt)
    {
        if (!world.AnalyticsEnabled)
            return;
        var agent = world.Agents[job.AgentId];
        Append(world, "decision_request", new
        {
            decision_id = job.RequestId, agent_id = job.AgentId, agent.Role, country_id = agent.CountryId,
            observation_tick = job.ObservationTick, dispatched_at = dispatchedAt,
            job, prompt_contract_version = "faultline.numbered_menu.v1",
            rules_note = job.Observation["note"]?.GetValue<string>(),
            // Worker renders this exact job; raw provider prompt/token counts are not returned to the engine.
            rendered_provider_prompt = (string?)null, token_usage = (object?)null,
            evidence_attribution = "engine supplied; not necessarily model cited"
        });
    }

    public static void Result(World world, DecisionJob job, DecisionResult result, bool accepted, string code, string eventId)
    {
        if (!world.AnalyticsEnabled)
            return;
        var index = Array.FindIndex(job.Candidates, candidate => result.Action is not null
            && candidate.ActionType == result.Action.ActionType && JsonNode.DeepEquals(candidate.Params, result.Action.Params));
        Append(world, "decision_result", new
        {
            decision_id = job.RequestId, agent_id = job.AgentId, observation_tick = job.ObservationTick,
            applied_tick = world.Tick, result, accepted, code, status = accepted ? "accepted" : "rejected",
            fallback = result.Source == "rule_fallback", selected_option_index = index >= 0 ? (int?)index : null,
            selection_provenance = "matched exact returned action to original zero-based menu",
            action_event_id = eventId
        });
    }

    public static void Canceled(World world, DecisionJob job, DecisionResult? result = null)
    {
        Append(world, "decision_result", new
        {
            decision_id = job.RequestId, agent_id = job.AgentId, observation_tick = job.ObservationTick,
            applied_tick = (int?)null, result, accepted = false, code = "HOST_SHUTDOWN",
            status = "canceled", fallback = result?.Source == "rule_fallback", selected_option_index = (int?)null,
            action_event_id = (string?)null
        });
    }

    public static void Transaction(World world, string from, string to, long amount, string kind)
    {
        if (!world.AnalyticsEnabled || amount == 0)
            return;
        var context = world.CurrentAuditContext;
        Append(world, "transaction", new
        {
            transaction_id = $"{world.Id}:a{world.AuditSequence + 1}", sender_account_id = from,
            recipient_account_id = to, from, to, kind, amount_minor = amount,
            transaction_group_id = context?.GroupId,
            decision_id = context?.DecisionId, event_id = context?.EventId, routine_id = context?.RoutineId,
            good = context?.Good, quantity = context?.Quantity, unit_price_minor = context?.UnitPriceMinor,
            causal_source = context?.EventId is not null ? "engine_event" : context?.RoutineId is not null ? "routine" : "unknown"
        });
    }

    public static IDisposable? Context(World world, AuditContext context)
    {
        if (!world.AnalyticsEnabled)
            return null;
        var previous = world.CurrentAuditContext;
        world.CurrentAuditContext = context;
        return new Restore(() => world.CurrentAuditContext = previous);
    }

    public static IDisposable? Group(World world, string? good = null, int? quantity = null, long? unitPrice = null, string? id = null)
    {
        if (!world.AnalyticsEnabled)
            return null;
        return Context(world, (world.CurrentAuditContext ?? new()) with
        {
            GroupId = id ?? world.CurrentAuditContext?.GroupId ?? $"{world.Id}:g{world.AuditSequence + 1}",
            Good = good ?? world.CurrentAuditContext?.Good,
            Quantity = quantity ?? world.CurrentAuditContext?.Quantity,
            UnitPriceMinor = unitPrice ?? world.CurrentAuditContext?.UnitPriceMinor
        });
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    // Capture only potentially action-controlled entities, not the entire population per decision.
    public static IDisposable? Effects(World world, Agent agent, ActionProposal action, string? decisionId, string eventId)
    {
        if (!world.AnalyticsEnabled)
            return null;
        var ids = new HashSet<string> { agent.Id };
        if (agent.OwnedBusinessId is { } owned)
            ids.Add(owned);
        foreach (var key in new[] { "business_id", "seller_id", "recipient_id", "to_agent_id" })
            if (action.Params[key] is JsonValue value && value.TryGetValue<string>(out var id) && world.Agents.ContainsKey(id))
                ids.Add(id);
        if (action.ActionType == "set_wage" && agent.OwnedBusinessId is { } firm)
            foreach (var worker in SocietyLabor.Staff(world, world.Agents[firm]))
                ids.Add(worker.Id);
        var before = Capture();
        return new Restore(() =>
        {
            if (agent.OwnedBusinessId is { } newFirm)
                ids.Add(newFirm);
            var after = Capture();
            foreach (var (key, state) in after)
            {
                before.TryGetValue(key, out var prior);
                if (!JsonNode.DeepEquals(prior, state))
                {
                    var changed = state.AsObject().Where(field => !JsonNode.DeepEquals(prior?[field.Key], field.Value))
                        .Select(field => field.Key).ToArray();
                    var beforeFields = prior is null ? null : new JsonObject(changed.Select(field =>
                        new KeyValuePair<string, JsonNode?>(field, prior[field]?.DeepClone())));
                    var afterFields = new JsonObject(changed.Select(field =>
                        new KeyValuePair<string, JsonNode?>(field, state[field]?.DeepClone())));
                    Append(world, "action_effect", new
                    {
                        decision_id = decisionId, event_id = eventId, entity_id = key[2..],
                        entity_type = key[0] switch { 'a' => "agent", 'n' => "nation", 'r' => "route", _ => "relationship" },
                        before = beforeFields, after = afterFields, link_type = "engine_execution"
                    });
                }
            }
        });

        Dictionary<string, JsonNode> Capture()
        {
            var states = ids.ToDictionary(id => "a:" + id, id =>
            {
                var state = Wire.Node(AgentState(world.Agents[id])).AsObject();
                foreach (var field in new[] { "model_decisions", "last_decision_tick", "last_attempt_tick" })
                    state.Remove(field);
                return (JsonNode)state;
            });
            if (agent.Role == "government" && world.Society is { } society)
            {
                var nation = society.Nations[agent.CountryId];
                states["n:" + nation.Id] = Wire.Node(new { nation.TaxBasisPoints, nation.WelfareMinor,
                    nation.UnemploymentBenefitMinor, nation.SalesTaxBasisPoints, nation.ServiceBudgetPerResidentMinor,
                    nation.Productivity });
                if (action.Params["edge_id"]?.GetValue<string>() is { } edge)
                    states["r:" + edge] = Wire.Node(world.Routes[edge]);
                if (action.Params["country_id"]?.GetValue<string>() is { } country)
                {
                    var relationship = SocietyDiplomacy.Relation(world, nation.Id, country);
                    states["d:" + relationship.Id] = Wire.Node(relationship);
                }
            }
            return states;
        }
    }
}
