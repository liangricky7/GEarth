using System.Text.Json;
using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Tests;

// Phase 3: war contests, tribute and terms, missiles and turrets, relations and embargo, unrest.
public sealed class WarTests
{
    private static World NewWorld() => SocietyFactory.Create("war_test", false, 1100);

    // Proposes a government action and enacts it now, whatever the government type's delay; true when enacted.
    private static bool Enact(World world, string countryId, string action, object parameters)
    {
        SocietyDiplomacy.Propose(world, world.Agents[countryId + "_government"],
            new ActionProposal(action, Wire.Node(parameters).AsObject(), "test", []), "test");
        foreach (var policy in world.Society!.Policies)
            policy.EffectiveTick = world.Tick;
        SocietyDiplomacy.ProcessPolicies(world);
        return world.Events.Last(item => item.EventType is "policy_enacted" or "policy_blocked").EventType == "policy_enacted";
    }

    private static Relationship StartWar(World world, string aggressor, string victim)
    {
        var relation = SocietyDiplomacy.Relation(world, aggressor, victim);
        relation.Score = -50;
        Assert.True(Enact(world, aggressor, "declare_conflict", new { country_id = victim }));
        Assert.True(relation.AtWar);
        return relation;
    }

    private static void Disarm(World world, string countryId)
    {
        foreach (var soldier in SocietyWar.Soldiers(world, world.Society!.Nations[countryId]))
            SocietyLabor.Fire(world, soldier);
    }

    private static void AdvanceDays(World world, int days)
    {
        for (var day = 0; day < days; day++)
            SocietyEngine.Advance(world); // asserts money and goods conservation every day
    }

    [Fact]
    public void ContestOddsFollowStrength()
    {
        var rolls = Enumerable.Range(0, 2000).Select(index => SocietyFactory.Sample(7, "contest" + index, "war_week")).ToArray();
        double Share(int own, int enemy) => rolls.Count(roll => SocietyWar.Wins(roll, own, enemy)) / (double)rolls.Length;

        Assert.InRange(Share(30, 10), 0.70, 0.80);
        Assert.InRange(Share(10, 10), 0.45, 0.55);
        Assert.Equal(1, Share(5, 0));
        Assert.Equal(0, Share(0, 5));
    }

    [Fact]
    public void DefencelessCountrySurrendersWithinTwoWeeksAndGrantsTerms()
    {
        var world = NewWorld();
        var relation = StartWar(world, "dune", "ionia");
        Disarm(world, "ionia");
        var events = world.Events.Count;
        var (winner, loser) = (world.Society!.Nations["dune"], world.Society.Nations["ionia"]);
        var richest = PhaseContracts.RawMaterials.OrderByDescending(good => PhaseContracts.Endowment(loser, good)).First();
        var (winnerBefore, loserBefore) = (PhaseContracts.Endowment(winner, richest), PhaseContracts.Endowment(loser, richest));

        AdvanceDays(world, 2 * SocietyLabor.DaysPerWeek + 1);

        Assert.False(relation.AtWar);
        var ended = world.Events.Skip(events).Single(item => item.EventType == "war_ended");
        Assert.Equal("dune", ended.Payload["winner"]!.GetValue<string>());
        Assert.Equal("surrender", ended.Payload["reason"]!.GetValue<string>());
        // With resources present, surrender cedes the richest deposit; the standalone war phase uses cash.
        if (PhaseContracts.HasRawMaterials)
        {
            Assert.Equal("cede_" + richest, ended.Payload["terms"]!.GetValue<string>());
            Assert.Equal(winnerBefore + loserBefore * SocietyWar.CessionShare, PhaseContracts.Endowment(winner, richest), 6);
            Assert.Equal(loserBefore * (1 - SocietyWar.CessionShare), PhaseContracts.Endowment(loser, richest), 6);
            Assert.Equal(0, ended.Payload["reparations_minor"]!.GetValue<long>());
        }
        else
        {
            Assert.Equal("reparations", ended.Payload["terms"]!.GetValue<string>());
            Assert.True(ended.Payload["reparations_minor"]!.GetValue<long>() > 0);
        }
        // The undefended side lost every week it fought and paid tribute.
        var weeks = world.Events.Skip(events).Where(item => item.EventType == "war_week").ToArray();
        Assert.NotEmpty(weeks);
        Assert.All(weeks, week => Assert.Equal("dune", week.Payload["winner"]!.GetValue<string>()));
        Assert.Contains(weeks, week => week.Payload["tribute_minor"]!.GetValue<long>() > 0);
    }

