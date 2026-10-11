using GEarth.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GEarth.Tests;

// Regressions reproduced during the adversarial review, exercised against the current economy.
public sealed class Phase3WarTests
{
    private static World NewWorld(bool live = false) => SocietyFactory.Create("phase3_regression", live, 1100);

    private static PolicyProposal Policy(string action, object parameters) => new()
    {
        ActionType = action, Params = SocietyActions.Make(action, parameters).Params, CauseId = "phase3_test"
    };

    private static Relationship War(World world, string first, string second)
    {
        var relation = SocietyDiplomacy.Relation(world, first, second);
        relation.AtWar = true;
        relation.Score = -100;
        relation.Aggressor = first;
        return relation;
    }

    private static void Disarm(World world, string country)
    {
        foreach (var soldier in SocietyWar.Soldiers(world, world.Society!.Nations[country]))
            soldier.WorkEffort = 0;
    }

    [Fact]
    public void MissileStockDoesNotPreventSurrenderThroughTheDailyEngine()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var war = War(world, "aster", "beryl");
        Disarm(world, nation.Id);
        nation.Missiles = 1;
        for (var day = 0; day < 14; day++)
            SocietyEngine.Advance(world);

        Assert.False(war.AtWar);
        Assert.Equal(1, nation.Missiles);
        Assert.Contains(world.Events, item => item.EventType == "war_ended"
            && (string?)item.Payload["reason"] == "surrender");
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void BothOngoingWarsEndWhenTheSameCountryReachesExhaustion()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var first = War(world, "aster", "beryl");
        var second = War(world, "aster", "cinder");
        Disarm(world, nation.Id);
        nation.NoFitSoldierWeeks = 1;
        world.Tick = 14;

        SocietyWar.Day(world);

        Assert.False(first.AtWar);
        Assert.False(second.AtWar);
        Assert.Equal(2, world.Events.Count(item => item.EventType == "war_ended"));
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void AnAcceptedPeaceDoesNotEraseExhaustionAgainstAnotherOpponent()
    {
        var world = NewWorld();
        var aster = world.Society!.Nations["aster"];
        var first = War(world, "aster", "beryl");
        var second = War(world, "aster", "cinder");
        Disarm(world, aster.Id);
        aster.NoFitSoldierWeeks = 1;

        SocietyWar.EndWar(world, first, world.Society.Nations["beryl"], aster, "none", "peace_accepted");
        Assert.Equal(1, aster.NoFitSoldierWeeks);
        world.Tick = 14;
        SocietyWar.Day(world);
        Assert.False(second.AtWar);
    }

    [Fact]
    public void VoluntaryStrikeContributesNeitherCombatStrengthNorPaidInternalOrder()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["dune"];
        var soldier = SocietyWar.Soldiers(world, nation).First();
        foreach (var peer in SocietyWar.Soldiers(world, nation))
            peer.Health = 100;
        var before = SocietyWar.Strength(world, nation);
        soldier.WorkEffort = 0;
        soldier.ArrearsMinor = 0;
        soldier.WeekWorkDays = 7;
        world.Tick = 7;
        SocietyEconomy.Week(world);

        Assert.Equal(before - 1, SocietyWar.Strength(world, nation));
        Assert.False(SocietyWar.Paid(world, soldier));
        Assert.Equal(0, soldier.ArrearsMinor);
        Assert.Equal(-1, soldier.MilitaryPaidTick);
        Assert.Equal(0, soldier.MilitaryPayMinor);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void ActualMilitaryPayrollIsRequiredAndExpiresAfterTheCompletedWeekWindow()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["dune"];
        var soldier = SocietyWar.Soldiers(world, nation).First();
        soldier.WorkEffort = 1;
        soldier.Health = 100;
        soldier.ArrearsMinor = 0;
        soldier.WeekWorkDays = 7;
        Assert.False(SocietyWar.Paid(world, soldier));
        world.Tick = 7;
        SocietyEconomy.Week(world);
        Assert.True(SocietyWar.Paid(world, soldier));
        world.Tick = 14;
        Assert.True(SocietyWar.Paid(world, soldier)); // War settles before the next payroll.
        world.Tick = 15;
        Assert.False(SocietyWar.Paid(world, soldier));
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void MilitaryOrdersAdvanceFundingAndReserveAllOutstandingProjects()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["dune"];
        var government = world.Agents["dune_government"];
        var contractor = world.Agents["dune_construction"];
        var treasury = government.CashMinor;
        var contractorCash = contractor.CashMinor;
        Assert.True(SocietyWar.Enact(world, nation, Policy("order_missile", new { })));
        var missile = Assert.Single(nation.Infrastructure);
        Assert.Equal(missile.CostMinor / 2, missile.PaidMinor);
        Assert.Equal(missile.PaidMinor, treasury - government.CashMinor);
        Assert.Equal(missile.PaidMinor, contractor.CashMinor - contractorCash);
        Assert.False(SocietyWar.Enact(world, nation, Policy("order_missile", new { })));

