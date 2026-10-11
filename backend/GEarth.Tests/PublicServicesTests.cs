using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Tests;

// Health, clinics, hunger, imports, subsidies, school and the planned economy's plan.
public sealed class PublicServicesTests
{
    private static World NewWorld() => SocietyFactory.Create("services_test", false, 1100);

    private static Agent Employee(World world, string country, string sector) => world.Agents.Values
        .Where(agent => agent.CountryId == country && agent.Role == "citizen" && agent.EmployerId is { } employer
            && world.Agents[employer].Specialty == sector).OrderBy(agent => agent.Id).First();

    private static DecisionRecord Decide(World world, Agent agent, string actionType, Func<JsonObject, JsonObject>? edit = null)
    {
        var job = SocietyEngine.Observe(world, agent.Id, DateTimeOffset.UtcNow);
        var candidate = job.Candidates.First(item => item.ActionType == actionType);
        var parameters = SocietyActions.Resolve(candidate);
        return SocietyEngine.Apply(world, job, new DecisionResult(job.RequestId,
            new ActionProposal(actionType, edit?.Invoke(parameters) ?? parameters, "test", []), "model", "test", 0), DateTimeOffset.UtcNow);
    }

    // Good health adds up to 20 points of quality of life; bad health takes up to 50, faster as it falls.
    // Below 30 a resident is too sick to work: no output and no pay for those days.
    [Fact]
    public void BadHealthHurtsQualityOfLifeSteeplyAndStopsWork()
    {
        Assert.Equal(20, SocietyEconomy.HealthPoints(100), 3);
        Assert.InRange(SocietyEconomy.HealthPoints(70), 12, 13);
        Assert.Equal(-50, SocietyEconomy.HealthPoints(0), 3);
        Assert.True(SocietyEconomy.HealthPoints(50) - SocietyEconomy.HealthPoints(30) > SocietyEconomy.HealthPoints(90) - SocietyEconomy.HealthPoints(70));

        var world = NewWorld();
        var farmer = Employee(world, "aster", "food");
        Assert.Equal(1, SocietyLabor.DayEffort(world, farmer));
        farmer.Health = 20;
        Assert.Equal(0, SocietyLabor.DayEffort(world, farmer));
        farmer.SickDays = 3;
        farmer.InKindMinor = 100;
        Assert.Equal(1000 * 4 / 7 - 100, SocietyEconomy.EarnedWage(farmer, 1000)); // sick days unpaid, food taken in kind deducted
        Assert.Equal(0, farmer.InKindMinor);
    }

    // A clinic never stays without a doctor: a closed clinic's doctor reopens one on a startup grant.
    [Fact]
    public void ADoctorReopensAClosedClinicOnAGrant()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var clinic = world.Agents["aster_clinic"];
        var government = world.Agents["aster_government"];
        var treasury = government.CashMinor;
        SocietyLabor.Close(world, nation, clinic, "test");
        Assert.Empty(SocietyLabor.Firms(world, "aster", "clinic"));
        foreach (var doctor in world.InCountry("aster").Where(agent => agent.Occupation == "doctor")) // too poor to reopen it themselves
        {
            SocietyEconomy.Transfer(world, doctor, world.Agents["aster_bank"], doctor.CashMinor);
            doctor.DepositMinor = 0;
        }
        treasury = government.CashMinor;

        for (var day = 0; day < SocietyLabor.DaysPerWeek; day++)
            SocietyEngine.Advance(world);

