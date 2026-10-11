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

    private static DecisionRecord Decide(World world, Agent agent, string actionType, Func<JsonObject, JsonObject>? edit = null,
        Func<Candidate, bool>? pick = null)
    {
        var job = SocietyEngine.Observe(world, agent.Id, DateTimeOffset.UtcNow);
        var candidate = job.Candidates.First(item => item.ActionType == actionType && (pick?.Invoke(item) ?? true));
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
        // Closed firms now offer residual inventory too: remove both active and estate supply.
        foreach (var firm in world.InCountry("aster").Where(agent => agent.Role is "business" or "defunct"))
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

        Assert.True(Decide(world, student, "enroll_school", pick: FullTime).Accepted);
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

    private static bool FullTime(Candidate candidate) => !candidate.Params["part_time"]!.GetValue<bool>();

    // Part time keeps the job at half hours and half pay for eight weeks; graduates are paid 15% more, and the
    // option shows the cost, the raise and the payback.
    [Fact]
    public void PartTimeStudentsKeepTheirJobAndGraduatesEarnMore()
    {
        var world = NewWorld();
        var student = Employee(world, "aster", "tools");
        SocietyEconomy.Transfer(world, world.Agents["aster_bank"], student, 5000);
        var (employer, salary) = (student.EmployerId, student.SalaryMinor);
        var offer = SocietyActions.Available(world, student).Single(candidate => candidate.ActionType == "enroll_school" && !FullTime(candidate));
        var raise = salary * SocietyLabor.GraduatePayPremiumPct / 100;
        Assert.Equal(raise, offer.Params["raise_minor_per_week"]!.GetValue<long>());
        Assert.Equal((2000 + 4 * salary + raise - 1) / raise, offer.Params["payback_weeks"]!.GetValue<long>());

        Assert.True(Decide(world, student, "enroll_school", pick: candidate => !FullTime(candidate)).Accepted);
        Assert.Equal(employer, student.EmployerId);
        student.Health = 100;
        student.WorkEffort = 1;
        Assert.Equal(0.5, SocietyLabor.DayEffort(world, student));
        Assert.Equal(salary / 2, SocietyEconomy.EarnedWage(student, salary));

        Assert.Equal(world.Tick + SocietyLabor.PartTimeCourseDays, student.StudentUntilTick);
        SocietySchool.Graduate(world, student); // still in the same job: the premium is paid at once
        Assert.Equal(salary + raise, student.SalaryMinor);
        Assert.False(student.PartTimeStudent);
        var hired = world.Agents.Values.First(person => person.Role == "citizen" && person.EmployerId is null && !person.Graduated);
        hired.Graduated = true;
        SocietyLabor.Hire(world, world.Agents["aster_tools"], hired, "factory_worker");
        Assert.Equal(world.Agents["aster_tools"].WageOfferMinor * 115 / 100, hired.SalaryMinor);
        SocietyEconomy.AssertConservation(world);
    }

    // A student who cannot pay borrows the tuition; repayment starts after the course, and after three missed
    // payments the government pays the bank and takes the debt over.
    [Fact]
    public void StudentLoansAreGuaranteedByTheGovernment()
    {
        var world = NewWorld();
        var student = Employee(world, "aster", "tools");
        var government = world.Agents["aster_government"];
        var bank = world.Agents["aster_bank"];
        SocietyEconomy.Transfer(world, student, government, student.CashMinor);

        Assert.True(Decide(world, student, "enroll_school", pick: candidate => FullTime(candidate) && candidate.Params.ContainsKey("student_loan_minor")).Accepted);
        var loan = Assert.Single(world.Society!.Credit, credit => credit.Kind == "student");
        Assert.Equal(2000, loan.PrincipalMinor);
        SocietyBank.Week(world, world.Society.Nations["aster"]);
        Assert.Equal(2000, loan.PrincipalLeftMinor); // nothing is due while studying

        SocietySchool.Graduate(world, student);
        var bankBefore = bank.CashMinor;
        for (var week = 0; week < SocietyBank.MissedPaymentsToDefault; week++)
            SocietyBank.Week(world, world.Society.Nations["aster"]);
        Assert.Equal(government.Id, loan.BankId);
        Assert.Equal(bankBefore + 2000, bank.CashMinor);
        SocietyEconomy.AssertConservation(world);
    }

    // A state employer that cannot pay owes the wages; two weeks owed and the worker strikes until paid.
    [Fact]
    public void UnpaidPublicWagesBecomeArrearsAndAStrike()
    {
        var world = NewWorld();
        var government = world.Agents["aster_government"];
        var school = Assert.Single(SocietyLabor.Firms(world, "aster", "school"));
        var teacher = SocietyLabor.Staff(world, school).First();
        var sink = world.Agents["aster_journalist"];
        SocietyEconomy.Transfer(world, government, sink, government.CashMinor);
        SocietyEconomy.Transfer(world, school, sink, school.CashMinor);
        var contractor = world.Agents["aster_construction"]; // an idle contractor returns its cash to the treasury
        SocietyEconomy.Transfer(world, contractor, sink, contractor.CashMinor);

        for (var day = 0; day < 2 * SocietyLabor.DaysPerWeek; day++)
            SocietyEngine.Advance(world);
        Assert.True(teacher.ArrearsMinor > 0);
        Assert.True(SocietyLabor.OnArrearsStrike(teacher));
        Assert.Equal(0, SocietyLabor.DayEffort(world, teacher));

        SocietyEconomy.Transfer(world, sink, government, sink.CashMinor);
        for (var day = 0; day < SocietyLabor.DaysPerWeek; day++)
            SocietyEngine.Advance(world);
        Assert.Equal(0, teacher.ArrearsMinor);
        Assert.False(SocietyLabor.OnArrearsStrike(teacher));
        SocietyEconomy.AssertConservation(world);
    }

    // A planned state short of a week's spending borrows from its state bank before wages go unpaid.
    [Fact]
    public void APlannedTreasuryBorrowsFromItsStateBank()
    {
        var world = NewWorld();
        var government = world.Agents["cinder_government"];
        for (var day = 0; day < SocietyLabor.DaysPerWeek; day++)
            SocietyEngine.Advance(world);
        SocietyEconomy.Transfer(world, government, world.Agents["cinder_journalist"], government.CashMinor);
        for (var day = 0; day < SocietyLabor.DaysPerWeek; day++)
            SocietyEngine.Advance(world);
        var loan = Assert.Single(world.Society!.Credit, credit => credit.Kind == "treasury");
        Assert.Equal(government.Id, loan.BorrowerId);
        Assert.Contains(world.Events, item => item.EventType == "treasury_borrowed");
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
    public void FoundersEnterOnAMarginUnmetDemandAndIdleWorkers()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var founder = Employee(world, "aster", "tools");
        SocietyEconomy.Transfer(world, world.Agents["aster_bank"], founder, SocietyLabor.StartupCost("food"));
        var farms = SocietyLabor.Firms(world, "aster", "food").ToArray();
        bool Offered() => SocietyActions.Available(world, founder).Any(candidate => candidate.ActionType == "start_business"
            && candidate.Params["sector"]!.GetValue<string>() == "food");

        foreach (var farm in farms)
            farm.PriceMinor = 60; // well above what a unit costs to make
        Assert.False(Offered()); // a fat margin alone is no gap: nobody goes without
        nation.PricedOutLastWeek = 3;
        Assert.False(Offered()); // residents go hungry, but nobody out of work could farm yet
        world.Tick = SocietyLabor.RelatedJobAfterDays;
        Assert.True(Offered()); // now the unemployed can take farm work
        foreach (var farm in farms)
            farm.PriceMinor = (int)SocietyLabor.UnitCost(world, nation, farm) + 1;
        Assert.False(Offered()); // priced near cost: no margin for a newcomer
        Assert.Equal(8 * SocietyLabor.TableWage["farmer"], SocietyLabor.StartupCost("food"));
    }

    // Income tax is one option, a new whole-percent rate near the current one, offered only at a budget turn
    // every four weeks.
    [Fact]
    public void GovernmentsSetTaxAtABudgetTurn()
    {
        var world = NewWorld();
        var government = world.Agents["aster_government"];
        var nation = world.Society!.Nations["aster"];
        var tax = Assert.Single(SocietyActions.Available(world, government), candidate => candidate.ActionType == "adjust_tax");
        var range = tax.Params["income_tax_percent"]!;
        Assert.Equal([10, 20, 15], new[] { range["min"]!.GetValue<int>(), range["max"]!.GetValue<int>(), range["default"]!.GetValue<int>() });

        var action = new ActionProposal("adjust_tax", new JsonObject { ["income_tax_percent"] = 13 }, "Test policy.", []);
        SocietyDiplomacy.Propose(world, government, action, "cause");
        for (var tick = 0; tick < Governments.Get(nation.GovernmentId).PolicyDelay; tick++)
            SocietyEngine.Advance(world);
        Assert.Equal(1300, nation.TaxBasisPoints);
        Assert.DoesNotContain(SocietyActions.Available(world, government), candidate => candidate.ActionType == "adjust_tax");
        world.Tick = nation.TaxChangedTick + SocietyActions.BudgetTurnDays;
        Assert.Contains(SocietyActions.Available(world, government), candidate => candidate.ActionType == "adjust_tax");
    }
}
