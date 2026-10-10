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
        // Leave enough for roughly a quarter of the payroll (short even if a rival poaches a worker) and nothing to sell.
        SocietyEconomy.Transfer(world, workshop, world.Agents["aster_government"], workshop.CashMinor - staff.Sum(worker => worker.SalaryMinor) / 4);
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
        Assert.True(owner.CashMinor >= ownerCash + remaining); // plus stock a competitor bought at half price
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
    public void OvertimeNeedsTheEmployersOfferAndIsPaidAtTimeAndAHalf()
    {
        var world = SocietyFactory.Create("overtime_test", false, 1100);
        var farm = world.Agents["aster_food"];
        var (volunteer, regular) = (SocietyLabor.Staff(world, farm)[0], SocietyLabor.Staff(world, farm)[1]);
        var unoffered = SocietyLabor.Staff(world, world.Agents["aster_food_2"])[0];
        foreach (var worker in new[] { volunteer, regular, unoffered })
            worker.SalaryMinor = 3000; // well paid, so no rival poaches them mid-test
        volunteer.WorkEffort = 2;
        unoffered.WorkEffort = 2; // consents, but its farm offers no overtime
        farm.OvertimeOffered = true;

        AdvanceDays(world, SocietyLabor.DaysPerWeek - 1);
        Assert.Equal(6, volunteer.OvertimeDays);
        Assert.Equal(0, regular.OvertimeDays);
        Assert.Equal(0, unoffered.OvertimeDays);
        Assert.Equal(1.25, SocietyLabor.DayEffort(world, volunteer));
        Assert.Equal(1, SocietyLabor.DayEffort(world, unoffered));

        AdvanceDays(world, 1); // payday: seven days of a quarter more hours at 1.5x pay
        Assert.Equal(regular.LastWeekInMinor + 3000 * 7 * 3 / 56, volunteer.LastWeekInMinor);
        Assert.Equal(0, volunteer.OvertimeDays);
    }

    [Fact]
    public void BusinessCashAboveItsPayrollBufferIsPaidToTheOwnerAsDividends()
    {
        var world = SocietyFactory.Create("dividend_test", false, 1100);
        var farm = world.Agents["aster_food"];
        var owner = world.Agents[farm.OwnerId!];
        SocietyEconomy.Transfer(world, world.Agents["aster_bank"], farm, 100000);
        var ownerCash = owner.CashMinor;

        AdvanceDays(world, SocietyLabor.DaysPerWeek);

        var buffer = Math.Max(10000, SocietyLabor.DividendBufferWeeks * SocietyLabor.Staff(world, farm).Sum(worker => worker.SalaryMinor));
        Assert.True(farm.CashMinor <= buffer);
        Assert.True(owner.CashMinor > ownerCash + 50000);
        Assert.True(world.Society!.Nations["aster"].DividendsPaidMinor >= 50000);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void SkilledWorkerStepsDownToUnskilledWorkAndReturnsWhenTheirTradeHires()
    {
        var world = SocietyFactory.Create("skill_test", false, 1100);
        var clinic = world.Agents["aster_clinic"];
        var farm = world.Agents["aster_food"];
        var doctor = SocietyLabor.Staff(world, clinic).Single();
        Assert.True(SocietyLabor.CanSwitch("doctor", "farmer"));
        Assert.False(SocietyLabor.CanSwitch("farmer", "doctor"));

        SocietyLabor.Hire(world, farm, doctor, "farmer"); // laid off and took farm work
        Assert.Equal("doctor", doctor.Occupation); // the qualification is kept
        clinic.TargetHeadcount = SocietyLabor.Staff(world, clinic).Length + 2;
        clinic.WageOfferMinor = SocietyLabor.TableWage["doctor"];

        AdvanceDays(world, SocietyLabor.DaysPerWeek);

        Assert.Equal(clinic.Id, doctor.EmployerId); // a doctor's opening pays far more than 10% extra
        Assert.True(doctor.SalaryMinor >= SocietyLabor.TableWage["doctor"]); // the clinic's offer, which may have risen
    }

    [Fact]
    public void EmptyShelvesRaisePricesInProportionUpToTwentyPercentAWeek()
    {
        var world = NewWorld();
        var farm = world.Agents["aster_food"];
        foreach (var worker in SocietyLabor.Workers(world, farm))
            worker.WorkEffort = 0; // a strike: nothing new to sell
        world.Agents["aster_government"].Inventory["food"] += farm.Inventory["food"];
        farm.Inventory["food"] = 0;
        var price = farm.PriceMinor;

        AdvanceDays(world, SocietyLabor.DaysPerWeek);

        Assert.Equal(price * 120 / 100, farm.PriceMinor);
    }

    [Fact]
    public void CitizenWithSavingsStartsABusinessThatUndercutsIncumbents()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var founder = world.Agents.Values.First(agent => agent.CountryId == "aster" && agent.Role == "citizen" && agent.Occupation == "farmer");
        foreach (var farm in SocietyLabor.Firms(world, "aster", "food"))
            farm.PriceMinor = 60; // incumbents are expensive: an opportunity
        var cost = SocietyLabor.StartupCost(founder, "food");
        SocietyEconomy.Transfer(world, world.Agents["aster_bank"], founder, cost);
        var cash = founder.CashMinor;

        var job = SocietyEngine.Observe(world, founder.Id, DateTimeOffset.UtcNow);
        var pick = job.Candidates.Single(candidate => candidate.ActionType == "start_business"
            && candidate.Params["sector"]!.GetValue<string>() == "food");
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
