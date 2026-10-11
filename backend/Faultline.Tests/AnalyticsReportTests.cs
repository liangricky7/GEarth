using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Faultline.Api;
using Faultline.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Faultline.Tests;

public sealed class AnalyticsReportTests
{
    private static World Finished(string status = "completed") => new()
    {
        Id = "audit_report_test", Status = status, Revision = 4, AuditSequence = 3, Seq = 6,
        AnalyticsEnabled = true, AuditStartTick = 0, AuditDailyThroughTick = 12,
        AuditOpeningBalancesCaptured = true, Tick = 12, Seed = 73, Mode = "RULE"
    };

    private static JsonObject Report(World world) => JsonSerializer.SerializeToNode(new
    {
        schema_version = AnalyticsService.ReportSchema, analysis_version = "1.0.0",
        run_id = world.Id, through_revision = world.Revision, through_sequence = world.AuditSequence,
        source_manifest_sha256 = new string('a', 64), generated_at = "2026-10-10T12:00:00Z",
        completeness = new { snapshot_complete = true, run_complete = world.Status == "completed",
            run_status = world.Status, missing_data = new string[0], metadata_unknown = new[] { "tokens" } },
        summary = new { record_count = world.AuditSequence, agent_count = 0, transaction_count = 0,
            transfer_minor = 0, decision_result_count = 0, fallback_count = 0 },
        checks = new object[0], agents = new object[0], countries = new object[0],
        government_comparison = new { status = "descriptive", reason = "One run" },
        evidence_refs = new[] { new { run_id = world.Id, sequence = 1, kind = "run_metadata" } }
    }, Wire.Json)!.AsObject();

