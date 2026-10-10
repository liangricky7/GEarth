using Faultline.Core;

namespace Faultline.Tests;

public sealed class FinanceTests
{
    // 170 agents = 10 countries x (10 citizens + 7 institutions); small and fast.
    private static World NewWorld() => SocietyFactory.Create("finance_test", false, 170);

    [Fact]
    public void ApprovedLoanMovesPrincipalAndIsRepaidWithInterest()
    {
        var world = NewWorld();
        var (borrower, bank, loan) = FundLoan(world);
        var bankAfterFunding = bank.CashMinor;
        Assert.Equal("active", loan.Status);

        // Ensure the borrower can pay regardless of how the economy treats it.
        SocietyEconomy.Transfer(world.Agents["aster_government"], borrower, loan.DueMinor);
        AdvanceUntilSettled(world, loan);

        Assert.Equal("repaid", loan.Status);
        Assert.Equal(bankAfterFunding + loan.DueMinor, bank.CashMinor);
        Assert.Equal(loan.PrincipalMinor * 105 / 100, loan.DueMinor);
    }

    [Fact]
    public void UnpayableLoanDefaultsAndRecordsTheLoss()
    {
        var world = NewWorld();
        var (_, bank, loan) = FundLoan(world);
        var bankAfterFunding = bank.CashMinor;
        loan.DueMinor = world.InitialCashMinor + 1; // more than exists in the world

        AdvanceUntilSettled(world, loan);

        Assert.Equal("defaulted", loan.Status);
        Assert.Equal(1, world.Society!.Nations["aster"].DefaultedLoans);
        var settled = world.Events.Single(item => item.EventType == "loan_settled");
        var paid = settled.Payload["paid_minor"]!.GetValue<long>();
        Assert.Equal(loan.DueMinor - paid, settled.Payload["loss_minor"]!.GetValue<long>());
        Assert.Equal(bankAfterFunding + paid, bank.CashMinor);
    }

    [Fact]
    public void SameApprovalAppliedTwiceFundsOnce()
    {
        var world = NewWorld();
        var borrower = DrainForLoan(world);
        Decide(world, borrower.Id, "request_loan");
        var bank = world.Agents["aster_bank"];
        var bankBefore = bank.CashMinor;
        var (job, result) = Propose(world, bank.Id, "approve_loan");

        var first = SocietyEngine.Apply(world, job, result, DateTimeOffset.UtcNow);
        var second = SocietyEngine.Apply(world, job, result, DateTimeOffset.UtcNow);

        Assert.True(first.Accepted, first.Code);
        Assert.Same(first, second);
        Assert.Equal(bankBefore - world.Society!.Credit.Single().PrincipalMinor, bank.CashMinor);
        Assert.Single(world.Events, item => item.EventType == "loan_funded");
    }

    [Fact]
    public void ApprovalIsRejectedWhenBankCashWasSpentAfterObservation()
    {
        var world = NewWorld();
        var borrower = DrainForLoan(world);
        Decide(world, borrower.Id, "request_loan");
        var bank = world.Agents["aster_bank"];
        var (job, result) = Propose(world, bank.Id, "approve_loan");
        var borrowerBefore = borrower.CashMinor;

        SocietyEconomy.Transfer(bank, world.Agents["aster_government"], bank.CashMinor - 1);
        var record = SocietyEngine.Apply(world, job, result, DateTimeOffset.UtcNow);

        Assert.False(record.Accepted);
        Assert.Equal("CURRENT_PRECONDITION_FAILED", record.Code);
        Assert.Equal("requested", world.Society!.Credit.Single().Status);
        Assert.Equal(borrowerBefore, borrower.CashMinor);
    }

    [Fact]
    public void DeliveredImportReleasesEscrowToSellerOnce()
    {
        var world = NewWorld();
        var (buyer, seller, cargo) = Import(world);
        var sellerBefore = seller.CashMinor;
        var buyerStock = buyer.Inventory[cargo.Good];
        var value = cargo.EscrowMinor;

        for (var step = 0; step < 200 && cargo.Status == "in_transit"; step++)
            TradeTick(world);

        Assert.Equal("delivered", cargo.Status);
        Assert.Equal(0, cargo.EscrowMinor);
        Assert.Equal(sellerBefore + value, seller.CashMinor);
        Assert.Equal(buyerStock + cargo.Quantity, buyer.Inventory[cargo.Good]);
        Assert.Single(world.Events, item => item.EventType == "shipment_delivered" && item.EntityIds.Contains(cargo.Id));
    }

