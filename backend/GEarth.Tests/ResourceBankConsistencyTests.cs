using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Tests;

public sealed class ResourceBankConsistencyTests
{
    private static World Create() => SocietyFactory.Create("resource_bank_consistency", true, 1100);
    private static Agent Resident(World world, string country = "aster") =>
        world.InCountry(country).First(person => person.Role == "citizen");
    private static void Week(World world) => SocietyLabor.Week(world, world.Society!.Nations["aster"],
        world.Agents["aster_government"], world.InCountry("aster").Where(SocietyLabor.IsResident).ToArray());
    private static void EmptyStock(World world, Agent firm)
    {
        foreach (var good in SocietyFactory.Goods)
        {
            world.Agents[firm.CountryId + "_government"].Inventory[good] += firm.Inventory[good];
            firm.Inventory[good] = 0;
        }
    }
    private static void DrainCash(World world, Agent account) =>
        SocietyEconomy.Transfer(world, account, world.Agents[account.CountryId + "_government"], account.CashMinor);
    private static void Finish(World world, Cargo cargo, bool fail)
    {
        if (fail) world.Tick = cargo.DeadlineTick;
        for (var day = 0; day < 61 && cargo.Status == "in_transit"; day++)
        {
            world.Tick++;
            SocietyTrade.Advance(world);
            SocietyEconomy.AssertConservation(world);
        }
        Assert.Equal(fail ? "failed" : "delivered", cargo.Status);
    }