    [Fact]
    public async Task HttpRoutesRequirePinnedAuditAndExposeNoPublicPublication()
    {
        var world = Finished(); var store = new TestStore(world);
        store.Records = Enumerable.Range(1, 3).Select(sequence => new AuditRecord(world.Id, sequence, 0, 4, "transaction", new())).ToArray();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        builder.Services.AddSingleton<IWorldStore>(store);
        builder.Services.AddSingleton<AnalyticsService>();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapAnalytics();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var path = $"/api/runs/{world.Id}/analytics";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path + "/export-metadata")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(path + "/audit?through_revision=3&through_sequence=3")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "/audit?through_revision=4&through_sequence=3&limit=2001")).StatusCode);
        var page = JsonNode.Parse(await client.GetStringAsync(path + "/audit?through_revision=4&through_sequence=3&limit=2"))!;
        Assert.Equal(2, page["next_after"]!.GetValue<int>());
        Assert.Equal(world.Id, page["records"]![0]!["run_id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "/report")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync(path + "/report", new StringContent("{}"))).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task ExportUsesPersistedCheckpointAndUnknownMetadataIsExplicit()
    {
        var world = Finished();
        var store = new TestStore(world);
        store.Records = [new(world.Id, 1, 0, 1, "run_metadata", new JsonObject { ["initial_population"] = 400 })];
        var result = JsonSerializer.SerializeToNode(await new AnalyticsService(store).ExportMetadata(world.Id, default), Wire.Json)!;
        Assert.Equal(4, result["through_revision"]!.GetValue<int>());
        Assert.Equal(3, result["through_sequence"]!.GetValue<int>());
        Assert.Equal(400, result["initial_population"]!.GetValue<int>());
        Assert.Equal("complete", result["completeness"]!.GetValue<string>());
        Assert.Contains("engine_commit", result["metadata_unknown"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(1, store.LastLimit); // A bounded metadata lookup, never an entire audit reconstruction.
    }

    [Fact]
    public async Task RunningRunsAndChangedPinsCannotBeExported()
    {
        var world = Finished("running");
        var service = new AnalyticsService(new TestStore(world));
        Assert.Equal(409, (await Assert.ThrowsAsync<AnalyticsException>(() => service.ExportMetadata(world.Id, default))).StatusCode);
        world.Status = "completed";
        Assert.Equal(400, (await Assert.ThrowsAsync<AnalyticsException>(() => service.AuditPage(world.Id, null, 3, 0, 500, default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<AnalyticsException>(() => service.AuditPage(world.Id, 3, 3, 0, 500, default))).StatusCode);
    }

    [Fact]
    public async Task AuditPagesHonorPinnedCursorAndRejectMissingCoverage()
    {
        var world = Finished(); var store = new TestStore(world);
        store.Records = Enumerable.Range(1, 3).Select(sequence => new AuditRecord(world.Id, sequence, 0, 4, "transaction", new())).ToArray();
        var service = new AnalyticsService(store);
        var page = JsonSerializer.SerializeToNode(await service.AuditPage(world.Id, 4, 3, 0, 2, default), Wire.Json)!;
        Assert.Equal(2, page["records"]!.AsArray().Count);
        Assert.Equal(2, page["next_after"]!.GetValue<int>());
        Assert.True(page["has_more"]!.GetValue<bool>());
        var end = JsonSerializer.SerializeToNode(await service.AuditPage(world.Id, 4, 3, 3, 2, default), Wire.Json)!;
        Assert.Empty(end["records"]!.AsArray()); Assert.False(end["has_more"]!.GetValue<bool>());
        store.Records = [store.Records[1]];
        Assert.Equal(409, (await Assert.ThrowsAsync<AnalyticsException>(() => service.AuditPage(world.Id, 4, 3, 0, 2, default))).StatusCode);
        Assert.Equal(0, store.WorldLoads);
        Assert.Equal(3, store.HeadReads);
    }

    [Fact]
    public async Task CachedReadNeverInventsAnUnimportedReportAndRejectsStaleReport()
    {
        var world = Finished(); var store = new TestStore(world); var service = new AnalyticsService(store);
        Assert.Equal(404, (await Assert.ThrowsAsync<AnalyticsException>(() => service.ReadReport(world.Id, default))).StatusCode);
        store.Report = Report(world);
        Assert.Same(store.Report, await service.ReadReport(world.Id, default));
        world.Revision++;
        Assert.Equal(409, (await Assert.ThrowsAsync<AnalyticsException>(() => service.ReadReport(world.Id, default))).StatusCode);
        Assert.Equal(0, store.WorldLoads);
        Assert.Equal(3, store.HeadReads);
    }

    [Fact]
    public async Task PublicationRequiresDigestAndManifestToMatchCommittedRun()
    {
        var world = Finished(); var store = new TestStore(world); var service = new AnalyticsService(store);
        var report = Report(world);
        var manifestNode = JsonSerializer.SerializeToNode(new { schema_version = "faultline.run-export.v1", record_schema_version = "faultline.audit.v1",
            run = new { run_id = world.Id, through_revision = world.Revision, through_sequence = world.AuditSequence,
                through_event_sequence = world.Seq, tick = world.Tick, seed = world.Seed, mode = world.Mode, status = world.Status },
            record_count = world.AuditSequence, completeness = new { snapshot_complete = true } })!;
        var manifest = JsonSerializer.SerializeToUtf8Bytes(manifestNode);
        await Assert.ThrowsAsync<AnalyticsException>(() => service.Publish(report, manifest, default));
        Assert.Null(store.Report);
        report["source_manifest_sha256"] = Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant();
        await service.Publish(report, manifest, default);
        Assert.Same(report, store.Report);
        // A large export manifest remains valid; its exact bytes (including whitespace) are authenticated.
        var largeManifest = new byte[2 * 1024 * 1024];
        Array.Fill(largeManifest, (byte)' ');
        manifest.CopyTo(largeManifest, 0);
        report["source_manifest_sha256"] = Convert.ToHexString(SHA256.HashData(largeManifest)).ToLowerInvariant();
        await service.Publish(report, largeManifest, default);
        var loadsBeforeOversize = store.WorldLoads;
        var oversized = await Assert.ThrowsAsync<AnalyticsException>(() => service.Publish(report,
            new byte[AnalyticsService.MaximumManifestBytes + 1], default));
        Assert.Contains("16 MiB", oversized.Message);
        Assert.Equal(loadsBeforeOversize, store.WorldLoads);
        manifestNode["run"]!["through_revision"] = 99;
        var changedManifest = JsonSerializer.SerializeToUtf8Bytes(manifestNode);
        report["source_manifest_sha256"] = Convert.ToHexString(SHA256.HashData(changedManifest)).ToLowerInvariant();
        Assert.Equal(409, (await Assert.ThrowsAsync<AnalyticsException>(() => service.Publish(report, changedManifest, default))).StatusCode);
    }

    [Fact]
    public void ReportValidationRejectsWrongSchemaForeignEvidenceAndFalseCompletion()
    {
        var world = Finished("stopped"); var report = Report(world);
        AnalyticsService.ValidateReport(report, world); // Complete audit snapshot may accompany a partial run.
        report["completeness"]!["run_complete"] = true;
        Assert.Throws<AnalyticsException>(() => AnalyticsService.ValidateReport(report, world));
        report = Report(world); report["schema_version"] = "other.v1";
        Assert.Throws<AnalyticsException>(() => AnalyticsService.ValidateReport(report, world));
        report = Report(world); report["evidence_refs"]![0]!["sequence"] = 4;
        Assert.Throws<AnalyticsException>(() => AnalyticsService.ValidateReport(report, world));
        report = Report(world); report["summary"]!["record_count"] = 2;
        Assert.Throws<AnalyticsException>(() => AnalyticsService.ValidateReport(report, world));
        report = Report(world); report["agents"]!.AsArray().Add(new JsonObject { ["agent_id"] = "one",
            ["evidence_refs"] = new JsonArray(new JsonObject { ["run_id"] = "foreign", ["sequence"] = 1, ["kind"] = "transaction" }) });
        Assert.Throws<AnalyticsException>(() => AnalyticsService.ValidateReport(report, world));
        report = Report(world); report["countries"]!.AsArray().Add(new JsonObject { ["country_id"] = "aster",
            ["evidence_ref"] = new JsonObject { ["run_id"] = world.Id, ["sequence"] = 99, ["kind"] = "nation_day" } });
        Assert.Throws<AnalyticsException>(() => AnalyticsService.ValidateReport(report, world));
    }

    private sealed class TestStore(World world) : IWorldStore, IRunAuditStore, IAnalyticsReportStore
    {
        public string Kind => "test";
        public bool SupportsAudit => true;
        public AuditRecord[] Records { get; set; } = [];
        public JsonObject? Report { get; set; }
        public int LastLimit { get; private set; }
        public int WorldLoads { get; private set; }
        public int HeadReads { get; private set; }
        public Task Initialize(CancellationToken ct) => Task.CompletedTask;
        public Task SaveNotes(RunNotes notes, CancellationToken ct) => Task.CompletedTask;
        public Task<RunNotes?> LoadNotes(string id, CancellationToken ct) => Task.FromResult<RunNotes?>(null);
        public Task Save(World value, long expected, CancellationToken ct, string? serialized = null) => throw new NotSupportedException();
        public Task<World?> Load(string id, CancellationToken ct)
        { WorldLoads++; return Task.FromResult<World?>(id == world.Id ? world : null); }
        public Task<RunAuditHead?> ReadAuditHead(string id, CancellationToken ct)
        {
            HeadReads++;
            return Task.FromResult<RunAuditHead?>(id == world.Id
                ? new(world.Id, world.Revision, world.AuditSequence, world.Status, world.AnalyticsEnabled) : null);
        }
        public Task<string[]> List(CancellationToken ct) => Task.FromResult(new[] { world.Id });
        public Task<WorldEvent[]> ReadEvents(string id, long after, int limit, CancellationToken ct) => Task.FromResult<WorldEvent[]>([]);
        public Task<AuditRecord[]> ReadAudit(string id, long after, int limit, CancellationToken ct)
        { LastLimit = limit; return Task.FromResult(Records.Where(item => item.Sequence > after).Take(limit).ToArray()); }
        public Task<JsonObject?> ReadAnalyticsReport(string id, CancellationToken ct) => Task.FromResult(Report);
        public Task SaveAnalyticsReport(string id, JsonObject report, CancellationToken ct) { Report = report; return Task.CompletedTask; }
    }
}
