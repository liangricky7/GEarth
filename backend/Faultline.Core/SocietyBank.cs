namespace Faultline.Core;

// One commercial bank per country, a firm in the "bank" sector run by its owner (state-owned in planned
// economies). Each week residents' savings above a cash buffer are swept into deposits that earn interest,
// and those deposits fund business and startup loans repaid in weekly installments. Only a fraction of
// deposits is kept as reserves, so when many depositors withdraw at once the bank runs short; the
// government's deposit insurance (set by government type) then pays the insured part and takes over that
// claim on the bank. The owner sets rates and a lending standard; the bank applies them to each request.
public static class SocietyBank
{
    public const string Sector = "bank";
    public const int ReserveRatioPct = 10;
    public const int CashBufferWeeks = 2; // residents keep two weeks of their pay as cash
    public const int LoanWeeks = 26;
    public const int LoansPerBanker = 10; // loans one banker (or the owner) can service
    public const int MissedPaymentsToDefault = 3;
    public const int WeeksPerYear = 52;
    // ponytail: fixed menus of annual rates; widen if owners keep picking an end of the range
    public static readonly int[] DepositRates = [1, 3, 5];
    public static readonly int[] LoanRates = [6, 10, 14];
    // A lending standard sets the founder's minimum down payment and the largest weekly installment as a
    // share of a firm's weekly revenue (a debt-service test).
    public static readonly Dictionary<string, (int DownPaymentPct, int RevenueSharePct)> Standards = new()
    {
        ["strict"] = (50, 10), ["normal"] = (25, 20), ["loose"] = (10, 35)
    };

    public static Agent BankOf(World world, string country) => world.Agents[$"{country}_{Sector}"];

    public static bool IsBank(Agent agent) => agent.Specialty == Sector;

    public static long Savings(Agent resident) => resident.CashMinor + resident.DepositMinor;

    // What a resident can spend now: cash plus the part of their deposit the bank's reserves can pay.
    public static long Liquid(World world, Agent resident) =>
        resident.CashMinor + Math.Min(resident.DepositMinor, BankOf(world, resident.CountryId).CashMinor);

    public static long Deposits(World world, string country) =>
        world.Agents.Values.Where(agent => agent.CountryId == country).Sum(agent => agent.DepositMinor);

    private static IEnumerable<Credit> Loans(World world, Agent bank, string status) =>
        world.Society!.Credit.Where(credit => credit.BankId == bank.Id && credit.Status == status);

    public static long LoansOutstanding(World world, Agent bank) => Loans(world, bank, "active").Sum(credit => credit.PrincipalLeftMinor);

    // Reserves and loans, less what the bank owes depositors.
    public static long Equity(World world, Agent bank) => bank.CashMinor + LoansOutstanding(world, bank) - Deposits(world, bank.CountryId);

    public static long RequiredReserves(World world, Agent bank) => Deposits(world, bank.CountryId) * ReserveRatioPct / 100;

    // A state bank without staff is still run by its management, so it can always service some loans.
    public static int LoanCapacity(World world, Agent bank) => LoansPerBanker * Math.Max(1, SocietyLabor.Workers(world, bank).Length);

    public static int LoanBook(World world, Agent bank) => Loans(world, bank, "active").Count() + Loans(world, bank, "requested").Count();

    public static Credit? LoanOf(World world, string borrowerId) =>
        world.Society!.Credit.LastOrDefault(credit => credit.BorrowerId == borrowerId && credit.Status is "requested" or "active");

    public static long InsuranceLimit(Nation nation) => Governments.Get(nation.GovernmentId).DepositInsuranceMinor;

    public static (long Principal, long Interest) Installment(long principal, int ratePct) =>
        ((principal + LoanWeeks - 1) / LoanWeeks,
            (principal * ratePct * LoanWeeks / (100L * WeeksPerYear) + LoanWeeks - 1) / LoanWeeks);

    // The most a founder can borrow toward a startup under the bank's current standard.
    public static long StartupLoan(World world, Agent founder, long cost) =>
        cost * (100 - Standards[BankOf(world, founder.CountryId).LendingStandard!].DownPaymentPct) / 100;

    public static void Deposit(World world, Agent depositor, long amount)
    {
        SocietyEconomy.Transfer(world, depositor, BankOf(world, depositor.CountryId), amount, "deposit");
        depositor.DepositMinor += amount;
    }

