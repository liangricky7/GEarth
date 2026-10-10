using System.Text.Json.Nodes;

namespace Faultline.Core;

public static class SocietyEngine
{
    public static void Advance(World world)
    {
        world.Tick++;
        SocietyDiplomacy.Advance(world);
        SocietyTrade.Advance(world);
        if (world.Tick % 5 == 0)
            SocietyEconomy.Cycle(world);
        SocietyEconomy.AssertConservation(world);
    }

    public static DecisionJob Observe(World world, string agentId, DateTimeOffset now)
    {
        var agent = world.Agents[agentId];
        var nation = world.Society!.Nations[agent.CountryId];
        var profile = Governments.Get(nation.GovernmentId);
        var neighbors = world.Society.Relations.Values.Where(relation => relation.First == nation.Id || relation.Second == nation.Id)
            .OrderByDescending(relation => relation.ProposedBy is not null).ThenByDescending(relation => relation.AtWar)
            .ThenByDescending(relation => relation.Tension).Take(4)
            .Select(relation => new
            {
                country_id = relation.First == nation.Id ? relation.Second : relation.First,
                relation.Tension, relation.TradeAgreement, relation.AtWar, relation.ProposedBy, relation.ProposalKind
            }).ToArray();
        var actions = SocietyActions.Available(world, agent);
        var offers = actions.Where(action => action.ActionType == "import_goods").Select(action =>
        {
            var seller = world.Agents[action.Params["seller_id"]!.GetValue<string>()];
            var good = action.Params["good"]!.GetValue<string>();
            var path = SocietyTrade.Path(world, seller.LocationId, agent.LocationId, action.Params["objective"]!.GetValue<string>())!;
            return new { seller_id = seller.Id, good, stock = seller.Inventory[good], seller.PriceMinor,
                path.TravelTicks, path.CostMinor, objective = path.Objective };
        }).ToArray();
        var observedEvents = world.Events.Where(item => item.EntityIds.Contains(nation.Id)
            && item.EventType is not ("action_accepted" or "action_rejected" or "economy_cycle")).TakeLast(2).ToArray();
        var evidence = new[] { $"country:{nation.Id}:tick{world.Tick}" }
            .Concat(observedEvents.Select(item => item.EventId)).ToArray();
        var observation = Wire.Node(new
        {
            world_stage = 2, tick = world.Tick,
            self = new
            {
                agent.Id, agent.Role, agent.Occupation, agent.Specialty, agent.Goal, agent.Traits, agent.Weights,
                agent.CashMinor, agent.Inventory, agent.WorkEffort, agent.TargetStock, agent.Wellbeing, agent.Health, agent.HealthThreshold,
                agent.SupportsLeadership, agent.PriceMinor, agent.Memories
            },
            country = new
            {
                nation.Id, government = profile.Name, profile.PolicyDelay, profile.PublicOwnershipPercent,
                nation.TaxBasisPoints, nation.WelfareMinor, nation.ServiceBudgetPerResidentMinor,
                nation.Approval, nation.Population, nation.Stocks, nation.UnmetNeedsLastCycle,
                treasury_minor = world.Agents[nation.Id + "_government"].CashMinor,
                nation.Productivity, nation.EducationFactor, nation.SalesTaxBasisPoints, nation.ProfitTaxBasisPoints,
                doctor_visit_price_minor = world.Agents[nation.Id + "_medicine"].PriceMinor,
                nation.CrisisGood, nation.CrisisUntilTick,
                pending_policy = world.Society.Policies.FirstOrDefault(policy => policy.CountryId == nation.Id)?.ActionType,
                government_authority_available = SocietyDiplomacy.HasAuthority(world, nation)
            },
            neighbors, import_offers = offers,
            requested_loans = world.Society.Credit.Where(credit => credit.Status == "requested"
                && (credit.BankId == agent.Id || credit.BorrowerId == agent.Id)).Take(4),
            public_events = observedEvents.Select(item => new { item.EventType, item.Payload }),
            note = "Routine consumption, purchases, wages and production follow standing policies every 5 ticks. Businesses pay workers 80% of last cycle's sales, split by effort, health, productivity and bargaining power, then pay profit tax. Public employees get a fixed salary the treasury may only partly cover; unpaid teachers lower productivity. Productivity weights also scale production; bargaining power only changes the share of funded business wages. Starting wealth is an initial allocation, not a recurring multiplier. Preference weights describe personal priorities, not guaranteed outcomes. Health falls with illness, extra work and unmet needs; below 30 you cannot work and pay scales with health. A doctor visit costs the clinic's medicine price, restores 40 health and happens automatically below your health policy threshold; public healthcare funds visits for the sickest. Household prices include sales tax. Extra work raises pay but costs wellbeing and health; striking stops wages and production. Imports use simulated escrow and physical transit. Welfare and services only spend available treasury. Migration costs 100 minor units. Conflict costs 5000 upfront plus 1000/cycle, destroys stock and blocks trade."
        });
        return new DecisionJob(Guid.NewGuid().ToString("N"), world.Id, agent.Id, world.Tick,
            now.AddSeconds(65), new Dictionary<string, int> { [agent.Id] = agent.Version }, evidence, observation, actions);
    }

