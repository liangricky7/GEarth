using Faultline.Core;

namespace Faultline.Tests;

public sealed class LaborTests
{
    private static World NewWorld() => SocietyFactory.Create("labor_test", false, 400);

    private static void AdvanceDays(World world, int days)
    {
        for (var day = 0; day < days; day++)
            SocietyEngine.Advance(world);
    }

    [Fact]
    public void LongUnemployedWorkerTakesTheBestPaidUnskilledOpening()
    {
        var world = NewWorld();
        var farm = world.Agents["aster_food"];
        var power = world.Agents["aster_energy"];
        var farmer = SocietyLabor.Staff(world, farm).OrderBy(worker => worker.Id).First();
        SocietyLabor.Fire(world, farmer);
        farmer.UnemployedSinceTick = world.Tick - SocietyLabor.RelatedJobAfterDays; // already looked for two weeks
        farm.TargetHeadcount = SocietyLabor.Staff(world, farm).Length;
        power.TargetHeadcount += 5; // the power plant is hiring

        AdvanceDays(world, 7);

        Assert.Equal(power.Id, farmer.EmployerId); // the power plant pays more than the farm
        Assert.Equal("energy_worker", farmer.Occupation);
        Assert.True(farmer.SalaryMinor > 0);
    }

    [Fact]
    public void FirmShortOfCashCutsPayAndLaysOffAndClosingReturnsCashToOwner()
    {
        var world = NewWorld();
        var workshop = world.Agents["aster_tools"];
        var owner = world.Agents[workshop.OwnerId!];
        var staff = SocietyLabor.Staff(world, workshop);
        var salary = staff[0].SalaryMinor;
        // Leave enough for roughly half the payroll and nothing to sell.
        SocietyEconomy.Transfer(workshop, world.Agents["aster_government"], workshop.CashMinor - staff.Sum(worker => worker.SalaryMinor) / 2);
        world.Agents["aster_government"].Inventory["tools"] += workshop.Inventory["tools"];
        workshop.Inventory["tools"] = 0;
        foreach (var worker in staff)
            worker.WorkEffort = 1;

        AdvanceDays(world, 7);

        Assert.True(SocietyLabor.Staff(world, workshop).All(worker => worker.SalaryMinor < salary));
        Assert.True(SocietyLabor.Staff(world, workshop).Length < staff.Length);
        Assert.Contains(world.Events, item => item.EventType == "labor_market" && item.Payload["pay_cuts"]!.GetValue<int>() > 0);

        // Closing (after four weeks unable to cover costs) lays everyone off and returns cash to the owner.
        var nation = world.Society!.Nations["aster"];
        var remaining = workshop.CashMinor;
        var ownerCash = owner.CashMinor;
        var laidOff = SocietyLabor.Staff(world, workshop);
        SocietyLabor.Close(world, nation, workshop, "test");

        Assert.Equal("defunct", workshop.Role);
        Assert.Equal(0, workshop.CashMinor);
        Assert.Equal(ownerCash + remaining, owner.CashMinor);
        Assert.Equal("citizen", owner.Role);
        Assert.Equal("factory_worker", owner.Occupation);
        Assert.Null(owner.OwnedBusinessId);
        Assert.All(laidOff, worker => Assert.Null(worker.EmployerId));
        Assert.Contains(world.Events, item => item.EventType == "business_closed" && item.EntityIds.Contains(workshop.Id));

        // At the next weekly review the richest resident able to run a workshop opens one.
        AdvanceDays(world, 7);
        Assert.Contains(SocietyLabor.Firms(world, "aster", "tools"), firm => firm.Id != workshop.Id);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void CitizenWithSavingsStartsABusinessThatUndercutsIncumbents()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var founder = world.Agents.Values.First(agent => agent.CountryId == "aster" && agent.Role == "citizen" && agent.Occupation == "farmer");
        var farm = world.Agents["aster_food"];
        farm.PriceMinor = 60; // incumbents are expensive: an opportunity
        var cost = SocietyLabor.StartupCost(founder);
        SocietyEconomy.Transfer(world.Agents["aster_bank"], founder, cost);
        var cash = founder.CashMinor;

        var job = SocietyEngine.Observe(world, founder.Id, DateTimeOffset.UtcNow);
        var pick = job.Candidates.Single(candidate => candidate.ActionType == "start_business");
        var record = SocietyEngine.Apply(world, job, new DecisionResult(job.RequestId,
            new ActionProposal(pick.ActionType, pick.Params, "test", []), "rule", "test", 0), DateTimeOffset.UtcNow);

        Assert.True(record.Accepted, record.Code);
        Assert.Equal("entrepreneur", founder.Role);
        var firm = world.Agents[founder.OwnedBusinessId!];
        Assert.Equal("food", firm.Specialty);
        Assert.Equal(54, firm.PriceMinor); // 10% under the cheapest incumbent
        Assert.Equal(cash - cost, founder.CashMinor);
        Assert.Equal(cost, firm.CashMinor);
        Assert.Equal(1, nation.BusinessesStarted);
        SocietyEconomy.AssertConservation(world);
    }
}
