using System.Text.Json.Nodes;
using Faultline.Core;

namespace Faultline.Tests;

public sealed class RunNotesTests
{
    private static World Create() => SocietyFactory.Create("notes_test", true, 100);

    [Fact]
    public void NotesSeparateAcceptedBehaviorFromFallbacksAndRejections()
    {
        var world = Create();
        var now = DateTimeOffset.UtcNow;
        var agent = "aster_citizen_00001";
        var job = SocietyEngine.Observe(world, agent, now);
        var choice = job.Candidates.First(item => item.ActionType == "work");
        var action = new ActionProposal(choice.ActionType, (JsonObject)choice.Params.DeepClone(), "Protect my savings.", []);
        SocietyEngine.Apply(world, job, new(job.RequestId, action, "model", "test", 1), now);
        var fallback = SocietyEngine.Observe(world, agent, now);
        SocietyEngine.Apply(world, fallback, new(fallback.RequestId, new("hold", new JsonObject(), "Unavailable.", []),
            "rule_fallback", "none", 1), now);
        var expired = SocietyEngine.Observe(world, agent, now);
        SocietyEngine.Apply(world, expired, new(expired.RequestId, action, "model", "test", 1), now.AddSeconds(66));
        world.Status = "stopped";
        var notes = RunAnalysis.Create(world, world.Events);
        Assert.Equal("stopped", notes.Status);
        Assert.Equal(1, notes.ModelCoverage);
        Assert.Equal(1, notes.AcceptedModelDecisions);
        Assert.Equal(1, notes.Fallbacks);
        Assert.Equal(1, notes.Rejections);
        Assert.Single(notes.Behaviors);
        Assert.Equal("model", notes.Behaviors[0].Source);
        Assert.Equal(1, notes.Behaviors[0].Count);
        Assert.Contains(notes.Observations, item => item.Category == "reliability" && item.Text.Contains("EXPIRED_PROPOSAL"));
        Assert.All(notes.Observations.SelectMany(item => item.EvidenceIds), id => Assert.Contains(world.Events, item => item.EventId == id));
        Assert.True(notes.HistoryComplete);
    }

    [Fact]
    public void NotesDescribePhysicalConsequencesAndDoNotClaimGovernmentCausality()
    {
        var world = Create();
        SocietyTrade.Dispatch(world, world.Agents["aster_food"], world.Agents["beryl_food"], "food", 5, "fastest", world.Events[0].EventId);
        for (var tick = 0; tick < 120; tick++)
            SocietyEngine.Advance(world);
        var notes = RunAnalysis.Create(world, world.Events);
        Assert.Contains(notes.Observations, item => item.Category == "trade" && item.Text.Contains("1 arrived"));
        Assert.Contains(notes.Observations, item => item.Category == "crisis" && item.Text.Contains("scripted food"));
        Assert.Equal(10, notes.Countries.Length);
        Assert.Contains(notes.Limits, item => item.Contains("not causal conclusions"));
        Assert.Contains("## Country outcomes", notes.Markdown);
        Assert.Contains(world.Events.First(item => item.EventType == "shipment_delivered").EventId, notes.Markdown);
    }

    [Fact]
    public void MissingHistoryIsExplicitInsteadOfClaimingACompleteAnalysis()
    {
        var world = Create();
        for (var tick = 0; tick < 10; tick++)
            SocietyEngine.Advance(world);
        var notes = RunAnalysis.Create(world, world.Events.TakeLast(2));
        Assert.False(notes.HistoryComplete);
        Assert.Contains(notes.Limits, item => item.Contains("Event history is incomplete"));
        Assert.Equal(world.Seq, notes.ThroughSequence);
    }

    [Fact]
    public async Task RunNotesPersistAndRestoreWithTheCheckpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalStore(directory);
            await store.Initialize(CancellationToken.None);
            var world = Create();
            world.Revision = 1;
            await store.Save(world, 0, CancellationToken.None);
            var notes = RunAnalysis.Create(world, world.Events);
            await store.SaveNotes(notes, CancellationToken.None);
            var restored = await store.LoadNotes(world.Id, CancellationToken.None);
            Assert.Equal(notes.Markdown, restored!.Markdown);
            Assert.Equal(notes.ThroughSequence, restored.ThroughSequence);
            Assert.Equal(notes.GeneratedAt, restored.GeneratedAt);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
