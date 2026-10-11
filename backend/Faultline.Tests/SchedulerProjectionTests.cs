using Faultline.Core;

namespace Faultline.Tests;

public sealed class SchedulerProjectionTests
{
    [Fact]
    public void ScopedAdmissionKeepsCitizenCapacityAvailableWhenInstitutionsAreFull()
    {
        var world = SocietyFactory.Create("role_capacity", true, 1100);
        world.Society!.ComputeProviders = [
            new("leaders", "Qwen/Qwen3-8B", 2, "institution"),
            new("residents", "Qwen/Qwen3-4B", 4, "citizen")];
        var queue = world.Agents.Values.Where(SocietyLabor.DecidesForItself).Select(agent => agent.Id).ToArray();
        var first = SocietyScheduler.SelectQueued(world, queue, new HashSet<string>(), 10);
        Assert.Equal(2, first.Count(agent => agent.Role != "citizen"));
        Assert.Equal(4, first.Count(agent => agent.Role == "citizen"));
        var pending = first.Where(agent => agent.Role != "citizen").Select(agent => agent.Id).ToHashSet();
        var next = SocietyScheduler.SelectQueued(world, queue, pending, 8);
        Assert.Equal(4, next.Length);
        Assert.All(next, agent => Assert.Equal("citizen", agent.Role));
    }

    [Fact]
    public void SharedProviderSlotsAreCountedOnceAcrossBothGroups()
    {
        var world = SocietyFactory.Create("shared_capacity", true, 1100);
        world.Society!.ComputeProviders = [new("legacy", "shared", 3)];
        var queue = SocietyScheduler.WeeklyCohort(world).Select(agent => agent.Id).ToArray();
        var selected = SocietyScheduler.SelectQueued(world, queue, new HashSet<string>(), 10);
        Assert.Equal(3, selected.Length);
        Assert.Empty(SocietyScheduler.SelectQueued(world, queue, selected.Select(agent => agent.Id).ToHashSet(), 10));
    }

    [Fact]
    public void WeeklyQueueDrainsEveryDeciderWithoutGivingOwnedFirmsExtraTurns()
    {
        var world = SocietyFactory.Create("fair_turns", true, 1100);
        world.Society!.ComputeProviders = [new("leaders", "8b", 1, "institution"), new("residents", "4b", 2, "citizen")];
        var queue = SocietyScheduler.WeeklyCohort(world).Select(agent => agent.Id).ToList();
        foreach (var citizen in world.Agents.Values.Where(agent => agent.Role == "citizen"))
        {
            var slot = SocietyScheduler.RotationSlot(world, citizen);
            Assert.Equal(slot == 0 ? 1 : slot * SocietyLabor.DaysPerWeek, SocietyScheduler.NextTurnTick(world, citizen));
        }
        var expected = queue.ToHashSet();
        var completed = new HashSet<string>();
        while (queue.Count > 0)
        {
            var selected = SocietyScheduler.SelectQueued(world, queue, new HashSet<string>(), 3);
            Assert.NotEmpty(selected);
            foreach (var agent in selected)
            {
                Assert.True(completed.Add(agent.Id));
                queue.Remove(agent.Id);
                Assert.True(SocietyLabor.DecidesForItself(agent));
            }
        }
        Assert.True(expected.SetEquals(completed));
    }

    [Fact]
    public void PendingAndQueuedMetricsRemainDistinctAndDropClosedFirms()
    {
        var world = SocietyFactory.Create("pending_metrics", true, 1100);
        var resident = world.Agents.Values.First(agent => agent.Role == "citizen");
        var ownedFirm = world.Agents.Values.First(agent => agent.Role == "business" && agent.OwnerId is not null);
        var pending = new HashSet<string> { "aster_government", resident.Id };
        SocietyScheduler.UpdateMetrics(world, pending, ["aster_journalist", ownedFirm.Id, resident.Id], world.Society!.StartedAt);
        Assert.Equal(2, world.Society.MaxPendingObserved);
        Assert.Equal(1, world.Society.PendingByRole["citizen"]);
        Assert.Equal(1, world.Society.PendingByRole["government"]);
        Assert.Equal(["aster_journalist"], world.Society.QueuedAgentIds);
        Assert.Equal(1, world.Society.DueAgents);
        SocietyScheduler.UpdateMetrics(world, new HashSet<string>(), [], world.Society.StartedAt);
        Assert.Empty(world.Society.PendingByRole);
        Assert.Equal(2, world.Society.MaxPendingObserved);
    }

    [Fact]
    public void AgesUseCommittedTimeAndEligibilityUsesTheWeeklyQueue()
    {
        var world = SocietyFactory.Create("age_metrics", true, 1100);
        var started = world.Society!.StartedAt;
        world.UpdatedAt = started.AddSeconds(700);
        world.Tick = 10;
        var resident = world.Agents.Values.First(agent => agent.Role == "citizen");
        resident.LastModelDecisionAt = started.AddSeconds(40);
        resident.ModelDecisions = 1;
        SocietyScheduler.UpdateMetrics(world, new HashSet<string>(), [resident.Id], world.UpdatedAt);
        Assert.Equal(3, world.Society.OldestWaitTicks);
        Assert.Equal(world.Tick, SocietyScheduler.NextTurnTick(world, resident));
        Assert.Equal(660, SocietyProjection.ModelDecisionAgeSeconds(world, resident));
        var snapshot = Wire.Node(world.Snapshot());
        var citizen = snapshot["compute"]![0]!["by_role"]!.AsArray().Single(item => item!["role"]!.GetValue<string>() == "citizen")!;
        Assert.Equal(1, citizen["eligible_agents"]!.GetValue<int>());
        Assert.Equal(660, citizen["model_decision_age_p95_seconds"]!.GetValue<double>());
        Assert.Null(SocietyProjection.ModelDecisionAgeSeconds(world, world.Agents["aster_journalist"]));
        var next = SocietyScheduler.NextTurnTick(world, world.Agents["aster_journalist"]);
        Assert.Equal(14, next);
        Assert.Null(SocietyScheduler.NextTurnTick(world, world.Agents.Values.First(agent => agent.OwnerId is not null)));
    }
}
