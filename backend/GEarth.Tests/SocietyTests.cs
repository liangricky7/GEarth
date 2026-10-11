using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Tests;

public sealed class SocietyTests
{
    [Fact]
    public void ThousandAgentWorldHasTenCountriesAndCompetingFirms()
    {
        var world = SocietyFactory.Create("society_test", false, 1000);

        Assert.Equal(1000, world.Agents.Count);
        Assert.Equal(10, world.Society!.Nations.Count);
        // Government, media and fourteen firms per country: 3 farms, 2 each of energy, tools and
        // entertainment, and one pharma, clinic, school, freight firm and bank.
        Assert.Equal(160, world.Agents.Values.Count(agent => agent.Role is "government" or "bank" or "media" or "business"));
        Assert.Equal(3, SocietyLabor.Firms(world, "aster", "food").Count());
        // Market countries' firms each have an entrepreneur owner; planned economies' firms and every school are state-owned.
        Assert.Equal(104, world.Agents.Values.Count(agent => agent.Role == "entrepreneur"));
        // Doctors are scarce: one employed doctor per country (plus the clinic's owner in market economies).
        Assert.All(world.Society.Nations.Keys, country => Assert.Equal(1,
            world.Agents.Values.Count(agent => agent.CountryId == country && agent.Role == "citizen" && agent.Occupation == "doctor")));
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
    public void EveryInstitutionDecidesWeeklyAndCitizensRotateOncePerCycle()
    {
        var world = SocietyFactory.Create("scheduler", true, 1100);
        var deciders = world.Agents.Values.Where(SocietyLabor.DecidesForItself).ToArray();
        var turns = new Dictionary<string, int>();
        for (var week = 0; week < SocietyScheduler.CitizenRotationWeeks; week++)
        {
            world.Tick = week * SocietyLabor.DaysPerWeek;
            var cohort = SocietyScheduler.WeeklyCohort(world);
            Assert.Equal("government", cohort[0].Role); // governments are served first
            Assert.All(deciders.Where(agent => agent.Role is not ("citizen" or "planner")), agent => Assert.Contains(agent, cohort));
            // The planned economies' planners set the plan every four weeks.
            Assert.Equal(week % SocietyPlanning.PlanningWeeks == 0 ? 2 : 0, cohort.Count(agent => agent.Role == "planner"));
            Assert.InRange(cohort.Count(agent => agent.Role == "citizen"), 60, 160);
            foreach (var agent in cohort)
                turns[agent.Id] = turns.GetValueOrDefault(agent.Id) + 1;
        }
        Assert.All(deciders.Where(agent => agent.Role == "citizen"), agent => Assert.Equal(1, turns[agent.Id]));
        Assert.DoesNotContain(world.Agents.Values.Where(agent => agent.Role == "business" && agent.OwnerId is not null),
            agent => turns.ContainsKey(agent.Id)); // owned firms act through their entrepreneur
    }

    [Fact]
    public void DecisionOnAViewOlderThanAWeekExpires()
    {
        var world = SocietyFactory.Create("expiry", true, 1000);
        var now = DateTimeOffset.UtcNow;
        var job = SocietyEngine.Observe(world, "aster_citizen_00001", now);
        for (var tick = 0; tick <= SocietyEngine.DecisionMaxAgeDays; tick++)
            SocietyEngine.Advance(world);
        var record = SocietyEngine.Apply(world, job, Choose(job, "household_policy"), now.AddSeconds(10));
        Assert.False(record.Accepted);
        Assert.Equal("EXPIRED_PROPOSAL", record.Code);
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
        var action = new ActionProposal("adjust_tax", new JsonObject { ["change_basis_points"] = -50 }, "Test policy.", []);
        SocietyDiplomacy.Propose(world, world.Agents["aster_government"], action, "cause");
        SocietyDiplomacy.Propose(world, world.Agents["dune_government"], action, "cause");
        Assert.Equal(950, world.Society!.Nations["dune"].TaxBasisPoints); // the dictatorship acts at once
        Assert.Equal(1500, world.Society.Nations["aster"].TaxBasisPoints);
        for (var tick = 0; tick < 3; tick++)
            SocietyEngine.Advance(world);
        Assert.Equal(1450, world.Society.Nations["aster"].TaxBasisPoints);
        Assert.Equal("adjust_tax", world.Society.Nations["aster"].LastPolicyAction);
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
}
