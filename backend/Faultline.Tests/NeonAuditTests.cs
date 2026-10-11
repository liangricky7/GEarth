using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Faultline.Core;

namespace Faultline.Tests;

public sealed class NeonAuditTests
{
    [Fact]
    public async Task BoundedStagingSurvivesFailedCommitAndLostCommitResponse()
    {
        var handler = new AuditHandler { FailCommit = true };
        using var store = new NeonStore("postgresql://test:test@ep-test.us-east-2.aws.neon.tech/neondb", handler);
        var world = SocietyFactory.Create("neon_audit", false, 160);
        RunAudit.Enable(world);
        for (var index = 0; index < 2500; index++)
            RunAudit.Append(world, "decision_request", new { context = new string('c', 1800), index });
        world.Revision = 1;
        var expected = world.AuditSequence;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Save(world, 0, CancellationToken.None));
        Assert.True(handler.AuditBatchSizes.Count >= 3);
        Assert.All(handler.AuditBatchSizes, size => Assert.InRange(size, 1, 2_000_001));
        Assert.NotEmpty(world.PendingAudit);
        Assert.Empty(await store.ReadAudit(world.Id, 0, 10000, CancellationToken.None));
        Assert.Null(await store.ReadAuditHead(world.Id, CancellationToken.None));
        handler.FailCommit = false;
        handler.LoseCommitResponse = true;
        await store.Save(world, 0, CancellationToken.None);
        Assert.Empty(world.PendingAudit);
        Assert.Equal(expected, (await store.ReadAudit(world.Id, 0, 10000, CancellationToken.None)).Length);
        Assert.Equal(expected, (await store.Load(world.Id, CancellationToken.None))!.AuditSequence);
        var head = (await store.ReadAuditHead(world.Id, CancellationToken.None))!;
        Assert.Equal(world.Revision, head.Revision);
        Assert.Equal(expected, head.AuditSequence);
        Assert.Equal("running", head.Status);
        Assert.True(head.AnalyticsEnabled);
        Assert.True(handler.UsedChunkedCheckpoint);
        Assert.All(handler.CheckpointPartSizes, size => Assert.InRange(size, 1, 512 * 1024));
        Assert.Contains("JOIN faultline.run_audit_commits", handler.ReadSql);
        Assert.Contains("a.revision<=r.revision", handler.ReadSql);
        Assert.Contains("sum(octet_length(envelope::text))", handler.ReadSql);
    }

    [Fact]
    public async Task CheckpointLargerThanLegacyLimitUsesOnlyBoundedPartsAndVerifiesLoadHash()
    {
        var handler = new AuditHandler();
        using var store = new NeonStore("postgresql://test:test@ep-test.us-east-2.aws.neon.tech/neondb", handler);
        var world = SocietyFactory.Create("neon_large", false, 160);
        RunAudit.Enable(world);
        world.Agents.Values.First().Goal = new string('x', 17 * 1024 * 1024) + "世界🌎";
        world.Revision = 1;
        await store.Save(world, 0, CancellationToken.None);
        Assert.True(handler.CheckpointPartSizes.Count > 100);
        Assert.All(handler.CheckpointPartSizes, size => Assert.InRange(size, 1, 512 * 1024));
        Assert.True(handler.UsedChunkedCheckpoint);
        var loaded = await store.Load(world.Id, CancellationToken.None);
        Assert.Equal(world.Agents.Values.First().Goal, loaded!.Agents.Values.First().Goal);
        handler.CorruptFirstPart = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Load(world.Id, CancellationToken.None));
    }

    [Fact]
    public async Task SameRevisionDifferentAuditPayloadIsRejected()
    {
        var handler = new AuditHandler();
        using var store = new NeonStore("postgresql://test:test@ep-test.us-east-2.aws.neon.tech/neondb", handler);
        var world = SocietyFactory.Create("neon_conflict", false, 160);
        RunAudit.Enable(world);
        world.Revision = 1;
        var pending = world.PendingAudit.ToArray();
        await store.Save(world, 0, CancellationToken.None);
        world.PendingAudit.AddRange(pending);
        world.PendingAudit[0] = pending[0] with { Payload = new JsonObject { ["conflicting"] = true } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Save(world, 0, CancellationToken.None));
    }

    // Protocol fake: stores staged data separately from the successful CAS checkpoint and its commit marker.
    private sealed class AuditHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, SortedDictionary<long, string>> audit = new();
        private readonly Dictionary<string, SortedDictionary<int, string>> parts = new();
        private readonly Dictionary<long, string> committed = new();
        private string? state;
        private string? digest;
        private int partCount;
        public bool FailCommit { get; set; }
        public bool LoseCommitResponse { get; set; }
        public bool CorruptFirstPart { get; set; }
        public bool UsedChunkedCheckpoint { get; private set; }
        public List<int> AuditBatchSizes { get; } = [];
        public List<int> CheckpointPartSizes { get; } = [];
        public string ReadSql { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var sql = body["query"]!.GetValue<string>();
            var parameters = body["params"]!.AsArray().Select(value => value?.GetValue<string>()).ToArray();
            var rows = new List<string?[]>();
            if (sql.StartsWith("SELECT revision,state->>"))
            {
                if (state is not null)
                {
                    var node = JsonNode.Parse(state)!;
                    rows.Add(sql.Contains("state->>'status'")
                        ? [node["revision"]!.ToString(), node["audit_sequence"]!.ToString(), node["status"]!.GetValue<string>(), node["analytics_enabled"]!.ToJsonString()]
                        : [node["revision"]!.ToString(), node["audit_sequence"]!.ToString()]);
                }
            }
            else if (sql.StartsWith("INSERT INTO faultline.run_audit("))
            {
                var key = parameters[2]!;
                if (!audit.TryGetValue(key, out var staged))
                    audit[key] = staged = new();
                AuditBatchSizes.Add(Encoding.UTF8.GetByteCount(parameters[3]!));
                foreach (var row in JsonNode.Parse(parameters[3]!)!.AsArray())
                    staged.TryAdd(row!["sequence"]!.GetValue<long>(), row.ToJsonString());
            }
            else if (sql.StartsWith("INSERT INTO faultline.checkpoint_parts"))
            {
                var key = parameters[1]!;
                if (!parts.TryGetValue(key, out var staged))
                    parts[key] = staged = new();
                staged.TryAdd(int.Parse(parameters[2]!), parameters[3]!);
                CheckpointPartSizes.Add(Encoding.UTF8.GetByteCount(parameters[3]!));
            }
            else if (sql.StartsWith("WITH input AS"))
            {
                Assert.Contains("encode(sha256(convert_to(serialized,'UTF8')),'hex')=$7", sql);
                Assert.Contains("stage1_runs.revision=$4::bigint", sql);
                Assert.Contains("audit_committed AS", sql);
                if (FailCommit)
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                var previousRevision = state is null ? 0 : JsonNode.Parse(state)!["revision"]!.GetValue<long>();
                if (previousRevision == long.Parse(parameters[3]!))
                {
                    UsedChunkedCheckpoint = parameters[2] is null;
                    digest = parameters[6];
                    partCount = int.Parse(parameters[7]!);
                    state = parameters[2] ?? string.Concat(parts[digest!].Values);
                    if (parameters[4] is { } batch)
                        committed[long.Parse(parameters[1]!)] = batch;
                    rows.Add([parameters[0]]);
                    if (LoseCommitResponse)
                    {
                        LoseCommitResponse = false;
                        throw new HttpRequestException("Lost response after commit");
                    }
                }
            }
            else if (sql.StartsWith("SELECT checkpoint_digest"))
            {
                if (state is not null)
                    rows.Add([digest, partCount.ToString()]);
            }
            else if (sql.StartsWith("SELECT part,content"))
            {
                foreach (var part in parts[parameters[1]!].Where(part => part.Key >= int.Parse(parameters[2]!)).Take(16))
                    rows.Add([part.Key.ToString(), CorruptFirstPart && part.Key == 0 ? " " + part.Value : part.Value]);
            }
            else if (sql.StartsWith("SELECT state FROM"))
            {
                if (state is not null)
                    rows.Add([state]);
            }
            else if (sql.StartsWith("SELECT batch_id"))
            {
                if (committed.TryGetValue(long.Parse(parameters[1]!), out var batch))
                    rows.Add([batch]);
            }
            else if (sql.StartsWith("WITH candidate AS"))
            {
                ReadSql = sql;
                foreach (var row in committed.OrderBy(item => item.Key).SelectMany(item => audit[item.Value])
                    .Where(row => row.Key > long.Parse(parameters[1]!)).Take(int.Parse(parameters[2]!)))
                    rows.Add([row.Value]);
            }
            else
                throw new InvalidOperationException("Unexpected mocked SQL: " + sql);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { rows }), Encoding.UTF8, "application/json")
            };
        }
    }
}
