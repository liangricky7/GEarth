namespace Faultline.Core;

// Decisions follow the weekly rhythm of the economy: every week each government, entrepreneur, bank,
// newsroom and state firm gets one turn, plus a rotating slice of citizens; a planned economy's planner
// sets the plan every four weeks. The run loop waits for the
// week's decisions before payroll and the weekly reviews, and a decision older than a week expires.
public static class SocietyScheduler
{
    public const int CitizenRotationWeeks = 8; // each citizen decides once every 8 weeks, about 12% a week

    public static Agent[] WeeklyCohort(World world)
    {
        var week = world.Tick / SocietyLabor.DaysPerWeek;
        return world.Agents.Values.Where(agent => SocietyLabor.DecidesForItself(agent)
                && (agent.Role != "citizen" || RotationSlot(world, agent) == week % CitizenRotationWeeks)
                && (agent.Role != "planner" || week % SocietyPlanning.PlanningWeeks == 0))
            .OrderBy(agent => agent.Role switch { "government" => 0, "citizen" => 2, _ => 1 }).ThenBy(agent => agent.Id)
            .ToArray();
    }

    public static int RotationSlot(World world, Agent agent) =>
        (int)Math.Round(SocietyFactory.Sample(world.Seed, agent.Id, "turn") * 255) % CitizenRotationWeeks;

    public static int? NextTurnTick(World world, Agent agent)
    {
        if (!SocietyLabor.DecidesForItself(agent))
            return null;
        if (world.Society!.QueuedAgentIds.Contains(agent.Id))
            return world.Tick;
        var week = world.Tick == 0 ? 0 : world.Tick / SocietyLabor.DaysPerWeek + 1;
        while (agent.Role == "citizen" && week % CitizenRotationWeeks != RotationSlot(world, agent)
               || agent.Role == "planner" && week % SocietyPlanning.PlanningWeeks != 0)
            week++;
        return week == 0 ? 1 : week * SocietyLabor.DaysPerWeek;
    }

    public static Agent[] SelectQueued(World world, IReadOnlyCollection<string> queued, IReadOnlySet<string> pending, int slots)
    {
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
        var selected = new List<Agent>();
        foreach (var id in queued.Distinct())
        {
            if (selected.Count >= slots)
                break;
            if (pending.Contains(id) || !world.Agents.TryGetValue(id, out var agent) || !SocietyLabor.DecidesForItself(agent))
                continue;
            if (agent.Role == "citizen" && residentSlots > 0)
                residentSlots--;
            else if (agent.Role != "citizen" && institutionSlots > 0)
                institutionSlots--;
            else if (sharedSlots > 0)
                sharedSlots--;
            else
                continue;
            selected.Add(agent);
        }
        return selected.ToArray();
    }

    public static void UpdateMetrics(World world, IReadOnlySet<string> pending, IReadOnlyCollection<string> queued, DateTimeOffset now)
    {
        var society = world.Society!;
        society.PendingDecisions = pending.Count;
        society.PendingByRole = world.Agents.Values.Where(agent => pending.Contains(agent.Id))
            .GroupBy(agent => agent.Role).ToDictionary(group => group.Key, group => group.Count());
        society.MaxPendingObserved = Math.Max(society.MaxPendingObserved, pending.Count);
        society.QueuedAgentIds = queued.Distinct().Where(id => world.Agents.TryGetValue(id, out var agent)
            && SocietyLabor.DecidesForItself(agent) && !pending.Contains(id)).ToArray();
        society.DueAgents = society.QueuedAgentIds.Length;
        var cohortTick = Math.Max(1, world.Tick / SocietyLabor.DaysPerWeek * SocietyLabor.DaysPerWeek);
        society.OldestWaitTicks = society.QueuedAgentIds.Length == 0 ? 0 : Math.Max(0, world.Tick - cohortTick);
        var elapsed = (now - society.StartedAt).TotalSeconds;
        society.RecentModelDecisionsPerSecond = elapsed > 0 ? society.ModelDecisionCount / elapsed : 0;
        if (society.CoverageCompletedAt is null && world.Agents.Values.Where(SocietyLabor.DecidesForItself).All(agent => agent.ModelDecisions > 0))
            society.CoverageCompletedAt = now;
    }
}