        var turretCost = PhaseContracts.CostOf("turret", 1);
        var insufficientForBoth = turretCost + missile.CostMinor - missile.PaidMinor - 1;
        SocietyEconomy.Transfer(world, government, world.Agents["dune_bank"], government.CashMinor - insufficientForBoth);
        var cash = government.CashMinor;
        Assert.False(SocietyWar.Enact(world, nation, Policy("build_turret", new { })));
        Assert.Equal(cash, government.CashMinor);
        Assert.Single(nation.Infrastructure);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void AnIdleContractorBuildsAnOrderedMissileAndCheckpointReplayMatches()
    {
        var world = NewWorld();
        for (var day = 0; day < 56; day++)
            SocietyEngine.Advance(world);
        var nation = world.Society!.Nations["dune"];
        var contractor = world.Agents["dune_construction"];
        Assert.Equal(0, SocietyBank.Liquid(world, contractor));
        Assert.True(SocietyWar.Enact(world, nation, Policy("order_missile", new { })));
        var project = Assert.Single(nation.Infrastructure);
        foreach (var recruit in world.InCountry("dune").Where(person => person.Role == "citizen" && person.EmployerId is null).Take(2).ToArray())
            SocietyLabor.Hire(world, contractor, recruit, "builder");
        for (var day = 0; day < 3; day++)
            SocietyEngine.Advance(world);
        var copy = Wire.Copy(world);
        for (var day = 0; day < 25; day++)
        {
            SocietyEngine.Advance(world);
            SocietyEngine.Advance(copy);
        }
        Assert.True(project.Complete);
        Assert.Equal(project.CostMinor, project.PaidMinor);
        Assert.Equal(1, nation.Missiles);
        Assert.DoesNotContain(nation.Infrastructure, item => item.Type == "missile");
        Assert.True(JsonNode.DeepEquals(Wire.Node(world.Society), Wire.Node(copy.Society!)),
            "Checkpoint replay must retain the same economic state irrespective of dictionary insertion order.");
        SocietyEconomy.AssertConservation(world);
        SocietyEconomy.AssertConservation(copy);
    }

