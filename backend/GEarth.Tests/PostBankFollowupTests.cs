using System.Text.Json;
using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Tests;

public sealed class PostBankFollowupTests
{
    private static World Create() => SocietyFactory.Create("post_bank_followup", true, 1100);
    private static Agent Resident(World world, string country = "aster") =>
        world.InCountry(country).First(person => person.Role == "citizen");
    private static void Week(World world) => SocietyLabor.Week(world, world.Society!.Nations["aster"],
        world.Agents["aster_government"], world.InCountry("aster").Where(SocietyLabor.IsResident).ToArray());

    [Fact]
    public void LendingCreatesMoneyWhileKeepingBaseCashAndBankEquityUnchanged()
    {
        var world = Create();
        var person = Resident(world);
        var bank = SocietyBank.BankOf(world, person.CountryId);
        var (cash, deposit, reserves, equity, broad) = (person.CashMinor, person.DepositMinor, bank.CashMinor,
            SocietyBank.Equity(world, bank), SocietyBank.BroadMoney(world));
        var loan = SocietyBank.StudentLoan(world, person, 1000);
        Assert.True(loan.DepositCreated);
        Assert.Equal(cash, person.CashMinor);
        Assert.Equal(deposit + 1000, person.DepositMinor);
        Assert.Equal(reserves, bank.CashMinor);
        Assert.Equal(equity, SocietyBank.Equity(world, bank));
        Assert.Equal(broad + 1000, SocietyBank.BroadMoney(world));
        Assert.Equal(1000, world.Society!.CreditCreatedMinor);
        SocietyEconomy.AssertConservation(world);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DepositPaymentsSettleDomesticallyOrMoveReservesAcrossBanks(bool foreign)
    {
        var world = Create();
        var payer = Resident(world);
        var recipient = world.Agents[foreign ? "beryl_food" : "aster_food"];
        SocietyBank.StudentLoan(world, payer, 1000);
        SocietyEconomy.Transfer(world, payer, world.Agents["aster_government"], payer.CashMinor);
        var source = SocietyBank.BankOf(world, "aster");
        var destination = SocietyBank.BankOf(world, recipient.CountryId);
        var (sourceCash, destinationCash, recipientDeposit, broad) = (source.CashMinor, destination.CashMinor,
            recipient.DepositMinor, SocietyBank.BroadMoney(world));
        SocietyEconomy.Transfer(world, payer, recipient, 500, "purchase");
        Assert.Equal(500, payer.DepositMinor);
        Assert.Equal(recipientDeposit + 500, recipient.DepositMinor);
        Assert.Equal(sourceCash - (foreign ? 500 : 0), source.CashMinor);
        Assert.Equal(destinationCash + (foreign ? 500 : 0), destination.CashMinor);
        Assert.Equal(broad, SocietyBank.BroadMoney(world));
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void CapitalAndReservesBothLimitAdditionalLoans()
    {
        var world = Create();
        var bank = SocietyBank.BankOf(world, "aster");
        var room = SocietyBank.LendingRoom(world, bank);
        Assert.True(room > 0);
        Assert.False(SocietyBank.CanLend(world, bank, room + 1));
        Assert.Throws<InvalidOperationException>(() => SocietyBank.StudentLoan(world, Resident(world), room + 1));
        Assert.Empty(world.Society!.Credit);
        SocietyEconomy.Transfer(world, bank, world.Agents["aster_government"], bank.CashMinor);
        Assert.False(SocietyBank.CanLend(world, bank, 1));
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void GuaranteedStudentDebtRetiresBankCreditOnceAndThenRepaysTheTreasury()
    {
        var world = Create();
        var student = Resident(world);
        var government = world.Agents["aster_government"];
        var loan = SocietyBank.StudentLoan(world, student, 1000);
        SocietyEconomy.Transfer(world, student, government, SocietyBank.Liquid(world, student));
        for (var week = 0; week < SocietyBank.MissedPaymentsToDefault; week++)
            SocietyBank.Week(world, world.Society!.Nations["aster"]);
        Assert.Equal(government.Id, loan.BankId);
        Assert.Equal(1000, world.Society!.CreditRetiredMinor);
        SocietyEconomy.Transfer(world, government, student, 2000);
        SocietyBank.Week(world, world.Society.Nations["aster"]);
        Assert.True(loan.PrincipalLeftMinor < loan.PrincipalMinor);
        Assert.Equal(1000, world.Society.CreditRetiredMinor);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void HungerRemainsNearIncapacitationAndEatingRestoresCapacity()
    {
        var world = Create();
        var person = Resident(world);
        var healthyCapacity = SocietyEconomy.Capacity(person);
        person.Hungry = true;
        Assert.Equal(healthyCapacity * 0.01, SocietyEconomy.Capacity(person), 8);
        SocietyEngine.Advance(world);
        Assert.False(person.Hungry);
        Assert.Same(person, world.Agents[person.Id]);
        Assert.True(SocietyEconomy.Capacity(person) > healthyCapacity * 0.5);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void APreviouslyUnprofitableFirmRetainsWageCatchUpWhenPricesStopRising()
    {
        var world = Create();
        var firm = world.Agents["aster_tools"];
        foreach (var farm in SocietyLabor.Firms(world, "aster", "food")) farm.PriceMinor = 80;
        world.Tick = SocietyLabor.IndexationDays;
        Week(world);
        var reference = firm.WageIndexReference;
        Assert.True(reference < SocietyLabor.LivingCost(world, world.Society!.Nations["aster"]));
        Assert.DoesNotContain(world.Events, item => item.EventType == "wage_indexed" && item.EntityIds.Contains(firm.Id));
        world.Society.PeriodRevenueMinor[firm.Id] = 10_000_000;
        world.Tick += SocietyLabor.IndexationDays;
        Week(world);
        var raised = Assert.Single(world.Events, item => item.EventType == "wage_indexed" && item.EntityIds.Contains(firm.Id));
        Assert.True(raised.Payload["raise_pct"]!.GetValue<double>() > 0);
        Assert.True(firm.WageIndexReference > reference);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void MarketEntryCostsRespondToActualIndustrialInputPrices()
    {
        var world = Create();
        var firm = world.Agents["aster_tools"];
        var before = SocietyLabor.UnitCost(world, world.Society!.Nations["aster"], firm);
        foreach (var mine in SocietyLabor.Firms(world, "aster", "mine")) mine.PriceMinor += 100;
        var after = SocietyLabor.UnitCost(world, world.Society.Nations["aster"], firm);
        Assert.Equal(100, after - before, 6);
    }

    [Fact]
    public void StudentsAndWorkRefusalsDoNotInflateLaborForceUnemployment()
    {
        var world = Create();
        var nation = world.Society!.Nations["aster"];
        var people = world.InCountry("aster").Where(person => person.Role == "citizen").Take(3).ToArray();
        foreach (var person in people) SocietyLabor.Fire(world, person);
        people[0].StudentUntilTick = world.Tick + 28;
        people[1].WorkEffort = 0;
        people[2].WorkEffort = 1;
        SocietyMetrics.Labor(world, nation);
        Assert.False(SocietyMetrics.SeekingWork(people[0]));
        Assert.False(SocietyMetrics.SeekingWork(people[1]));
        Assert.True(SocietyMetrics.SeekingWork(people[2]));
        Assert.Equal(nation.EmployedResidents + nation.Unemployed, nation.LaborForce);
        Assert.Equal(world.InCountry("aster").Count(SocietyLabor.IsResident), nation.LaborForce + nation.Nonparticipants);
        Assert.Contains(world.InCountry("aster"), person => person.Role == "entrepreneur" && SocietyMetrics.Employed(person));
    }

    [Fact]
    public void StalePurchaseGetsOneFreshMenuWithinItsOriginalDeadline()
    {
        var world = Create();
        var person = Resident(world);
        var now = DateTimeOffset.UtcNow;
        var job = SocietyEngine.Observe(world, person.Id, now);
        var choice = job.Candidates.First(candidate => candidate.ActionType == "buy_goods");
        var seller = world.Agents[choice.Params["seller_id"]!.GetValue<string>()];
        seller.PriceMinor++;
        var result = new DecisionResult(job.RequestId, new(choice.ActionType, (JsonObject)choice.Params.DeepClone(), "Buy essentials.", []), "model", "test", 0);
        var record = SocietyEngine.Apply(world, job, result, now);
        Assert.Equal("CURRENT_PRECONDITION_FAILED", record.Code);
        var refreshed = SocietyEngine.Refresh(world, record, now)!;
        Assert.NotNull(refreshed);
        Assert.Equal(job.Deadline, refreshed.Deadline);
        Assert.Equal(1, refreshed.RefreshAttempt);
        Assert.Null(SocietyEngine.Refresh(world, record with { Job = refreshed }, now));
        Assert.Null(SocietyEngine.Refresh(world, record, job.Deadline.AddSeconds(-4)));
        Assert.DoesNotContain("refresh_attempt", JsonSerializer.Serialize(refreshed, Wire.Json));
    }

    [Fact]
    public void ReportingUsesHistoricalRolesAfterAnOwnerReturnsToCitizenship()
    {
        var world = Create();
        var firm = world.Agents["aster_entertainment"];
        var owner = world.Agents[firm.OwnerId!];
        var now = DateTimeOffset.UtcNow;
        var job = SocietyEngine.Observe(world, owner.Id, now);
        SocietyEngine.Apply(world, job, new(job.RequestId, new("hold", new(), "Wait.", []), "rule_fallback", "test", 1), now);
        SocietyLabor.Close(world, world.Society!.Nations["aster"], firm, "test");
        var notes = RunAnalysis.Create(world, world.Events);
        Assert.Contains(notes.Observations, item => item.Category == "reliability_by_role" && item.Text.StartsWith("entrepreneur: 1/1"));
    }
}
