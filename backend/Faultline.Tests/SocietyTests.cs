using System.Text.Json.Nodes;
using Faultline.Core;

namespace Faultline.Tests;

public sealed class SocietyTests
{
    [Fact]
    public void ThousandAgentWorldHasTenCountriesAndSeventyInstitutions()
    {
        var world = SocietyFactory.Create("society_test", false, 1000);

        Assert.Equal(1000, world.Agents.Count);
        Assert.Equal(10, world.Society!.Nations.Count);
        Assert.Equal(70, world.Agents.Values.Count(agent => agent.Role != "citizen"));
        Assert.Equal(2, Wire.Node(world.Snapshot())["run"]!["stage"]!.GetValue<int>());
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void EconomicCyclesPreserveCashAndAccountForAllGoods()
    {
        var world = SocietyFactory.Create("economy_test", false, 1000);

        for (var tick = 0; tick < 120; tick++)
            SocietyEngine.Advance(world);

        SocietyEconomy.AssertConservation(world);
        Assert.True(world.Society!.ProducedGoods["food"] > 0);
        Assert.True(world.Society.ConsumedGoods["energy"] > 0);
        Assert.Contains(world.Events, item => item.EventType == "resource_crisis");
        Assert.Contains(world.Events, item => item.EventType == "election_result");
    }

    private static DecisionResult Choose(DecisionJob job, string actionType)
    {
        var candidate = job.Candidates.First(item => item.ActionType == actionType);
        return new(job.RequestId, new(actionType, (JsonObject)candidate.Params.DeepClone(),
            "Test decision.", [job.EvidenceIds[0]]), "model", "test-model", 1);
    }

    [Fact]
    public void SchedulerBoundsRequestsAndDoesNotStarveResidents()
    {
        var world = SocietyFactory.Create("scheduler", true, 1100);
        var now = DateTimeOffset.UtcNow;
        var pending = world.Agents.Values.Take(10).Select(agent => agent.Id).ToHashSet();
        var selected = SocietyScheduler.Select(world, pending, 64, now);
        Assert.Equal(64, selected.Length);
        Assert.Contains(selected, agent => agent.Role == "government");
        Assert.Contains(selected, agent => agent.Role == "citizen");
        Assert.All(selected, agent => Assert.DoesNotContain(agent.Id, pending));
        Assert.Empty(SocietyScheduler.Select(world, pending, 0, now));
        var resident = selected.First(agent => agent.Role == "citizen");
        resident.LastAttemptAt = now;
        Assert.False(SocietyScheduler.IsDue(world, resident, now.AddSeconds(599)));
        Assert.True(SocietyScheduler.IsDue(world, resident, now.AddSeconds(600)));
    }

    [Fact]
    public void IntentSurvivesRoutineEconomyButReplayCannotApplyTwice()
    {
        var world = SocietyFactory.Create("intent", true, 1000);
        var now = DateTimeOffset.UtcNow;
        var job = SocietyEngine.Observe(world, "aster_citizen_00001", now);
        var result = Choose(job, "household_policy");
        for (var tick = 0; tick < 5; tick++)
            SocietyEngine.Advance(world);
        Assert.True(SocietyEngine.Apply(world, job, result, now.AddSeconds(10)).Accepted);
        world.Decisions.Clear();
        var balance = world.Agents[job.AgentId].CashMinor;
        Assert.False(SocietyEngine.Apply(world, job, result, now.AddSeconds(11)).Accepted);
        Assert.Equal(balance, world.Agents[job.AgentId].CashMinor);
        Assert.Equal(1, world.Society!.ModelDecisionCount);
    }

    [Fact]
    public void MediaReportKeepsItsObservationTickWhenEconomyChanges()
    {
        var world = SocietyFactory.Create("media", true, 1000);
        var now = DateTimeOffset.UtcNow;
        var job = SocietyEngine.Observe(world, "aster_journalist", now);
        for (var tick = 0; tick < 5; tick++)
            SocietyEngine.Advance(world);
        Assert.True(SocietyEngine.Apply(world, job, Choose(job, "publish"), now.AddSeconds(10)).Accepted);
        var report = world.Events.Last(item => item.EventType == "public_report");
        Assert.Equal(0, report.Payload["report_tick"]!.GetValue<int>());
    }

    [Fact]
    public void EscrowMovesGoodsAndPaysSellerOnlyAfterDelivery()
    {
        var world = SocietyFactory.Create("shipping", false, 1000);
        var buyer = world.Agents["aster_food"];
        var seller = world.Agents["beryl_food"];
        var sellerCash = seller.CashMinor;
        var buyerStock = buyer.Inventory["food"];
        SocietyTrade.Dispatch(world, buyer, seller, "food", 10, "fastest", "test_cause");
        var cargo = Assert.Single(world.Society!.Cargo);
        Assert.Equal(sellerCash, seller.CashMinor);
        Assert.True(cargo.EscrowMinor > 0);
        SocietyEconomy.AssertConservation(world);
        while (cargo.Status == "in_transit")
        {
            world.Tick++;
            SocietyTrade.Advance(world);
        }
        Assert.Equal("delivered", cargo.Status);
        Assert.Equal(buyerStock + 10, buyer.Inventory["food"]);
        Assert.Equal(sellerCash + seller.PriceMinor * 10, seller.CashMinor);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void BlockedShipmentRefundsEscrowAndAccountsForLostCargo()
    {
        var world = SocietyFactory.Create("blocked", false, 1000);
        var buyer = world.Agents["aster_food"];
        var seller = world.Agents["beryl_food"];
        SocietyTrade.Dispatch(world, buyer, seller, "food", 10, "fastest", "test_cause");
        var cargo = Assert.Single(world.Society!.Cargo);
        var escrow = cargo.EscrowMinor;
        var cash = buyer.CashMinor;
        SocietyDiplomacy.Relation(world, buyer.CountryId, seller.CountryId).AtWar = true;
        world.Tick = cargo.DeadlineTick;
        SocietyTrade.Advance(world);
        Assert.Equal("failed", cargo.Status);
        Assert.Equal(cash + escrow, buyer.CashMinor);
        Assert.Equal(10, world.Society.LostGoods["food"]);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void DifferentInstitutionsEnactTheSamePolicyWithDifferentDelays()
    {
        var world = SocietyFactory.Create("policy", false, 1000);
        var action = new ActionProposal("set_tax", new JsonObject { ["tax_basis_points"] = 500 }, "Test policy.", []);
        SocietyDiplomacy.Propose(world, world.Agents["aster_government"], action, "cause");
        SocietyDiplomacy.Propose(world, world.Agents["dune_government"], action, "cause");
        Assert.Equal(500, world.Society!.Nations["dune"].TaxBasisPoints);
        Assert.Equal(1500, world.Society.Nations["aster"].TaxBasisPoints);
        for (var tick = 0; tick < 3; tick++)
            SocietyEngine.Advance(world);
        Assert.Equal(500, world.Society.Nations["aster"].TaxBasisPoints);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public async Task EventArchiveSurvivesCheckpointWindowTrimming()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalStore(directory);
            await store.Initialize(CancellationToken.None);
            var world = SocietyFactory.Create("archive_test", false, 1000);
            world.Revision = 1;
            await store.Save(world, 0, CancellationToken.None);
            world.Events.Clear();
            world.Emit("test_later", "rule", [world.Id], new { value = 1 });
            world.Revision = 2;
            await store.Save(world, 1, CancellationToken.None);
            var events = await store.ReadEvents(world.Id, 0, 100, CancellationToken.None);
            Assert.Equal(2, events.Length);
            Assert.Equal("world_initialized", events[0].EventType);
            Assert.Equal("test_later", events[1].EventType);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FundedCreditSettlesOnceWithExplicitDefaultOrRepayment(bool repay)
    {
        var world = SocietyFactory.Create("credit_test", true, 1000);
        var now = DateTimeOffset.UtcNow;
        var borrower = world.Agents["aster_tools"];
        var bank = world.Agents["aster_bank"];
        var government = world.Agents["aster_government"];
        SocietyEconomy.Transfer(borrower, government, borrower.CashMinor - 100);
        var request = SocietyEngine.Observe(world, borrower.Id, now);
        Assert.True(SocietyEngine.Apply(world, request, Choose(request, "request_loan"), now).Accepted);
        var loan = Assert.Single(world.Society!.Credit);
        var approval = SocietyEngine.Observe(world, bank.Id, now);
        var bankCash = bank.CashMinor;
        var result = Choose(approval, "approve_loan");
        Assert.True(SocietyEngine.Apply(world, approval, result, now).Accepted);
        SocietyEngine.Apply(world, approval, result, now);
        Assert.Equal(bankCash - loan.PrincipalMinor, bank.CashMinor);
        if (repay)
            SocietyEconomy.Transfer(government, borrower, loan.DueMinor);
        else
        {
            SocietyEconomy.Transfer(borrower, government, borrower.CashMinor);
            bank.Inventory["tools"] += borrower.Inventory["tools"];
            borrower.Inventory["tools"] = 0;
        }
        foreach (var citizen in world.Agents.Values.Where(agent => agent.EmployerId == borrower.Id))
            citizen.WorkEffort = 0;
        world.Tick = loan.DueTick;
        SocietyEconomy.Cycle(world);
        Assert.Equal(repay ? "repaid" : "defaulted", loan.Status);
        Assert.Single(world.Events, item => item.EventType == "loan_settled");
        world.Tick += 5;
        SocietyEconomy.Cycle(world);
        Assert.Single(world.Events, item => item.EventType == "loan_settled");
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void SingleSlotSelectionsPreserveResidentShareUnderInstitutionalBacklog()
    {
        var world = SocietyFactory.Create("fairness", true, 1100);
        var selected = Enumerable.Range(0, 10).Select(_ =>
            Assert.Single(SocietyScheduler.Select(world, new HashSet<string>(), 1, DateTimeOffset.UtcNow))).ToArray();
        Assert.Equal(3, selected.Count(agent => agent.Role != "citizen"));
        Assert.Equal(7, selected.Count(agent => agent.Role == "citizen"));
    }

    [Fact]
    public void FailedResidentTurnCanRetryWithoutWaitingTenMinutes()
    {
        var world = SocietyFactory.Create("retry", true, 1100);
        var now = DateTimeOffset.UtcNow;
        var agent = world.Agents["aster_citizen_00001"];
        var job = SocietyEngine.Observe(world, agent.Id, now);
        agent.LastAttemptAt = now;
        var result = new DecisionResult(job.RequestId, new("hold", new JsonObject(), "Timed out.", []),
            "rule_fallback", "none", 0, "TimeoutError");
        SocietyEngine.Apply(world, job, result, now);
        Assert.False(SocietyScheduler.IsDue(world, agent, now.AddSeconds(29)));
        Assert.True(SocietyScheduler.IsDue(world, agent, now.AddSeconds(30)));
    }
}
