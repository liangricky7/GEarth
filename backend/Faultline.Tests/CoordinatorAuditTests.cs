using Faultline.Api;
using Faultline.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Faultline.Tests;

[CollectionDefinition("Coordinator audit environment", DisableParallelization = true)]
public sealed class CoordinatorAuditCollection;

[Collection("Coordinator audit environment")]
public sealed class CoordinatorAuditTests
{
    [Fact]
    public async Task CancellationAfterCommitResponseWasLostPreservesStoppedFinalData()
    {
        var settings = new Dictionary<string, string?>
        {
            ["FAULTLINE_ANALYTICS"] = "true", ["FAULTLINE_WORLD"] = "true", ["FAULTLINE_LIVE"] = "false",
            ["FAULTLINE_TICKS"] = "3", ["FAULTLINE_POPULATION"] = "160", ["FAULTLINE_INTERVAL_MS"] = "0",
            ["FAULTLINE_VERBOSE"] = "false", ["FAULTLINE_SAVED_RUN_ID"] = null,
            ["FAULTLINE_COMPUTE_PROVIDERS"] = null
        };
        var previous = settings.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var directory = Path.Combine(Path.GetTempPath(), "audit_shutdown_" + Guid.NewGuid().ToString("N"));
        string? runId = null;
        try
        {
            foreach (var (key, value) in settings)
                Environment.SetEnvironmentVariable(key, value);
            using var cancellation = new CancellationTokenSource();
            var backing = new LocalStore(directory);
            var store = new InterruptAfterSave(backing, cancellation);
            var notes = new RunNotesService(store, NullLogger<RunNotesService>.Instance);
            using var coordinator = new Coordinator(store, notes, NullLogger<Coordinator>.Instance);
            await coordinator.StartAsync(cancellation.Token);
            await coordinator.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(coordinator.Error);
            var final = Assert.IsType<World>(coordinator.Current);
            runId = final.Id;
            Assert.Equal("stopped", final.Status);
            Assert.Equal(1, final.Tick);
            Assert.Equal(3, final.Revision);
            Assert.Equal(1, final.AuditDailyThroughTick);
            var persisted = (await backing.Load(runId, CancellationToken.None))!;
            Assert.Equal(final.AuditSequence, persisted.AuditSequence);
            var records = await backing.ReadAudit(runId, 0, 10000, CancellationToken.None);
            Assert.Equal(final.AuditSequence, records.Length);
            var daily = records.Where(row => row.Kind == "agent_day" && row.Payload["frame_kind"]!.GetValue<string>() == "day");
            var terminal = records.Where(row => row.Kind == "agent_day" && row.Payload["frame_kind"]!.GetValue<string>() == "end");
            Assert.Equal(final.Agents.Count, daily.Count());
            Assert.Equal(final.Agents.Count, terminal.Count());
            Assert.Contains(records, row => row.Kind == "event" && row.Payload["event_type"]!.GetValue<string>() == "run_stopped");
            Assert.Contains(records, row => row.Kind == "decision_request");
            SocietyEconomy.AssertConservation(persisted);
        }
        finally
        {
            foreach (var (key, value) in previous)
                Environment.SetEnvironmentVariable(key, value);
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
            if (runId is not null)
                foreach (var extension in new[] { ".json", ".md" })
                    File.Delete(Path.GetFullPath(Path.Combine(".data/run-notes", runId + extension)));
        }
    }

    private sealed class InterruptAfterSave(LocalStore backing, CancellationTokenSource cancellation) : IWorldStore
    {
        private bool interrupted;
        public string Kind => backing.Kind;
        public Task Initialize(CancellationToken token) => backing.Initialize(token);
        public Task SaveNotes(RunNotes notes, CancellationToken token) => backing.SaveNotes(notes, token);
        public Task<RunNotes?> LoadNotes(string id, CancellationToken token) => backing.LoadNotes(id, token);
        public Task<World?> Load(string id, CancellationToken token) => backing.Load(id, token);
        public Task<string[]> List(CancellationToken token) => backing.List(token);
        public Task<WorldEvent[]> ReadEvents(string id, long afterSequence, int limit, CancellationToken token) => backing.ReadEvents(id, afterSequence, limit, token);
        public async Task Save(World world, long expectedRevision, CancellationToken token, string? serialized = null)
        {
            await backing.Save(world, expectedRevision, token, serialized);
            if (!interrupted && world.Tick == 1 && world.Status == "running")
            {
                interrupted = true;
                cancellation.Cancel();
                throw new OperationCanceledException(token);
            }
        }
    }
}
