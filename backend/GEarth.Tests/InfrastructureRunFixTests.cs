using GEarth.Core;
namespace GEarth.Tests;

public sealed class InfrastructureRunFixTests
{
    [Fact]
    public void NutritionHasGradualDeclineAndRecoveryWithoutRemovingResidents()
    {
        var p = new Agent { Health = 100 };
        SocietyEconomy.RecordMeal(p, false);
        Assert.InRange(SocietyEconomy.Capacity(p), 0.9, 1);
        for (var i = 1; i < 21; i++) SocietyEconomy.RecordMeal(p, false);
        Assert.Equal(0.01, SocietyEconomy.Capacity(p), 8);
        SocietyEconomy.RecordMeal(p, true);
        Assert.InRange(SocietyEconomy.Capacity(p), 0.1, 0.11);
        for (var i = 0; i < 20; i++) SocietyEconomy.RecordMeal(p, true);
        Assert.Equal(1, SocietyEconomy.Capacity(p));
    }

    [Fact]
    public void SpoilageConservesGoodsAndFractionalLossSurvivesCheckpoint()
    {
        var world = SocietyFactory.Create("spoilage", false, 1100);
        var p = world.Agents["aster_food"];
        var before = p.Inventory["food"];
        var losses = world.Society!.LostGoods["food"];
        SocietyEconomy.SpoilFood(world, p);
        Assert.Equal(before / 100, before - p.Inventory["food"]);
        Assert.Equal(losses + before / 100, world.Society.LostGoods["food"]);
        var copy = Wire.Copy(world);
        SocietyEconomy.SpoilFood(world, p);
        SocietyEconomy.SpoilFood(copy, copy.Agents[p.Id]);
        Assert.Equal(p.Inventory["food"], copy.Agents[p.Id].Inventory["food"]);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void DelinquentTreasuryCannotRollOverAndImpairmentReducesBankEquity()
    {
        var world = SocietyFactory.Create("treasury", false, 1100);
        var n = world.Society!.Nations["cinder"];
        Assert.True(SocietyBank.BorrowForTreasury(world, n, 10000) > 0);
        var loan = Assert.Single(world.Society.Credit, c => c.Kind == "treasury");
        var bank = world.Agents[loan.BankId];
        var gov = world.Agents[loan.BorrowerId];
        SocietyEconomy.Transfer(world, gov, world.Agents["cinder_food"], SocietyBank.Liquid(world, gov));
        for (var i = 0; i < 3; i++) { world.Tick += 7; SocietyBank.Week(world, n); }
        Assert.True(loan.MissedPayments >= 3);
        Assert.True(loan.ProvisionMinor > 0);
        var provision = loan.ProvisionMinor;
        loan.MissedPayments = 0; // renewed delinquency cannot erase an existing allowance
        for (var i = 0; i < 3; i++) { world.Tick += 7; SocietyBank.Week(world, n); }
        Assert.True(loan.ProvisionMinor >= provision);
        Assert.Equal(0, SocietyBank.BorrowForTreasury(world, n, 10000));
        Assert.Equal(bank.CashMinor + SocietyBank.LoansOutstanding(world, bank) - SocietyBank.Deposits(world, n.Id) - loan.ProvisionMinor,
            SocietyBank.Equity(world, bank));
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void RepairRequiresTimeCrewMaterialsAndFundedFees()
    {
        var world = SocietyFactory.Create("repair", false, 1100);
        var n = world.Society!.Nations["aster"];
        var p = new Project { Id = "repair-road", Type = "roads", CostMinor = 31000, Complete = true, Condition = 50 };
        n.Infrastructure.Add(p);
        var firm = world.Agents["aster_construction"];
        foreach (var worker in world.InCountry("aster").Where(a => a.Role == "citizen").Take(2).ToArray())
        { SocietyLabor.Hire(world, firm, worker, "builder"); worker.Health = 100; worker.WorkEffort = 1; }
        Assert.True(SocietyInfrastructure.Enact(world, n, new PolicyProposal {
            ActionType = "repair_infrastructure", Params = SocietyActions.Make("repair_infrastructure", new { project_id = p.Id }).Params }));
        var tools = world.Society.ConsumedGoods["tools"];
        world.Tick = 7;
        SocietyInfrastructure.Day(world);
        Assert.InRange(p.Condition, 58, 60);
        Assert.Contains(p.Id, n.RepairOrders);
        Assert.True(world.Society.ConsumedGoods["tools"] > tools);
        Assert.Contains(world.Events, e => e.EventType == "infrastructure_repaired");
        SocietyEconomy.AssertConservation(world);
    }
}
