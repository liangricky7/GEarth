using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GEarth.Api;
using GEarth.Core;

namespace GEarth.Tests;

public sealed class AnalyticsBrowseTests
{
    private static JsonObject Node(object value) => JsonSerializer.SerializeToNode(value, Wire.Json)!.AsObject();
    private sealed class Fixture : IDisposable
    {
        public readonly string Root = RunDataTestDirectory.Create("gearth-browse-test-");
        public string Bundle => Path.Combine(Root, "input");
        public readonly World World = new() { Id = "world_browse_test", Revision = 1, Status = "completed", AnalyticsEnabled = true, Tick = 365 };
        public LocalStore Store { get; }
        public AnalyticsBrowseStore Browse { get; }
        public AnalyticsService Service { get; }
        public JsonObject Report { get; private set; } = new();
        public JsonObject Manifest { get; private set; } = new();
        public byte[] Source { get; private set; } = [];
        public Fixture()
        {
            Directory.CreateDirectory(Bundle); Store = new LocalStore(Path.Combine(Root, "runs")); Directory.CreateDirectory(Path.Combine(Root, "runs"));
            Browse = new AnalyticsBrowseStore(Path.Combine(Root, "cache")); Service = new AnalyticsService(Store, Browse);
        }
        public async Task Initialize(int rows = 350)
        {
            RunAudit.Append(World, "run_metadata", new { engine_commit = "fixture" }); await Store.Save(World, 0, default);
            Source = JsonSerializer.SerializeToUtf8Bytes(new { schema_version = "gearth.run-export.v1", record_schema_version = "gearth.audit.v1",
                run = new { run_id = World.Id, through_revision = World.Revision, through_sequence = World.AuditSequence,
                    through_event_sequence = World.Seq, tick = World.Tick, seed = World.Seed, mode = World.Mode, status = World.Status },
                record_count = World.AuditSequence, completeness = new { snapshot_complete = true } }, Wire.Json);
            Report = Node(new { schema_version = "gearth.analytics-report.v1", analysis_version = "1.0.0", run_id = World.Id,
                through_revision = World.Revision, through_sequence = World.AuditSequence, source_manifest_sha256 = AnalyticsBrowseStore.Digest(Source),
                generated_at = "2026-10-11T10:00:00Z", completeness = new { snapshot_complete = true, run_complete = true, run_status = "completed", missing_data = new string[0], metadata_unknown = new string[0] },
                summary = new { record_count = World.AuditSequence, agent_count = 0, transaction_count = 0, transfer_minor = 0, decision_result_count = 0, fallback_count = 0 },
                checks = new object[0], agents = new object[0], countries = new object[0], government_comparison = new { status = "descriptive" }, evidence_refs = new object[0] });
            var chunks = new JsonArray();
            for (var offset = 0; offset < rows; offset += 200)
            {
                var count = Math.Min(rows - offset, 200); var relative = $"silver.agent_daily/{offset / 200:D6}.ndjson.gz"; var path = Path.Combine(Bundle, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var text = string.Concat(Enumerable.Range(offset, count).Select(i => Node(new { run_id = World.Id, payload_json = "{\"hungry\":false,\"missing\":null}", cash_minor = (long?)(i % 2 == 0 ? 0 : null), agent_id = "a" + i }).ToJsonString(Wire.Json) + "\n"));
                var bytes = Encoding.UTF8.GetBytes(text);
                await using (var stream = File.Create(path)) await using (var gzip = new GZipStream(stream, CompressionLevel.Fastest)) await gzip.WriteAsync(bytes);
                var compressed = await File.ReadAllBytesAsync(path);
                chunks.Add(Node(new { path = relative, row_count = count, sha256 = AnalyticsBrowseStore.Digest(compressed), compressed_bytes = compressed.Length, uncompressed_bytes = bytes.Length }));
            }
            var dataset = Node(new { dataset_id = "silver.agent_daily", table_name = "workspace.gearth_silver.agent_daily", availability = "available", row_count = rows,
                columns = new object[] { new { name = "run_id", data_type = "string", nullable = false, non_null_count = rows }, new { name = "payload_json", data_type = "string", nullable = false, non_null_count = rows },
                    new { name = "cash_minor", data_type = "bigint", nullable = true, non_null_count = (rows + 1) / 2 }, new { name = "agent_id", data_type = "string", nullable = false, non_null_count = rows } },
                payload_fields = new[] { new { path = "/hungry", present_count = rows, null_count = 0, non_null_count = rows, zero_or_false_count = rows },
                    new { path = "/missing", present_count = rows, null_count = rows, non_null_count = 0, zero_or_false_count = 0 } },
                payload_source = rows == 0 ? null : "payload_json", field_coverage_scope = "all_run_rows_bounded_inventory", field_coverage_limit = new { max_depth = 4, array_elements = false } });
            dataset["chunks"] = chunks;
            if (rows == 0) dataset["payload_fields"] = new JsonArray();
            var absent = Node(new { dataset_id = "silver.bank_ledger", table_name = "workspace.gearth_silver.bank_ledger", availability = "unavailable", unavailable_reason = "table_not_present",
                row_count = (long?)null, columns = new object[0], payload_fields = new object[0], chunks = new object[0], field_coverage_scope = "unavailable" });
            Manifest = Node(new { schema_version = "gearth.analytics-browse.v1", run_id = World.Id, through_revision = World.Revision, through_sequence = World.AuditSequence,
                source_manifest_sha256 = AnalyticsBrowseStore.Digest(Source), generated_at = "2026-10-11T10:00:00Z", qualification_status = "pass" });
            Manifest["datasets"] = new JsonArray(dataset, absent); await Rebuild();
        }
        public JsonObject Dataset => Manifest["datasets"]![0]!.AsObject();
        public JsonObject Chunk => Dataset["chunks"]![0]!.AsObject();
        public async Task Rebuild()
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(Manifest, Wire.Json); await File.WriteAllBytesAsync(Path.Combine(Bundle, "browse-manifest.json"), bytes);
            Report["dataset_catalog"] = AnalyticsBrowseStore.Catalog(Manifest, AnalyticsBrowseStore.Digest(bytes));
        }
        public Task Publish(CancellationToken ct = default) => Service.Publish(Report, Source, ct, Bundle);
        public string Cache => Path.Combine(Root, "cache", World.Id, Report["source_manifest_sha256"]!.GetValue<string>(), Report["dataset_catalog"]!["bundle_manifest_sha256"]!.GetValue<string>());
        public void Dispose() => Directory.Delete(Root, true);
    }
    [Fact]
    public async Task PublishedRowsCrossChunksAndReachBeyondSummaryPreviewWithoutDatabricksCommands()
    {
        using var f = new Fixture(); await f.Initialize(); await f.Publish();
        string? cursor = null; var agents = new List<string>();
        do
        {
            var page = Node(await f.Browse.Rows(f.Report, "silver.agent_daily", cursor, 100, default));
            Assert.Equal("databricks_cached", page["source"]!.GetValue<string>());
            agents.AddRange(page["rows"]!.AsArray().Select(r => r!["agent_id"]!.GetValue<string>()));
            cursor = page["next_cursor"]?.GetValue<string>();
        } while (cursor is not null);
        Assert.Equal(350, agents.Count); Assert.Equal("a349", agents[^1]);
        Assert.True(File.Exists(Path.Combine(f.Cache, "browse-manifest.json")));
        var service = new RunDataService(f.Store, f.Service, f.Browse); var manifest = Node(await service.Manifest(f.World.Id, default));
        Assert.Equal("available", manifest["databricks_status"]!.GetValue<string>()); Assert.Null(manifest["dataset_catalog"]!["datasets"]);
        var datasets = Node(await service.Datasets(f.World.Id, default)); Assert.Equal(2, datasets["dataset_catalog"]!["datasets"]!.AsArray().Count);
        Assert.Equal(f.Report["dataset_catalog"]!["bundle_manifest_sha256"]!.GetValue<string>(), datasets["bundle_manifest_sha256"]!.GetValue<string>());
        var noRows = await Assert.ThrowsAsync<AnalyticsException>(() => f.Browse.Rows(f.Report, "silver.bank_ledger", null, 100, default)); Assert.Equal(404, noRows.StatusCode);
    }
    [Fact]
    public async Task CursorRejectsCrossDatasetAndStalePublication()
    {
        using var f = new Fixture(); await f.Initialize(); await f.Publish();
        var page = Node(await f.Browse.Rows(f.Report, "silver.agent_daily", null, 100, default)); var cursor = page["next_cursor"]!.GetValue<string>();
        var fields = JsonNode.Parse(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/') + new string('=', (4 - cursor.Length % 4) % 4)))!;
        fields["dataset_id"] = "silver.transactions";
        var tampered = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(fields, Wire.Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(409, (await Assert.ThrowsAsync<AnalyticsException>(() => f.Browse.Rows(f.Report, "silver.agent_daily", tampered, 100, default))).StatusCode);
        f.Manifest["generated_at"] = "2026-10-11T11:00:00Z"; await f.Rebuild(); await f.Publish();
        Assert.Equal(409, (await Assert.ThrowsAsync<AnalyticsException>(() => f.Browse.Rows(f.Report, "silver.agent_daily", cursor, 100, default))).StatusCode);
    }
    [Theory]
    [InlineData("availability")]
    [InlineData("hash")]
    [InlineData("count")]
    [InlineData("schema")]
    [InlineData("coverage")]
    [InlineData("payload_coverage")]
    [InlineData("path")]
    [InlineData("source")]
    [InlineData("run")]
    public async Task InvalidNewBundleDoesNotReplacePreviousPublishedReport(string failure)
    {
        using var f = new Fixture(); await f.Initialize(); await f.Publish(); var previous = (await f.Store.ReadAnalyticsReport(f.World.Id, default))!.ToJsonString();
        switch (failure)
        {
            case "availability": f.Dataset["availability"] = "not_verified"; break;
            case "hash": f.Chunk["sha256"] = new string('a', 64); break;
            case "count": f.Chunk["row_count"] = 199; break;
            case "schema": f.Dataset["columns"]![0]!["name"] = "not_run_id"; break;
            case "coverage": f.Dataset["columns"]![2]!["non_null_count"] = 1; break;
            case "payload_coverage": f.Dataset["payload_fields"]![0]!["zero_or_false_count"] = 1; break;
            case "path": f.Chunk["path"] = "silver.agent_daily/../../secrets.ndjson.gz"; break;
            case "source": f.Manifest["source_manifest_sha256"] = new string('b', 64); break;
            case "run": f.Manifest["run_id"] = "other_run"; break;
        }
        await f.Rebuild(); await Assert.ThrowsAsync<AnalyticsException>(() => f.Publish());
        Assert.Equal(previous, (await f.Store.ReadAnalyticsReport(f.World.Id, default))!.ToJsonString());
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.Root, "cache"), ".staging-*", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task InterruptedPublicationAndCorruptedGzipPreservePreviousPointer()
    {
        using var f = new Fixture(); await f.Initialize(); await f.Publish(); var previous = (await f.Store.ReadAnalyticsReport(f.World.Id, default))!.ToJsonString();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Publish(cancellation.Token));
        var path = Path.Combine(f.Bundle, f.Chunk["path"]!.GetValue<string>()); var bytes = (await File.ReadAllBytesAsync(path))[..8]; await File.WriteAllBytesAsync(path, bytes);
        f.Chunk["sha256"] = AnalyticsBrowseStore.Digest(bytes); f.Chunk["compressed_bytes"] = bytes.Length; await f.Rebuild();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Publish()); Assert.Equal(previous, (await f.Store.ReadAnalyticsReport(f.World.Id, default))!.ToJsonString());
    }
    [Fact]
    public async Task ChunkSymlinksAndOversizedManifestAreRejected()
    {
        using var f = new Fixture(); await f.Initialize();
        var path = Path.Combine(f.Bundle, f.Chunk["path"]!.GetValue<string>()); var other = Path.Combine(f.Root, "other.gz"); File.Move(path, other); File.CreateSymbolicLink(path, other);
        await Assert.ThrowsAsync<AnalyticsException>(() => f.Publish()); File.Delete(path); File.Move(other, path);
        await File.WriteAllBytesAsync(Path.Combine(f.Bundle, "browse-manifest.json"), new byte[16 * 1024 * 1024 + 1]);
        Assert.Contains("16 MiB", (await Assert.ThrowsAsync<AnalyticsException>(() => f.Publish())).Message);
    }
    [Fact]
    public async Task SummaryOnlySameSourcePublicationPreservesVerifiedRowCacheAndLegacyReportStillWorks()
    {
        using var f = new Fixture(); await f.Initialize(); await f.Publish(); var original = f.Report["dataset_catalog"]!.ToJsonString();
        var summary = f.Report.DeepClone().AsObject(); summary.Remove("dataset_catalog"); await f.Service.Publish(summary, f.Source, default);
        Assert.Equal(original, (await f.Store.ReadAnalyticsReport(f.World.Id, default))!["dataset_catalog"]!.ToJsonString());
        Directory.Delete(f.Cache, true);
        var service = new RunDataService(f.Store, f.Service, f.Browse);
        Assert.Equal("report_only", Node(await service.Manifest(f.World.Id, default))["databricks_status"]!.GetValue<string>());
        Assert.Equal("report_only", Node(await service.Datasets(f.World.Id, default))["status"]!.GetValue<string>());
        summary.Remove("dataset_catalog"); await f.Store.SaveAnalyticsReport(f.World.Id, summary, default);
        Assert.Null((await f.Service.ReadReport(f.World.Id, default))["dataset_catalog"]);
    }
    [Fact]
    public async Task ActualTypedEmptyTableDiffersFromAbsentTable()
    {
        using var f = new Fixture(); await f.Initialize(0); await f.Publish();
        var page = Node(await f.Browse.Rows(f.Report, "silver.agent_daily", null, 100, default)); Assert.Empty(page["rows"]!.AsArray()); Assert.False(page["has_more"]!.GetValue<bool>());
        Assert.Equal(404, (await Assert.ThrowsAsync<AnalyticsException>(() => f.Browse.Rows(f.Report, "silver.bank_ledger", null, 100, default))).StatusCode);
    }
}
