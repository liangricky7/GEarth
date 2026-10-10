namespace Faultline.Core;

public static class SocietyProjection
{
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
                unique_model_deciding = world.Agents.Values.Count(agent => agent.ModelDecisions > 0)
            },
            agents = world.Agents.Values.Where(agent => agent.Role != "citizen").Select(agent => new
            { agent.Id, agent.Role, agent.CountryId, agent.Occupation, agent.CashMinor, agent.ModelDecisions }),
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
                latency_p95_ms = society.LatenciesMs.Count == 0 ? 0 : society.LatenciesMs.Order().ElementAt(Math.Min(society.LatenciesMs.Count - 1, (int)(society.LatenciesMs.Count * 0.95)))
            },
            compute = new[]
            {
                new
                {
                    id = "spark_a",
                    status = world.Mode == "LIVE" ? "configured" : "disabled",
                    society.WorkerCapacity,
                    society.PendingDecisions,
                    society.DueAgents,
                    society.OldestWaitTicks,
                    average_model_decisions_per_second = society.RecentModelDecisionsPerSecond,
                    society.MaxPendingObserved,
                    society.InstitutionIntervalSeconds,
                    society.ResidentIntervalSeconds,
                    society.TickIntervalMilliseconds
                }
            }
        };
    }
}
