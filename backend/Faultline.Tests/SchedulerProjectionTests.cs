using Faultline.Core;

namespace Faultline.Tests;

public sealed class SchedulerProjectionTests
{
    [Fact]
    public void ResidentTurnsCoverEveryResidentDespiteRepeatedInstitutionBacklog()
    {
        var world = SocietyFactory.Create("fair_turns", true, 100);
        var now = world.Society!.StartedAt;
        var residents = new HashSet<string>();
        for (var turn = 0; turn < 100; turn++)
        {
            var agent = Assert.Single(SocietyScheduler.Select(world, new HashSet<string>(), 1, now));
            if (agent.Role == "citizen")
            {
                Assert.True(residents.Add(agent.Id));
                agent.LastAttemptAt = now;
            }
        }
        Assert.Equal(world.Agents.Values.Count(agent => agent.Role == "citizen"), residents.Count);
    }

    [Fact]
    public void PendingCountsAreMeasuredBeforeFastRequestsComplete()
    {
        var world = SocietyFactory.Create("pending_metrics", true, 100);
        var pending = new HashSet<string> { "aster_government", "aster_citizen_00001" };
        SocietyScheduler.UpdateMetrics(world, pending, world.Society!.StartedAt);
        Assert.Equal(2, world.Society.MaxPendingObserved);
        Assert.Equal(1, world.Society.PendingByRole["citizen"]);
        Assert.Equal(1, world.Society.PendingByRole["government"]);
        SocietyScheduler.UpdateMetrics(world, new HashSet<string>(), world.Society.StartedAt);
        Assert.Equal(0, world.Society.PendingDecisions);
        Assert.Empty(world.Society.PendingByRole);
        Assert.Equal(2, world.Society.MaxPendingObserved);
    }

    [Fact]
    public void AgesAndEligibilityUseCommittedTimeAndExcludeAgentsWithoutModelDecisions()
    {
        var world = SocietyFactory.Create("age_metrics", true, 100);
        var started = world.Society!.StartedAt;
        world.UpdatedAt = started.AddSeconds(700);
        var resident = world.Agents["aster_citizen_00001"];
        resident.LastAttemptAt = started;
        resident.LastModelDecisionAt = started.AddSeconds(40);
        resident.ModelDecisions = 1;
        Assert.Equal(started.AddSeconds(600), SocietyScheduler.DueAt(world, resident));
        Assert.Equal(660, SocietyProjection.ModelDecisionAgeSeconds(world, resident));
        var snapshot = Wire.Node(world.Snapshot());
        var citizen = snapshot["compute"]![0]!["by_role"]!.AsArray()
            .Single(item => item!["role"]!.GetValue<string>() == "citizen")!;
        Assert.Equal(1, citizen["unique_model_deciding"]!.GetValue<int>());
        Assert.Equal(660, citizen["model_decision_age_p95_seconds"]!.GetValue<double>());
        Assert.Null(SocietyProjection.ModelDecisionAgeSeconds(world, world.Agents["aster_bank"]));
        Assert.Equal(700, snapshot["compute"]![0]!["oldest_eligible_wait_seconds"]!.GetValue<double>());
        Assert.Equal(Wire.Node(world.Snapshot()).ToJsonString(), snapshot.ToJsonString());
    }
}
