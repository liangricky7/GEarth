using System.Text.Json;
using System.Text.Json.Nodes;
using Faultline.Core;

namespace Faultline.Tests;

public sealed class AuditTests
{
    [Fact]
    public async Task FailedCheckpointCannotExposeStagedAuditAndRetryIsIdempotent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "audit_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalStore(directory);
            await store.Initialize(CancellationToken.None);
            var world = SocietyFactory.Create("audit_retry", false, 160);
            RunAudit.Enable(world);
            world.Revision = 1;
            await store.Save(world, 0, CancellationToken.None);
            var committedSequence = world.AuditSequence;
            Assert.Empty(world.PendingAudit);
            var copy = Wire.Copy(world);
            Assert.Equal(committedSequence, copy.AuditSequence);
            RunAudit.Append(world, "transaction", new { amount_minor = 7 });
            world.Revision = 2;
            var pending = world.PendingAudit.ToArray();
            var blockedTemporaryPath = Path.Combine(directory, world.Id + ".json.tmp");
            Directory.CreateDirectory(blockedTemporaryPath);
            Assert.True(await Record.ExceptionAsync(() => store.Save(world, 1, CancellationToken.None))
                is IOException or UnauthorizedAccessException);
            Assert.NotEmpty(world.PendingAudit);
            Assert.Empty(await store.ReadAudit(world.Id, committedSequence, 100, CancellationToken.None));
            var head = (await store.ReadAuditHead(world.Id, CancellationToken.None))!;
            Assert.Equal(1, head.Revision);
            Assert.Equal(committedSequence, head.AuditSequence);
            Directory.Delete(blockedTemporaryPath);
            await store.Save(world, 1, CancellationToken.None);
            world.PendingAudit.AddRange(pending);
            await store.Save(world, 1, CancellationToken.None);
            var row = Assert.Single(await store.ReadAudit(world.Id, committedSequence, 100, CancellationToken.None));
            Assert.Equal(2, row.Revision);
            Assert.Equal(7, row.Payload["amount_minor"]!.GetValue<int>());
            Assert.Empty(world.PendingAudit);
            world.PendingAudit.Add(pending[0] with { Payload = new JsonObject { ["amount_minor"] = 9 } });
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.Save(world, 1, CancellationToken.None));
            var serialized = await File.ReadAllTextAsync(Path.Combine(directory, world.Id + ".json"));
            Assert.DoesNotContain("pending_audit", serialized);
            Assert.DoesNotContain("current_audit_context", serialized);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LosingStagedRevisionDoesNotMixWithWinningCheckpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "audit_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalStore(directory);
            await store.Initialize(CancellationToken.None);
            var world = SocietyFactory.Create("audit_conflict", false, 160);
            RunAudit.Enable(world);
            world.Revision = 1;
            await store.Save(world, 0, CancellationToken.None);
            var cursor = world.AuditSequence;
            var winner = Wire.Copy(world);
            world.Revision++;
            RunAudit.Append(world, "transaction", new { amount_minor = 99 });
            var blocked = Path.Combine(directory, world.Id + ".json.tmp");
            Directory.CreateDirectory(blocked);
            Assert.True(await Record.ExceptionAsync(() => store.Save(world, 1, CancellationToken.None))
                is IOException or UnauthorizedAccessException);
            Directory.Delete(blocked);
            winner.Revision++;
            RunAudit.Append(winner, "transaction", new { amount_minor = 12 });
            await store.Save(winner, 1, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.Save(world, 1, CancellationToken.None));
            var row = Assert.Single(await store.ReadAudit(world.Id, cursor, 100, CancellationToken.None));
            Assert.Equal(12, row.Payload["amount_minor"]!.GetValue<int>());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CopyingAnUncommittedWorldCannotSilentlyDropItsAudit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "audit_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalStore(directory);
            await store.Initialize(CancellationToken.None);
            var world = SocietyFactory.Create("audit_missing", false, 160);
            RunAudit.Enable(world);
            world.Revision = 1;
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.Save(Wire.Copy(world), 0, CancellationToken.None));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void IndividualTransfersReconcileWhileVerboseAggregationRemainsCompatible()
    {
        var world = SocietyFactory.Create("audit_money", false, 1100);
        RunAudit.Enable(world);
        world.Flows = new();
        var from = world.Agents.Values.First(agent => agent.Role == "citizen");
        var to = world.Agents[from.CountryId + "_government"];
        var initial = from.CashMinor;
        SocietyEconomy.Transfer(world, from, to, 7, "gift");
        SocietyEconomy.Transfer(world, from, to, 11, "gift");
        SocietyEconomy.Transfer(world, from, to, 0, "gift");
        var rows = world.PendingAudit.Where(row => row.Kind == "transaction").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal([7L, 11L], rows.Select(row => row.Payload["amount_minor"]!.GetValue<long>()));
        Assert.All(rows, row => Assert.Equal("unknown", row.Payload["causal_source"]!.GetValue<string>()));
        Assert.Equal((18L, 2), world.Flows[(from.Id, to.Id, "gift")]);
        Assert.Equal(initial - 18, from.CashMinor);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void OriginalContextSurvivesDecisionCompactionAndEffectsAreLinked()
    {
        var world = SocietyFactory.Create("audit_context", false, 160);
        RunAudit.Enable(world);
        var agent = world.Agents.Values.First(agent => agent.Role == "citizen");
        var now = DateTimeOffset.UtcNow;
        string? first = null;
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var job = SocietyEngine.Observe(world, agent.Id, now);
            first ??= job.RequestId;
            var candidate = job.Candidates.First(candidate => candidate.ActionType == "household_policy");
            RunAudit.Request(world, job, now);
            var result = new DecisionResult(job.RequestId,
                new(candidate.ActionType, (JsonObject)candidate.Params.DeepClone(), "Test standing policy.", []), "model", "test", 1);
            Assert.True(SocietyEngine.Apply(world, job, result, now).Accepted);
        }
        Assert.Equal(256, world.Decisions.Count);
        Assert.DoesNotContain(first!, world.Decisions.Keys);
        var requests = world.PendingAudit.Where(row => row.Kind == "decision_request").ToArray();
        Assert.Equal(300, requests.Length);
        Assert.NotEmpty(requests[0].Payload["job"]!["candidates"]!.AsArray());
        Assert.NotEmpty(requests[0].Payload["job"]!["observation"]!.AsObject());
        var results = world.PendingAudit.Where(row => row.Kind == "decision_result").ToArray();
        Assert.Equal(300, results.Length);
        var effect = world.PendingAudit.First(row => row.Kind == "action_effect");
        Assert.Equal(first, effect.Payload["decision_id"]!.GetValue<string>());
        Assert.Equal(results[0].Payload["action_event_id"]!.GetValue<string>(), effect.Payload["event_id"]!.GetValue<string>());
        Assert.NotEqual(effect.Payload["before"]!["target_stock"]!.GetValue<int>(), effect.Payload["after"]!["target_stock"]!.GetValue<int>());
    }

    [Fact]
    public void OpeningDailyAndTerminalFramesPreserveTickBoundaryAndCancellation()
    {
        var world = SocietyFactory.Create("audit_days", false, 160);
        RunAudit.Enable(world);
        RunAudit.Day(world);
        Assert.DoesNotContain(world.PendingAudit, row => row.Kind == "agent_day");
        Assert.Equal(world.Agents.Count, world.PendingAudit.Count(row => row.Kind == "opening_balance"));
        SocietyEngine.Advance(world);
        var agent = world.Agents.Values.First(agent => agent.Role == "citizen");
        var job = SocietyEngine.Observe(world, agent.Id, DateTimeOffset.UtcNow);
        RunAudit.Request(world, job, DateTimeOffset.UtcNow);
        RunAudit.Canceled(world, job);
        RunAudit.Day(world);
        RunAudit.Day(world);
        world.Status = "stopped";
        RunAudit.Day(world, terminal: true);
        var days = world.PendingAudit.Where(row => row.Kind == "agent_day" && row.Payload["frame_kind"]!.GetValue<string>() == "day").ToArray();
        var ends = world.PendingAudit.Where(row => row.Kind == "agent_day" && row.Payload["frame_kind"]!.GetValue<string>() == "end").ToArray();
        Assert.Equal(world.Agents.Count, days.Length);
        Assert.Equal(world.Agents.Count, ends.Length);
        Assert.All(days, row => Assert.Equal(1, row.Tick));
        Assert.Equal(1, world.AuditDailyThroughTick);
        var canceled = Assert.Single(world.PendingAudit, row => row.Kind == "decision_result");
        Assert.Equal("canceled", canceled.Payload["status"]!.GetValue<string>());
        Assert.Null(canceled.Payload["applied_tick"]);
    }

    [Fact]
    public void FallbackAndRejectedResultsRetainTheirDifferentOutcomes()
    {
        var world = SocietyFactory.Create("audit_outcomes", false, 160);
        RunAudit.Enable(world);
        var agent = world.Agents.Values.First(agent => agent.Role == "citizen");
        var now = DateTimeOffset.UtcNow;
        var fallback = SocietyEngine.Observe(world, agent.Id, now);
        var stale = SocietyEngine.Observe(world, agent.Id, now);
        RunAudit.Request(world, fallback, now);
        RunAudit.Request(world, stale, now);
        SocietyEngine.Apply(world, fallback, new(fallback.RequestId,
            new("hold", new JsonObject(), "Inference unavailable.", []), "rule_fallback", "none", 0, "HttpRequestException"), now);
        SocietyEngine.Apply(world, stale, new(stale.RequestId,
            new("hold", new JsonObject(), "Old view.", []), "model", "test", 5), now);
        var results = world.PendingAudit.Where(row => row.Kind == "decision_result").ToArray();
        Assert.True(results[0].Payload["fallback"]!.GetValue<bool>());
        Assert.True(results[0].Payload["accepted"]!.GetValue<bool>());
        Assert.Equal("HttpRequestException", results[0].Payload["result"]!["error"]!.GetValue<string>());
        Assert.False(results[1].Payload["accepted"]!.GetValue<bool>());
        Assert.Equal("STALE_AGENT_INTENT", results[1].Payload["code"]!.GetValue<string>());
        Assert.Equal("rejected", results[1].Payload["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ResumeDisablesInheritedCaptureAndRejectsExplicitAnalytics(bool inherited, bool requested)
    {
        var world = SocietyFactory.Create("audit_resume", false, 160);
        if (inherited)
        {
            RunAudit.Enable(world);
            SocietyEngine.Advance(world);
            RunAudit.Day(world);
            world = Wire.Copy(world);
        }
        var before = JsonSerializer.Serialize(world, Wire.Json);
        if (requested)
        {
            Assert.Throws<AnalyticsTimelineException>(() => RunAudit.PrepareResume(world, requested));
            Assert.Equal(before, JsonSerializer.Serialize(world, Wire.Json));
        }
        else
        {
            RunAudit.PrepareResume(world, requested);
            Assert.False(world.AnalyticsEnabled);
            var expected = JsonNode.Parse(before)!.AsObject();
            expected["analytics_enabled"] = false;
            Assert.True(JsonNode.DeepEquals(expected, Wire.Node(world)));
        }
    }

    [Fact]
    public void ResumeCannotDiscardUncommittedAuditRecords()
    {
        var world = SocietyFactory.Create("audit_resume_pending", false, 160);
        RunAudit.Enable(world);
        Assert.Throws<InvalidOperationException>(() => RunAudit.PrepareResume(world, false));
        Assert.True(world.AnalyticsEnabled);
        Assert.NotEmpty(world.PendingAudit);
    }

    [Fact]
    public void CaptureDoesNotChangeSimulationStateOrMoney()
    {
        var plain = SocietyFactory.Create("audit_equivalence", false, 160);
        var audited = Wire.Copy(plain);
        RunAudit.Enable(audited);
        for (var day = 1; day <= 14; day++)
        {
            SocietyEngine.Advance(plain);
            SocietyEngine.Advance(audited);
            if (day == 1 || day % SocietyLabor.DaysPerWeek == 0)
                foreach (var agent in SocietyScheduler.WeeklyCohort(plain))
                {
                    var job = SocietyEngine.Observe(plain, agent.Id, DateTimeOffset.UnixEpoch);
                    var candidate = job.Candidates[(day + agent.Id.Length) % job.Candidates.Length];
                    var result = new DecisionResult(job.RequestId,
                        new(candidate.ActionType, (JsonObject)candidate.Params.DeepClone(), "Same rule choice.", []), "rule", "none", 0);
                    RunAudit.Request(audited, job, DateTimeOffset.UnixEpoch);
                    SocietyEngine.Apply(plain, job, result, DateTimeOffset.UnixEpoch);
                    SocietyEngine.Apply(audited, job, result, DateTimeOffset.UnixEpoch);
                }
            RunAudit.Day(audited);
        }
        JsonObject State(World world)
        {
            var state = Wire.Node(world).AsObject();
            foreach (var key in state.Select(item => item.Key).Where(key => key.StartsWith("audit_") || key == "analytics_enabled").ToArray())
                state.Remove(key);
            return state;
        }
        Assert.True(JsonNode.DeepEquals(State(plain), State(audited)));
        SocietyEconomy.AssertConservation(audited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscrowDeliveryAndRefundEachRecordActualAccountLeg(bool refund)
    {
        var world = SocietyFactory.Create("audit_escrow", false, 1100);
        RunAudit.Enable(world);
        var buyer = world.Agents.Values.First(agent => agent.Role == "business" && agent.CountryId == "aster" && agent.Specialty == "food");
        var seller = world.Agents.Values.First(agent => agent.Role == "business" && agent.CountryId == "beryl" && agent.Specialty == "food");
        var before = world.Agents.Values.ToDictionary(agent => agent.Id, agent => agent.CashMinor);
        SocietyTrade.Dispatch(world, buyer, seller, "food", 10, "fastest", "known_action_event");
        var cargo = Assert.Single(world.Society!.Cargo);
        if (refund)
            world.Tick = cargo.DeadlineTick;
        else
            cargo.LocationId = cargo.DestinationId;
        SocietyTrade.Advance(world);
        var transactions = world.PendingAudit.Where(row => row.Kind == "transaction").ToArray();
        var escrow = Assert.Single(transactions, row => row.Payload["kind"]!.GetValue<string>() == "import_escrow");
        var release = Assert.Single(transactions, row => row.Payload["kind"]!.GetValue<string>() == (refund ? "import_refund" : "import"));
        Assert.Equal(cargo.Id, escrow.Payload["recipient_account_id"]!.GetValue<string>());
        Assert.Equal(cargo.Id, release.Payload["sender_account_id"]!.GetValue<string>());
        Assert.Equal(escrow.Payload["amount_minor"]!.GetValue<long>(), release.Payload["amount_minor"]!.GetValue<long>());
        Assert.All(transactions, row => Assert.Equal("known_action_event", row.Payload["event_id"]!.GetValue<string>()));
        Assert.All(transactions, row => Assert.Equal(cargo.Id, row.Payload["transaction_group_id"]!.GetValue<string>()));
        foreach (var agent in world.Agents.Values)
        {
            var incoming = transactions.Where(row => row.Payload["recipient_account_id"]!.GetValue<string>() == agent.Id).Sum(row => row.Payload["amount_minor"]!.GetValue<long>());
            var outgoing = transactions.Where(row => row.Payload["sender_account_id"]!.GetValue<string>() == agent.Id).Sum(row => row.Payload["amount_minor"]!.GetValue<long>());
            Assert.Equal(agent.CashMinor - before[agent.Id], incoming - outgoing);
        }
        Assert.Equal(0, cargo.EscrowMinor);
        SocietyEconomy.AssertConservation(world);
    }
}
