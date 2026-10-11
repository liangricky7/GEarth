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
        world.Society!.PeriodRevenueMinor[workshop.Id] = 10_000_000; // a profitable week
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
        var government = world.Agents["aster_government"];
        var benefit = Assert.Single(SocietyActions.Available(world, government), candidate => candidate.ActionType == "set_unemployment_benefit")
            .Params["amount_minor"]!;
        Assert.Equal([0, 900, world.Society!.Nations["aster"].UnemploymentBenefitMinor],
            new[] { benefit["min"]!.GetValue<int>(), benefit["max"]!.GetValue<int>(), benefit["default"]!.GetValue<int>() });

        var relation = SocietyDiplomacy.Relation(world, "aster", "ionia");
        relation.Tension = 99;
        bool ConflictOffered() => SocietyActions.Available(world, government).Any(candidate => candidate.ActionType == "declare_conflict"
            && candidate.Params["country_id"]!.GetValue<string>() == "ionia" && candidate.Params.ContainsKey("effect"));
        Assert.False(ConflictOffered()); // high tension alone: talk first
        (relation.ProposedBy, relation.ProposalKind, relation.ProposalExpiresTick) = ("ionia", "trade", world.Tick - 1);
        SocietyDiplomacy.Advance(world); // the offer lapses unanswered
        Assert.True(ConflictOffered());
    }
}
