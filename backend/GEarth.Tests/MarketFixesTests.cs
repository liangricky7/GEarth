using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Tests;

// Fixes from the no-bank run: lost sales bring prices down, owners see no pointless price rise, menus are
// shuffled and the chosen position is logged, and war spending goes to soldiers.
public sealed class MarketFixesTests
{
    private static World NewWorld() => SocietyFactory.Create("market_fixes", false, 1100);

    [Fact]
    public void ShoppersPricedOutOfFoodBringThePriceDown()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var farms = SocietyLabor.Firms(world, "aster", "food").ToArray();
        foreach (var farm in farms)
            farm.PriceMinor = 3 * SocietyLabor.SectorOf("food").BasePrice;
        var shopper = world.InCountry("aster").First(agent => agent.Role == "citizen");
        SocietyEconomy.Transfer(world, shopper, world.Agents["aster_government"], shopper.CashMinor - 100); // 120 is too dear, 96 is not

        Assert.Equal(0, SocietyEconomy.Acquire(world, shopper, "food", 4, true));
        Assert.All(farms, farm => Assert.Equal(1, world.Society.TurnedAwayThisWeek[farm.Id]));
        SocietyLabor.Week(world, nation, world.Agents["aster_government"], world.InCountry("aster").Where(SocietyLabor.IsResident).ToArray());
        Assert.All(farms, farm => Assert.True(farm.PriceMinor < 3 * SocietyLabor.SectorOf("food").BasePrice));
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void OwnersSeeNoPriceRiseAtTheCapAndWhatTheirLastChangeDid()
    {
        var world = NewWorld();
        var farm = world.Agents["aster_food"];
        var owner = world.Agents[farm.OwnerId!];
        farm.PriceMinor = 3 * SocietyLabor.SectorOf("food").BasePrice;
        var changes = SocietyActions.Available(world, owner).Where(candidate => candidate.ActionType == "set_price")
            .Select(candidate => candidate.Params["change_pct"]!.GetValue<int>()).ToArray();
        Assert.Equal([-10], changes);

        farm.LastWeekUnitsSold = 40;
        var job = SocietyEngine.Observe(world, owner.Id, DateTimeOffset.UtcNow);
        var cut = job.Candidates.Single(candidate => candidate.ActionType == "set_price");
        Assert.True(SocietyEngine.Apply(world, job, new DecisionResult(job.RequestId,
            new ActionProposal("set_price", cut.Params, "test", []), "model", "test", 0), DateTimeOffset.UtcNow).Accepted);
        var view = SocietyEngine.Observe(world, owner.Id, DateTimeOffset.UtcNow).Observation["business"]!;
        Assert.Equal(-10, view["last_price_change"]!["change_pct"]!.GetValue<int>());
        Assert.Equal(40, view["last_price_change"]!["units_sold_week_before"]!.GetValue<int>());
        Assert.NotNull(view["competitors"]);
    }

    [Fact]
    public void MenusAreShuffledPerDayAndTheChosenPositionIsLogged()
    {
        var world = NewWorld();
        var government = world.Agents["aster_government"];
        string Order() => string.Join(",", SocietyActions.Available(world, government).Select(candidate => candidate.ActionType + candidate.Params.ToJsonString()));
        var today = Order();
        Assert.Equal(today, Order()); // the same view gives the same order
        var orders = new HashSet<string> { today };
        for (var day = 1; day <= 5; day++)
        {
            world.Tick = day;
            orders.Add(Order());
        }
        Assert.True(orders.Count > 1);

        var job = SocietyEngine.Observe(world, government.Id, DateTimeOffset.UtcNow);
        var index = Array.FindIndex(job.Candidates, candidate => candidate.ActionType == "hold");
        SocietyEngine.Apply(world, job, new DecisionResult(job.RequestId,
            new ActionProposal("hold", new JsonObject(), "test", []), "model", "test", 0), DateTimeOffset.UtcNow);
        var accepted = world.Events.Last(item => item.EventType == "action_accepted").Payload;
        Assert.Equal(index, accepted["choice_index"]!.GetValue<int>());
        Assert.Equal(job.Candidates.Length, accepted["menu_size"]!.GetValue<int>());
    }

    [Fact]
    public void WarSpendingIsPaidToSoldiersNotTheBank()
    {
        var world = NewWorld();
        var government = world.Agents["dune_government"];
        var bank = world.Agents["dune_bank"];
        var soldiers = world.InCountry("dune").Where(person => person.EmployerId == government.Id && person.Occupation == "soldier").ToArray();
        Assert.NotEmpty(soldiers);
        var relation = SocietyDiplomacy.Relation(world, "dune", "ionia");
        relation.Tension = 80;
        relation.DiplomacyFailedTick = world.Tick;
        var (bankBefore, soldiersBefore) = (bank.CashMinor, soldiers.Sum(soldier => soldier.CashMinor));

        SocietyDiplomacy.Propose(world, government, new ActionProposal("declare_conflict",
            new JsonObject { ["country_id"] = "ionia" }, "test", []), "cause"); // a dictatorship acts at once
        Assert.True(relation.AtWar);
        Assert.Equal(bankBefore, bank.CashMinor);
        Assert.True(soldiers.Sum(soldier => soldier.CashMinor) > soldiersBefore);
        SocietyEconomy.AssertConservation(world);
    }
}