    // Pays out from reserves. Whatever the reserves cannot pay is covered by deposit insurance up to the
    // government's limit (and its cash); the government takes over that claim. Returns the cash received.
    public static long Withdraw(World world, Agent depositor, long amount)
    {
        var bank = BankOf(world, depositor.CountryId);
        amount = Math.Min(amount, depositor.DepositMinor);
        var paid = Math.Min(amount, bank.CashMinor);
        SocietyEconomy.Transfer(world, bank, depositor, paid, "withdrawal");
        depositor.DepositMinor -= paid;
        var shortfall = amount - paid;
        if (shortfall == 0)
            return paid;
        bank.UnmetWithdrawalsMinor += shortfall;
        var government = world.Agents[depositor.CountryId + "_government"];
        if (depositor == government)
            return paid;
        var nation = world.Society!.Nations[depositor.CountryId];
        // ponytail: the limit applies to each withdrawal, not to a depositor's total; track insured totals if banks fail repeatedly
        var covered = Math.Min(Math.Min(shortfall, InsuranceLimit(nation)), government.CashMinor);
        SocietyEconomy.Transfer(world, government, depositor, covered, "deposit_insurance");
        depositor.DepositMinor -= covered;
        government.DepositMinor += covered;
        nation.DepositInsurancePaidMinor += covered;
        return paid + covered;
    }

    // Tops up a resident's cash from their deposit before a payment (a debit-card purchase).
    public static void Draw(World world, Agent resident, long amount)
    {
        if (resident.CashMinor < amount && resident.DepositMinor > 0)
            Withdraw(world, resident, amount - resident.CashMinor);
    }

    // A resident who stops trusting the bank takes everything out; one who returns is swept in again weekly.
    public static void SetKeepsSavings(World world, Agent resident, bool keep, string cause)
    {
        resident.KeepsSavingsInBank = keep;
        if (keep)
            return;
        var requested = resident.DepositMinor;
        var received = Withdraw(world, resident, requested);
        world.Emit("savings_withdrawn", "rule", [resident.Id, BankOf(world, resident.CountryId).Id], new
        {
            requested_minor = requested, received_minor = received, still_owed_minor = resident.DepositMinor
        }, cause);
    }

    public static Credit Request(World world, Agent borrower, long amount, string? sector, int launchPrice, string cause)
    {
        var credit = new Credit
        {
            Id = $"loan_{world.Seq}", BankId = BankOf(world, borrower.CountryId).Id, BorrowerId = borrower.Id,
            PrincipalMinor = amount, Sector = sector, LaunchPriceMinor = launchPrice, RequestedTick = world.Tick
        };
        world.Society!.Credit.Add(credit);
        credit.RequestEventId = world.Emit("loan_requested", "rule", [credit.Id, borrower.Id, credit.BankId], new
        {
            amount_minor = amount, purpose = sector is null ? "business" : $"start a {sector} business"
        }, cause).EventId;
        return credit;
    }

    // Weekly, before payroll: collect installments, credit interest on deposits, take over an insolvent
    // bank, then decide the week's loan requests under the owner's standard.
    public static void Week(World world, Nation nation)
    {
        var society = world.Society!;
        var bank = BankOf(world, nation.Id);
        foreach (var loan in Loans(world, bank, "active").ToArray())
            Collect(world, nation, bank, loan);
        // Interest is added to deposits: a cost to the bank that moves no cash.
        foreach (var depositor in world.Agents.Values.Where(agent => agent.CountryId == nation.Id
                     && SocietyLabor.IsResident(agent) && agent.DepositMinor > 0))
        {
            var interest = (long)Math.Round(depositor.DepositMinor * bank.DepositRatePct / (100.0 * WeeksPerYear));
            depositor.DepositMinor += interest;
            society.PeriodCostMinor[bank.Id] = society.PeriodCostMinor.GetValueOrDefault(bank.Id) + interest;
        }
        var equity = Equity(world, bank);
        if (equity < 0 && bank.OwnerId is { } ownerId)
        {
            SocietyLabor.ReleaseOwner(world, world.Agents[ownerId]);
            bank.OwnerId = null;
            nation.BankFailures++;
            world.Emit("bank_failed", "rule", [bank.Id, nation.Id], new
            {
                owner = ownerId, equity_minor = equity, deposits_minor = Deposits(world, nation.Id),
                outcome = "state takeover; the owner loses their stake and depositors keep their claims"
            });
        }
        var shortOfReserves = bank.UnmetWithdrawalsMinor > 0;
        foreach (var request in Loans(world, bank, "requested").ToArray())
            Decide(world, nation, bank, request, shortOfReserves);
        world.Emit("bank_week", "rule", [bank.Id, nation.Id], new
        {
            reserves_minor = bank.CashMinor, deposits_minor = Deposits(world, nation.Id), loans_minor = LoansOutstanding(world, bank),
            equity_minor = Equity(world, bank), unmet_withdrawals_minor = bank.UnmetWithdrawalsMinor,
            insurance_paid_minor = nation.DepositInsurancePaidMinor, bank.DepositRatePct, bank.LoanRatePct, bank.LendingStandard
        });
        bank.UnmetWithdrawalsMinor = 0;
    }

