using System.Text.Json;
using GEarth.Api;
using GEarth.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace GEarth.Tests;

public sealed class RendererReplayTests
{
    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public async Task AFailedCandidateCannotLeakIntoTheWinningRevision(int winningTick)
    {
        var directory = Path.Combine(Path.GetTempPath(), "renderer_winner_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalStore(directory);
            await store.Initialize(CancellationToken.None);
            var original = SocietyFactory.Create("renderer_winner", false, 200);
            original.RendererCaptureEnabled = true;
            original.Revision = 1;
            await store.Save(original, 0, CancellationToken.None);
            var loser = Wire.Copy(original);
            for (var day = 0; day < 7; day++) SocietyEngine.Advance(loser);
            loser.Emit("losing_candidate", "rule", [], new { });
            loser.Revision = 2;
            var block = Path.Combine(directory, original.Id + ".json.tmp");
            Directory.CreateDirectory(block);
            Assert.True(await Record.ExceptionAsync(() => store.Save(loser, 1, CancellationToken.None)) is IOException or UnauthorizedAccessException);
            Directory.Delete(block);
            var winner = Wire.Copy(original);
            for (var day = 0; day < winningTick; day++) SocietyEngine.Advance(winner);
            winner.Emit("winning_candidate", "rule", [], new { });
            winner.Revision = 2;
            await store.Save(winner, 1, CancellationToken.None);
            var frame = await store.LoadRendererFrame(original.Id, 2, CancellationToken.None);
            if (winningTick == 6) Assert.Null(frame);
            else
            {
                Assert.Contains(frame!.Events, item => item.EventType == "winning_candidate");
                Assert.DoesNotContain(frame.Events, item => item.EventType == "losing_candidate");
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.Save(loser, 1, CancellationToken.None));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task WeeklyStatesSurviveLaterChangesAndLocalRestartWithoutAnalytics()
    {
        var directory = Path.Combine(Path.GetTempPath(), "renderer_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalStore(directory);
            await store.Initialize(CancellationToken.None);
            var world = SocietyFactory.Create("renderer_history", false, 200);
            world.RendererCaptureEnabled = true;
            async Task Save()
            {
                var before = world.Revision++;
                await store.Save(world, before, CancellationToken.None);
            }
            await Save();
            for (var day = 1; day <= 15; day++)
            {
                SocietyEngine.Advance(world);
                if (day % 7 == 0 || day == 6) await Save();
            }
            world.Status = "completed";
            await Save();
            var restarted = new LocalStore(directory);
            var frames = await restarted.ReadRendererFrames(world.Id, 0, 100, CancellationToken.None);
            Assert.Equal([0, 7, 14, 15], frames.Select(frame => frame.Tick));
            Assert.Equal(["opening", "week", "week", "terminal"], frames.Select(frame => frame.FrameKind));
            Assert.Equal([0, 1, 2, 3], frames.Select(frame => frame.Week));
            var firstWeek = (await restarted.LoadRendererFrame(world.Id, frames[1].Revision, CancellationToken.None))!;
            Assert.False(firstWeek.AnalyticsEnabled);
            var frame = RendererReplay.Frame(firstWeek);
            Assert.Equal(RendererReplay.Contract, frame["schema_version"]!.GetValue<string>());
            Assert.Equal(firstWeek.Agents.Count, frame["snapshot"]!["agents"]!.AsArray().Count);
            Assert.Equal(10, frame["snapshot"]!["banks"]!.AsArray().Count);
            Assert.All(frame["snapshot"]!["agents"]!.AsArray(), agent =>
            {
                Assert.NotNull(agent!["deposit_minor"]);
                Assert.NotNull(agent["inventory"]!["ore"]);
                Assert.NotNull(agent["inventory"]!["fuel"]);
            });
            Assert.Equal(7, firstWeek.Tick);
            Assert.Equal(15, (await restarted.Load(world.Id, CancellationToken.None))!.Tick);
            Assert.Null(await restarted.LoadRendererFrame(world.Id, frames[1].Revision - 1, CancellationToken.None));
            SocietyEconomy.AssertConservation(firstWeek);
            var paged = await restarted.ReadRendererFrames(world.Id, frames[1].Revision, 1, CancellationToken.None);
            Assert.Equal(14, Assert.Single(paged).Tick);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task StagedFrameIsInvisibleUntilCheckpointCommitsAndRetryIsIdempotent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "renderer_retry_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalStore(directory);
            await store.Initialize(CancellationToken.None);
            var world = SocietyFactory.Create("renderer_retry", false, 200);
            world.RendererCaptureEnabled = true;
            world.Revision = 1;
            await store.Save(world, 0, CancellationToken.None);
            for (var day = 0; day < 7; day++) SocietyEngine.Advance(world);
            world.Revision = 2;
            var block = Path.Combine(directory, world.Id + ".json.tmp");
            Directory.CreateDirectory(block);
            Assert.True(await Record.ExceptionAsync(() => store.Save(world, 1, CancellationToken.None)) is IOException or UnauthorizedAccessException);
            Assert.Single(await store.ReadRendererFrames(world.Id, 0, 100, CancellationToken.None));
            Assert.Null(await store.LoadRendererFrame(world.Id, 2, CancellationToken.None));
            Directory.Delete(block);
            await store.Save(world, 1, CancellationToken.None);
            await store.Save(world, 1, CancellationToken.None);
            Assert.Equal(2, (await store.ReadRendererFrames(world.Id, 0, 100, CancellationToken.None)).Length);
            var path = Path.Combine(directory, world.Id + ".json.renderer", "000000000002.json.gz");
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await Assert.ThrowsAnyAsync<InvalidDataException>(() => store.LoadRendererFrame(world.Id, 2, CancellationToken.None));
        }
        finally { Directory.Delete(directory, true); }
    }
}

[Collection("Coordinator audit environment")]
public sealed class RendererCoordinatorTests
{
    [Fact]
    public async Task OrdinaryNewRunsCaptureOpeningWeeksAndPartialEndByDefault()
    {
        var settings = new Dictionary<string, string?>
        {
            ["GEARTH_ANALYTICS"] = "false", ["GEARTH_WORLD"] = "true", ["GEARTH_LIVE"] = "false",
            ["GEARTH_TICKS"] = "15", ["GEARTH_POPULATION"] = "200", ["GEARTH_INTERVAL_MS"] = "0",
            ["GEARTH_VERBOSE"] = "false", ["GEARTH_SAVED_RUN_ID"] = null,
            ["GEARTH_COMPUTE_PROVIDERS"] = null, ["GEARTH_RESUME_RUN_ID"] = null,
            ["GEARTH_CONTROL_DIRECTORY"] = null
        };
        var previous = settings.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var directory = Path.Combine(Path.GetTempPath(), "renderer_coordinator_" + Guid.NewGuid().ToString("N"));
        string? runId = null;
        try
        {
            foreach (var (key, value) in settings) Environment.SetEnvironmentVariable(key, value);
            var store = new LocalStore(directory);
            var notes = new RunNotesService(store, NullLogger<RunNotesService>.Instance);
            using var coordinator = new Coordinator(store, notes, NullLogger<Coordinator>.Instance);
            await coordinator.StartAsync(CancellationToken.None);
            await coordinator.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Null(coordinator.Error);
            var final = Assert.IsType<World>(coordinator.Current);
            runId = final.Id;
            Assert.True(final.RendererCaptureEnabled);
            var frames = await store.ReadRendererFrames(runId, 0, 100, CancellationToken.None);
            Assert.Equal([0, 7, 14, 15], frames.Select(frame => frame.Tick));
            Assert.Equal("completed", final.Status);
            Assert.False(final.AnalyticsEnabled);
            foreach (var head in frames)
            {
                var state = (await store.LoadRendererFrame(runId, head.Revision, CancellationToken.None))!;
                SocietyEconomy.AssertConservation(state);
            }
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            if (runId is not null)
                foreach (var extension in new[] { ".json", ".md" })
                    File.Delete(Path.GetFullPath(Path.Combine(".data/run-notes", runId + extension)));
        }
    }
}