        var reopened = Assert.Single(SocietyLabor.Firms(world, "aster", "clinic"));
        Assert.NotEmpty(SocietyLabor.Workers(world, reopened));
        var grant = world.Events.Single(item => item.EventType == "startup_grant");
        Assert.Equal(SocietyLabor.StartupCost("clinic"), grant.Payload["grant_minor"]!.GetValue<long>());
        Assert.True(government.CashMinor < treasury);
        SocietyEconomy.AssertConservation(world);
    }

    // A missed meal cuts output by 99% until the next one; farm workers eat first, as pay in kind when broke.
    [Fact]
    public void FarmWorkersEatFirstAndHungerCutsOutput()
    {
        var world = NewWorld();
        var farmer = Employee(world, "aster", "food");
        var healthy = SocietyEconomy.Capacity(farmer);
        farmer.Hungry = true;
        Assert.Equal(healthy * SocietyEconomy.HungerOutputFactor, SocietyEconomy.Capacity(farmer), 6);

        SocietyEconomy.Transfer(world, farmer, world.Agents["aster_government"], farmer.CashMinor);
        farmer.DepositMinor = 0;
        world.Society!.LostGoods["food"] += farmer.Inventory["food"];
        farmer.Inventory["food"] = 0;
        SocietyEngine.Advance(world);

        Assert.False(farmer.Hungry);
        Assert.True(farmer.InKindMinor > 0); // owed back from the next wage
        SocietyEconomy.AssertConservation(world);
    }

    // With no domestic supplier left, firms import their inputs automatically.
    [Fact]
    public void FirmsImportInputsWhenNothingIsLeftAtHome()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        foreach (var plant in SocietyLabor.Firms(world, "aster", "energy").ToArray())
            SocietyLabor.Close(world, nation, plant, "test");
        foreach (var firm in world.InCountry("aster").Where(agent => agent.Role == "business"))
        {
            world.Society!.LostGoods["energy"] += firm.Inventory["energy"];
            firm.Inventory["energy"] = 0;
        }
        foreach (var farm in SocietyLabor.Firms(world, "aster", "food")) // empty shelves: the farms must produce today
        {
            world.Society!.LostGoods["food"] += farm.Inventory["food"];
            farm.Inventory["food"] = 0;
        }

        SocietyEngine.Advance(world);

        var dispatched = world.Events.Where(item => item.EventType == "shipment_dispatched"
            && item.Payload["good"]!.GetValue<string>() == "energy" && item.EntityIds[1].StartsWith("aster_")).ToArray();
        Assert.NotEmpty(dispatched);
        Assert.All(dispatched, item => Assert.Empty(item.CauseIds)); // placed by rule, not by a decision
        SocietyEconomy.AssertConservation(world);
    }

    // A food subsidy has the treasury pay its share; the farm still receives the full price.
    [Fact]
    public void FoodSubsidySplitsThePriceWithTheTreasury()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        nation.FoodSubsidyPct = 50;
        var shopper = Employee(world, "aster", "tools");
        var government = world.Agents["aster_government"];
        var (cash, treasury) = (shopper.CashMinor, government.CashMinor);
        var price = SocietyEconomy.Sellers(world, nation, "food").First().PriceMinor;

        Assert.Equal(1, SocietyEconomy.Acquire(world, shopper, "food", 1, true));

        Assert.True(cash - shopper.CashMinor <= price / 2 + 1);
        Assert.Equal(price - price / 2, nation.FoodSubsidyPaidMinor);
        Assert.True(treasury - government.CashMinor < price); // the subsidy less the sales tax on the household's half
        SocietyEconomy.AssertConservation(world);
    }

    // School is full time: an employee leaves their job to enroll; four weeks later they graduate producing
    // a quarter more.
    [Fact]
    public void EmployeesQuitToStudyAndGraduatesProduceMore()
    {
        var world = NewWorld();
        var student = Employee(world, "aster", "tools");
        SocietyEconomy.Transfer(world, world.Agents["aster_bank"], student, 5000);
        var school = Assert.Single(SocietyLabor.Firms(world, "aster", "school"));
        var schoolCash = school.CashMinor;

        Assert.True(Decide(world, student, "enroll_school").Accepted);
        Assert.Null(student.EmployerId);
        Assert.True(SocietyLabor.IsStudent(student));
        Assert.Equal(schoolCash + 2000, school.CashMinor);

        for (var day = 0; day < SocietyLabor.CourseDays; day++)
            SocietyEngine.Advance(world);

        Assert.True(student.Graduated);
        Assert.False(SocietyLabor.IsStudent(student));
        student.Health = 100;
        student.WorkEffort = 1;
        Assert.Equal(SocietyLabor.GraduateOutputBoost, SocietyLabor.DayEffort(world, student));
        SocietyEconomy.AssertConservation(world);
    }

    // The planner sets free-number quotas; state firms make their share whatever is selling.
    [Fact]
    public void PlannerSetsFreeQuotasThatStateFirmsFollow()
    {
        var world = NewWorld();
        var planner = world.InCountry("cinder").Single(agent => agent.Role == "planner");
        Assert.Equal(SocietyLabor.TableWage["planner"], planner.SalaryMinor);
        var nation = world.Society!.Nations["cinder"];

        var tooMuch = Decide(world, planner, "set_plan", plan => { plan["food"] = int.MaxValue; return plan; });
        Assert.False(tooMuch.Accepted); // outside the offered range
        Assert.True(Decide(world, planner, "set_plan", plan => { plan["food"] = 0; return plan; }).Accepted);
        Assert.Equal(0, nation.Plan["food"]);

        var stock = SocietyLabor.Firms(world, "cinder", "food").Sum(farm => farm.Inventory["food"]);
        var produced = world.Society.ProducedGoods["food"];
        SocietyEngine.Advance(world);
        var aster = world.Society.ProducedGoods["food"] - produced;
        Assert.True(aster > 0); // market farms still produce
        Assert.True(SocietyLabor.Firms(world, "cinder", "food").Sum(farm => farm.Inventory["food"]) <= stock); // the plan made none
    }

    // A founder sees a gap only where residents feel the shortfall and the market shows it.
    [Fact]
    public void FoundersNeedAFeltGapAndPayBySector()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var founder = Employee(world, "aster", "tools");
        SocietyEconomy.Transfer(world, world.Agents["aster_bank"], founder, SocietyLabor.StartupCost("food"));
        foreach (var farm in SocietyLabor.Firms(world, "aster", "food"))
            farm.PriceMinor = 60; // the market shows a gap
        bool Offered() => SocietyActions.Available(world, founder).Any(candidate => candidate.ActionType == "start_business"
            && candidate.Params["sector"]!.GetValue<string>() == "food");

        nation.QolParts["needs"] = 0.9;
        Assert.False(Offered()); // residents are fed: no felt gap
        nation.QolParts["needs"] = 0.5;
        Assert.True(Offered()); // a factory worker may start a farm
        Assert.Equal(8 * SocietyLabor.TableWage["farmer"], SocietyLabor.StartupCost("food"));
    }

    // Income tax moves in small steps chosen by the government.
    [Fact]
    public void GovernmentsAdjustTaxInSmallSteps()
    {
        var world = NewWorld();
        var steps = SocietyActions.Available(world, world.Agents["aster_government"])
            .Where(candidate => candidate.ActionType == "adjust_tax")
            .Select(candidate => candidate.Params["change_basis_points"]!.GetValue<int>()).Order().ToArray();
        Assert.Equal([-100, -50, -25, 25, 50, 100], steps);
        Assert.DoesNotContain(SocietyActions.Available(world, world.Agents["aster_government"]), candidate => candidate.ActionType == "set_tax");
    }
}
