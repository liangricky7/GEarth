namespace GEarth.Core;

public static class SocietyProjection
{
    public static double? ModelDecisionAgeSeconds(World world, Agent agent) => agent.LastModelDecisionAt is { } at
        ? Math.Max(0, (world.UpdatedAt - at).TotalSeconds) : null;

    public static object AgentSummary(World world, Agent agent) => new
    {
        agent.Id,
        agent.Role,
        agent.CountryId,
        agent.Occupation,
        agent.CashMinor,
        agent.Weights,
        agent.ModelDecisions,
        agent.LastAttemptAt,
        agent.LastModelDecisionAt,
        model_decision_age_seconds = ModelDecisionAgeSeconds(world, agent),
        decision_maker = SocietyLabor.DecidesForItself(agent),
        next_attempt_due_tick = world.Society is null || world.ScenarioId != "earthlike_society_v3_daily"
            ? (int?)null : SocietyScheduler.NextTurnTick(world, agent)
    };

    private static object[] RoleMetrics(World world) => world.Agents.Values.GroupBy(agent => agent.Role)
        .OrderBy(group => group.Key).Select(group =>
        {
            var ages = group.Select(agent => ModelDecisionAgeSeconds(world, agent))
                .Where(age => age.HasValue).Select(age => age!.Value).Order().ToArray();
            var society = world.Society!;
            return (object)new
            {
                role = group.Key,
                population = group.Count(),
                unique_model_deciding = group.Count(agent => agent.ModelDecisions > 0),
                model_decision_count = group.Sum(agent => agent.ModelDecisions),
                never_attempted = group.Count(agent => agent.LastAttemptAt is null),
                eligible_agents = group.Count(agent => society.QueuedAgentIds.Contains(agent.Id)),
                pending_decisions = society.PendingByRole.Values.Sum() == society.PendingDecisions
                    ? (int?)society.PendingByRole.GetValueOrDefault(group.Key) : null,
                model_decision_age_p95_seconds = ages.Length == 0 ? (double?)null
                    : ages[Math.Min(ages.Length - 1, (int)(ages.Length * 0.95))],
                model_decision_age_max_seconds = ages.Length == 0 ? (double?)null : ages[^1]
            };
        }).ToArray();

    public static object Snapshot(World world)
    {
        var society = world.Society ?? throw new InvalidOperationException("Society state missing");
        return new
        {
            world.SchemaVersion,
            world.Seq,
            world.Tick,
            world.UpdatedAt,
            run = new
            {
                world.Id,
                world.ScenarioId,
                world.Mode,
                world.Status,
                world.Seed,
                population = world.Agents.Count,
                world.Revision,
                world.ScenarioHash,
                stage = 2
            },
            world.Countries,
            world.Locations,
            nations = society.Nations.Values,
            government_profiles = Governments.Profiles,
            routes = world.Routes.Values,
            relationships = society.Relations.Values,
            policies = society.Policies,
            shipments = society.Cargo,
            loans = society.Credit,
            agents_summary = new
            {
                spawned = world.Agents.Count,
                by_role = world.Agents.Values.GroupBy(agent => agent.Role)
                    .ToDictionary(group => group.Key, group => group.Count()),
                unique_model_deciding = world.Agents.Values.Count(agent => agent.ModelDecisions > 0),
                decision_makers = world.Agents.Values.Count(SocietyLabor.DecidesForItself)
            },
            agents = world.Agents.Values.Where(agent => agent.Role != "citizen")
                .Select(agent => AgentSummary(world, agent)),
            metrics = new
            {
                society.ModelDecisionCount,
                society.FallbackDecisionCount,
                society.RejectedDecisionCount,
                society.RuleDecisionCount,
                cash_minor = world.Agents.Values.Sum(agent => agent.CashMinor),
                escrow_minor = society.Cargo.Sum(cargo => cargo.EscrowMinor),
                world.InitialCashMinor,
                world.UnmetNeeds,
                society.DeliveredShipments,
                society.FailedShipments,
                society.InitialGoods,
                society.ProducedGoods,
                society.ConsumedGoods,
                society.LostGoods,
                held_goods = SocietyFactory.Goods.ToDictionary(good => good, good =>
                    world.Agents.Values.Sum(agent => (long)agent.Inventory[good])
                    + society.Cargo.Where(cargo => cargo.Status == "in_transit" && cargo.Good == good).Sum(cargo => (long)cargo.Quantity)),
                society.StartedAt,
                society.CoverageCompletedAt,
                elapsed_seconds = (world.UpdatedAt - society.StartedAt).TotalSeconds,
                latency_sample_scope = "last_200_positive_worker_durations_all_sources",
                latency_sample_count = society.LatenciesMs.Count,
                latency_p95_ms = society.LatenciesMs.Count == 0 ? 0 : society.LatenciesMs.Order().ElementAt(Math.Min(society.LatenciesMs.Count - 1, (int)(society.LatenciesMs.Count * 0.95)))
            },
            compute = new[]
            {
                new
                {
                    id = "decision_pool",
                    providers = society.ComputeProviders,
                    status = world.Mode == "LIVE" ? "configured" : "disabled",
                    society.WorkerCapacity,
                    society.PendingDecisions,
                    society.DueAgents,
                    society.OldestWaitTicks,
                    society.PendingByRole,
                    by_role = RoleMetrics(world),
                    average_model_decisions_per_second = society.RecentModelDecisionsPerSecond,
                    society.MaxPendingObserved,
                    scheduling_mode = world.ScenarioId == "earthlike_society_v3_daily" ? "weekly" : "legacy_wall_clock",
                    citizen_rotation_weeks = world.ScenarioId == "earthlike_society_v3_daily" ? (int?)SocietyScheduler.CitizenRotationWeeks : null,
                    society.TickIntervalMilliseconds
                }
            }
        };
    }
}
