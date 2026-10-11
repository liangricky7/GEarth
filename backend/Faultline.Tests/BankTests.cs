using Faultline.Core;

namespace Faultline.Tests;

public sealed class BankTests
{
    // 400 agents = 10 countries x (25 residents + 15 institutions). Aster is a market democracy
    // (deposits insured to 50,000); Dune is a dictatorship (no deposit insurance).
    private static World NewWorld() => SocietyFactory.Create("bank_test", false, 400);

    private static Agent Resident(World world, string country) =>
        world.Agents.Values.Where(agent => agent.CountryId == country && agent.Role == "citizen").OrderBy(agent => agent.Id).First();

    private static void Give(World world, Agent agent, long amount) =>
        SocietyEconomy.Transfer(world, world.Agents[agent.CountryId + "_government"], agent, amount);

    private static long Deposit(World world, Agent resident, long cash)
    {
        Give(world, resident, cash);
        SocietyBank.Sweep(world, world.Society!.Nations[resident.CountryId], [resident]);
        return resident.DepositMinor;
    }

    private static void Decide(World world, string agentId, string action, Func<Candidate, bool>? match = null)
    {
        var job = SocietyEngine.Observe(world, agentId, DateTimeOffset.UtcNow);
        var pick = job.Candidates.First(candidate => candidate.ActionType == action && (match?.Invoke(candidate) ?? true));
        var result = new DecisionResult(job.RequestId, new ActionProposal(pick.ActionType, pick.Params, "test", []), "rule", "test", 0);
        var record = SocietyEngine.Apply(world, job, result, DateTimeOffset.UtcNow);
        Assert.True(record.Accepted, record.Code);
    }

    // The tools firm's owner borrows four weeks of payroll; enough revenue this week passes the standard.
    private static (Agent Firm, Agent Bank, Credit Loan) FundLoan(World world)
    {
        var firm = world.Agents["aster_tools"];
        var smallest = SocietyActions.Available(world, world.Agents[firm.OwnerId!]).Where(candidate => candidate.ActionType == "request_loan")
            .Min(candidate => candidate.Params["amount_minor"]!.GetValue<long>());
        Decide(world, firm.OwnerId!, "request_loan", candidate => candidate.Params["amount_minor"]!.GetValue<long>() == smallest);
        world.Society!.PeriodRevenueMinor[firm.Id] = 100000;
        var bank = world.Agents["aster_bank"];
        var (bankBefore, firmBefore) = (bank.CashMinor, firm.CashMinor);
        SocietyBank.Week(world, world.Society.Nations["aster"]);
        var loan = Assert.Single(world.Society.Credit);
        Assert.Equal("active", loan.Status);
        Assert.Equal(bankBefore - loan.PrincipalMinor, bank.CashMinor);
        Assert.Equal(firmBefore + loan.PrincipalMinor, firm.CashMinor);
        return (firm, bank, loan);
    }

