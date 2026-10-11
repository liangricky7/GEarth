using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Tests;

public sealed class RunDataNeonTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public readonly List<(string Sql, JsonArray Parameters)> Queries = [];
        public Func<string, JsonArray> Reply = _ => new JsonArray();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var input = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject(); var sql = input["query"]!.GetValue<string>();
            Queries.Add((sql, input["params"]!.DeepClone().AsArray()));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["rows"] = Reply(sql) }.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
    [Fact]
    public async Task CatalogReadsScalarHeadsAndOriginalNullableCaptureMetadataRemainsUnknown()
    {
        using var handler = new Handler(); handler.Reply = sql => sql.Contains("json_build_object")
            ? new JsonArray(new JsonArray("{\"id\":\"world_old\",\"revision\":4,\"audit_sequence\":0,\"status\":\"completed\",\"analytics_enabled\":null,\"tick\":365,\"updated_at\":\"2026-10-11T10:00:00Z\"}"))
            : new JsonArray(new JsonArray("world_old", "4", null, null, "f", "2026-10-11T10:00:00Z"));
        using var store = new NeonStore("postgresql://fixture:fixture@example.neon.tech/neondb", handler);
        var catalog = await store.ReadRunCatalog(null, 51, default); Assert.Single(catalog);
        Assert.DoesNotContain("state", handler.Queries[0].Sql); Assert.Contains("COLLATE \"C\"", handler.Queries[0].Sql);
        var head = await store.ReadRunDataHead("world_old", default); Assert.Null(head!.AnalyticsEnabled); Assert.Null(head.AuditDailyThroughTick); Assert.Equal(365, head.Tick);
        Assert.Contains("'analytics_enabled',(state->>'analytics_enabled')::boolean", handler.Queries[1].Sql);
        Assert.All(handler.Queries, query => Assert.StartsWith("SELECT", query.Sql));
    }
    [Fact]
    public async Task PinnedReadsUseOnlyCommittedBatchesAndBothRevisionAndSequenceBounds()
    {
        using var handler = new Handler(); var row = new AuditRecord("world_test", 1, 6, 4, "run_metadata", new());
        handler.Reply = sql => sql.Contains("WITH candidate") ? new JsonArray(new JsonArray(JsonSerializer.Serialize(row, Wire.Json))) : new JsonArray(new JsonArray("1"));
        using var store = new NeonStore("postgresql://fixture:fixture@example.neon.tech/neondb", handler);
        Assert.True(await store.VerifyAuditPin("world_test", 4, 1, default));
        Assert.Single(await store.ReadPinnedAudit("world_test", 4, 1, 0, 500, default));
        var sql = handler.Queries[1].Sql; Assert.Contains("c.batch_id=a.batch_id", sql); Assert.Contains("a.sequence<=$3::bigint", sql);
        Assert.Contains("a.revision<=$4::bigint", sql); Assert.Contains("c.revision<=r.revision", sql); Assert.Contains("a.sequence<=c.through_sequence", sql);
        Assert.Contains("bytes<=8000000", sql); Assert.Equal("500", handler.Queries[1].Parameters[4]!.GetValue<string>());
        Assert.All(handler.Queries, query => Assert.True(query.Sql.StartsWith("SELECT") || query.Sql.StartsWith("WITH candidate")));
    }
}
