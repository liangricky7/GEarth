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
            agents = world.Agents.Values,
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
                society.LostGoods
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
                    society.RecentModelDecisionsPerSecond
                }
            }
        };
    }
}