    [Fact]
    public void MissedDeadlineRefundsEscrowAndCountsGoodsLost()
    {
        var world = NewWorld();
        var (buyer, seller, cargo) = Import(world);
        var buyerBefore = buyer.CashMinor;
        var sellerBefore = seller.CashMinor;
        var value = cargo.EscrowMinor;
        cargo.DeadlineTick = world.Tick + 1;

        TradeTick(world);

        Assert.Equal("failed", cargo.Status);
        Assert.Equal(buyerBefore + value, buyer.CashMinor);
        Assert.Equal(sellerBefore, seller.CashMinor);
        Assert.Equal(cargo.Quantity, world.Society!.LostGoods[cargo.Good]);
    }

    [Fact]
    public void ClosedRouteReroutesOrHoldsCargoWithoutLosingMoney()
    {
        var world = NewWorld();
        var (_, _, cargo) = Import(world);
        var closed = world.Routes[cargo.Plan!.EdgeIds[0]];
        var origin = cargo.LocationId;
        closed.Open = false;
        var alternative = SocietyTrade.Path(world, cargo.LocationId, cargo.DestinationId, "cautious");

        TradeTick(world); // conservation is asserted inside

        if (alternative is null)
        {
            Assert.Equal(origin, cargo.LocationId);
            Assert.Equal("in_transit", cargo.Status);
        }
        else
        {
            Assert.DoesNotContain(closed.Id, cargo.Plan!.EdgeIds);
            Assert.Contains(world.Events, item => item.EventType == "shipment_rerouted" && item.EntityIds.Contains(cargo.Id));
        }
    }

    [Fact]
    public void CitizenPaymentMovesCashAndTheRecipientRemembersIt()
    {
        var world = NewWorld();
        var payer = world.Agents["aster_citizen_00001"];
        var (job, result) = Propose(world, payer.Id, "pay", ToResident(world));
        var recipient = world.Agents[result.Action.Params["recipient_id"]!.GetValue<string>()];
        var (payerBefore, recipientBefore) = (payer.CashMinor, recipient.CashMinor);

        var record = SocietyEngine.Apply(world, job, result, DateTimeOffset.UtcNow);

        Assert.True(record.Accepted, record.Code);
        Assert.Equal(payerBefore - 100, payer.CashMinor);
        Assert.Equal(recipientBefore + 100, recipient.CashMinor);
        Assert.Contains(recipient.Memories, memory => memory.Contains($"received 100 minor from {payer.Id}"));
        Assert.Single(world.Events, item => item.EventType == "payment_sent");
    }

    [Fact]
    public void PaymentChecksAffordabilityNotWhetherRecipientIsStillNeediest()
    {
        var world = NewWorld();
        var payer = world.Agents["aster_citizen_00001"];
        var (job, result) = Propose(world, payer.Id, "pay", ToResident(world));
        world.Agents["aster_citizen_00007"].Wellbeing = 0; // a different resident is now the neediest

        Assert.True(SocietyEngine.Apply(world, job, result, DateTimeOffset.UtcNow).Accepted);

        var (second, secondResult) = Propose(world, payer.Id, "pay", ToResident(world));
        SocietyEconomy.Transfer(payer, world.Agents["aster_government"], payer.CashMinor - 50);
        var rejected = SocietyEngine.Apply(world, second, secondResult, DateTimeOffset.UtcNow);

        Assert.Equal("CURRENT_PRECONDITION_FAILED", rejected.Code);
        Assert.Equal(50, payer.CashMinor);
    }

    [Fact]
    public void HouseholdPurchaseMovesGoodsAtTheQuotedPrice()
    {
        var world = NewWorld();
        var buyer = world.Agents["aster_citizen_00001"];
        var seller = world.Agents["aster_food"];
        var government = world.Agents["aster_government"];
        var (cash, stock, sellerCash, sellerStock, treasury) = (buyer.CashMinor, buyer.Inventory["food"], seller.CashMinor, seller.Inventory["food"], government.CashMinor);
        var total = SocietyPayments.BuyQuantity * seller.PriceMinor;
        var salesTax = total * world.Society!.Nations["aster"].SalesTaxBasisPoints / 10000;

        Decide(world, buyer.Id, "buy_goods", Good("food"));

        Assert.Equal(cash - total, buyer.CashMinor);
        Assert.Equal(sellerCash + total - salesTax, seller.CashMinor);
        Assert.Equal(treasury + salesTax, government.CashMinor);
        Assert.Equal(stock + SocietyPayments.BuyQuantity, buyer.Inventory["food"]);
        Assert.Equal(sellerStock - SocietyPayments.BuyQuantity, seller.Inventory["food"]);
    }

