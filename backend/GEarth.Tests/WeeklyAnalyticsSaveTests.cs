using GEarth.Api;
using GEarth.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace GEarth.Tests;

[Collection("Coordinator audit environment")]
public sealed class WeeklyAnalyticsSaveTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(14)]
    public async Task WeeklyNeonSavesRetainAllDaysDecisionsTransactionsAndEvents(int ticks)
    {
        using var fixture = new RunFixture(ticks);
        var backing = new LocalStore(fixture.Directory);
        var store = new RecordingNeonStore(backing);
        var notes = new RunNotesService(store, NullLogger<RunNotesService>.Instance);
        using var coordinator = new Coordinator(store, notes, NullLogger<Coordinator>.Instance);
        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(coordinator.Error);
        var final = Assert.IsType<World>(coordinator.Current);
        fixture.RunId = final.Id;
        Assert.Equal("completed", final.Status);
        var expectedTicks = new[] { 0 }.Concat(Enumerable.Range(1, ticks - 1)
            .Where(day => (day + 1) % SocietyLabor.DaysPerWeek == 0)).Append(ticks).ToArray();
        Assert.Equal(expectedTicks, store.SavedTicks);
        var persisted = (await backing.Load(final.Id, CancellationToken.None))!;
        var records = await ReadAll(backing, final.Id);
        Assert.Equal(final.AuditSequence, records.Count);
        Assert.Equal(Enumerable.Range(1, records.Count).Select(sequence => (long)sequence), records.Select(row => row.Sequence));
        Assert.Equal(ticks, persisted.AuditDailyThroughTick);
        var openingIds = records.Where(row => row.Kind == "opening_balance" && row.Payload["account_type"]!.GetValue<string>() == "agent")
            .Select(row => row.Payload["account_id"]!.GetValue<string>()).ToArray();
        for (var day = 1; day <= ticks; day++)
        {
            var ids = records.Where(row => row.Kind == "agent_day" && row.Tick == day
                && row.Payload["frame_kind"]!.GetValue<string>() == "day")
                .Select(row => row.Payload["agent_id"]!.GetValue<string>()).ToArray();
            Assert.Equal(ids.Length, ids.Distinct().Count());
            Assert.All(openingIds, id => Assert.Contains(id, ids));
            Assert.Equal(10, records.Count(row => row.Kind == "nation_day" && row.Tick == day
                && row.Payload["frame_kind"]!.GetValue<string>() == "day"));
        }
        Assert.Equal(final.Agents.Count, records.Count(row => row.Kind == "agent_day"
            && row.Payload["frame_kind"]!.GetValue<string>() == "end"));
        Assert.Contains(records, row => row.Kind == "transaction");
        Assert.Equal(records.Where(row => row.Kind == "decision_request").Select(row => row.Payload["decision_id"]!.GetValue<string>()).Order(),
            records.Where(row => row.Kind == "decision_result").Select(row => row.Payload["decision_id"]!.GetValue<string>()).Order());
        Assert.Equal(final.Seq, records.Count(row => row.Kind == "event"));
        Assert.Empty(store.RunningWorld!.PendingAudit);
        SocietyEconomy.AssertConservation(persisted);
    }

    [Fact]
    public async Task GracefulShutdownFlushesDaysBeforeTheFirstWeeklySave()
    {
        using var fixture = new RunFixture(14, interval: 40);
        using var cancellation = new CancellationTokenSource();
        var backing = new LocalStore(fixture.Directory);
        var store = new RecordingNeonStore(backing);
        var notes = new RunNotesService(store, NullLogger<RunNotesService>.Instance);
        using var coordinator = new Coordinator(store, notes, NullLogger<Coordinator>.Instance);
        await coordinator.StartAsync(cancellation.Token);
        var running = await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        while (running.Tick < 3 && !coordinator.ExecuteTask!.IsCompleted)
            await Task.Delay(1);
        cancellation.Cancel();
        await coordinator.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(coordinator.Error);
        var final = Assert.IsType<World>(coordinator.Current);
        fixture.RunId = final.Id;
        Assert.Equal("stopped", final.Status);
        Assert.InRange(final.Tick, 3, 5);
        Assert.Equal(new[] { 0, final.Tick }, store.SavedTicks);
        var records = await ReadAll(backing, final.Id);
        Assert.Equal(final.AuditSequence, records.Count);
        Assert.Equal(final.Tick, final.AuditDailyThroughTick);
        for (var day = 1; day <= final.Tick; day++)
            Assert.Equal(final.Agents.Count, records.Count(row => row.Kind == "agent_day" && row.Tick == day
                && row.Payload["frame_kind"]!.GetValue<string>() == "day"));
        Assert.Equal(final.Agents.Count, records.Count(row => row.Kind == "agent_day"
            && row.Payload["frame_kind"]!.GetValue<string>() == "end"));
        Assert.Contains(records, row => row.Kind == "event" && row.Payload["event_type"]!.GetValue<string>() == "run_stopped");
    }

    private static async Task<List<AuditRecord>> ReadAll(LocalStore store, string runId)
    {
        var records = new List<AuditRecord>();
        while (true)
        {
            var page = await store.ReadAudit(runId, records.Count == 0 ? 0 : records[^1].Sequence, 2000, CancellationToken.None);
            if (page.Length == 0)
                return records;
            records.AddRange(page);
        }
    }

    private sealed class RecordingNeonStore(LocalStore backing) : IWorldStore
    {
        public string Kind => "neon_https";
        public List<int> SavedTicks { get; } = [];
        public World? RunningWorld { get; private set; }
        public TaskCompletionSource<World> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Initialize(CancellationToken token) => backing.Initialize(token);
        public Task SaveNotes(RunNotes notes, CancellationToken token) => backing.SaveNotes(notes, token);
        public Task<RunNotes?> LoadNotes(string id, CancellationToken token) => backing.LoadNotes(id, token);
        public Task<World?> Load(string id, CancellationToken token) => backing.Load(id, token);
        public Task<string[]> List(CancellationToken token) => backing.List(token);
        public Task<WorldEvent[]> ReadEvents(string id, long after, int limit, CancellationToken token) => backing.ReadEvents(id, after, limit, token);
        public async Task Save(World world, long expectedRevision, CancellationToken token, string? serialized = null)
        {
            await backing.Save(world, expectedRevision, token, serialized);
            SavedTicks.Add(world.Tick);
            RunningWorld = world;
            Started.TrySetResult(world);
        }
    }

    private sealed class RunFixture : IDisposable
    {
        private readonly Dictionary<string, string?> previous;
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "weekly_audit_" + Guid.NewGuid().ToString("N"));
        public string? RunId { get; set; }
        public RunFixture(int ticks, int interval = 0)
        {
            var settings = new Dictionary<string, string?>
            {
                ["GEARTH_ANALYTICS"] = "true", ["GEARTH_WORLD"] = "true", ["GEARTH_LIVE"] = "false",
                ["GEARTH_TICKS"] = ticks.ToString(), ["GEARTH_POPULATION"] = "170", ["GEARTH_INTERVAL_MS"] = interval.ToString(),
                ["GEARTH_VERBOSE"] = "false", ["GEARTH_SAVED_RUN_ID"] = null,
                ["GEARTH_COMPUTE_PROVIDERS"] = null, ["GEARTH_RESUME_RUN_ID"] = null
            };
            previous = settings.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
            foreach (var (key, value) in settings)
                Environment.SetEnvironmentVariable(key, value);
        }
        public void Dispose()
        {
            foreach (var (key, value) in previous)
                Environment.SetEnvironmentVariable(key, value);
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, true);
            if (RunId is not null)
                foreach (var extension in new[] { ".json", ".md" })
                    File.Delete(Path.GetFullPath(Path.Combine(".data/run-notes", RunId + extension)));
        }
    }
}
