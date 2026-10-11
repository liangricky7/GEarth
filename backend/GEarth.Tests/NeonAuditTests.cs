using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Tests;

public sealed class NeonAuditTests
{
    [Fact]
    public async Task BoundedStagingSurvivesFailedCommitAndLostCommitResponse()
    {
        var handler = new AuditHandler { FailCommit = true };
        using var store = new NeonStore("postgresql://test:test@ep-test.us-east-2.aws.neon.tech/neondb", handler);
        var world = SocietyFactory.Create("neon_audit", false, 170);
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
        Assert.Equal(1, handler.CheckpointRegistrations);
        Assert.All(handler.CheckpointPartSizes, size => Assert.InRange(size, 1, 512 * 1024));
        Assert.Contains("JOIN gearth.run_audit_commits", handler.ReadSql);
        Assert.Contains("a.revision<=r.revision", handler.ReadSql);
        Assert.Contains("sum(octet_length(envelope::text))", handler.ReadSql);
        Assert.Contains("COALESCE(r.audit_sequence,(r.state->>'audit_sequence')::bigint)", handler.ReadSql);
    }

    [Fact]
    public async Task CheckpointLargerThanLegacyLimitUsesOnlyBoundedPartsAndVerifiesLoadHash()
    {
        var handler = new AuditHandler();
        using var store = new NeonStore("postgresql://test:test@ep-test.us-east-2.aws.neon.tech/neondb", handler);
        var world = SocietyFactory.Create("neon_large", false, 170);
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
    public async Task OrdinaryForkKeepsItsInheritedAuditPrefixAndBoundedCheckpointTransport()
    {
        var handler = new AuditHandler();
        using var store = new NeonStore("postgresql://test:test@ep-test.us-east-2.aws.neon.tech/neondb", handler);
        var parent = SocietyFactory.Create("neon_fork", false, 170);
        RunAudit.Enable(parent);
        SocietyEngine.Advance(parent);
        RunAudit.Day(parent);
        parent.Agents.Values.First().Goal = new string('x', 17 * 1024 * 1024);
        parent.Revision = 1;
        await store.Save(parent, 0, CancellationToken.None);
        var prefix = JsonSerializer.Serialize(await store.ReadAudit(parent.Id, 0, 10000, CancellationToken.None), Wire.Json);
        var auditBatchCount = handler.AuditBatchSizes.Count;
        var fork = (await store.Load(parent.Id, CancellationToken.None))!;
        RunAudit.PrepareResume(fork, false);
        fork.Emit("timeline_forked", "rule", [fork.Id], new { from_tick = fork.Tick });
        SocietyEngine.Advance(fork);
        RunAudit.Day(fork);
        fork.Status = "completed";
        RunAudit.Day(fork, terminal: true);
        fork.Revision++;
        await store.Save(fork, 1, CancellationToken.None);
        await store.Save(fork, 1, CancellationToken.None);

        Assert.True(handler.UsedChunkedCheckpoint);
        Assert.All(handler.CheckpointPartSizes, size => Assert.InRange(size, 1, 512 * 1024));
        Assert.Equal(auditBatchCount, handler.AuditBatchSizes.Count);
        Assert.Empty(fork.PendingAudit);
        Assert.Equal(prefix, JsonSerializer.Serialize(await store.ReadAudit(parent.Id, 0, 10000, CancellationToken.None), Wire.Json));
        var head = (await store.ReadAuditHead(fork.Id, CancellationToken.None))!;
        Assert.False(head.AnalyticsEnabled);
        Assert.Equal(2, head.Revision);
        Assert.Equal(parent.AuditSequence, head.AuditSequence);
        var loaded = (await store.Load(fork.Id, CancellationToken.None))!;
        Assert.False(loaded.AnalyticsEnabled);
        Assert.Equal(parent.AuditDailyThroughTick, loaded.AuditDailyThroughTick);
        Assert.Equal(parent.Agents.Values.First().Goal, loaded.Agents.Values.First().Goal);
        loaded.Revision++;
        loaded.AuditSequence++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Save(loaded, 2, CancellationToken.None));
    }

