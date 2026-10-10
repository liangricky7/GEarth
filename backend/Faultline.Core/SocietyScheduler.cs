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
        var providers = world.Society!.ComputeProviders;
        var residentCapacity = providers.Length == 0 ? int.MaxValue
            : providers.Where(provider => provider.DecisionGroup == "citizen").Sum(provider => provider.Capacity);
        var institutionCapacity = providers.Length == 0 ? int.MaxValue
            : providers.Where(provider => provider.DecisionGroup == "institution").Sum(provider => provider.Capacity);
        var pendingResidents = pending.Count(id => world.Agents.TryGetValue(id, out var agent) && agent.Role == "citizen");
        var pendingInstitutions = pending.Count - pendingResidents;
        var residentSlots = Math.Max(0, residentCapacity - pendingResidents);
        var institutionSlots = Math.Max(0, institutionCapacity - pendingInstitutions);
        var sharedSlots = Math.Max(0, providers.Where(provider => provider.DecisionGroup == "all").Sum(provider => provider.Capacity)
            - Math.Max(0, pendingResidents - residentCapacity) - Math.Max(0, pendingInstitutions - institutionCapacity));

        bool HasInstitution() => institutionIndex < institutions.Length && (institutionSlots > 0 || sharedSlots > 0);
        bool HasResident() => residentIndex < residents.Length && (residentSlots > 0 || sharedSlots > 0);

        while (selected.Count < slots && (HasInstitution() || HasResident()))
        {
            var institutionalTurn = world.SchedulerCursor++ % 10 < 3;
            if (HasInstitution() && (institutionalTurn || !HasResident()))
            {
                selected.Add(institutions[institutionIndex++]);
                if (institutionSlots > 0)
                    institutionSlots--;
                else
                    sharedSlots--;
            }
            else
            {
                selected.Add(residents[residentIndex++]);
                if (residentSlots > 0)
                    residentSlots--;
                else
                    sharedSlots--;
            }
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