    [Fact]
    public void SolventIlliquidBankDoesNotInsureRoutineBuffersOrDomesticPayments()
    {
        var world = Create();
        var borrower = Resident(world);
        var bank = SocietyBank.BankOf(world, "aster");
        SocietyBank.StudentLoan(world, borrower, 500_000);
        DrainCash(world, borrower);
        SocietyEconomy.Transfer(world, borrower, world.Agents["beryl_government"], bank.CashMinor);
        var person = world.InCountry("aster").First(agent => agent.Role == "citizen" && agent != borrower);
        SocietyBank.Deposit(world, person, person.CashMinor);
        SocietyEconomy.Transfer(world, borrower, world.Agents["beryl_government"], bank.CashMinor);
        Assert.Equal(0, bank.CashMinor);
        Assert.True(SocietyBank.Equity(world, bank) > 0);
        var savings = SocietyBank.Savings(person);
        SocietyBank.Sweep(world, world.Society!.Nations["aster"], [person]);
        Assert.Equal(1, SocietyEconomy.Acquire(world, person, "food", 1, true));
        Assert.True(SocietyBank.Savings(person) < savings);
        Assert.Equal(0, world.Society.Nations["aster"].DepositInsurancePaidMinor);
        Assert.Equal(0, bank.UnmetWithdrawalsMinor);
        DrainCash(world, bank);
        SocietyBank.SetKeepsSavings(world, person, false, "test");
        Assert.True(bank.UnmetWithdrawalsMinor > 0);
        Assert.True(world.Society.Nations["aster"].DepositInsurancePaidMinor > 0);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void PayrollSpendsDepositsWithoutRequestingInsurance()
    {
        var world = Create();
        var firm = world.Agents["aster_tools"];
        SocietyBank.Deposit(world, firm, firm.CashMinor);
        var bank = SocietyBank.BankOf(world, "aster");
        DrainCash(world, bank);
        var before = firm.DepositMinor;
        Week(world);
        Assert.True(firm.DepositMinor < before);
        Assert.Equal(0, bank.UnmetWithdrawalsMinor);
        Assert.Equal(0, world.Society!.Nations["aster"].DepositInsurancePaidMinor);
        Assert.True(world.Society.Nations["aster"].WagesPaidMinor > 0);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void AutomaticTopUpsAreProportionalAndLeaveRequiredReserves()
    {
        var world = Create();
        foreach (var firm in world.InCountry("aster").Where(SocietyBank.Banks)) DrainCash(world, firm);
        var people = world.InCountry("aster").Where(agent => agent.Role == "citizen").Take(2).ToArray();
        foreach (var person in people)
        {
            person.SalaryMinor = 500;
            person.Occupation = "farmer";
            person.EmployerId = "aster_tools";
            SocietyBank.Deposit(world, person, person.CashMinor);
        }
        var bank = SocietyBank.BankOf(world, "aster");
        var required = SocietyBank.RequiredReserves(world, bank);
        SocietyEconomy.Transfer(world, bank, world.Agents["aster_government"], bank.CashMinor - required - 500);
        SocietyBank.Sweep(world, world.Society!.Nations["aster"], people.Reverse().ToArray());
        Assert.Equal(250, people[0].CashMinor);
        Assert.Equal(250, people[1].CashMinor);
        Assert.True(bank.CashMinor >= SocietyBank.RequiredReserves(world, bank));
        Assert.Equal(0, bank.UnmetWithdrawalsMinor);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void DepositFundedGovernmentInvestmentAndStartupGrantActuallyExecute()
    {
        var world = Create();
        var nation = world.Society!.Nations["cinder"];
        var government = world.Agents["cinder_government"];
        SocietyBank.Deposit(world, government, government.CashMinor);
        var offered = Assert.Single(SocietyActions.Available(world, government), item => item.ActionType == "invest");
        var productivity = nation.Productivity;
        SocietyDiplomacy.Propose(world, government, new("invest", (JsonObject)offered.Params.DeepClone(), "test", []), "cause");
        world.Tick += Governments.Get(nation.GovernmentId).PolicyDelay;
        SocietyDiplomacy.ProcessPolicies(world);
        Assert.Equal(productivity + 0.05, nation.Productivity);
        Assert.DoesNotContain(world.Events, item => item.EventType == "policy_blocked");
        Assert.NotNull(SocietyLabor.Grant(world, nation, "food", "test"));
        Assert.Equal(0, government.CashMinor);
        SocietyEconomy.AssertConservation(world);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForeignEscrowNeedsPrincipalCashButDomesticFeesCanUseDeposits(bool enoughReserves)
    {
        var world = Create();
        var buyer = world.Agents["aster_tools"];
        var seller = world.Agents["beryl_mine"];
        var bank = SocietyBank.BankOf(world, "aster");
        SocietyBank.Deposit(world, buyer, buyer.CashMinor);
        var value = 5L * seller.PriceMinor;
        SocietyEconomy.Transfer(world, bank, world.Agents["aster_government"], bank.CashMinor - value + (enoughReserves ? 0 : 1));
        var stocks = seller.Inventory["ore"];
        var deposit = buyer.DepositMinor;
        Assert.Equal(enoughReserves, SocietyTrade.CanImport(world, buyer, seller, "ore", 5, "cheapest"));
        if (enoughReserves)
        {
            SocietyTrade.Dispatch(world, buyer, seller, "ore", 5, "cheapest", "test");
            Assert.Equal(value, Assert.Single(world.Society!.Cargo).EscrowMinor);
            Assert.Equal(0, buyer.CashMinor);
            Assert.True(buyer.DepositMinor < deposit - value);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => SocietyTrade.Dispatch(world, buyer, seller, "ore", 5, "cheapest", "test"));
            Assert.Empty(world.Society!.Cargo);
            Assert.Equal(stocks, seller.Inventory["ore"]);
            Assert.Equal(deposit, buyer.DepositMinor);
        }
        Assert.Equal(0, bank.UnmetWithdrawalsMinor);
        Assert.Equal(0, world.Society!.Nations["aster"].DepositInsurancePaidMinor);
        SocietyEconomy.AssertConservation(world);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public void ClosedBuyerAndSellerKeepCargoClaimsAcrossResume(bool closeBuyer, bool fail, bool resume)
    {
        var world = Create();
        var buyer = world.Agents["aster_tools"];
        var seller = world.Agents["beryl_mine"];
        SocietyTrade.Dispatch(world, buyer, seller, "ore", 5, "cheapest", "test");
        var estate = closeBuyer ? buyer : seller;
        SocietyLabor.Close(world, world.Society!.Nations[estate.CountryId], estate, "test");
        Assert.False(estate.EstateFinalized);
        var beneficiary = world.Agents[estate.EstateBeneficiaryId!];
        var before = SocietyBank.Savings(beneficiary);
        var ore = buyer.Inventory["ore"];
        var value = world.Society.Cargo.Single().EscrowMinor;
        if (resume)
        {
            world = Wire.Copy(world);
            world.Reindex();
            estate = world.Agents[estate.Id];
            buyer = world.Agents[buyer.Id];
            beneficiary = world.Agents[beneficiary.Id];
        }
        Finish(world, world.Society!.Cargo.Single(), fail);
        Assert.Equal(ore + (fail ? 0 : 5), buyer.Inventory["ore"]);
        Assert.Equal(before + (closeBuyer == fail ? value : 0), SocietyBank.Savings(beneficiary));
        if (closeBuyer && !fail)
            Assert.Contains(SocietyEconomy.Offers(world, world.Society.Nations["aster"], "ore"), offer => offer.Seller == estate);
        Assert.DoesNotContain(estate, SocietyLabor.Firms(world, estate.CountryId, estate.Specialty!));
        var closed = world.Society.Nations[estate.CountryId].BusinessesClosed;
        SocietyLabor.Close(world, world.Society.Nations[estate.CountryId], estate, "again");
        Assert.Equal(closed, world.Society.Nations[estate.CountryId].BusinessesClosed);
        SocietyEconomy.AssertConservation(world);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void LateEstateProceedsPayExistingOwnerDebtExactlyOnce(bool ownerAlreadyPaid, bool resume)
    {
        var world = Create();
        var firm = world.Agents["aster_tools"];
        var owner = world.Agents[firm.OwnerId!];
        var loan = SocietyBank.StudentLoan(world, firm, 1000);
        loan.Kind = "business";
        SocietyTrade.Dispatch(world, world.Agents["beryl_tools"], firm, "tools", 5, "cheapest", "test");
        var value = world.Society!.Cargo.Single().EscrowMinor;
        SocietyEconomy.Transfer(world, firm, world.Agents["aster_government"], SocietyBank.Liquid(world, firm));
        SocietyEconomy.Transfer(world, owner, world.Agents["aster_government"], SocietyBank.Liquid(world, owner));
        SocietyLabor.Close(world, world.Society.Nations["aster"], firm, "test");
        var debt = Assert.Single(world.Society.Credit, credit => credit.Kind == "owner_debt");
        Assert.Equal(debt.Id, Assert.Single(firm.EstateCreditIds));
        if (ownerAlreadyPaid)
        {
            SocietyEconomy.Transfer(world, world.Agents["aster_government"], owner, 1000);
            Assert.Equal(1000, SocietyBank.RepayPrincipal(world, owner, debt));
        }
        if (resume)
        {
            world = Wire.Copy(world);
            world.Reindex();
            firm = world.Agents[firm.Id];
            owner = world.Agents[owner.Id];
            debt = world.Society!.Credit.Single(credit => credit.Id == debt.Id);
        }
        Finish(world, world.Society!.Cargo.Single(), false);
        var retired = ownerAlreadyPaid ? 1000 : value;
        Assert.Equal(retired, world.Society.CreditRetiredMinor);
        Assert.Equal(ownerAlreadyPaid ? 0 : 1000 - value, debt.PrincipalLeftMinor);
        Assert.Equal(ownerAlreadyPaid ? value : 0, SocietyBank.Savings(owner));
        SocietyEstates.Settle(world, firm);
        Assert.Equal(retired, world.Society.CreditRetiredMinor);
        Assert.Equal(ownerAlreadyPaid ? value : 0, SocietyBank.Savings(owner));
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void EstateRawStockIsDiscountedAndAccessibleWithoutReopeningProduction()
    {
        var world = Create();
        var mine = world.Agents["aster_mine"];
        var units = mine.Inventory["ore"];
        Assert.True(units > 0);
        var owner = world.Agents[mine.OwnerId!];
        var ownedOre = owner.Inventory["ore"];
        SocietyLabor.Close(world, world.Society!.Nations["aster"], mine, "test");
        Assert.Equal(ownedOre, owner.Inventory["ore"]);
        var offer = SocietyEconomy.Offers(world, world.Society.Nations["aster"], "ore").First();
        Assert.Equal(mine, offer.Seller);
        Assert.Equal(SocietyEstates.Price("ore"), offer.PriceMinor);
        var tools = world.Agents["aster_tools"];
        Assert.Equal(offer.PriceMinor, SocietyResources.InputUnitCost(world, tools, "ore", 1));
        var savings = SocietyBank.Savings(owner);
        var bought = SocietyEconomy.Acquire(world, tools, "ore", 1, false);
        Assert.Equal(1, bought);
        Assert.Equal(units - 1, mine.Inventory["ore"]);
        Assert.Equal(savings + offer.PriceMinor, SocietyBank.Savings(owner));
        Assert.Equal(0, SocietyEconomy.Acquire(world, Resident(world), "ore", 1, true));
        Assert.DoesNotContain(SocietyLabor.Firms(world, "aster", "mine"), firm => firm == mine);
        Assert.Single(SocietyActions.Available(world, mine)); // hold only; never scheduled for decisions
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void ClosedFirmPaysDomesticDepositsEvenWhenReservesAreUnavailable()
    {
        var world = Create();
        var firm = world.Agents["aster_tools"];
        var owner = world.Agents[firm.OwnerId!];
        SocietyBank.Deposit(world, firm, firm.CashMinor);
        var amount = firm.DepositMinor;
        var savings = SocietyBank.Savings(owner);
        var bank = SocietyBank.BankOf(world, "aster");
        DrainCash(world, bank);
        SocietyLabor.Close(world, world.Society!.Nations["aster"], firm, "test");
        Assert.Equal(0, firm.DepositMinor);
        Assert.Equal(savings + amount, SocietyBank.Savings(owner));
        Assert.Equal(0, bank.UnmetWithdrawalsMinor);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void MigratedBeneficiaryLeavesUnsettledEstateClaimsInTheirOriginalBank()
    {
        var world = Create();
        var firm = world.Agents["aster_tools"];
        SocietyLabor.Close(world, world.Society!.Nations["aster"], firm, "test");
        var owner = world.Agents[firm.EstateBeneficiaryId!];
        var now = DateTimeOffset.UtcNow;
        var job = SocietyEngine.Observe(world, owner.Id, now);
        var candidate = job.Candidates.First(item => item.ActionType == "migrate");
        Assert.True(SocietyEngine.Apply(world, job, new(job.RequestId,
            new("migrate", candidate.Params, "test", []), "rule", "test", 0), now).Accepted);
        SocietyEconomy.Transfer(world, world.Agents["aster_government"], firm, 1000);
        SocietyBank.Deposit(world, firm, 1000);
        DrainCash(world, SocietyBank.BankOf(world, "aster"));
        var savings = SocietyBank.Savings(owner);
        SocietyEstates.Settle(world, firm);
        Assert.Equal(1000, firm.DepositMinor);
        Assert.Equal("aster", firm.CountryId);
        Assert.Equal(savings, SocietyBank.Savings(owner));
        SocietyEconomy.Transfer(world, world.Agents["aster_government"], SocietyBank.BankOf(world, "aster"), 1000);
        SocietyEstates.Settle(world, firm);
        Assert.Equal(0, firm.DepositMinor);
        Assert.Equal(savings + 1000, SocietyBank.Savings(owner));
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void MigrationRevalidatesFullSavingsSettlementWithoutErasingClaims()
    {
        var world = Create();
        var person = Resident(world);
        SocietyBank.Deposit(world, person, person.CashMinor);
        var now = DateTimeOffset.UtcNow;
        var job = SocietyEngine.Observe(world, person.Id, now);
        var candidate = job.Candidates.First(item => item.ActionType == "migrate");
        var deposit = person.DepositMinor;
        DrainCash(world, SocietyBank.BankOf(world, "aster"));
        Assert.DoesNotContain(SocietyActions.Available(world, person), item => item.ActionType == "migrate");
        var record = SocietyEngine.Apply(world, job, new(job.RequestId,
            new("migrate", candidate.Params, "test", []), "rule", "test", 0), now);
        Assert.False(record.Accepted);
        Assert.Equal("aster", person.CountryId);
        Assert.Equal(deposit, person.DepositMinor);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void InsolventStateContinuationStopsInterestAndLoansButHonorsDomesticClaims()
    {
        var world = Create();
        var person = Resident(world);
        SocietyBank.Deposit(world, person, person.CashMinor);
        var bank = SocietyBank.BankOf(world, "aster");
        DrainCash(world, bank);
        var deposit = person.DepositMinor;
        var broad = SocietyBank.BroadMoney(world);
        SocietyBank.Week(world, world.Society!.Nations["aster"]);
        Assert.Null(bank.OwnerId);
        Assert.True(bank.DepositInterestSuspended);
        Assert.Equal(deposit, person.DepositMinor);
        Assert.Equal(broad, SocietyBank.BroadMoney(world));
        Assert.False(SocietyBank.CanLend(world, bank, 1));
        var view = Wire.Node(SocietyBank.View(world, bank, true));
        Assert.True(view["nominal_domestic_claims_guaranteed"]!.GetValue<bool>());
        Assert.True(view["capital_shortfall_minor"]!.GetValue<long>() > 0);
        SocietyEconomy.Transfer(world, person, world.Agents["aster_food"], 100, "purchase");
        Assert.Equal(deposit - 100, person.DepositMinor);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void InventoryPurchaseTimingWithinFourWeeksDoesNotChangeWageEligibility()
    {
        static (long[] Salaries, long[] History) Run(int purchaseWeek)
        {
            var world = Create();
            var firm = world.Agents["aster_tools"];
            foreach (var farm in SocietyLabor.Firms(world, "aster", "food")) farm.PriceMinor = 80;
            // Use a well-funded working firm and the same actual cash receipts, payroll and purchases.
            SocietyEconomy.Transfer(world, world.Agents["aster_government"], firm, 50_000);
            for (var week = 1; week <= 4; week++)
            {
                world.Tick = week * 7;
                foreach (var farm in SocietyLabor.Firms(world, "aster", "food")) farm.PriceMinor = 80;
                foreach (var mine in SocietyLabor.Firms(world, "aster", "mine")) mine.PriceMinor = 15;
                SocietyEconomy.Sell(world, world.Agents["aster_government"], firm, 20_000, kind: "public_investment");
                if (week == purchaseWeek)
                    SocietyEconomy.Acquire(world, firm, "ore", 300, false);
                Week(world);
                SocietyEconomy.AssertConservation(world);
            }
            Assert.Contains(world.Events, item => item.EventType == "wage_indexed" && item.EntityIds.Contains(firm.Id));
            var indexed = world.Events.Last(item => item.EventType == "wage_indexed" && item.EntityIds.Contains(firm.Id));
            Assert.Equal(35, indexed.Payload["effective_from_tick"]!.GetValue<int>());
            Assert.Equal(4, indexed.Payload["observed_weeks"]!.GetValue<int>());
            return (SocietyLabor.Staff(world, firm).OrderBy(worker => worker.Id).Select(worker => worker.SalaryMinor).ToArray(),
                firm.OperatingSurplusWeeksMinor.ToArray());
        }
        var earlier = Run(2);
        var later = Run(4);
        Assert.Equal(earlier.Salaries, later.Salaries);
        Assert.Equal(earlier.History.Sum(), later.History.Sum());
    }

    [Fact]
    public void IndexedPayrollIsRetainedAfterOwnerDistributions()
    {
        var world = Create();
        var firm = world.Agents["aster_tools"];
        foreach (var farm in SocietyLabor.Firms(world, "aster", "food")) farm.PriceMinor = 80;
        SocietyEconomy.Sell(world, world.Agents["aster_government"], firm, 100_000, kind: "public_investment");
        world.Tick = SocietyLabor.IndexationDays;
        Week(world);
        Assert.Contains(world.Events, item => item.EventType == "wage_indexed" && item.EntityIds.Contains(firm.Id));
        Assert.True(SocietyBank.Liquid(world, firm) >= SocietyLabor.Staff(world, firm).Sum(worker => worker.SalaryMinor));
        Assert.Single(firm.OperatingSurplusWeeksMinor); // a new observed week is not padded with zeroes
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void LastEssentialClosureStartsRecoveryClockImmediatelyAndReopensAtDeadline()
    {
        var world = Create();
        var nation = world.Society!.Nations["aster"];
        var pharma = world.Agents["aster_medicine"];
        var owner = world.Agents[pharma.OwnerId!];
        world.Tick = 7;
        SocietyLabor.Close(world, nation, pharma, "test");
        Assert.Equal(7, nation.NeededSince["medicine"]);
        // Isolate grant recovery from the richest-founder entry rule.
        foreach (var person in world.InCountry("aster").Where(agent => agent.Role == "citizen"))
            SocietyEconomy.Transfer(world, person, world.Agents["aster_government"], SocietyBank.Liquid(world, person));
        world.Tick = 7 + SocietyLabor.GrantAfterDays - 7;
        Week(world);
        Assert.Empty(SocietyLabor.Firms(world, "aster", "medicine"));
        world = Wire.Copy(world);
        world.Reindex();
        world.Tick += 7;
        Week(world);
        Assert.NotEmpty(SocietyLabor.Firms(world, "aster", "medicine"));
        Assert.False(world.Society!.Nations["aster"].NeededSince.ContainsKey("medicine"));
        Assert.Equal("entrepreneur", world.Agents[owner.Id].Role);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void UnresolvedEstateAssetsRemainInWealthWithoutSubtractingOwnerDebtTwice()
    {
        var world = Create();
        var firm = world.Agents["aster_tools"];
        var owner = world.Agents[firm.OwnerId!];
        var loan = SocietyBank.StudentLoan(world, firm, 1000);
        loan.Kind = "business";
        SocietyEconomy.Transfer(world, firm, world.Agents["aster_government"], SocietyBank.Liquid(world, firm));
        SocietyEconomy.Transfer(world, owner, world.Agents["aster_government"], SocietyBank.Liquid(world, owner));
        SocietyLabor.Close(world, world.Society!.Nations["aster"], firm, "test");
        var liquidation = firm.Inventory.Sum(item => (long)item.Value * SocietyEstates.Price(item.Key));
        Assert.Equal(liquidation - 1000, SocietyMetrics.NetWealth(world, owner));
        SocietyEconomy.Transfer(world, world.Agents["aster_government"], firm, 500);
        SocietyBank.Deposit(world, firm, 500);
        SocietyEstates.Settle(world, firm);
        Assert.Equal(liquidation - 500, SocietyMetrics.NetWealth(world, owner));
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void TreasuryHeldGuaranteedPrincipalDoesNotRetireBankCreditAgain()
    {
        var world = Create();
        var person = Resident(world);
        var loan = SocietyBank.StudentLoan(world, person, 1000);
        SocietyEconomy.Transfer(world, person, world.Agents["aster_government"], SocietyBank.Liquid(world, person));
        for (var week = 0; week < SocietyBank.MissedPaymentsToDefault; week++)
            SocietyBank.Week(world, world.Society!.Nations["aster"]);
        Assert.Equal("aster_government", loan.BankId);
        Assert.Equal(1000, world.Society!.CreditRetiredMinor);
        var estate = world.Agents["aster_tools"];
        SocietyLabor.Close(world, world.Society.Nations["aster"], estate, "test");
        estate.EstateCreditIds.Add(loan.Id);
        SocietyEconomy.Transfer(world, world.Agents["aster_government"], estate, 1000);
        SocietyEstates.Settle(world, estate);
        Assert.Equal("repaid", loan.Status);
        Assert.Equal(1000, world.Society.CreditRetiredMinor);
        SocietyEconomy.AssertConservation(world);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterestSuspensionUsesEquityAfterActualLoanCollections(bool repaymentAvailable)
    {
        var world = Create();
        var person = Resident(world);
        var bank = SocietyBank.BankOf(world, "aster");
        SocietyBank.StudentLoan(world, person, 100_000);
        // Bring equity to exactly zero without erasing any depositor's claim.
        SocietyEconomy.Transfer(world, bank, world.Agents["aster_government"], SocietyBank.Equity(world, bank));
        Assert.Equal(0, SocietyBank.Equity(world, bank));
        if (!repaymentAvailable)
            SocietyEconomy.Transfer(world, person, world.Agents["aster_government"], SocietyBank.Liquid(world, person));
        SocietyBank.Week(world, world.Society!.Nations["aster"]);
        Assert.Equal(!repaymentAvailable, bank.DepositInterestSuspended);
        Assert.Equal(repaymentAvailable, world.Events.Any(item => item.EventType == "bank_ledger"
            && item.Payload["kind"]!.GetValue<string>() == "deposit_interest"));
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void TerminalEstatePaymentsKeepShipmentAuditCauseAndCreditEvidence()
    {
        var world = Create();
        RunAudit.Enable(world);
        var firm = world.Agents["aster_tools"];
        SocietyTrade.Dispatch(world, world.Agents["beryl_tools"], firm, "tools", 5, "cheapest", "shipment_cause");
        SocietyLabor.Close(world, world.Society!.Nations["aster"], firm, "test");
        world.PendingAudit.Clear();
        var cargo = world.Society.Cargo.Single();
        Finish(world, cargo, false);
        var payment = Assert.Single(world.PendingAudit, row => row.Kind == "transaction"
            && row.Payload["kind"]!.GetValue<string>() == "closure_payout");
        Assert.Equal(cargo.Id, payment.Payload["transaction_group_id"]!.GetValue<string>());
        Assert.Equal("shipment_cause", payment.Payload["event_id"]!.GetValue<string>());
    }

    [Fact]
    public void StateWorkingCapitalFundsInputsAfterPayrollWithoutCreatingResources()
    {
        var world = Create();
        var nation = world.Society!.Nations["cinder"];
        var firm = world.Agents["cinder_energy"];
        EmptyStock(world, firm);
        var payroll = SocietyLabor.Staff(world, firm).Sum(worker => worker.SalaryMinor);
        var inputs = SocietyResources.InputWorkingCapital(world, nation, firm);
        Assert.True(inputs > 0);
        DrainCash(world, firm);
        var beforeGoods = world.Agents.Values.Sum(agent => (long)agent.Inventory["fuel"]);
        SocietyEconomy.Week(world);
        Assert.True(SocietyBank.Liquid(world, firm) >= inputs);
        Assert.Equal(beforeGoods, world.Agents.Values.Sum(agent => (long)agent.Inventory["fuel"]));
        Assert.True(nation.WagesPaidMinor >= payroll);
        SocietyEconomy.AssertConservation(world);
    }
}