    [Fact]
    public void LosersSoldiersAreWoundedNotRemoved()
    {
        var world = NewWorld();
        StartWar(world, "dune", "ionia");
        var ionia = world.Society!.Nations["ionia"];
        var soldiers = SocietyWar.Soldiers(world, ionia).Length;
        world.Society.Nations["dune"].Missiles = 100; // overwhelming strength: dune wins every week

        AdvanceDays(world, SocietyLabor.DaysPerWeek);

        // Clinics may already have treated them the same day; nobody is removed.
        Assert.Equal(soldiers, SocietyWar.Soldiers(world, ionia).Length);
        var week = world.Events.Single(item => item.EventType == "war_week");
        Assert.Equal("dune", week.Payload["winner"]!.GetValue<string>());
        Assert.Equal((int)Math.Ceiling(soldiers * SocietyWar.WoundedShare), week.Payload["wounded"]!.GetValue<int>());
    }

    [Fact]
    public void PeaceTermsMoveCashAndEndowment()
    {
        var world = NewWorld();
        var (dune, ionia) = (world.Society!.Nations["dune"], world.Society.Nations["ionia"]);
        var (winnerOre, loserOre) = (PhaseContracts.Endowment(dune, "ore"), PhaseContracts.Endowment(ionia, "ore"));
        var relation = StartWar(world, "dune", "ionia");
        SocietyWar.EndWar(world, relation, dune, ionia, "cede_ore", "test");
        // Phase 1 supplies unequal concentrations instead of the standalone contract's fallback of 1.
        Assert.Equal(loserOre * (1 - SocietyWar.CessionShare), PhaseContracts.Endowment(ionia, "ore"), 6);
        Assert.Equal(winnerOre + loserOre * SocietyWar.CessionShare, PhaseContracts.Endowment(dune, "ore"), 6);

        relation = StartWar(world, "ionia", "dune");
        var (payer, payee) = (world.Agents["dune_government"], world.Agents["ionia_government"]);
        var (before, received) = (payer.CashMinor, payee.CashMinor);
        SocietyWar.EndWar(world, relation, ionia, dune, "reparations", "test");
        Assert.Equal(before - before / 4, payer.CashMinor);
        Assert.Equal(received + before / 4, payee.CashMinor);
        Assert.False(relation.AtWar);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void AcceptedPeaceOfferGrantsTheOfferersTerms()
    {
        var world = NewWorld();
        var relation = StartWar(world, "dune", "ionia");
        var payer = world.Agents["ionia_government"];
        var before = payer.CashMinor;

        Assert.True(Enact(world, "dune", "offer_peace", new { country_id = "ionia", terms = "reparations" }));
        Assert.True(Enact(world, "ionia", "respond_agreement", new { country_id = "dune", accept = true }));

        Assert.False(relation.AtWar);
        Assert.True(payer.CashMinor < before);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void MissileBuiltAloneJoinsTheStockpileAndTurretStopsAboutThirtyPercent()
    {
        var world = NewWorld();
        var dune = world.Society!.Nations["dune"];
        var workshop = SocietyLabor.Firms(world, "dune", "tools").OrderBy(firm => firm.Id).First();
        var paid = workshop.CashMinor;
        var strength = SocietyWar.Strength(world, dune);
        Assert.True(Enact(world, "dune", "order_missile", new { cost_minor = 10000, ticks = 7 }));
        Assert.True(workshop.CashMinor >= paid + 10000 - 1); // paid upfront (no construction firm on this branch)

        AdvanceDays(world, PhaseContracts.Spec(PhaseContracts.Missile).Ticks);

        Assert.Equal(1, dune.Missiles);
        Assert.Empty(dune.Infrastructure);
        Assert.Equal(SocietyWar.SoldiersPerMissile, SocietyWar.Strength(world, dune) - strength);

        // Strikes on a target with a completed turret and a road: about 30% are stopped (a hit can damage the
        // turret itself, so it is repaired between strikes here).
        var ionia = world.Society.Nations["ionia"];
        int Hits()
        {
            var events = world.Events.Count;
            for (var index = 0; index < 1000; index++)
            {
                SocietyWar.Strike(world, dune, ionia, "strike" + index, "test");
                ionia.Infrastructure.ForEach(project => project.Condition = 100);
            }
            return world.Events.Skip(events).Count(item => item.Payload["hit"]!.GetValue<bool>());
        }
        Assert.Equal(1000, Hits());
        ionia.Infrastructure.Add(new Project { Id = "turret", Type = PhaseContracts.Turret, CostMinor = 40000, Complete = true });
        ionia.Infrastructure.Add(new Project { Id = "road", Type = "roads", CostMinor = 31000, Complete = true });
        Assert.InRange(Hits(), 640, 760);
        var strike = 0;
        do
            SocietyWar.Strike(world, dune, ionia, "hit" + strike++, "test");
        while (!world.Events[^1].Payload["hit"]!.GetValue<bool>());
        Assert.Single(ionia.Infrastructure, project => project.Condition == 50);
    }

    [Fact]
    public void LaunchingNeedsAWarAndAMissile()
    {
        var world = NewWorld();
        var dune = world.Society!.Nations["dune"];
        dune.Missiles = 1;
        Assert.False(Enact(world, "dune", "launch_missile", new { country_id = "ionia" }));
        StartWar(world, "dune", "ionia");
        Assert.True(Enact(world, "dune", "launch_missile", new { country_id = "ionia" }));
        Assert.Equal(0, dune.Missiles);
        Assert.Contains(world.Events, item => item.EventType == "missile_strike");
    }

    [Fact]
    public void ThirdCountriesTurnOnAnAggressorAndEmbargoClosesRoutes()
    {
        var world = NewWorld();
        var thirds = world.Society!.Nations.Keys.Where(id => id is not ("dune" or "ionia")).ToArray();
        var before = thirds.ToDictionary(id => id, id => SocietyDiplomacy.Relation(world, id, "dune").Score);
        StartWar(world, "dune", "ionia");
        Assert.All(thirds, id => Assert.True(SocietyDiplomacy.Relation(world, id, "dune").Score < before[id], id));

        var route = world.Routes.Values.First(route => !route.From.StartsWith("dune") && !route.To.StartsWith("dune")
            && !route.From.StartsWith("ionia") && !route.To.StartsWith("ionia"));
        var relation = SocietyDiplomacy.Relation(world, route.From.Replace("_city", ""), route.To.Replace("_city", ""));
        Assert.True(SocietyDiplomacy.BorderPermits(world, route));
        relation.Score = SocietyWar.EmbargoBelow;
        Assert.False(SocietyDiplomacy.BorderPermits(world, route));

        // Relations drift back toward zero each week.
        relation.Score = 50;
        world.Tick = 6;
        SocietyEngine.Advance(world);
        Assert.InRange(relation.Score, 40, 49);
    }

    [Fact]
    public void GiftsRaiseRelationsUpToTheCap()
    {
        var world = NewWorld();
        var relation = SocietyDiplomacy.Relation(world, "dune", "ionia");
        relation.Score = 0;
        world.Agents["ionia_government"].LastWeekOutMinor = 10000;
        Assert.True(Enact(world, "dune", "send_gift", new { country_id = "ionia", kind = "cash", amount = 2000 }));
        Assert.Equal(3, relation.Score);
        Assert.True(Enact(world, "dune", "send_gift", new { country_id = "ionia", kind = "cash", amount = 50000 }));
        Assert.Equal(3 + SocietyWar.GiftGainCap, relation.Score);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void RecruitsComeFromTheUnemployedFirst()
    {
        var world = NewWorld();
        var dune = world.Society!.Nations["dune"];
        var jobless = world.InCountry("dune").Where(person => person.Role == "citizen" && person.EmployerId is null)
            .OrderBy(person => person.Id).Take(SocietyWar.SoldierStep).ToArray();
        Assert.Equal(SocietyWar.SoldierStep, jobless.Length);
        var soldiers = SocietyWar.Soldiers(world, dune).Length;

        Assert.True(Enact(world, "dune", "recruit_soldiers", new { count = 2, weekly_pay_minor = 1400 }));

        Assert.Equal(soldiers + SocietyWar.SoldierStep, SocietyWar.Soldiers(world, dune).Length);
        Assert.All(jobless, person => Assert.Equal("soldier", person.Occupation));
        Assert.True(Enact(world, "dune", "discharge_soldiers", new { count = 2 }));
        Assert.Equal(soldiers, SocietyWar.Soldiers(world, dune).Length);
    }

    [Fact]
    public void LongUnrestCallsAnElectionInADemocracyAndARevolutionInAnUnderpaidAutocracy()
    {
        var world = NewWorld();
        var nations = world.Society!.Nations.Values;
        var democracy = nations.First(nation => Governments.Get(nation.GovernmentId).Elected);
        var autocracy = nations.First(nation => nation.GovernmentId is "dictatorship" or "monarchy");
        var regime = autocracy.GovernmentId;
        foreach (var soldier in SocietyWar.Soldiers(world, autocracy))
            soldier.ArrearsMinor = 1400; // unpaid soldiers do not defend the regime
        foreach (var resident in world.Agents.Values.Where(SocietyLabor.IsResident))
            resident.Wellbeing = SocietyWar.UnrestQol - 20;

        for (var tick = 7; tick <= 7 + SocietyWar.UnrestDays; tick += SocietyLabor.DaysPerWeek)
        {
            world.Tick = tick;
            SocietyWar.Day(world);
        }

        Assert.Contains(world.Events, item => item.EventType == "unrest_started" && item.EntityIds.Contains(democracy.Id));
        Assert.Equal(world.Tick + 1, democracy.NextElectionTick);
        Assert.NotEqual(regime, autocracy.GovernmentId);
        Assert.Contains(world.Events, item => item.EventType == "revolution" && item.EntityIds.Contains(autocracy.Id));
    }

    [Fact]
    public void WarWearinessLowersSecurity()
    {
        var world = NewWorld();
        var resident = world.InCountry("ionia").First(person => person.Role == "citizen" && person.EmployerId is not null);
        var security = SocietyEconomy.QolParts(resident, 2, false)["security"];
        StartWar(world, "dune", "ionia");
        world.Tick = 6;

        SocietyEngine.Advance(world);

        Assert.Equal(SocietyWar.WearinessStep, resident.WarWeariness, 6);
        Assert.True(SocietyEconomy.QolParts(resident, 2, false)["security"] < security);
    }

    [Fact]
    public void OldSavesReadTensionAsRelations()
    {
        var relation = JsonSerializer.Deserialize<Relationship>("""{"id":"aster:beryl","tension":70}""", Wire.Json)!;
        Assert.Equal(-30, relation.Score);
        Assert.DoesNotContain("tension", JsonSerializer.Serialize(relation, Wire.Json));
    }

    [Fact]
    public void RuleModeOffersWarOnlyWhenTradeIsBlockedAndStronger()
    {
        var world = NewWorld();
        var (dune, ionia) = (world.Society!.Nations["dune"], world.Society.Nations["ionia"]);
        SocietyDiplomacy.Relation(world, "dune", "ionia").Score = -50;
        Assert.False(SocietyWar.OffersWar(world, dune, ionia)); // dune can still buy everything
        world.Mode = "LIVE";
        Assert.True(SocietyWar.OffersWar(world, dune, ionia)); // a model always sees the choice
        Assert.Contains(SocietyActions.Available(world, world.Agents["dune_government"]),
            candidate => candidate.ActionType == "declare_conflict" && candidate.Params["country_id"]!.GetValue<string>() == "ionia");
    }
}