    [Fact]
    public void PeacefulRawGiftDeliversTheExactQuantityAndCompensatesPrivateSuppliers()
    {
        var world = NewWorld();
        var donor = world.Society!.Nations["aster"];
        var holders = world.InCountry(donor.Id).Where(person => person.Role == "business"
            && person.Inventory.GetValueOrDefault("ore") > 0).ToArray();
        Assert.All(holders, holder => Assert.NotNull(holder.OwnerId));
        var before = holders.ToDictionary(holder => holder.Id,
            holder => (Stock: holder.Inventory["ore"], Cash: SocietyBank.Liquid(world, holder)));
        var received = world.InCountry("beryl").Sum(person => person.Inventory.GetValueOrDefault("ore"));

        Assert.True(SocietyWar.Enact(world, donor,
            Policy("send_gift", new { country_id = "beryl", kind = "ore", amount = 10 })));

        Assert.Equal(10, world.InCountry("beryl").Sum(person => person.Inventory.GetValueOrDefault("ore")) - received);
        Assert.Equal(10, holders.Sum(holder => before[holder.Id].Stock - holder.Inventory["ore"]));
        foreach (var holder in holders.Where(holder => holder.Inventory["ore"] < before[holder.Id].Stock))
            Assert.True(SocietyBank.Liquid(world, holder) > before[holder.Id].Cash, "Peaceful gifts must compensate a private supplier.");
        var sent = Assert.Single(world.Events, item => item.EventType == "gift_sent");
        Assert.Equal(10, sent.Payload["amount"]!.GetValue<int>());
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void InsufficientGiftFundingCannotPartlyConfiscatePrivateInputs()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var government = world.Agents["aster_government"];
        SocietyEconomy.Transfer(world, government, world.Agents["aster_bank"], SocietyBank.Liquid(world, government));
        var stocks = world.Agents.Values.ToDictionary(person => person.Id, person => person.Inventory["ore"]);

        Assert.False(SocietyWar.Enact(world, nation,
            Policy("send_gift", new { country_id = "beryl", kind = "ore", amount = 10 })));

        Assert.All(world.Agents.Values, person => Assert.Equal(stocks[person.Id], person.Inventory["ore"]));
        Assert.DoesNotContain(world.Events, item => item.EventType == "gift_sent");
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void EveryActiveWarHasAnAccessiblePeaceChoiceEvenAfterTheFourthOpponent()
    {
        var world = NewWorld(true);
        var enemies = world.Society!.Nations.Keys.Where(id => id != "aster").Take(6).ToArray();
        foreach (var enemy in enemies)
            War(world, "aster", enemy);
        // An incoming offer must also remain answerable, even when it ranks after four other wars.
        var offered = SocietyDiplomacy.Relation(world, "aster", enemies[^1]);
        offered.ProposedBy = enemies[^1];
        offered.ProposalKind = "peace";
        offered.ProposalExpiresTick = 60;
        var options = SocietyActions.Available(world, world.Agents["aster_government"]);

        foreach (var enemy in enemies[..^1])
            Assert.Contains(options, candidate => candidate.ActionType == "offer_peace"
                && (string?)candidate.Params["country_id"] == enemy);
        Assert.Contains(options, candidate => candidate.ActionType == "respond_agreement"
            && (string?)candidate.Params["country_id"] == enemies[^1]
            && candidate.Params["accept"]!.GetValue<bool>());
    }

    [Fact]
    public void AStaleDeclarationRechecksFailedDiplomacyAtEnactment()
    {
        var world = NewWorld(true);
        var nation = world.Society!.Nations["aster"];
        var relation = SocietyDiplomacy.Relation(world, "aster", "ionia");
        relation.Score = -100;
        relation.DiplomacyFailedTick = world.Tick;
        var government = world.Agents["aster_government"];
        var candidate = Assert.Single(SocietyActions.Available(world, government), option => option.ActionType == "declare_conflict"
            && (string?)option.Params["country_id"] == "ionia");
        SocietyDiplomacy.Propose(world, government,
            new(candidate.ActionType, SocietyActions.Resolve(candidate), "regression", []), "cause");
        relation.DiplomacyFailedTick = -1;
        world.Tick += Governments.Get(nation.GovernmentId).PolicyDelay;
        SocietyDiplomacy.ProcessPolicies(world);
        Assert.False(relation.AtWar);
        Assert.Contains(world.Events, item => item.EventType == "policy_blocked"
            && (string?)item.Payload["action"] == "declare_conflict");
        SocietyEconomy.AssertConservation(world);
    }

    private static void EnactPolicy(World world, string country, string action, object parameters)
    {
        var policy = Policy(action, parameters);
        policy.CountryId = country;
        policy.EffectiveTick = world.Tick;
        world.Society!.Policies.Add(policy);
        SocietyDiplomacy.ProcessPolicies(world);
    }

    [Theory]
    [InlineData("reparations")]
    [InlineData("cede_ore")]
    public void SettlementClearsPendingOffersAndCannotChargeTwice(string terms)
    {
        var world = NewWorld(true);
        var relation = War(world, "aster", "beryl");
        EnactPolicy(world, "beryl", "offer_peace", new { country_id = "aster", terms });
        Assert.Equal("beryl", relation.ProposedBy);
        var response = Assert.Single(SocietyActions.Available(world, world.Agents["aster_government"]),
            option => option.ActionType == "respond_agreement" && (string?)option.Params["country_id"] == "beryl"
                && option.Params["accept"]!.GetValue<bool>());
        Assert.Contains("25%", (string?)response.Params["effect"]);
        var winner = world.Society!.Nations["beryl"];
        var loser = world.Society.Nations["aster"];
        SocietyWar.EndWar(world, relation, winner, loser, terms, "surrender");
        var cash = world.Agents["aster_government"].CashMinor;
        var endowment = loser.Endowment["ore"];
        Assert.Null(relation.ProposedBy);
        Assert.Null(relation.ProposalKind);
        Assert.Null(relation.ProposalTerms);

        EnactPolicy(world, "aster", response.ActionType, response.Params);
        SocietyWar.EndWar(world, relation, winner, loser, terms, "stale_settlement");
        Assert.Equal(cash, world.Agents["aster_government"].CashMinor);
        Assert.Equal(endowment, loser.Endowment["ore"]);
        Assert.Single(world.Events, item => item.EventType == "war_ended");
        Assert.Contains(world.Events, item => item.EventType == "policy_blocked"
            && (string?)item.Payload["action"] == "respond_agreement");
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void DelayedPeaceResponseCannotAcceptAReplacementOffer()
    {
        var world = NewWorld(true);
        var relation = War(world, "aster", "beryl");
        EnactPolicy(world, "beryl", "offer_peace", new { country_id = "aster", terms = "none" });
        var response = Assert.Single(SocietyActions.Available(world, world.Agents["aster_government"]),
            option => option.ActionType == "respond_agreement" && (string?)option.Params["country_id"] == "beryl"
                && option.Params["accept"]!.GetValue<bool>());
        EnactPolicy(world, "aster", "respond_agreement", new { country_id = "beryl", accept = false });
        EnactPolicy(world, "beryl", "offer_peace", new { country_id = "aster", terms = "reparations" });
        var cash = world.Agents["aster_government"].CashMinor;
        EnactPolicy(world, "aster", response.ActionType, response.Params);
        Assert.True(relation.AtWar);
        Assert.Equal(cash, world.Agents["aster_government"].CashMinor);
        Assert.Equal("reparations", relation.ProposalTerms);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void WarDeclarationCancelsTradeOffersAndBlocksTheirStaleResponse()
    {
        var world = NewWorld(true);
        var relation = SocietyDiplomacy.Relation(world, "aster", "beryl");
        relation.Score = -100;
        relation.DiplomacyFailedTick = 0;
        EnactPolicy(world, "beryl", "propose_agreement", new { country_id = "aster" });
        Assert.Equal("trade", relation.ProposalKind);
        EnactPolicy(world, "aster", "declare_conflict", new { country_id = "beryl" });
        Assert.True(relation.AtWar);
        Assert.Null(relation.ProposedBy);
        EnactPolicy(world, "aster", "respond_agreement", new { country_id = "beryl", accept = true });
        Assert.False(relation.TradeAgreement);
        Assert.Equal(-100, relation.Score);
        SocietyEconomy.AssertConservation(world);
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(60, -20)]
    [InlineData(100, -60)]
    public void LegacyRelationshipTensionLoadsIntoScoreAndNewSavesRetainNegativeRelations(int tension, int score)
    {
        var legacy = new JsonObject { ["id"] = "aster:beryl", ["first"] = "aster", ["second"] = "beryl", ["tension"] = tension };
        var relation = legacy.Deserialize<Relationship>(Wire.Json)!;
        Assert.Equal(score, relation.Score);
        relation.Score = -95;
        relation.Aggressor = "aster";
        relation.AtWar = true;
        var json = Wire.Node(relation);
        Assert.Equal(-95, json["score"]!.GetValue<int>());
        Assert.Null(json["tension"]);
        var copy = Wire.Copy(relation);
        Assert.Equal(-95, copy.Score);
        Assert.Equal("aster", copy.Aggressor);
        Assert.True(copy.AtWar);
    }

    [Fact]
    public void ActiveWarCheckpointRetainsMissilesExhaustionAndDeterministicSettlement()
    {
        var world = NewWorld();
        War(world, "aster", "beryl");
        Disarm(world, "aster");
        var nation = world.Society!.Nations["aster"];
        nation.Missiles = 1;
        for (var day = 0; day < 7; day++)
            SocietyEngine.Advance(world);
        Assert.Equal(1, nation.NoFitSoldierWeeks);
        var copy = Wire.Copy(world);
        Assert.Equal(1, copy.Society!.Nations["aster"].NoFitSoldierWeeks);
        Assert.Equal(1, copy.Society.Nations["aster"].Missiles);
        for (var day = 0; day < 7; day++)
        {
            SocietyEngine.Advance(world);
            SocietyEngine.Advance(copy);
        }
        Assert.False(SocietyDiplomacy.Relation(copy, "aster", "beryl").AtWar);
        Assert.True(JsonNode.DeepEquals(Wire.Node(world.Society), Wire.Node(copy.Society)),
            "Checkpoint replay must retain the same war and economic state.");
        SocietyEconomy.AssertConservation(copy);
    }

    [Theory]
    [InlineData("dune")]
    [InlineData("cinder")]
    public void CrisisTransitionsPreserveOwnershipAndNeverCreateAnIncompatiblePlanner(string country)
    {
        var world = NewWorld();
        var nation = world.Society!.Nations[country];
        var planned = SocietyLabor.IsPlanned(nation);
        var previous = nation.GovernmentId;
        var owners = world.InCountry(country).Where(person => person.Role == "business")
            .ToDictionary(firm => firm.Id, firm => firm.OwnerId);
        var pending = Policy("set_welfare", new { amount_minor = 100 });
        pending.CountryId = country;
        pending.EffectiveTick = 100;
        world.Society.Policies.Add(pending);
        foreach (var person in world.InCountry(country).Where(SocietyLabor.IsResident))
            person.Wellbeing = 0;
        SocietyWar.Day(world);
        world.Tick = 28;
        SocietyWar.Day(world);

        Assert.Equal(planned, SocietyLabor.IsPlanned(nation));
        Assert.All(world.InCountry(country).Where(person => person.Role == "business"), firm => Assert.Equal(owners[firm.Id], firm.OwnerId));
        if (planned)
        {
            Assert.Contains(pending, world.Society.Policies);
            Assert.Equal(previous, nation.GovernmentId);
            Assert.Contains(world.Events, item => item.EventType == "revolution_deferred");
        }
        else
        {
            Assert.DoesNotContain(pending, world.Society.Policies);
            Assert.Contains(world.Events, item => item.EventType == "policy_blocked"
                && (string?)item.Payload["reason"] == "LEADERSHIP_CHANGED");
            Assert.NotEqual(previous, nation.GovernmentId);
            Assert.DoesNotContain(world.InCountry(country), person => person.Role == "planner");
            Assert.Contains(world.Events, item => item.EventType == "government_changed"
                && (string?)item.Payload["reason"] == "revolution");
        }
        SocietyEconomy.AssertConservation(world);
    }

    [Theory]
    [InlineData(0.9, false)]
    [InlineData(0.2, true)]
    public void UnrestCallsAnElectionButApprovalStillDecidesItsOutcome(double approval, bool replaced)
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        foreach (var resident in world.InCountry(nation.Id).Where(SocietyLabor.IsResident)) resident.Wellbeing = 0;
        SocietyWar.Day(world);
        world.Tick = 28;
        SocietyWar.Day(world);
        Assert.Equal(29, nation.NextElectionTick);
        nation.Approval = approval;
        world.Tick = 29;
        SocietyDiplomacy.Advance(world);
        var election = Assert.Single(world.Events, item => item.EventType == "election_result" && item.EntityIds.Contains(nation.Id));
        Assert.Equal(replaced, election.Payload["replaced"]!.GetValue<bool>());
    }

    [Fact]
    public void LegacyWorldCheckpointDoesNotPayTributeOnHistoricalTaxes()
    {
        var original = NewWorld();
        War(original, "aster", "beryl");
        original.Tick = 7;
        original.Society!.Nations["aster"].TaxesCollectedMinor = 1_000_000;
        original.Society.Nations["beryl"].TaxesCollectedMinor = 2_000_000;
        var legacy = Wire.Node(original);
        foreach (var entry in legacy["society"]!["relations"]!.AsObject())
        {
            var relation = entry.Value!.AsObject();
            relation["tension"] = Math.Clamp(40 - relation["score"]!.GetValue<int>(), 0, 100);
            relation.Remove("score");
        }
        foreach (var entry in legacy["society"]!["nations"]!.AsObject())
        {
            entry.Value!.AsObject().Remove("war_accounting_initialized");
            entry.Value.AsObject().Remove("tax_mark_minor");
        }
        var world = legacy.Deserialize<World>(Wire.Json)!;
        SocietyWar.Day(world);
        var contest = Assert.Single(world.Events, item => item.EventType == "war_week");
        Assert.Equal(0, contest.Payload["tribute_minor"]!.GetValue<long>());
        foreach (var nation in world.Society!.Nations.Values) nation.TaxesCollectedMinor += 1000;
        world.Tick = 14;
        SocietyWar.Day(world);
        Assert.Equal(100, world.Events.Last(item => item.EventType == "war_week").Payload["tribute_minor"]!.GetValue<long>());
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void TreasuryOwnedGiftLeavesPrivateInputsAloneAndCanHaveZeroRelationGain()
    {
        var world = NewWorld();
        var giver = world.Agents["aster_government"];
        var receiver = world.Agents["beryl_government"];
        var supplier = world.Agents["aster_mine"];
        supplier.Inventory["ore"] -= 10;
        giver.Inventory["ore"] += 10;
        var privateStocks = world.InCountry("aster").Where(agent => agent.Role == "business")
            .ToDictionary(agent => agent.Id, agent => agent.Inventory["ore"]);
        receiver.LastWeekOutMinor = 1_000_000;
        var cash = giver.CashMinor;
        Assert.True(SocietyWar.Enact(world, world.Society!.Nations["aster"],
            Policy("send_gift", new { country_id = "beryl", kind = "ore", amount = 10 })));
        Assert.Equal(cash, giver.CashMinor);
        Assert.Equal(0, giver.Inventory["ore"]);
        Assert.All(privateStocks, item => Assert.Equal(item.Value, world.Agents[item.Key].Inventory["ore"]));
        Assert.Equal(0, Assert.Single(world.Events, item => item.EventType == "gift_sent").Payload["relation_gain"]!.GetValue<int>());
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void MissileLaunchConsumesStockTurretsInterceptAndUpkeepDoesNotEraseDamage()
    {
        var world = NewWorld();
        var attacker = world.Society!.Nations["aster"];
        var target = world.Society.Nations["beryl"];
        War(world, attacker.Id, target.Id);
        var turret = new Project { Id = "test_turret", Type = "turret", Complete = true, CostMinor = 40000, PaidMinor = 40000 };
        target.Infrastructure.Add(turret);
        var policy = Policy("launch_missile", new { country_id = target.Id });
        policy.Id = Enumerable.Range(0, 100).Select(index => $"missile_{index}")
            .First(key => SocietyFactory.Sample(world.Seed, key, "missile_hit") < 0.30);
        attacker.Missiles = 2;
        Assert.True(SocietyWar.Enact(world, attacker, policy));
        Assert.Equal(1, attacker.Missiles);
        Assert.Equal(100, turret.Condition);
        Assert.False(world.Events.Last(item => item.EventType == "missile_strike").Payload["hit"]!.GetValue<bool>());
        policy.Id = Enumerable.Range(0, 100).Select(index => $"missile_{index}")
            .First(key => SocietyFactory.Sample(world.Seed, key, "missile_hit") >= 0.30);
        Assert.True(SocietyWar.Enact(world, attacker, policy));
        Assert.Equal(0, attacker.Missiles);
        Assert.Equal(50, turret.Condition);
        Assert.Equal(0.15, PhaseContracts.Boost(target, "missile_defense"), 6);
        Assert.False(SocietyWar.Enact(world, attacker, policy));
        var builder = world.InCountry(target.Id).First(person => person.Role == "citizen" && person.EmployerId is null);
        SocietyLabor.Hire(world, world.Agents["beryl_construction"], builder, "builder");
        builder.Health = 100;
        builder.WorkEffort = 1;
        world.Tick = 7;
        SocietyInfrastructure.Day(world);
        Assert.Equal(50, turret.Condition); // approved e1efff repair rules: upkeep prevents wear, repairs restore damage
        SocietyEconomy.AssertConservation(world);
    }
}