    [Fact]
    public void PurchaseIsRejectedWhenSellerRepricedAfterObservation()
    {
        var world = NewWorld();
        var buyer = world.Agents["aster_citizen_00001"];
        var (job, result) = Propose(world, buyer.Id, "buy_goods", Good("food"));
        var cash = buyer.CashMinor;
        world.Agents["aster_food"].PriceMinor += 10;

        var record = SocietyEngine.Apply(world, job, result, DateTimeOffset.UtcNow);

        Assert.Equal("CURRENT_PRECONDITION_FAILED", record.Code);
        Assert.Equal(cash, buyer.CashMinor);
    }

    [Fact]
    public void GovernmentPaymentWaitsForItsPolicyDelay()
    {
        var world = NewWorld();
        var government = world.Agents["aster_government"];
        var delay = Governments.Get(world.Society!.Nations["aster"].GovernmentId).PolicyDelay;
        Assert.True(delay > 0); // aster starts as a market democracy

        Decide(world, government.Id, "pay", ToResident(world));
        Assert.DoesNotContain(world.Events, item => item.EventType == "payment_sent");

        for (var tick = 0; tick < delay; tick++)
            SocietyEngine.Advance(world);

        var paid = world.Events.Single(item => item.EventType == "payment_sent");
        Assert.Equal(government.Id, paid.Payload["from"]!.GetValue<string>());
        Assert.Equal(1000, paid.Payload["amount_minor"]!.GetValue<long>());
        Assert.Contains(world.Events, item => item.EventType == "policy_enacted");
    }

    private static Func<Candidate, bool> ToResident(World world) =>
        candidate => SocietyLabor.IsResident(world.Agents[candidate.Params["recipient_id"]!.GetValue<string>()]);

    private static Func<Candidate, bool> Good(string good) =>
        candidate => candidate.Params["good"]!.GetValue<string>() == good;

    // Moves cash out (not deleted, so conservation still holds) until the business qualifies for a loan.
    private static Agent DrainForLoan(World world)
    {
        var borrower = world.Agents["aster_food"];
        SocietyEconomy.Transfer(borrower, world.Agents["aster_government"], borrower.CashMinor - 1000);
        return borrower;
    }

    private static (Agent Borrower, Agent Bank, Credit Loan) FundLoan(World world)
    {
        var borrower = DrainForLoan(world);
        Decide(world, borrower.Id, "request_loan");
        var bank = world.Agents["aster_bank"];
        var bankBefore = bank.CashMinor;
        var borrowerBefore = borrower.CashMinor;
        Decide(world, bank.Id, "approve_loan");
        var loan = world.Society!.Credit.Single();
        Assert.Equal(bankBefore - loan.PrincipalMinor, bank.CashMinor);
        Assert.Equal(borrowerBefore + loan.PrincipalMinor, borrower.CashMinor);
        return (borrower, bank, loan);
    }

    private static (Agent Buyer, Agent Seller, Cargo Cargo) Import(World world)
    {
        var buyer = world.Agents["aster_food"];
        Decide(world, buyer.Id, "import_goods");
        var cargo = world.Society!.Cargo.Single();
        return (buyer, world.Agents[cargo.SellerId], cargo);
    }

    private static void AdvanceUntilSettled(World world, Credit loan)
    {
        while (loan.Status == "active" && world.Tick <= loan.DueTick + SocietyLabor.DaysPerWeek)
            SocietyEngine.Advance(world);
    }

    // Moves cargo only, so no wages or purchases blur the cash assertions.
    private static void TradeTick(World world)
    {
        world.Tick++;
        SocietyTrade.Advance(world);
        SocietyEconomy.AssertConservation(world);
    }

    // Same path a model decision takes: observe, pick a legal candidate, apply through the validator.
    private static (DecisionJob Job, DecisionResult Result) Propose(World world, string agentId, string action,
        Func<Candidate, bool>? match = null)
    {
        var job = SocietyEngine.Observe(world, agentId, DateTimeOffset.UtcNow);
        var pick = job.Candidates.First(candidate => candidate.ActionType == action && (match?.Invoke(candidate) ?? true));
        return (job, new DecisionResult(job.RequestId,
            new ActionProposal(pick.ActionType, pick.Params, "test", []), "rule", "test", 0));
    }

    private static void Decide(World world, string agentId, string action, Func<Candidate, bool>? match = null)
    {
        var (job, result) = Propose(world, agentId, action, match);
        var record = SocietyEngine.Apply(world, job, result, DateTimeOffset.UtcNow);
        Assert.True(record.Accepted, record.Code);
    }
}
