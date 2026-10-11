using System.Text.Json.Nodes;

namespace Faultline.Core;

public static class SocietyEngine
{
    // A decision made on a view older than a week no longer fits the world and expires.
    public const int DecisionMaxAgeDays = 7;

    // Numbers come from the engine constants so the model is never told a stale rule.
    public static readonly string RulesNote = $"One tick is one day. Daily: everyone uses 1 food and 1 energy, bought automatically up to your target stock, mostly from cheaper local sellers. Health falls 1 per day, more with illness or unmet needs; you visit the cheapest open clinic automatically when below your health threshold, and public healthcare pays for the sickest. Lower health lowers the output you produce. quality_of_life (0-100) is scored from outcomes with diminishing returns: needs met, health, household goods and entertainment bought last week, security (a job or business plus savings) and free time. Weekly: households automatically buy household goods (tools) and entertainment while the next unit adds more quality of life than the savings it costs. Employees receive their agreed salary; effort 1 is normal hours; effort 2 means you accept overtime when your employer offers it (overtime_offered), working a quarter more hours for 1.5x pay on those hours at the cost of free time and some health, and it does nothing when no overtime is offered; effort 0 is a strike and forfeits that week's pay. Firms short of cash cut pay 10% or lay off staff. Unemployed residents are hired automatically by firms with openings in their occupation; after {SocietyLabor.RelatedJobAfterDays} days anyone can take an unskilled job (skilled workers keep their qualification and return when a matching job pays 10% more), and doctors and researchers can swap; employed workers switch for at least 10% more pay. Several firms compete in most sectors. A citizen with savings can start a business related to their occupation, or an entertainment venue, where the market shows an opportunity, launching 10% below the cheapest competitor; entrepreneurs are paid from profit (up to their former trade's wage, then half the rest; nothing in a losing week), receive as dividends any business cash above {SocietyLabor.DividendBufferWeeks} weeks of payroll, and set price, wage offer and headcount. A business that cannot cover costs for {SocietyLabor.DistressWeeksToClose} weeks closes and returns its cash to the owner. Prices adjust weekly in proportion to shortages or gluts, up to {SocietyLabor.MaxPriceStepPct}%. Household prices include sales tax, wages carry income tax and firms pay profit tax. Governments pay public salaries on a fixed scale, welfare, unemployment benefit and public healthcare from the treasury; budget_last_week shows income and spending. Residents whose quality of life falls sharply withdraw support from the leadership. Imports use simulated escrow and transit; freight fees go to a domestic freight firm. Conflict costs 5000 upfront plus 1000 per week. Decisions apply only within {DecisionMaxAgeDays} days of the observation they were made on.";

    private static void DropNulls(JsonNode? node)
    {
        if (node is JsonObject fields)
            foreach (var (key, value) in fields.ToArray())
            {
                if (value is null)
                    fields.Remove(key);
                else
                    DropNulls(value);
            }
        else if (node is JsonArray items)
            foreach (var item in items)
                DropNulls(item);
    }

