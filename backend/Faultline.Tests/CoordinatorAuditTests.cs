using Faultline.Api;
using Faultline.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Faultline.Tests;

[CollectionDefinition("Coordinator audit environment", DisableParallelization = true)]
public sealed class CoordinatorAuditCollection;

[Collection("Coordinator audit environment")]
public sealed class CoordinatorAuditTests
{
    [Theory]
    [InlineData("local", 1)]
    [InlineData("neon_https", 6)]
    public async Task CancellationAfterCommitResponseWasLostPreservesStoppedFinalData(string kind, int interruptedTick)
    {
        var settings = new Dictionary<string, string?>
        {
            ["FAULTLINE_ANALYTICS"] = "true", ["FAULTLINE_WORLD"] = "true", ["FAULTLINE_LIVE"] = "false",
            ["FAULTLINE_TICKS"] = "8", ["FAULTLINE_POPULATION"] = "170", ["FAULTLINE_INTERVAL_MS"] = "0",
            ["FAULTLINE_VERBOSE"] = "false", ["FAULTLINE_SAVED_RUN_ID"] = null,
            ["FAULTLINE_COMPUTE_PROVIDERS"] = null, ["FAULTLINE_RESUME_RUN_ID"] = null
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
            var store = new InterruptAfterSave(backing, cancellation, kind, interruptedTick);
            var notes = new RunNotesService(store, NullLogger<RunNotesService>.Instance);
            using var coordinator = new Coordinator(store, notes, NullLogger<Coordinator>.Instance);
            await coordinator.StartAsync(cancellation.Token);
            await coordinator.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(coordinator.Error);
            var final = Assert.IsType<World>(coordinator.Current);
            runId = final.Id;
            Assert.Equal("stopped", final.Status);
            Assert.Equal(interruptedTick, final.Tick);
            Assert.Equal(3, final.Revision);
            Assert.Equal(interruptedTick, final.AuditDailyThroughTick);
            var persisted = (await backing.Load(runId, CancellationToken.None))!;
            Assert.Equal(final.AuditSequence, persisted.AuditSequence);
            var records = await backing.ReadAudit(runId, 0, 10000, CancellationToken.None);
            Assert.Equal(final.AuditSequence, records.Length);
            var daily = records.Where(row => row.Kind == "agent_day" && row.Payload["frame_kind"]!.GetValue<string>() == "day");
            var terminal = records.Where(row => row.Kind == "agent_day" && row.Payload["frame_kind"]!.GetValue<string>() == "end");
            Assert.Equal(final.Agents.Count * interruptedTick, daily.Count());
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

    [Fact]
    public async Task OrdinaryForkFromCapturedParentContinuesWithFrozenAuditAndIncompleteExportMetadata()
    {
        var runId = "audit_fork_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), runId);
        var parentDirectory = Path.Combine(directory, "parent");
        var forkDirectory = Path.Combine(directory, "fork");
        var settings = new Dictionary<string, string?>
        {
            ["FAULTLINE_ANALYTICS"] = "false", ["FAULTLINE_WORLD"] = "true", ["FAULTLINE_LIVE"] = "false",
            ["FAULTLINE_TICKS"] = "8", ["FAULTLINE_POPULATION"] = "170", ["FAULTLINE_INTERVAL_MS"] = "0",
            ["FAULTLINE_VERBOSE"] = "false", ["FAULTLINE_SAVED_RUN_ID"] = null,
            ["FAULTLINE_COMPUTE_PROVIDERS"] = null, ["FAULTLINE_RESUME_RUN_ID"] = runId,
            ["FAULTLINE_TIMELINE"] = "ordinary_fork", ["FAULTLINE_FORK_GOVERNMENT"] = null
        };
        var previous = settings.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (key, value) in settings)
                Environment.SetEnvironmentVariable(key, value);
            var parentStore = new LocalStore(parentDirectory);
            await parentStore.Initialize(CancellationToken.None);
            var parent = SocietyFactory.Create(runId, false, 170);
            RunAudit.Enable(parent);
            for (var day = 1; day <= 6; day++)
            {
                SocietyEngine.Advance(parent);
                RunAudit.Day(parent);
            }
            parent.Revision = 1;
            await parentStore.Save(parent, 0, CancellationToken.None);
            var prefix = Wire.Node(await parentStore.ReadAudit(runId, 0, 10000, CancellationToken.None)).ToJsonString();
            // Model a database branch: both the checkpoint and its committed audit prefix are inherited.
            Directory.CreateDirectory(forkDirectory);
            foreach (var source in Directory.GetFiles(parentDirectory, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(forkDirectory, Path.GetRelativePath(parentDirectory, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
            var forkStore = new LocalStore(forkDirectory);
            var notes = new RunNotesService(forkStore, NullLogger<RunNotesService>.Instance);
            using var coordinator = new Coordinator(forkStore, notes, NullLogger<Coordinator>.Instance);
            await coordinator.StartAsync(CancellationToken.None);
            await coordinator.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(coordinator.Error);
            var fork = (await forkStore.Load(runId, CancellationToken.None))!;
            Assert.Equal("completed", fork.Status);
            Assert.Equal(8, fork.Tick);
            Assert.False(fork.AnalyticsEnabled);
            Assert.Equal(parent.AuditSequence, fork.AuditSequence);
            Assert.Equal(6, fork.AuditDailyThroughTick);
            Assert.Empty(fork.PendingAudit);
            Assert.Contains(fork.Events, item => item.EventType == "timeline_forked");
            Assert.Equal(prefix, Wire.Node(await forkStore.ReadAudit(runId, 0, 10000, CancellationToken.None)).ToJsonString());
            var unchangedParent = (await parentStore.Load(runId, CancellationToken.None))!;
            Assert.True(unchangedParent.AnalyticsEnabled);
            Assert.Equal("running", unchangedParent.Status);
            Assert.Equal(6, unchangedParent.Tick);
            Assert.Equal(prefix, Wire.Node(await parentStore.ReadAudit(runId, 0, 10000, CancellationToken.None)).ToJsonString());
            var metadata = Wire.Node(await new AnalyticsService(forkStore).ExportMetadata(runId, CancellationToken.None));
            Assert.False(metadata["analytics_enabled"]!.GetValue<bool>());
            Assert.False(metadata["audit_from_tick_zero"]!.GetValue<bool>());
            Assert.Equal("partial", metadata["completeness"]!.GetValue<string>());
            SocietyEconomy.AssertConservation(fork);
        }
        finally
        {
            foreach (var (key, value) in previous)
                Environment.SetEnvironmentVariable(key, value);
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
            foreach (var extension in new[] { ".json", ".md" })
                File.Delete(Path.GetFullPath(Path.Combine(".data/run-notes", runId + extension)));
        }
    }

    private sealed class InterruptAfterSave(LocalStore backing, CancellationTokenSource cancellation, string kind, int interruptedTick) : IWorldStore
    {
        private bool interrupted;
        public string Kind => kind;
        public Task Initialize(CancellationToken token) => backing.Initialize(token);
        public Task SaveNotes(RunNotes notes, CancellationToken token) => backing.SaveNotes(notes, token);
        public Task<RunNotes?> LoadNotes(string id, CancellationToken token) => backing.LoadNotes(id, token);
        public Task<World?> Load(string id, CancellationToken token) => backing.Load(id, token);
        public Task<string[]> List(CancellationToken token) => backing.List(token);
        public Task<WorldEvent[]> ReadEvents(string id, long afterSequence, int limit, CancellationToken token) => backing.ReadEvents(id, afterSequence, limit, token);
        public async Task Save(World world, long expectedRevision, CancellationToken token, string? serialized = null)
        {
            await backing.Save(world, expectedRevision, token, serialized);
            if (!interrupted && world.Tick == interruptedTick && world.Status == "running")
            {
                interrupted = true;
                cancellation.Cancel();
                throw new OperationCanceledException(token);
            }
        }
    }
}