    [Fact]
    public void SavingsAboveTwoWeeksPayAreDepositedAndEarnInterest()
    {
        var world = NewWorld();
        var resident = Resident(world, "aster");
        var bank = world.Agents["aster_bank"];
        var bankBefore = bank.CashMinor;

        var deposit = Deposit(world, resident, 20000);

        Assert.Equal((long)(2 * SocietyEconomy.WeeklyPay(resident)), resident.CashMinor);
        Assert.Equal(bankBefore + SocietyBank.Deposits(world, "aster"), bank.CashMinor); // firms bank their spare cash too
        SocietyBank.Week(world, world.Society!.Nations["aster"]);
        Assert.Equal(deposit + (long)Math.Round(deposit * 3 / 5200.0), resident.DepositMinor); // 3% a year, weekly
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void BusinessLoanIsRepaidInWeeklyInstallmentsWithInterest()
    {
        var world = NewWorld();
        var (firm, bank, loan) = FundLoan(world);
        var bankAfterFunding = bank.CashMinor;
        Give(world, firm, loan.PrincipalMinor); // enough for every installment

        for (var week = 0; week < SocietyBank.LoanWeeks && loan.Status == "active"; week++)
            SocietyBank.Week(world, world.Society!.Nations["aster"]);

        Assert.Equal("repaid", loan.Status);
        Assert.True(bank.CashMinor > bankAfterFunding + loan.PrincipalMinor); // principal back plus interest
        Assert.Single(world.Events, item => item.EventType == "loan_settled");
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void ThreeMissedInstallmentsCloseTheFirmAndTheOwnerOwesTheRest()
    {
        var world = NewWorld();
        var (firm, bank, loan) = FundLoan(world);
        var government = world.Agents["aster_government"];
        var owner = world.Agents[firm.OwnerId!];
        SocietyEconomy.Transfer(world, firm, government, firm.CashMinor);
        SocietyEconomy.Transfer(world, owner, government, owner.CashMinor - 1000); // the owner can cover only 1,000
        foreach (var good in SocietyFactory.Goods) // nothing left to sell on closure
        {
            government.Inventory[good] += firm.Inventory[good];
            firm.Inventory[good] = 0;
        }

        for (var week = 0; week < SocietyBank.MissedPaymentsToDefault; week++)
            SocietyBank.Week(world, world.Society!.Nations["aster"]);

        Assert.Equal("defunct", firm.Role);
        Assert.Equal("citizen", owner.Role);
        Assert.Equal(0, owner.CashMinor + owner.DepositMinor); // the owner's savings went to the bank first
        Assert.Equal(0, world.Society!.Nations["aster"].DefaultedLoans); // nothing written off
        var debt = Assert.Single(world.Society.Credit, credit => credit.Kind == "owner_debt");
        Assert.Equal((owner.Id, bank.Id, loan.PrincipalMinor - 1000), (debt.BorrowerId, debt.BankId, debt.PrincipalLeftMinor));

        Give(world, owner, 5000); // the owner earns again: the debt is collected weekly
        SocietyBank.Week(world, world.Society.Nations["aster"]);
        Assert.Equal(loan.PrincipalMinor - 1000 - debt.WeeklyPrincipalMinor, debt.PrincipalLeftMinor);
        SocietyEconomy.AssertConservation(world);
    }

    // Private firms bank their cash above two weeks of payroll and draw on it for wages.
    [Fact]
    public void FirmsDepositSpareCashAndDrawOnItForPayroll()
    {
        var world = NewWorld();
        var firm = world.Agents["aster_tools"];
        SocietyEconomy.Transfer(world, world.Agents["aster_bank"], firm, 30000);
        SocietyBank.Sweep(world, world.Society!.Nations["aster"], []);
        Assert.Equal(SocietyBank.FirmBuffer(world, firm), firm.CashMinor);
        Assert.True(firm.DepositMinor > 0);

        SocietyEconomy.Transfer(world, firm, world.Agents["aster_government"], firm.CashMinor);
        SocietyBank.Draw(world, firm, 3000);
        Assert.Equal(3000, firm.CashMinor);
        SocietyEconomy.AssertConservation(world);
    }

    // Deposit insurance pays each depositor up to the limit in total, however many withdrawals they make.
    [Fact]
    public void DepositInsuranceLimitIsPerDepositor()
    {
        var world = NewWorld();
        var resident = Resident(world, "ember"); // a monarchy: deposits insured up to 10,000
        var bank = world.Agents["ember_bank"];
        var limit = Governments.Get("monarchy").DepositInsuranceMinor;
        SocietyEconomy.Transfer(world, bank, resident, 3 * limit);
        SocietyBank.Sweep(world, world.Society!.Nations["ember"], [resident]);
        SocietyEconomy.Transfer(world, bank, world.Agents["ember_journalist"], bank.CashMinor); // the reserves are gone

        Assert.Equal(limit * 3 / 5, SocietyBank.Withdraw(world, resident, limit * 3 / 5));
        Assert.Equal(limit * 2 / 5, SocietyBank.Withdraw(world, resident, limit * 3 / 5));
        Assert.Equal(0, SocietyBank.Withdraw(world, resident, limit));
        Assert.Equal(limit, resident.InsuredMinor);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void StartupLoanLetsAFounderWithOnlyTheDownPaymentOpenABusiness()
    {
        var world = NewWorld();
        var founder = Resident(world, "aster");
        foreach (var venue in SocietyLabor.Firms(world, "aster", "entertainment"))
            venue.PriceMinor = 2 * SocietyLabor.SectorOf("entertainment").BasePrice; // an opening for a cheaper venue
        var cost = SocietyLabor.StartupCost("entertainment");
        SocietyEconomy.Transfer(world, founder, world.Agents["aster_government"], founder.CashMinor);
        Give(world, founder, cost / 2); // half the cost: more than the 25% down payment, less than the whole

        Decide(world, founder.Id, "start_business", candidate => candidate.Params["sector"]!.GetValue<string>() == "entertainment"
            && candidate.Params.ContainsKey("loan_minor"));
        SocietyBank.Week(world, world.Society!.Nations["aster"]);

        var loan = Assert.Single(world.Society.Credit);
        Assert.Equal("active", loan.Status);
        Assert.Equal("entrepreneur", founder.Role);
        Assert.Equal(founder.OwnedBusinessId, loan.BorrowerId); // the firm owes the loan, not the founder
        var started = world.Events.Single(item => item.EventType == "business_started");
        Assert.Contains(loan.RequestEventId, started.CauseIds);
        SocietyEconomy.AssertConservation(world);
    }

    [Theory]
    [InlineData("aster", true)]
    [InlineData("dune", false)]
    public void BankRunIsPaidFromReservesThenByDepositInsurance(string country, bool insured)
    {
        var world = NewWorld();
        var resident = Resident(world, country);
        var bank = world.Agents[country + "_bank"];
        var government = world.Agents[country + "_government"];
        var deposit = Deposit(world, resident, 20000);
        SocietyEconomy.Transfer(world, bank, government, bank.CashMinor - 1000); // the rest is lent out
        var cash = resident.CashMinor;

        Decide(world, resident.Id, "bank_savings", candidate => !candidate.Params["keep_in_bank"]!.GetValue<bool>());

        Assert.False(resident.KeepsSavingsInBank);
        Assert.Equal(0, bank.CashMinor);
        Assert.Equal(deposit - 1000, bank.UnmetWithdrawalsMinor);
        Assert.Equal(cash + (insured ? deposit : 1000), resident.CashMinor);
        Assert.Equal(insured ? 0 : deposit - 1000, resident.DepositMinor); // uninsured savings stay owed
        Assert.Equal(insured ? deposit - 1000 : 0, government.DepositMinor); // the insurer takes over the claim
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void InsolventBankIsTakenOverAndItsOwnerLosesTheStake()
    {
        var world = NewWorld();
        var bank = world.Agents["aster_bank"];
        var owner = world.Agents[bank.OwnerId!];
        Deposit(world, Resident(world, "aster"), 20000);
        SocietyEconomy.Transfer(world, bank, world.Agents["aster_government"], bank.CashMinor - 100); // losses wiped out the capital

        SocietyBank.Week(world, world.Society!.Nations["aster"]);

        Assert.Null(bank.OwnerId);
        Assert.Equal("citizen", owner.Role);
        Assert.Equal(1, world.Society.Nations["aster"].BankFailures);
        Assert.Single(world.Events, item => item.EventType == "bank_failed");
        Assert.True(SocietyLabor.DecidesForItself(bank)); // now run by the state
    }
}
