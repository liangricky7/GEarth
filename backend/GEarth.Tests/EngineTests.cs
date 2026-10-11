using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Tests;

public sealed class EngineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static World CreateWorld()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GEarth.sln")))
            directory = directory.Parent;

        var scenarioPath = Path.Combine(directory!.FullName, "docs/gearth/scenario.demo.json");
        return Engine.Create(File.ReadAllText(scenarioPath), "test_run", true);
    }

    private static DecisionResult Purchase(DecisionJob job)
    {
        var candidate = job.Candidates.First(item => item.ActionType == "purchase");
        var action = new ActionProposal("purchase", (JsonObject)candidate.Params.DeepClone(),
            "Buy supplies for my household.", [job.EvidenceIds[0]]);

        return new DecisionResult(job.RequestId, action, "model", "test-model", 1);
    }

    [Fact]
    public void AgentProfilesAreDistinctAndReproducible()
    {
        var first = CreateWorld();
        var second = CreateWorld();

        Assert.Equal(50, first.Agents.Count);
        Assert.True(JsonNode.DeepEquals(Wire.Node(first.Agents), Wire.Node(second.Agents)));
        Assert.False(JsonNode.DeepEquals(Wire.Node(first.Agents["aster_citizen_001"].Traits),
            Wire.Node(first.Agents["aster_citizen_002"].Traits)));
        Assert.All(first.Agents.Values.SelectMany(agent => agent.Traits.Values), value => Assert.InRange(value, 0, 1));
    }

    [Fact]
    public void PurchaseConservesCashAndTransfersInventory()
    {
        var world = CreateWorld();
        var job = Engine.Observe(world, "aster_citizen_001", Now);
        var buyer = world.Agents[job.AgentId];
        var cashBefore = world.Agents.Values.Sum(agent => agent.CashMinor);

        var record = Engine.Apply(world, job, Purchase(job), Now);

        Assert.True(record.Accepted);
        Assert.Equal(2, buyer.Inventory["food"]);
        Assert.Equal(1900, buyer.CashMinor);
        Assert.Equal(98, world.Agents["aster_business_1"].Inventory["food"]);
        Assert.Equal(cashBefore, world.Agents.Values.Sum(agent => agent.CashMinor));
        Assert.All(world.Agents.Values, agent => Assert.True(agent.CashMinor >= 0));
    }

    [Fact]
    public void RepeatedRequestCannotSpendTwice()
    {
        var world = CreateWorld();
        var job = Engine.Observe(world, "aster_citizen_001", Now);
        var result = Purchase(job);

        Engine.Apply(world, job, result, Now);
        var sequence = world.Seq;
        var balance = world.Agents[job.AgentId].CashMinor;
        Engine.Apply(world, job, result, Now);

        Assert.Equal(sequence, world.Seq);
        Assert.Equal(balance, world.Agents[job.AgentId].CashMinor);
        Assert.Single(world.Decisions);
    }

    [Fact]
    public void ConflictingObservationCannotSpendChangedInventory()
    {
        var world = CreateWorld();
        var first = Engine.Observe(world, "aster_citizen_001", Now);
        var second = Engine.Observe(world, "aster_citizen_002", Now);

        Engine.Apply(world, first, Purchase(first), Now);
        var record = Engine.Apply(world, second, Purchase(second), Now);

        Assert.Equal("STALE_DEPENDENCY", record.Code);
        Assert.Equal(2000, world.Agents[second.AgentId].CashMinor);
    }

    [Fact]
    public void UnrelatedCountryMutationDoesNotInvalidatePurchase()
    {
        var world = CreateWorld();
        var first = Engine.Observe(world, "aster_citizen_001", Now);
        var second = Engine.Observe(world, "beryl_citizen_001", Now);

        Engine.Apply(world, first, Purchase(first), Now);
        var record = Engine.Apply(world, second, Purchase(second), Now);

        Assert.True(record.Accepted);
    }

    [Fact]
    public void InsufficientFundsCannotCreateNegativeBalance()
    {
        var world = CreateWorld();
        world.Agents["aster_citizen_001"].CashMinor = 1;
        var job = Engine.Observe(world, "aster_citizen_001", Now);

        var record = Engine.Apply(world, job, Purchase(job), Now);

        Assert.Equal("INSUFFICIENT_FUNDS", record.Code);
        Assert.Equal(1, world.Agents[job.AgentId].CashMinor);
    }

    [Fact]
    public void CitizensCannotPublishEvenWithForgedCandidate()
    {
        var world = CreateWorld();
        var job = Engine.Observe(world, "aster_citizen_001", Now);
        var action = new ActionProposal("publish", new JsonObject
        {
            ["text"] = "A report.",
            ["claim_event_id"] = null
        }, "Publish an update.", []);

        var record = Engine.Apply(world, job, new(job.RequestId, action, "model", "test-model", 0), Now);

        Assert.Equal("ROLE_FORBIDDEN", record.Code);
    }

    [Fact]
    public void UnknownEvidenceIsRejected()
    {
        var world = CreateWorld();
        var job = Engine.Observe(world, "aster_citizen_001", Now);
        var result = Purchase(job);
        result = result with { Action = result.Action with { EvidenceIds = ["invented_event"] } };

        var record = Engine.Apply(world, job, result, Now);

        Assert.Equal("UNKNOWN_EVIDENCE", record.Code);
    }

    [Fact]
    public void ExpiredDecisionCannotChangeState()
    {
        var world = CreateWorld();
        var job = Engine.Observe(world, "aster_citizen_001", Now);

        var record = Engine.Apply(world, job, Purchase(job), Now.AddSeconds(16));

        Assert.Equal("EXPIRED_PROPOSAL", record.Code);
        Assert.Equal(0, world.Agents[job.AgentId].Inventory["food"]);
    }

    [Fact]
    public void BorderClosureChangesFastestAndCheapestPaths()
    {
        var world = CreateWorld();
        Assert.Equal(8, Engine.FindPath(world, "fastest")!.TravelTicks);
        Assert.Equal(500, Engine.FindPath(world, "fastest")!.CostMinor);

        for (var tick = 0; tick < 8; tick++)
            Engine.Advance(world);

        var fastest = Engine.FindPath(world, "fastest")!;
        var cheapest = Engine.FindPath(world, "cheapest")!;

        Assert.Equal(12, fastest.TravelTicks);
        Assert.Equal(1700, fastest.CostMinor);
        Assert.Equal(14, cheapest.TravelTicks);
        Assert.Equal(600, cheapest.CostMinor);
        Assert.DoesNotContain("r_direct", fastest.EdgeIds);
    }

    [Fact]
    public void DisconnectedGraphReturnsNoRoute()
    {
        var world = CreateWorld();
        world.Routes["r_hospital"].Open = false;

        Assert.Null(Engine.FindPath(world, "fastest"));
    }

    [Fact]
    public async Task CheckpointRestoresDecisionsAndRejectsCompetingWriter()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var store = new LocalStore(directory);
            await store.Initialize(CancellationToken.None);
            var world = CreateWorld();
            world.Revision = 1;
            var job = Engine.Observe(world, "aster_citizen_001", Now);
            Engine.Apply(world, job, Purchase(job), Now);

            await store.Save(world, 0, CancellationToken.None);
            var restored = await store.Load(world.Id, CancellationToken.None);
            Assert.True(JsonNode.DeepEquals(Wire.Node(world), Wire.Node(restored!)));

            await store.Save(world, 0, CancellationToken.None);
            var stale = Wire.Copy(world);
            stale.Tick++;
            stale.Revision = 2;
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.Save(stale, 0, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