    public static void Advance(World world)
    {
        world.Tick++;
        using var routine = RunAudit.Context(world, new(RoutineId: $"{world.Id}:day:{world.Tick}"));
        SocietyDiplomacy.Advance(world);
        SocietyTrade.Advance(world);
        SocietyEconomy.Day(world);
        if (world.Tick % SocietyLabor.DaysPerWeek == 0)
            SocietyEconomy.Week(world);
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
                path.TravelTicks, shipping_minor = path.CostMinor };
        }).DistinctBy(offer => (offer.seller_id, offer.good)).ToArray(); // one row per seller; sizes are in the menu
        // Weekly labor summaries are already in the market block, so they are not repeated as events.
        var observedEvents = world.Events.Where(item => item.EntityIds.Contains(nation.Id)
            && item.EventType is not ("action_accepted" or "action_rejected" or "economy_cycle" or "labor_market")).TakeLast(2).ToArray();
        var evidence = new[] { $"country:{nation.Id}:tick{world.Tick}" }
            .Concat(observedEvents.Select(item => item.EventId)).ToArray();
        var firm = agent.OwnedBusinessId is { } owned ? world.Agents[owned] : agent.Role == "business" ? agent : null;
        var firmGood = firm is null ? null : SocietyLabor.SectorOf(firm.Specialty!).Good;
        var business = firm is null ? null : new
        {
            firm.Id, sector = firm.Specialty, status = firm.Role, firm.CashMinor, firm.PriceMinor,
            wage_offer_minor_per_week = firm.WageOfferMinor, staff = SocietyLabor.Staff(world, firm).Length,
            firm.TargetHeadcount, stock = firmGood is null ? (int?)null : firm.Inventory[firmGood],
            last_week_income_minor = firm.LastWeekInMinor, last_week_spending_minor = firm.LastWeekOutMinor,
            weeks_unable_to_cover_costs = firm.DistressWeeks,
            competitor_prices = SocietyLabor.Firms(world, nation.Id, firm.Specialty!).Where(other => other != firm)
                .Select(other => other.PriceMinor).Order().Take(5)
        };
        // Each role sees what its choices depend on; prompts stay small so the model keeps up with the weekly economy.
        var resident = SocietyLabor.IsResident(agent);
        var governing = agent.Role == "government";
        var market = agent.Role is "bank" or "media" ? null : new
        {
            cheapest_price_minor = SocietyLabor.Sectors.Where(sector => sector.BasePrice > 0).ToDictionary(sector => sector.Id,
                sector => SocietyLabor.Firms(world, nation.Id, sector.Id).Select(other => (int?)other.PriceMinor).Min()),
            best_wage_offer_minor_per_week = resident || governing ? SocietyLabor.Sectors.ToDictionary(sector => sector.Occupation,
                sector => SocietyLabor.Firms(world, nation.Id, sector.Id).Select(other => (long?)other.WageOfferMinor).Max()) : null,
            unemployed_residents = nation.Unemployed, nation.UnemploymentBenefitMinor,
            startup_cost_minor = agent.Role == "citizen"
                ? SocietyLabor.StartableSectors(agent).ToDictionary(sector => sector, sector => SocietyLabor.StartupCost(agent, sector)) : null,
            business_opportunities = agent.Role == "citizen"
                ? SocietyLabor.StartableSectors(agent).Where(sector => SocietyLabor.HasOpportunity(world, nation, sector)).ToArray() : null
        };
        var treasury = world.Agents[nation.Id + "_government"].CashMinor;
        object country = governing
            ? new
            {
                nation.Id, government = profile.Name, profile.PolicyDelay, profile.PublicOwnershipPercent,
                nation.TaxBasisPoints, nation.WelfareMinor, nation.ServiceBudgetPerResidentMinor,
                nation.Approval, nation.Population, nation.Stocks, nation.UnmetNeedsLastCycle, treasury_minor = treasury,
                nation.Productivity, nation.EducationFactor, nation.SalesTaxBasisPoints, nation.ProfitTaxBasisPoints,
                nation.CrisisGood, crisis_until_tick = nation.CrisisGood is null ? (int?)null : nation.CrisisUntilTick,
                pending_policy = world.Society.Policies.FirstOrDefault(policy => policy.CountryId == nation.Id)?.ActionType,
                government_authority_available = SocietyDiplomacy.HasAuthority(world, nation)
            }
            : new
            {
                nation.Id, government = profile.Name, nation.Approval, nation.Stocks, nation.UnmetNeedsLastCycle,
                nation.TaxBasisPoints, nation.WelfareMinor, treasury_minor = treasury, nation.CrisisGood
            };
        var loans = world.Society.Credit.Where(credit => credit.Status == "requested"
            && (credit.BankId == agent.Id || credit.BorrowerId == agent.Id)).Take(4).ToArray();
        var observation = Wire.Node(new
        {
            world_stage = 2, tick = world.Tick,
            self = new
            {
                agent.Id, agent.Role, agent.Occupation, specialty = agent.Role == "business" ? agent.Specialty : null,
                agent.Goal, agent.Traits, agent.Weights, agent.CashMinor,
                inventory = resident ? agent.Inventory.Where(item => item.Value > 0).ToDictionary() : null,
                work_effort = resident ? agent.WorkEffort : (int?)null, target_stock = resident ? agent.TargetStock : (int?)null,
                quality_of_life = resident ? agent.Wellbeing : (int?)null, health = resident ? agent.Health : (int?)null,
                health_threshold = resident ? agent.HealthThreshold : (int?)null,
                supports_leadership = resident ? agent.SupportsLeadership : (bool?)null,
                memories = agent.Memories.TakeLast(3).Select(memory => memory.Length > 100 ? memory[..100] : memory),
                employer_id = agent.EmployerId,
                salary_minor_per_week = agent.EmployerId is null ? (long?)null : agent.SalaryMinor,
                overtime_offered = agent.EmployerId is { } employer && world.Agents[employer].OvertimeOffered ? true : (bool?)null,
                unemployed_days = agent.EmployerId is null && agent.UnemployedSinceTick >= 0 ? world.Tick - agent.UnemployedSinceTick : (int?)null
            },
            business, market,
            budget_last_week = governing
                ? new { income_minor = agent.LastWeekInMinor, spending_minor = agent.LastWeekOutMinor,
                    net_minor = agent.LastWeekInMinor - agent.LastWeekOutMinor } : null,
            country,
            neighbors = governing ? neighbors : null,
            import_offers = offers.Length > 0 ? offers : null,
            requested_loans = loans.Length > 0 ? loans : null,
            public_events = observedEvents.Select(item => new { item.EventType, item.Payload }),
            // The rules are identical for every agent; the worker sends them as a shared, cacheable system prompt.
            note = RulesNote
        });
        DropNulls(observation);
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
        // The full observation stays only in the recent-decisions window; each agent keeps a slim copy.
        if (world.Agents.TryGetValue(job.AgentId, out var decidingAgent))
            decidingAgent.LastDecision = record with { Job = compactJob with { Observation = new JsonObject() } };
        var decisionEvent = world.Emit(accepted ? "action_accepted" : "action_rejected",
            result.Source is "model" or "rule" or "rule_fallback" ? result.Source : "rule",
            [job.AgentId], new
            {
                decision_id = job.RequestId, code, action = result.Action, result.Model, result.Backend,
                result.LatencyMs, result.Error, observation_tick = job.ObservationTick
            });
        RunAudit.Result(world, job, result, accepted, code, decisionEvent.EventId);
        if (!accepted)
            return record;
        var agent = world.Agents[job.AgentId];
        using var auditContext = RunAudit.Context(world, new(DecisionId: job.RequestId, EventId: decisionEvent.EventId));
        // Government effects are captured when the policy actually enacts (which may be delayed).
        using var effects = agent.Role == "government" ? null
            : RunAudit.Effects(world, agent, result.Action, job.RequestId, decisionEvent.EventId);
        Execute(world, agent, result.Action, decisionEvent.EventId);
        agent.LastDecisionTick = world.Tick;
        agent.LastRequestId = job.RequestId;
        if (result.Source == "model")
        {
            agent.ModelDecisions++;
            agent.LastModelDecisionAt = now;
        }
        agent.Version++;
        agent.Remember($"T{world.Tick}: {result.Action.ActionType}: {result.Action.Reason}");
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
        if (job.ObservationTick > world.Tick || now > job.Deadline || world.Tick - job.ObservationTick > DecisionMaxAgeDays)
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
        if (agent.Role == "entrepreneur" && action.Params.ContainsKey("business_id"))
            return OwnerActionValid(world, agent, action) ? "OK" : "CURRENT_PRECONDITION_FAILED";
        if (action.ActionType == "start_business")
            return SocietyLabor.CanStart(world, agent, action.Params["sector"]!.GetValue<string>()) ? "OK" : "CURRENT_PRECONDITION_FAILED";
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

    // Owner choices act on the owned business; prices and wages move relative to their current level.
    private static bool OwnerActionValid(World world, Agent owner, ActionProposal action)
    {
        var parameters = action.Params;
        if (parameters["business_id"]!.GetValue<string>() != owner.OwnedBusinessId
            || !world.Agents.TryGetValue(owner.OwnedBusinessId!, out var firm) || firm.Role != "business")
            return false;
        return action.ActionType != "import_goods" || SocietyTrade.CanImport(world, firm,
            world.Agents[parameters["seller_id"]!.GetValue<string>()], parameters["good"]!.GetValue<string>(),
            parameters["quantity"]!.GetValue<int>(), parameters["objective"]!.GetValue<string>());
    }

    private static void Execute(World world, Agent agent, ActionProposal action, string cause)
    {
        var parameters = action.Params;
        if (parameters["business_id"]?.GetValue<string>() is { } businessId)
        {
            var firm = world.Agents[businessId];
            var spec = SocietyLabor.SectorOf(firm.Specialty!);
            switch (action.ActionType)
            {
                case "set_price":
                    firm.PriceMinor = Math.Clamp(firm.PriceMinor * (100 + parameters["change_pct"]!.GetValue<int>()) / 100,
                        spec.BasePrice / 2, spec.BasePrice * 3);
                    return;
                case "set_wage":
                    var change = parameters["change_pct"]!.GetValue<int>();
                    var table = SocietyLabor.TableWage[spec.Occupation];
                    firm.WageOfferMinor = Math.Clamp(firm.WageOfferMinor * (100 + change) / 100, table / 2, table * 3);
                    foreach (var worker in SocietyLabor.Staff(world, firm))
                        worker.SalaryMinor = worker.SalaryMinor * (100 + change) / 100;
                    return;
                case "set_headcount":
                    firm.TargetHeadcount = Math.Max(0, firm.TargetHeadcount + parameters["change"]!.GetValue<int>());
                    return;
                case "import_goods":
                    SocietyTrade.Dispatch(world, firm, world.Agents[parameters["seller_id"]!.GetValue<string>()],
                        parameters["good"]!.GetValue<string>(), parameters["quantity"]!.GetValue<int>(),
                        parameters["objective"]!.GetValue<string>(), cause);
                    return;
            }
        }
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
            case "start_business":
                SocietyLabor.Start(world, agent, parameters["sector"]!.GetValue<string>(),
                    parameters["launch_price_minor"]!.GetValue<int>(), cause);
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
                    Id = $"loan_{world.NextEntityNumber()}", BorrowerId = agent.Id, BankId = agent.CountryId + "_bank",
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
                SocietyEconomy.Transfer(world, agent, world.Agents[approved.BorrowerId], approved.PrincipalMinor, "loan");
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
        SocietyEconomy.Transfer(world, agent, world.Agents[destination + "_government"], 100, "migration_fee");
        agent.CountryId = destination;
        world.Reindex();
        agent.LocationId = destination + "_city";
        agent.EmployerId = null;
        agent.SalaryMinor = 0;
        agent.UnemployedSinceTick = world.Tick;
        world.Society!.Nations[previous].Population--;
        world.Society.Nations[destination].Population++;
        world.Emit("agent_migrated", "rule", [agent.Id, previous, destination], new
        {
            from = previous, to = destination, cost_minor = 100, occupation = agent.Occupation
        }, cause);
    }
}