    [Fact]
    public async Task RetryingAfterInterruptedLsnRegistrationRepairsTheCheckpointMarker()
    {
        var handler = new AuditHandler { FailCheckpointRegistrationOnce = true };
        using var store = new NeonStore("postgresql://test:test@ep-test.us-east-2.aws.neon.tech/neondb", handler);
        var world = SocietyFactory.Create("neon_lsn", false, 170);
        RunAudit.Enable(world);
        world.Revision = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Save(world, 0, CancellationToken.None));
        Assert.NotEmpty(world.PendingAudit);
        Assert.Equal(1, (await store.Load(world.Id, CancellationToken.None))!.Revision);
        await store.Save(world, 0, CancellationToken.None);
        Assert.Empty(world.PendingAudit);
        Assert.Equal(1, handler.CheckpointRegistrations);
    }

    [Fact]
    public async Task SameRevisionDifferentAuditPayloadIsRejected()
    {
        var handler = new AuditHandler();
        using var store = new NeonStore("postgresql://test:test@ep-test.us-east-2.aws.neon.tech/neondb", handler);
        var world = SocietyFactory.Create("neon_conflict", false, 170);
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
        public bool FailCheckpointRegistrationOnce { get; set; }
        public int CheckpointRegistrations { get; private set; }
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
            if (sql.StartsWith("SELECT revision,COALESCE(audit_sequence,"))
            {
                if (sql.Contains("state->>'status'"))
                {
                    Assert.Contains("COALESCE(run_status,state->>'status')", sql);
                    Assert.Contains("COALESCE(analytics_enabled,(state->>'analytics_enabled')::boolean)", sql);
                }
                if (state is not null)
                {
                    var node = JsonNode.Parse(state)!;
                    rows.Add(sql.Contains("state->>'status'")
                        ? [node["revision"]!.ToString(), node["audit_sequence"]!.ToString(), node["status"]!.GetValue<string>(), node["analytics_enabled"]!.GetValue<bool>() ? "t" : "f"]
                        : [node["revision"]!.ToString(), node["audit_sequence"]!.ToString()]);
                }
            }
            else if (sql.StartsWith("INSERT INTO gearth.run_audit("))
            {
                var key = parameters[2]!;
                if (!audit.TryGetValue(key, out var staged))
                    audit[key] = staged = new();
                AuditBatchSizes.Add(Encoding.UTF8.GetByteCount(parameters[3]!));
                foreach (var row in JsonNode.Parse(parameters[3]!)!.AsArray())
                    staged.TryAdd(row!["sequence"]!.GetValue<long>(), row.ToJsonString());
            }
            else if (sql.StartsWith("INSERT INTO gearth.checkpoint_parts"))
            {
                var key = parameters[1]!;
                if (!parts.TryGetValue(key, out var staged))
                    parts[key] = staged = new();
                staged.TryAdd(int.Parse(parameters[2]!), parameters[3]!);
                CheckpointPartSizes.Add(Encoding.UTF8.GetByteCount(parameters[3]!));
            }
            else if (sql.StartsWith("WITH input AS"))
            {
                Assert.Contains("SELECT serialized::json AS state", sql);
                Assert.Contains("audit_sequence=EXCLUDED.audit_sequence,run_status=EXCLUDED.run_status,analytics_enabled=EXCLUDED.analytics_enabled", sql);
                Assert.Contains("json_array_elements(saved.state->'events')", sql);
                Assert.DoesNotContain("serialized::jsonb", sql);
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
            else if (sql.StartsWith("INSERT INTO gearth.checkpoints("))
            {
                Assert.NotNull(state);
                Assert.Equal(JsonNode.Parse(state!)!["revision"]!.GetValue<long>(), long.Parse(parameters[2]!));
                if (FailCheckpointRegistrationOnce)
                {
                    FailCheckpointRegistrationOnce = false;
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }
                CheckpointRegistrations++;
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
