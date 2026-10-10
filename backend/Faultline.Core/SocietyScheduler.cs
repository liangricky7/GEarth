namespace Faultline.Core;

public static class SocietyScheduler
{
    public static DateTimeOffset DueAt(World world, Agent agent)
    {
        var seconds = agent.Role == "citizen" ? world.Society!.ResidentIntervalSeconds
            : world.Society!.InstitutionIntervalSeconds;
        if (world.Mode == "LIVE" && agent.LastDecision is { } decision
            && (!decision.Accepted || decision.Result.Source == "rule_fallback"))
            seconds = Math.Min(seconds, 30);
        return agent.LastAttemptAt?.AddSeconds(seconds) ?? world.Society!.StartedAt;
    }

    public static bool IsDue(World world, Agent agent, DateTimeOffset now) => now >= DueAt(world, agent);

    public static Agent[] Select(World world, IReadOnlySet<string> pending, int slots, DateTimeOffset now)
    {
        if (slots <= 0)
            return [];
        var eligible = world.Agents.Values.Where(agent => !pending.Contains(agent.Id) && IsDue(world, agent, now)).ToArray();
        var institutions = eligible.Where(agent => agent.Role != "citizen")
            .OrderBy(agent => Priority(world, agent)).ThenBy(agent => agent.LastAttemptAt)
            .ThenBy(agent => agent.Role == "government" ? 0 : 1).ThenBy(agent => agent.Id).ToArray();
        var residents = eligible.Where(agent => agent.Role == "citizen")
            .OrderBy(agent => Priority(world, agent)).ThenBy(agent => agent.LastAttemptAt)
            .ThenBy(agent => agent.Id.Split('_').Last()).ThenBy(agent => agent.CountryId).ToArray();
        var selected = new List<Agent>();
        var institutionIndex = 0;
        var residentIndex = 0;
        while (selected.Count < slots && (institutionIndex < institutions.Length || residentIndex < residents.Length))
        {
            var institutionalTurn = world.SchedulerCursor++ % 10 < 3;
            if (institutionIndex < institutions.Length && (institutionalTurn || residentIndex >= residents.Length))
                selected.Add(institutions[institutionIndex++]);
            else
                selected.Add(residents[residentIndex++]);
        }
        return selected.ToArray();
    }

    private static int Priority(World world, Agent agent) => agent.LastAttemptAt is null ? 0
        : world.Mode == "LIVE" && agent.ModelDecisions == 0 ? 1 : 2;

    public static void UpdateMetrics(World world, IReadOnlySet<string> pending, DateTimeOffset now)
    {
        var society = world.Society!;
        var due = world.Agents.Values.Where(agent => !pending.Contains(agent.Id) && IsDue(world, agent, now)).ToArray();
        society.PendingDecisions = pending.Count;
        society.PendingByRole = world.Agents.Values.Where(agent => pending.Contains(agent.Id))
            .GroupBy(agent => agent.Role).ToDictionary(group => group.Key, group => group.Count());
        society.MaxPendingObserved = Math.Max(society.MaxPendingObserved, pending.Count);
        society.DueAgents = due.Length;
        society.OldestWaitTicks = due.Length == 0 ? 0 : due.Max(agent => world.Tick - Math.Max(0, agent.LastAttemptTick));
        var elapsed = (now - society.StartedAt).TotalSeconds;
        society.RecentModelDecisionsPerSecond = elapsed > 0 ? society.ModelDecisionCount / elapsed : 0;
        if (society.CoverageCompletedAt is null && world.Agents.Values.All(agent => agent.ModelDecisions > 0))
            society.CoverageCompletedAt = now;
    }
}