    public static DecisionRecord Apply(World world, DecisionJob job, DecisionResult result, DateTimeOffset now)
    {
        if (world.Decisions.TryGetValue(job.RequestId, out var existing))
            return existing;
        var code = Validate(world, job, result, now);
        var accepted = code == "OK";
        var compactJob = job with { Candidates = [] };
        var record = new DecisionRecord(compactJob, result, accepted, code);
        world.Decisions[job.RequestId] = record;
        while (world.Decisions.Count > 256)
            world.Decisions.Remove(world.Decisions.Keys.First());
        var society = world.Society!;
        if (!accepted)
            society.RejectedDecisionCount++;
        if (result.Source == "rule_fallback")
            society.FallbackDecisionCount++;
        if (accepted && result.Source == "model")
            society.ModelDecisionCount++;
        if (accepted && result.Source == "rule")
            society.RuleDecisionCount++;
        if (result.LatencyMs > 0)
        {
            society.LatenciesMs.Add(result.LatencyMs);
            if (society.LatenciesMs.Count > 200)
                society.LatenciesMs.RemoveAt(0);
        }
        if (world.Agents.TryGetValue(job.AgentId, out var decidingAgent))
            decidingAgent.LastDecision = record;
        var decisionEvent = world.Emit(accepted ? "action_accepted" : "action_rejected",
            result.Source is "model" or "rule" or "rule_fallback" ? result.Source : "rule",
            [job.AgentId], new
            {
                decision_id = job.RequestId, code, action = result.Action, result.Model, result.Backend,
                result.LatencyMs, result.Error, observation_tick = job.ObservationTick
            });
        if (!accepted)
            return record;
        var agent = world.Agents[job.AgentId];
        Execute(world, agent, result.Action, decisionEvent.EventId);
        agent.LastDecisionTick = world.Tick;
        agent.LastRequestId = job.RequestId;
        if (result.Source == "model")
        {
            agent.ModelDecisions++;
            agent.LastModelDecisionAt = now;
        }
        agent.Version++;
        agent.Memories.Add($"T{world.Tick}: {result.Action.ActionType}: {result.Action.Reason}");
        if (agent.Memories.Count > 5)
            agent.Memories.RemoveAt(0);
        SocietyEconomy.AssertConservation(world);
        return record;
    }

    private static string Validate(World world, DecisionJob job, DecisionResult result, DateTimeOffset now)
    {
        if (job.RunId != world.Id || job.RequestId != result.RequestId || !world.Agents.TryGetValue(job.AgentId, out var agent))
            return "UNKNOWN_ENTITY";
        if (agent.LastRequestId == job.RequestId)
            return "DUPLICATE_REQUEST";
        if (job.Dependencies.GetValueOrDefault(agent.Id, -1) != agent.Version)
            return "STALE_AGENT_INTENT";
        if (result.Source is not ("model" or "rule" or "rule_fallback"))
            return "INVALID_SOURCE";
        if (job.ObservationTick > world.Tick || now > job.Deadline || world.Tick - job.ObservationTick > 90)
            return "EXPIRED_PROPOSAL";
        var action = result.Action;
        if (action is null || action.Params is null || string.IsNullOrWhiteSpace(action.Reason)
            || action.Reason.Length > 240 || action.EvidenceIds is null || action.EvidenceIds.Length > 8)
            return "INVALID_SCHEMA";
        if (action.EvidenceIds.Distinct().Count() != action.EvidenceIds.Length
            || action.EvidenceIds.Any(evidence => !job.EvidenceIds.Contains(evidence)))
            return "UNKNOWN_EVIDENCE";
        if (!job.Candidates.Any(candidate => candidate.ActionType == action.ActionType
            && JsonNode.DeepEquals(candidate.Params, action.Params)))
            return "UNAVAILABLE_ACTION";
        if (action.ActionType == "publish" && agent.Role == "media")
            return "OK";
        if (action.ActionType is "pay" or "buy_goods" && agent.Role != "government")
            return SocietyPayments.StillValid(world, agent, action.Params) ? "OK" : "CURRENT_PRECONDITION_FAILED";
        if (action.ActionType == "migrate" && agent.Role == "citizen")
        {
            var destination = action.Params["country_id"]!.GetValue<string>();
            return destination != agent.CountryId && world.Society!.Nations.ContainsKey(destination)
                && agent.CashMinor >= 100 && !SocietyDiplomacy.Relation(world, agent.CountryId, destination).AtWar
                ? "OK" : "CURRENT_PRECONDITION_FAILED";
        }
        if (!SocietyActions.Available(world, agent).Any(candidate => candidate.ActionType == action.ActionType
            && JsonNode.DeepEquals(candidate.Params, action.Params)))
            return "CURRENT_PRECONDITION_FAILED";
        return "OK";
    }