    // After the week's spending: savings above the cash buffer go into the bank, residents short of it
    // draw on deposits, and those who chose to keep savings out withdraw what the bank still owes them.
    // The government then recovers insurance claims from reserves above the requirement.
    public static void Sweep(World world, Nation nation, Agent[] residents)
    {
        foreach (var resident in residents)
        {
            var buffer = (long)(CashBufferWeeks * SocietyEconomy.WeeklyPay(resident));
            if (!resident.KeepsSavingsInBank)
                Withdraw(world, resident, resident.DepositMinor);
            else if (resident.CashMinor > buffer)
                Deposit(world, resident, resident.CashMinor - buffer);
            else if (resident.DepositMinor > 0)
                Withdraw(world, resident, buffer - resident.CashMinor);
        }
        var bank = BankOf(world, nation.Id);
        var government = world.Agents[nation.Id + "_government"];
        var spare = bank.CashMinor - RequiredReserves(world, bank);
        if (government.DepositMinor > 0 && spare > 0)
            Withdraw(world, government, Math.Min(spare, government.DepositMinor));
    }

    private static void Collect(World world, Nation nation, Agent bank, Credit loan)
    {
        var society = world.Society!;
        var borrower = world.Agents[loan.BorrowerId];
        var principal = Math.Min(loan.PrincipalLeftMinor, loan.WeeklyPrincipalMinor);
        var due = principal + loan.WeeklyInterestMinor;
        if (borrower.CashMinor < due)
        {
            if (++loan.MissedPayments >= MissedPaymentsToDefault)
                SocietyLabor.Close(world, nation, borrower, $"missed {MissedPaymentsToDefault} loan payments");
            return;
        }
        SocietyEconomy.Transfer(world, borrower, bank, due, "loan_repayment");
        loan.PrincipalLeftMinor -= principal;
        loan.MissedPayments = 0;
        society.PeriodRevenueMinor[bank.Id] = society.PeriodRevenueMinor.GetValueOrDefault(bank.Id) + loan.WeeklyInterestMinor;
        society.PeriodCostMinor[borrower.Id] = society.PeriodCostMinor.GetValueOrDefault(borrower.Id) + loan.WeeklyInterestMinor;
        if (loan.PrincipalLeftMinor == 0)
            Settle(world, nation, bank, loan, 0);
    }

    // A closing firm repays its bank before its owner is paid; what it cannot repay is the bank's loss.
    public static void Recover(World world, Nation nation, Agent firm)
    {
        foreach (var loan in world.Society!.Credit.Where(credit => credit.BorrowerId == firm.Id && credit.Status == "active").ToArray())
        {
            var bank = world.Agents[loan.BankId];
            var paid = Math.Min(firm.CashMinor, loan.PrincipalLeftMinor);
            SocietyEconomy.Transfer(world, firm, bank, paid, "loan_recovery");
            loan.PrincipalLeftMinor -= paid;
            Settle(world, nation, bank, loan, paid);
        }
    }

    private static void Settle(World world, Nation nation, Agent bank, Credit loan, long recovered)
    {
        var loss = loan.PrincipalLeftMinor;
        loan.PrincipalLeftMinor = 0;
        loan.Status = loss == 0 ? "repaid" : "defaulted";
        if (loss > 0)
        {
            nation.DefaultedLoans++;
            world.Society!.PeriodCostMinor[bank.Id] = world.Society.PeriodCostMinor.GetValueOrDefault(bank.Id) + loss;
        }
        world.Emit("loan_settled", "rule", [loan.Id, loan.BorrowerId, bank.Id], new
        {
            loan.Status, recovered_on_closure_minor = recovered, loss_minor = loss, settlement = "simulated internal ledger"
        });
    }

