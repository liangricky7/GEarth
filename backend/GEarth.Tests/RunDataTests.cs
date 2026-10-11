using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GEarth.Api;
using GEarth.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;

namespace GEarth.Tests;

public sealed class RunDataTests
{
    private static string Temporary() => RunDataTestDirectory.Create("gearth-run-data-test-");
    private static JsonObject Node(object value) => JsonSerializer.SerializeToNode(value, Wire.Json)!.AsObject();
    private static RunDataService Service(LocalStore store, string directory) => new(store, new AnalyticsService(store), new AnalyticsBrowseStore(directory));
    private static World World(string id = "world_test", int tick = 6) => new() { Id = id, Revision = 1, Tick = tick, AnalyticsEnabled = true, AuditStartTick = 0,
        AuditDailyThroughTick = tick, AuditOpeningBalancesCaptured = true, UpdatedAt = DateTimeOffset.Parse("2026-10-11T10:00:00Z") };
    private static async Task Populate(LocalStore store, World world, int rows)
    {
        RunAudit.Append(world, "run_metadata", new { features = new { banking = "v1" }, banking_paused = false });
        for (var i = 1; i < rows; i++) RunAudit.Append(world, "agent_day", new JsonObject { ["agent_id"] = "a" + i, ["new_field"] = i % 2 == 0 ? JsonValue.Create(false) : null });
        await store.Save(world, 0, default);
    }
    [Fact]
    public async Task CatalogPagesPastOneHundredWithoutReadingWorldBodiesAndDiscoversNewIds()
    {
        var directory = Temporary();
        try
        {
            var store = new LocalStore(directory); var service = Service(store, directory);
            for (var i = 0; i < 151; i++)
                await File.WriteAllTextAsync(Path.Combine(directory, $"world_{i:D3}.json"), $"{{\"id\":\"world_{i:D3}\",\"revision\":1,\"status\":\"running\",\"tick\":6,\"scheduler_cursor\":0,\"agents\":INVALID_POPULATION_BODY");
            string? cursor = null; var ids = new List<string>();
            do
            {
                var page = Node(await service.Catalog(50, cursor, default));
                ids.AddRange(page["runs"]!.AsArray().Select(r => r!["run_id"]!.GetValue<string>())); cursor = page["next_cursor"]?.GetValue<string>();
            } while (cursor is not null);
            Assert.Equal(151, ids.Distinct().Count()); Assert.Equal("world_150", ids[0]);
            var manifest = Node(await service.Manifest("world_000", default));
            Assert.Null(manifest["analytics_enabled"]); Assert.Equal("unknown", manifest["audit_status"]!.GetValue<string>());
            Assert.Null(manifest["capture_metadata"]);
            await File.WriteAllTextAsync(Path.Combine(directory, "world_999.json"), "{\"id\":\"world_999\",\"revision\":1,\"status\":\"running\",\"tick\":0,\"scheduler_cursor\":0,\"agents\":INVALID");
            Assert.Equal("world_999", Node(await service.Catalog(50, null, default))["runs"]![0]!["run_id"]!.GetValue<string>());
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task SavedPinSurvivesHeadAdvanceAndEmptyFilteredPageAdvancesWithoutLosingMatches()
    {
        var directory = Temporary();
        try
        {
            var store = new LocalStore(directory); var world = World(); await Populate(store, world, 601); var service = Service(store, directory);
            var manifest = Node(await service.Manifest(world.Id, default));
            Assert.Equal(6, manifest["tick"]!.GetValue<int>()); Assert.Equal("available", manifest["audit_status"]!.GetValue<string>());
            Assert.False(manifest["capture_metadata"]!["banking_paused"]!.GetValue<bool>());
            world.Revision++; world.Tick = 7; RunAudit.Append(world, "transaction", new { amount_minor = 1 }); await store.Save(world, 1, default);
            var empty = Node(await service.Records(world.Id, 1, 601, 0, 100, 1, 7, "transaction", default));
            Assert.Empty(empty["records"]!.AsArray()); Assert.Equal(500, empty["next_after"]!.GetValue<long>()); Assert.True(empty["has_more"]!.GetValue<bool>());
            var last = Node(await service.Records(world.Id, 1, 601, 500, 200, 1, 7, "agent_day", default));
            Assert.Equal(101, last["records"]!.AsArray().Count); Assert.Equal(601, last["next_after"]!.GetValue<long>()); Assert.False(last["has_more"]!.GetValue<bool>());
            var pin = await Assert.ThrowsAsync<AnalyticsException>(() => service.Records(world.Id, 1, 602, 0, 100, null, null, null, default));
            Assert.Equal(409, pin.StatusCode);
            var latest = Node(await service.Records(world.Id, 2, 602, 601, 100, null, null, null, default));
            Assert.Single(latest["records"]!.AsArray());
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task AuditPageStopsAtLastConsumedRowAndPreservesOriginalFieldPresence()
    {
        var directory = Temporary();
        try
        {
            var store = new LocalStore(directory); var world = World(); await Populate(store, world, 6); var service = Service(store, directory);
            var first = Node(await service.Records(world.Id, 1, 6, 0, 2, null, null, "agent_day", default));
            Assert.Equal(3, first["next_after"]!.GetValue<long>());
            var payloads = first["records"]!.AsArray().Select(r => r!["payload"]!.AsObject()).ToArray();
            Assert.True(payloads[0].ContainsKey("new_field")); Assert.Null(payloads[0]["new_field"]); Assert.False(payloads[1]["new_field"]!.GetValue<bool>());
            Assert.False(payloads[0].ContainsKey("not_added_yet"));
            var rest = Node(await service.Records(world.Id, 1, 6, 3, 200, null, null, "agent_day", default)); Assert.Equal(3, rest["records"]!.AsArray().Count);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task SaveMarkersAreDistinctFromActualWeeklyFramesAndStagedHistoryIsHidden()
    {
        var directory = Temporary();
        try
        {
            var store = new LocalStore(directory); var world = World(); await Populate(store, world, 2);
            world.Revision = 2; world.Tick = 7; world.RendererCaptureEnabled = true; world.Society = new SocietyState(); RunAudit.Append(world, "agent_day", new { agent_id = "a" }); await store.Save(world, 1, default);
            var service = Service(store, directory); var page = Node(await service.Checkpoints(world.Id, 0, 25, default));
            var entries = page["checkpoints"]!.AsArray(); Assert.Equal(2, entries.Count);
            Assert.Equal("save_marker", entries[0]!["kind"]!.GetValue<string>()); Assert.Null(entries[0]!["tick"]); Assert.False(entries[0]!["replay_available"]!.GetValue<bool>());
            Assert.Equal("week", entries[1]!["kind"]!.GetValue<string>()); Assert.Equal(7, entries[1]!["tick"]!.GetValue<int>()); Assert.True(entries[1]!["replay_available"]!.GetValue<bool>());
            await File.WriteAllTextAsync(Path.Combine(directory, world.Id + ".json.audit", "000000000003.json"), "{}");
            Assert.Equal(2, Node(await service.Checkpoints(world.Id, 0, 25, default))["checkpoints"]!.AsArray().Count);
            world.AnalyticsEnabled = false; world.Revision = 3; await store.Save(world, 2, default);
            Assert.Equal("unknown", Node(await service.Manifest(world.Id, default))["timeline_status"]!.GetValue<string>());
            Assert.Equal(409, (await Assert.ThrowsAsync<AnalyticsException>(() => service.Records(world.Id, 2, 3, 0, 100, null, null, null, default))).StatusCode);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task HttpNewRoutesAreReadOnlyWithMalformedAndUnknownResponses()
    {
        var directory = Temporary();
        try
        {
            var store = new LocalStore(directory); var world = World(); await Populate(store, world, 2);
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
            builder.Services.AddSingleton<IWorldStore>(store); builder.Services.AddSingleton<AnalyticsService>(); builder.Services.AddSingleton(new AnalyticsBrowseStore(directory)); builder.Services.AddSingleton<RunDataService>();
            await using var app = builder.Build(); app.Urls.Add("http://127.0.0.1:0"); app.MapRunData(); await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/data/runs")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/data/runs?cursor=bad!")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/runs/missing/data/manifest")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/runs/{world.Id}/data/records")).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync($"/api/runs/{world.Id}/data/manifest", new StringContent("{}"))).StatusCode);
            await app.StopAsync();
        }
        finally { Directory.Delete(directory, true); }
    }
}

internal static class RunDataTestDirectory
{
    public static string Create(string prefix)
    {
        var root = Path.GetTempPath();
        // macOS exposes system temp through /var or /tmp symlink aliases; tests use
        // their canonical /private paths without relaxing production symlink checks.
        if (OperatingSystem.IsMacOS() && (root.StartsWith("/var/", StringComparison.Ordinal)
            || root.StartsWith("/tmp/", StringComparison.Ordinal) || root == "/tmp" || root == "/var"))
            root = "/private" + root;
        var path = Path.Combine(root, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
