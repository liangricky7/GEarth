namespace Faultline.Core;

// Decisions follow the weekly rhythm of the economy: every week each government, entrepreneur, bank,
// newsroom and state firm gets one turn, plus a rotating slice of citizens. The run loop waits for the
// week's decisions before payroll and the weekly reviews, and a decision older than a week expires.
public static class SocietyScheduler
{
    public const int CitizenRotationWeeks = 8; // each citizen decides once every 8 weeks, about 12% a week

    public static Agent[] WeeklyCohort(World world)
    {
        var week = world.Tick / SocietyLabor.DaysPerWeek;
        return world.Agents.Values.Where(agent => SocietyLabor.DecidesForItself(agent)
                && (agent.Role != "citizen" || RotationSlot(world, agent) == week % CitizenRotationWeeks))
            .OrderBy(agent => agent.Role switch { "government" => 0, "citizen" => 2, _ => 1 }).ThenBy(agent => agent.Id)
            .ToArray();
    }

    public static int RotationSlot(World world, Agent agent) =>
        (int)Math.Round(SocietyFactory.Sample(world.Seed, agent.Id, "turn") * 255) % CitizenRotationWeeks;

    public static void UpdateMetrics(World world, IReadOnlySet<string> pending, IReadOnlyCollection<string> queued, DateTimeOffset now)
    {
        var society = world.Society!;
        society.PendingDecisions = pending.Count;
        society.MaxPendingObserved = Math.Max(society.MaxPendingObserved, pending.Count);
        society.DueAgents = queued.Count;
        society.OldestWaitTicks = queued.Count == 0 ? 0
            : queued.Max(id => world.Tick - Math.Max(0, world.Agents[id].LastAttemptTick));
        var elapsed = (now - society.StartedAt).TotalSeconds;
        society.RecentModelDecisionsPerSecond = elapsed > 0 ? society.ModelDecisionCount / elapsed : 0;
        if (society.CoverageCompletedAt is null && world.Agents.Values.Where(SocietyLabor.DecidesForItself).All(agent => agent.ModelDecisions > 0))
            society.CoverageCompletedAt = now;
    }
}