    private static void Decide(World world, Nation nation, Agent bank, Credit request, bool shortOfReserves)
    {
        var borrower = world.Agents[request.BorrowerId];
        var standard = Standards[bank.LendingStandard!];
        var (principal, interest) = Installment(request.PrincipalMinor, bank.LoanRatePct);
        var revenue = world.Society!.PeriodRevenueMinor.GetValueOrDefault(borrower.Id);
        var reason = shortOfReserves ? "the bank could not meet withdrawals"
            : Loans(world, bank, "active").Count() >= LoanCapacity(world, bank) ? "no banker free to service another loan"
            : bank.CashMinor - request.PrincipalMinor < RequiredReserves(world, bank) ? "reserves are needed for deposits"
            : request.Sector is { } sector
                ? !SocietyLabor.CanStart(world, borrower, sector, request.PrincipalMinor)
                  || borrower.CashMinor + request.PrincipalMinor + Math.Min(borrower.DepositMinor, bank.CashMinor - request.PrincipalMinor)
                     < SocietyLabor.StartupCost(sector)
                    ? "the founder no longer qualifies" : null
            : borrower.Role != "business" ? "the business closed"
            : (principal + interest) * 100 > revenue * standard.RevenueSharePct ? "installments too large for the firm's revenue"
            : null;
        if (reason is not null)
        {
            request.Status = "rejected";
            world.Emit("loan_rejected", "rule", [request.Id, borrower.Id, bank.Id], new { amount_minor = request.PrincipalMinor, reason });
            return;
        }
        request.RatePct = bank.LoanRatePct;
        (request.WeeklyPrincipalMinor, request.WeeklyInterestMinor) = (principal, interest);
        request.PrincipalLeftMinor = request.PrincipalMinor;
        request.Status = "active";
        SocietyEconomy.Transfer(world, bank, borrower, request.PrincipalMinor, "loan");
        if (request.Sector is { } opening) // the new firm, not the founder, owes the loan (limited liability)
            request.BorrowerId = SocietyLabor.Start(world, borrower, opening, request.LaunchPriceMinor, request.RequestEventId).Id;
        world.Emit("loan_funded", "rule", [request.Id, bank.Id, request.BorrowerId], new
        {
            request.PrincipalMinor, request.RatePct, weekly_payment_minor = principal + interest, weeks = LoanWeeks,
            settlement = "simulated internal ledger"
        });
    }

    // Dividends come only from equity above the owner's capital (and a tenth of the loan book), never from
    // the reserves that back deposits.
    public static long Dividend(World world, Agent bank) => Math.Max(0, Math.Min(bank.CashMinor - RequiredReserves(world, bank),
        Equity(world, bank) - Math.Max(bank.InvestedMinor, LoansOutstanding(world, bank) / 10)));

    // What residents see of their bank, and the full balance sheet for whoever runs it.
    public static object View(World world, Agent bank, bool manager)
    {
        var nation = world.Society!.Nations[bank.CountryId];
        var deposits = Deposits(world, bank.CountryId);
        var limit = InsuranceLimit(nation);
        return new
        {
            bank.DepositRatePct, bank.LoanRatePct,
            reserves_pct_of_deposits = deposits == 0 ? (long?)null : bank.CashMinor * 100 / deposits,
            unmet_withdrawals_minor = bank.UnmetWithdrawalsMinor,
            deposits_insured_up_to = limit == Governments.FullDepositInsurance ? "all" : limit.ToString(),
            state_run = bank.OwnerId is null ? true : (bool?)null,
            reserves_minor = manager ? bank.CashMinor : (long?)null,
            deposits_minor = manager ? deposits : (long?)null,
            required_reserves_minor = manager ? RequiredReserves(world, bank) : (long?)null,
            loans_outstanding_minor = manager ? LoansOutstanding(world, bank) : (long?)null,
            equity_minor = manager ? Equity(world, bank) : (long?)null,
            lending_standard = manager ? bank.LendingStandard : null,
            loans_active = manager ? Loans(world, bank, "active").Count() : (int?)null,
            loans_requested = manager ? Loans(world, bank, "requested").Count() : (int?)null,
            loan_capacity = manager ? LoanCapacity(world, bank) : (int?)null,
            defaulted_loans = manager ? nation.DefaultedLoans : (int?)null
        };
    }
}
