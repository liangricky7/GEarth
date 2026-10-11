using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Tests;

// Fixes from the first run with banks on: pay follows the cost of living, failing small ventures close
// instead of starving their owner, bank owners hold most of their wealth as savings, and government levers
// show their current level while conflict waits for failed diplomacy.
public sealed class RealismFixesTests
{
    private static World NewWorld() => SocietyFactory.Create("realism_fixes", false, 1100);

    private static void Week(World world, string country) => SocietyLabor.Week(world, world.Society!.Nations[country],
        world.Agents[country + "_government"], world.InCountry(country).Where(SocietyLabor.IsResident).ToArray());

    [Fact]
    public void ProfitableFirmsPassHalfTheRiseInTheCostOfLivingIntoPay()
    {
        var world = NewWorld();
        foreach (var farm in SocietyLabor.Firms(world, "aster", "food"))
            farm.PriceMinor = 80; // food at twice its base price
        var workshop = world.Agents["aster_tools"];
        SocietyEconomy.Sell(world, world.Agents["aster_government"], workshop, 100_000, kind: "public_investment"); // funded receipts
        var pay = SocietyLabor.Staff(world, workshop).ToDictionary(worker => worker.Id, worker => worker.SalaryMinor);
        var power = world.Agents["aster_energy"]; // sold nothing this week
        var powerOffer = power.WageOfferMinor;
        world.Tick = SocietyLabor.IndexationDays;

        Week(world, "aster");

        // A week of food and energy went from 7 x (40 + 25) to 7 x (80 + 25): up 61.5%, so pay rises 30.77%.
        var raise = world.Events.Last(item => item.EventType == "labor_market" && item.EntityIds.Contains("aster"))
            .Payload["cost_of_living_raise_pct"]!.GetValue<double>();
        Assert.Equal(30.77, raise);
        Assert.All(SocietyLabor.Staff(world, workshop).Where(worker => pay.ContainsKey(worker.Id)),
            worker => Assert.Equal(pay[worker.Id] * 13077 / 10000, worker.SalaryMinor));
        Assert.Equal(powerOffer, power.WageOfferMinor);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void AnOwnerHungryAndUnpaidForTwoWeeksClosesASmallFailingVenture()
    {
        var world = NewWorld();
        var venue = world.Agents["aster_entertainment"];
        var owner = world.Agents[venue.OwnerId!];
        foreach (var worker in SocietyLabor.Staff(world, venue))
            worker.EmployerId = null;
        venue.TargetHeadcount = 0;
        SocietyEconomy.Transfer(world, venue, world.Agents["aster_government"], venue.CashMinor);

        owner.HungryThisWeek = true;
        Week(world, "aster");
        Assert.Equal("business", venue.Role); // one bad week is not enough
        owner.HungryThisWeek = true;
        Week(world, "aster");

        Assert.Equal("defunct", venue.Role);
        Assert.Equal("citizen", owner.Role);
        Assert.Contains(world.Events, item => item.EventType == "business_closed" && item.EntityIds.Contains(venue.Id)
            && item.Payload["reason"]!.GetValue<string>().Contains("hungry"));
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void ABankOwnerStakesAShareAndKeepsTheRestAsSavings()
    {
        var world = NewWorld();
        var bank = world.Agents["aster_bank"];
        var owner = world.Agents[bank.OwnerId!];

        Assert.Equal(bank.CashMinor * SocietyBank.OwnerStakePct / 100, bank.InvestedMinor);
        Assert.Equal(bank.CashMinor - bank.InvestedMinor, owner.DepositMinor);
        Assert.Equal(bank.InvestedMinor, SocietyBank.Equity(world, bank));
        var stateBank = world.Agents["cinder_bank"]; // planned economies keep the state's full capital
        Assert.Null(stateBank.OwnerId);
        Assert.Equal(stateBank.CashMinor, SocietyBank.Equity(world, stateBank));
    }

    [Fact]
    public void LeversShowTheCurrentLevelAndConflictComesOnlyAfterFailedDiplomacy()
    {
        var world = NewWorld();
        world.Mode = "LIVE"; // Phase 3 rule governments also require a blocked input need and superior strength.
        var government = world.Agents["aster_government"];
        var level = world.Society!.Nations["aster"].UnemploymentBenefitMinor;
        var benefits = SocietyActions.Available(world, government).Where(candidate => candidate.ActionType == "set_unemployment_benefit")
            .Select(candidate => candidate.Params["amount_minor"]!.GetValue<int>()).Order().ToArray();
        Assert.Equal(new[] { Math.Max(0, level - 300), Math.Min(900, level + 300) }.Where(amount => amount != level).Distinct(), benefits);

        var relation = SocietyDiplomacy.Relation(world, "aster", "ionia");
        relation.Tension = 99;
        bool ConflictOffered() => SocietyActions.Available(world, government).Any(candidate => candidate.ActionType == "declare_conflict"
            && candidate.Params["country_id"]!.GetValue<string>() == "ionia" && candidate.Params.ContainsKey("effect"));
        Assert.False(ConflictOffered()); // high tension alone: talk first
        (relation.ProposedBy, relation.ProposalKind, relation.ProposalExpiresTick) = ("ionia", "trade", world.Tick - 1);
        SocietyDiplomacy.Advance(world); // the offer lapses unanswered
        Assert.True(ConflictOffered());
    }

    [Fact]
    public void SchoolEntryUsesOccupiedSeatsEvenWhenNoTuitionWasSoldLastWeek()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var school = world.Agents["aster_school"];
        var idle = world.InCountry("aster").First(person => person.Role == "citizen" && person.Occupation == "factory_worker");
        SocietyLabor.Fire(world, idle);
        idle.Occupation = "teacher";
        Assert.False(SocietyLabor.HasGap(world, nation, "school"));

        var seats = SocietyLabor.Seats(world, school);
        foreach (var student in world.InCountry("aster").Where(person => person.Role == "citizen" && person != idle
                     && person.EmployerId != school.Id).Take(seats))
            (student.SchoolId, student.StudentUntilTick) = (school.Id, world.Tick + SocietyLabor.CourseDays);

        Assert.Equal(0, school.LastWeekUnitsSold);
        Assert.True(SocietyLabor.HasGap(world, nation, "school"));
    }

    [Fact]
    public void EmptyGoodsShelvesCountAsDemandBeforeAnySalesCanBeMade()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var idle = world.InCountry("aster").First(person => person.Role == "citizen" && person.Occupation == "farmer");
        SocietyLabor.Fire(world, idle);
        foreach (var farm in SocietyLabor.Firms(world, "aster", "food"))
        {
            farm.PriceMinor = 80;
            Assert.Equal(0, farm.LastWeekUnitsSold);
            world.Agents["aster_government"].Inventory["food"] += farm.Inventory["food"];
            farm.Inventory["food"] = 0;
        }

        Assert.True(SocietyLabor.HasGap(world, nation, "food"));
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void LargerFirmDoesNotCloseForOwnerHungerAlone()
    {
        var world = NewWorld();
        var firm = world.Agents["aster_tools"];
        var owner = world.Agents[firm.OwnerId!];
        // Keep the firm staffed and able to pay wages, but make no profit or owner draw.
        foreach (var workshop in SocietyLabor.Firms(world, "aster", "tools"))
        {
            world.Agents["aster_government"].Inventory["tools"] += workshop.Inventory["tools"];
            workshop.Inventory["tools"] = 0;
        }
        Assert.True(SocietyLabor.Staff(world, firm).Length > 1);

        owner.HungryThisWeek = true;
        Week(world, "aster");
        Assert.Equal("business", firm.Role);
        Assert.Equal(0, owner.UnpaidHungryWeeks);
        Assert.True(SocietyLabor.Staff(world, firm).Length > 1);
        owner.HungryThisWeek = true;
        Week(world, "aster");

        Assert.Equal("business", firm.Role);
        Assert.Equal("entrepreneur", owner.Role);
        Assert.Equal(0, owner.UnpaidHungryWeeks);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void AnInterveningFedWeekResetsTheOwnerExitCounter()
    {
        var world = NewWorld();
        var firm = world.Agents["aster_entertainment"];
        var owner = world.Agents[firm.OwnerId!];
        foreach (var worker in SocietyLabor.Staff(world, firm))
            SocietyLabor.Fire(world, worker);
        firm.TargetHeadcount = 0;
        SocietyEconomy.Transfer(world, firm, world.Agents["aster_government"], firm.CashMinor);

        owner.HungryThisWeek = true;
        Week(world, "aster");
        Assert.Equal(1, owner.UnpaidHungryWeeks);
        owner.HungryThisWeek = false;
        Week(world, "aster");
        Assert.Equal(0, owner.UnpaidHungryWeeks);
        owner.HungryThisWeek = true;
        Week(world, "aster");
        Assert.Equal("business", firm.Role);
        Assert.Equal(1, owner.UnpaidHungryWeeks);
    }

    [Fact]
    public void IdleServicesWithNoCapacityNeedEvidenceOfUnmetDemand()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var idle = world.InCountry("aster").First(person => person.Role == "citizen" && person.Occupation == "service_worker");
        SocietyLabor.Fire(world, idle);
        var venues = SocietyLabor.Firms(world, "aster", "entertainment").ToArray();
        foreach (var venue in venues)
        {
            venue.PriceMinor = 180;
            foreach (var worker in SocietyLabor.Workers(world, venue))
                worker.WorkEffort = 0;
        }

        Assert.False(SocietyLabor.HasGap(world, nation, "entertainment"));
        venues[0].LastWeekTurnedAway = 1;
        Assert.True(SocietyLabor.HasGap(world, nation, "entertainment"));
    }

    [Fact]
    public void AConflictPolicyCannotEnactWithoutFailedDiplomacy()
    {
        var world = NewWorld();
        var relation = SocietyDiplomacy.Relation(world, "aster", "ionia");
        var government = world.Agents["aster_government"];
        relation.Tension = 99;
        var cash = government.CashMinor;
        var action = new ActionProposal("declare_conflict", new JsonObject { ["country_id"] = "ionia" }, "test", []);
        SocietyDiplomacy.Propose(world, government, action, "cause");
        world.Tick += Governments.Get(world.Society!.Nations["aster"].GovernmentId).PolicyDelay;
        SocietyDiplomacy.ProcessPolicies(world);

        Assert.False(relation.AtWar);
        Assert.Equal(cash, government.CashMinor);
        Assert.Contains(world.Events, item => item.EventType == "policy_blocked" && item.Payload["action"]!.GetValue<string>() == "declare_conflict");

        relation.DiplomacyFailedTick = world.Tick;
        SocietyDiplomacy.Propose(world, government, action, "cause");
        world.Tick += Governments.Get(world.Society.Nations["aster"].GovernmentId).PolicyDelay;
        SocietyDiplomacy.ProcessPolicies(world);
        Assert.True(relation.AtWar);
        Assert.InRange(government.CashMinor, cash - 5000, cash - 1); // soldiers' income tax returns to the treasury
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void ClosingForAnotherReasonClearsTheOwnersUnpaidHungryWeeks()
    {
        var world = NewWorld();
        var firm = world.Agents["aster_entertainment"];
        var owner = world.Agents[firm.OwnerId!];
        owner.UnpaidHungryWeeks = 1;

        SocietyLabor.Close(world, world.Society!.Nations["aster"], firm, "test closure");

        Assert.Equal("citizen", owner.Role);
        Assert.Equal(0, owner.UnpaidHungryWeeks);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void IndexationDoesNotCutAnOfferAlreadyAboveTheCeiling()
    {
        var world = NewWorld();
        foreach (var farm in SocietyLabor.Firms(world, "aster", "food"))
            farm.PriceMinor = 80;
        var workshop = world.Agents["aster_tools"];
        workshop.WageOfferMinor = 5000;
        SocietyEconomy.Sell(world, world.Agents["aster_government"], workshop, 100_000, kind: "public_investment");
        world.Tick = SocietyLabor.IndexationDays;

        Week(world, "aster");

        Assert.Equal(5000, workshop.WageOfferMinor);
        SocietyEconomy.AssertConservation(world);
    }

    [Theory]
    [InlineData("set_unemployment_benefit", "amount_minor")]
    [InlineData("set_welfare", "amount_minor")]
    [InlineData("set_sales_tax", "sales_tax_basis_points")]
    [InlineData("fund_services", "amount_minor")]
    public void GovernmentLeversAcceptOnlyTheOfferedMovesAndEnactThem(string actionType, string field)
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var government = world.Agents["aster_government"];
        var now = DateTimeOffset.UtcNow;
        var job = SocietyEngine.Observe(world, government.Id, now);
        var offered = job.Candidates.Where(item => item.ActionType == actionType).ToArray();
        Assert.NotEmpty(offered);
        var move = offered.MaxBy(item => item.Params[field]!.GetValue<int>())!;
        var chosen = move.Params[field]!.GetValue<int>();
        var bare = new JsonObject { [field] = chosen };
        Assert.DoesNotContain(job.Candidates, item => SocietyActions.Matches(item, actionType, bare)); // the effect is part of the move
        var result = new DecisionResult(job.RequestId, new(actionType, (JsonObject)move.Params.DeepClone(), "test", []), "model", "test", 0);
        Assert.True(SocietyEngine.Apply(world, job, result, now).Accepted);
        world.Tick += Governments.Get(nation.GovernmentId).PolicyDelay;
        SocietyDiplomacy.ProcessPolicies(world);
        var value = actionType switch
        {
            "set_unemployment_benefit" => nation.UnemploymentBenefitMinor,
            "set_welfare" => nation.WelfareMinor,
            "set_sales_tax" => nation.SalesTaxBasisPoints,
            _ => nation.ServiceBudgetPerResidentMinor
        };
        Assert.Equal(chosen, value);
    }
}