    private static void Execute(World world, Agent agent, ActionProposal action, string cause)
    {
        var parameters = action.Params;
        if (agent.Role == "government" && action.ActionType != "hold")
        {
            SocietyDiplomacy.Propose(world, agent, action, cause);
            return;
        }
        switch (action.ActionType)
        {
            case "household_policy":
                agent.TargetStock = parameters["target_stock"]!.GetValue<int>();
                break;
            case "health_policy":
                agent.HealthThreshold = parameters["visit_doctor_below_health"]!.GetValue<int>();
                break;
            case "work":
                agent.WorkEffort = parameters["effort"]!.GetValue<int>();
                break;
            case "express_support":
                agent.SupportsLeadership = parameters["support"]!.GetValue<bool>();
                break;
            case "set_price":
                agent.PriceMinor = parameters["price_minor"]!.GetValue<int>();
                break;
            case "publish":
                world.Emit("public_report", "rule", [agent.CountryId, agent.Id], new
                {
                    text = parameters["text"]!.GetValue<string>(),
                    provenance = "engine-generated country aggregate at observation tick",
                    report_tick = parameters["observed_tick"]!.GetValue<int>()
                }, cause);
                break;
            case "migrate":
                Migrate(world, agent, parameters["country_id"]!.GetValue<string>(), cause);
                break;
            case "import_goods":
                SocietyTrade.Dispatch(world, agent, world.Agents[parameters["seller_id"]!.GetValue<string>()],
                    parameters["good"]!.GetValue<string>(), parameters["quantity"]!.GetValue<int>(),
                    parameters["objective"]!.GetValue<string>(), cause);
                break;
            case "request_loan":
                var amount = parameters["amount_minor"]!.GetValue<int>();
                var credit = new Credit
                {
                    Id = $"loan_{world.Seq}", BorrowerId = agent.Id, BankId = agent.CountryId + "_bank",
                    PrincipalMinor = amount, DueMinor = amount * 105L / 100, DueTick = world.Tick + 60
                };
                world.Society!.Credit.Add(credit);
                world.Emit("loan_requested", "rule", [credit.Id, agent.Id, credit.BankId], new
                {
                    amount_minor = amount, credit.DueMinor, credit.DueTick
                }, cause);
                break;
            case "approve_loan":
                var approved = world.Society!.Credit.Single(credit => credit.Id == parameters["loan_id"]!.GetValue<string>());
                SocietyEconomy.Transfer(agent, world.Agents[approved.BorrowerId], approved.PrincipalMinor);
                approved.Status = "active";
                approved.DueTick = world.Tick + 60;
                world.Emit("loan_funded", "rule", [approved.Id, agent.Id, approved.BorrowerId], new
                {
                    approved.PrincipalMinor, approved.DueMinor, approved.DueTick, settlement = "simulated internal ledger"
                }, cause);
                break;
            case "pay":
                SocietyPayments.Pay(world, agent, parameters, cause);
                break;
            case "buy_goods":
                SocietyPayments.Buy(world, agent, parameters, cause);
                break;
        }
    }

    private static void Migrate(World world, Agent agent, string destination, string cause)
    {
        var previous = agent.CountryId;
        SocietyEconomy.Transfer(agent, world.Agents[destination + "_government"], 100);
        agent.CountryId = destination;
        agent.LocationId = destination + "_city";
        var occupation = agent.Occupation switch
        {
            "farmer" => "food", "energy_worker" => "energy", "factory_worker" or "dockworker" => "tools",
            "doctor" or "researcher" => "medicine", _ => "government"
        };
        agent.EmployerId = destination + "_" + occupation;
        world.Society!.Nations[previous].Population--;
        world.Society.Nations[destination].Population++;
        world.Emit("agent_migrated", "rule", [agent.Id, previous, destination], new
        {
            from = previous, to = destination, cost_minor = 100, occupation = agent.Occupation
        }, cause);
    }
}
