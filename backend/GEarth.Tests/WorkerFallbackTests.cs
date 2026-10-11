using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Tests;

public sealed class WorkerFallbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FallbackDoesNotIncreaseModelCoverageOrRefreshDecisionAge(bool alreadyCovered)
    {
        var world = SocietyFactory.Create("worker_fallback", true, 400);
        var now = DateTimeOffset.UtcNow;
        var agent = world.Agents["aster_citizen_00001"];

        DecisionResult Hold(DecisionJob job, string source, string? error = null) =>
            new(job.RequestId, new("hold", new JsonObject(), "Test hold.", []),
                source, "test-model", 20, error);

        if (alreadyCovered)
        {
            var first = SocietyEngine.Observe(world, agent.Id, now);
            Assert.True(SocietyEngine.Apply(world, first, Hold(first, "model"), now).Accepted);
        }

        var previousModelCount = agent.ModelDecisions;
        var previousModelTime = agent.LastModelDecisionAt;
        var job = SocietyEngine.Observe(world, agent.Id, now.AddSeconds(1));
        var result = Hold(job, "rule_fallback", "TimeoutError");
        var record = SocietyEngine.Apply(world, job, result, now.AddSeconds(2));

        Assert.True(record.Accepted);
        Assert.Equal("rule_fallback", record.Result.Source);
        Assert.Equal("TimeoutError", record.Result.Error);
        Assert.Equal(1, world.Society!.FallbackDecisionCount);
        Assert.Equal(previousModelCount, world.Society.ModelDecisionCount);
        Assert.Equal(0, world.Society.RuleDecisionCount);
        Assert.Equal(previousModelCount, agent.ModelDecisions);
        Assert.Equal(previousModelTime, agent.LastModelDecisionAt);
        var snapshot = Wire.Node(world.Snapshot());
        Assert.Equal(alreadyCovered ? 1 : 0,
            snapshot["agents_summary"]!["unique_model_deciding"]!.GetValue<int>());
        Assert.Single(world.Events, item => item.EventType == "action_accepted"
            && item.Source == "rule_fallback");

        // Re-reading the same result must not count the fallback twice.
        Assert.Same(record, SocietyEngine.Apply(world, job, result, now.AddSeconds(3)));
        Assert.Equal(1, world.Society.FallbackDecisionCount);

        // A later valid model decision still applies and advances real coverage.
        SocietyEngine.Advance(world);
        var next = SocietyEngine.Observe(world, agent.Id, now.AddSeconds(4));
        Assert.True(SocietyEngine.Apply(world, next, Hold(next, "model"), now.AddSeconds(5)).Accepted);
        Assert.Equal(previousModelCount + 1, world.Society.ModelDecisionCount);
        Assert.Equal(previousModelCount + 1, agent.ModelDecisions);
        Assert.Equal(now.AddSeconds(5), agent.LastModelDecisionAt);
        Assert.Equal(1, world.Society.FallbackDecisionCount);
        SocietyEconomy.AssertConservation(world);
    }
}
