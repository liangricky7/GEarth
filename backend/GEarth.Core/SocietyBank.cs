namespace GEarth.Core;

// One commercial bank per country, a firm in the "bank" sector run by its owner (state-owned in planned
// economies). Each week residents' savings and private firms' cash above a buffer are swept into deposits that
// earn interest. Loans create matching deposits, constrained by equity and settlement reserves. Only a fraction of
// deposits is kept as reserves, so when many depositors withdraw at once the bank runs short; the
// government's deposit insurance (set by government type) then pays the insured part and takes over that
// claim on the bank. The owner sets rates and a lending standard; the bank applies them to each request.
public static class SocietyBank
{
    public const string Sector = "bank";
    public const bool Paused = false; // recorded in run metadata (banking_paused) so analytics can tell runs apart
    public const int ReserveRatioPct = 10;
    public const int CapitalRatioPct = 10; // simplified risk weight: every loan has weight one
    public const string AccountingVersion = "deposit_creation_v1";
    public const int OwnerStakePct = 15; // of the bank's opening cash; the rest is the owner's deposit
    public const int CashBufferWeeks = 2; // residents keep two weeks of their pay as cash, firms two weeks of payroll
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

    // Domestic payments transfer deposit claims without withdrawing bank reserves.
    public static long Liquid(World world, Agent resident) =>
        resident.CashMinor + resident.DepositMinor;

    public static long CashAvailable(World world, Agent resident) =>
        resident.CashMinor + Math.Min(resident.DepositMinor, BankOf(world, resident.CountryId).CashMinor);

    // A cash-only operation may use existing reserves, but is not a request for insurance.
    public static long WithdrawAvailable(World world, Agent depositor, long requested)
    {
        if (requested < 0)
            throw new InvalidOperationException("Negative withdrawal");
        var bank = BankOf(world, depositor.CountryId);
        var paid = Math.Min(requested, Math.Min(depositor.DepositMinor, bank.CashMinor));
        SocietyEconomy.Transfer(world, bank, depositor, paid, "withdrawal");
        depositor.DepositMinor -= paid;
        Ledger(world, bank, depositor, null, paid, "cash_withdrawal_claim_reduced");
        return paid;
    }

    public static long PaymentCapacity(World world, Agent payer, Agent recipient) => payer.CashMinor
        + (payer.CountryId == recipient.CountryId ? payer.DepositMinor
            : Math.Min(payer.DepositMinor, BankOf(world, payer.CountryId).CashMinor));

    // Deposit payments settle within one bank, or move reserves between country banks. A payment to the
    // bank itself extinguishes that liability (loan repayment/fees), rather than giving the bank a deposit.
    public static void TransferDeposit(World world, Agent payer, Agent recipient, long amount, string kind)
    {
        if (amount == 0)
            return;
        if (amount < 0 || amount > payer.DepositMinor || amount > PaymentCapacity(world, payer, recipient) - payer.CashMinor)
            throw new InvalidOperationException("Unfunded deposit transfer");
        var source = BankOf(world, payer.CountryId);
        var destination = BankOf(world, recipient.CountryId);
        payer.DepositMinor -= amount;
        if (source != destination)
            SocietyEconomy.Transfer(world, source, destination, amount, "reserve_settlement");
        if (recipient != destination)
            recipient.DepositMinor += amount;
        Ledger(world, source, payer, recipient, amount, kind);
    }

    private static void Ledger(World world, Agent bank, Agent? from, Agent? to, long amount, string kind, string? creditId = null)
    {
        if (amount == 0)
            return;
        var payload = new
        {
            bank_id = bank.Id, from = from?.Id, to = to?.Id, amount_minor = amount, kind,
            credit_id = creditId,
            reserves_minor = bank.CashMinor, deposits_minor = Deposits(world, bank.CountryId),
            loans_minor = LoansOutstanding(world, bank), equity_minor = Equity(world, bank)
        };
        // Capture-only causal IDs must not change ordinary simulation events or saved state.
        var auditPayload = Wire.Node(payload).AsObject();
        auditPayload["event_id"] = world.CurrentAuditContext?.EventId;
        auditPayload["transaction_group_id"] = world.CurrentAuditContext?.GroupId;
        auditPayload["decision_id"] = world.CurrentAuditContext?.DecisionId;
        RunAudit.Append(world, "bank_ledger", auditPayload);
        world.Emit("bank_ledger", "rule", new[] { bank.Id, from?.Id, to?.Id }.OfType<string>().Distinct().ToArray(), payload);
    }

    private static void Issue(World world, Agent bank, Agent borrower, Credit loan, long amount)
    {
        borrower.DepositMinor += amount;
        loan.DepositCreated = true;
        world.Society!.CreditCreatedMinor += amount;
        Ledger(world, bank, null, borrower, amount, "loan_deposit_created", loan.Id);
    }

    public static long BroadMoney(World world) => world.Agents.Values.Where(agent => !IsBank(agent)).Sum(agent => agent.CashMinor)
        + world.Agents.Values.Sum(agent => agent.DepositMinor) + world.Society!.Cargo.Sum(cargo => cargo.EscrowMinor);

    public static long Deposits(World world, string country) =>
        world.Agents.Values.Where(agent => agent.CountryId == country).Sum(agent => agent.DepositMinor);

    private static IEnumerable<Credit> Loans(World world, Agent bank, string status) =>
        world.Society!.Credit.Where(credit => credit.BankId == bank.Id && credit.Status == status);

    public static long LoansOutstanding(World world, Agent bank) => Loans(world, bank, "active").Sum(credit => credit.PrincipalLeftMinor);

    // Reserves and loans, less what the bank owes depositors.
    public static long Equity(World world, Agent bank) => bank.CashMinor + LoansOutstanding(world, bank) - Loans(world, bank, "active").Sum(loan => loan.ProvisionMinor) - Deposits(world, bank.CountryId);

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
        if (amount < 0 || amount > depositor.CashMinor)
            throw new InvalidOperationException("A cash deposit requires cash");
        SocietyEconomy.Transfer(world, depositor, BankOf(world, depositor.CountryId), amount, "deposit");
        depositor.DepositMinor += amount;
        Ledger(world, BankOf(world, depositor.CountryId), null, depositor, amount, "cash_deposit_claim_created");
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
        Ledger(world, bank, depositor, null, paid, "cash_withdrawal_claim_reduced");
        var shortfall = amount - paid;
        if (shortfall == 0)
            return paid;
        bank.UnmetWithdrawalsMinor += shortfall;
        var government = world.Agents[depositor.CountryId + "_government"];
        if (depositor == government)
            return paid;
        var nation = world.Society!.Nations[depositor.CountryId];
        // The limit is per depositor across every withdrawal the bank could not pay.
        var limit = InsuranceLimit(nation);
        var room = limit == Governments.FullDepositInsurance ? shortfall : Math.Max(0, limit - depositor.InsuredMinor);
        var covered = Math.Min(Math.Min(shortfall, room), government.CashMinor);
        SocietyEconomy.Transfer(world, government, depositor, covered, "deposit_insurance");
        depositor.DepositMinor -= covered;
        depositor.InsuredMinor += covered;
        government.DepositMinor += covered;
        nation.DepositInsurancePaidMinor += covered;
        Ledger(world, bank, depositor, government, covered, "insurance_claim_assigned");
        return paid + covered;
    }

    // Withdraw cash for cash-only settlement, including imports into shipment escrow.
    public static void Draw(World world, Agent resident, long amount)
    {
        if (resident.CashMinor < amount && resident.DepositMinor > 0)
            WithdrawAvailable(world, resident, amount - resident.CashMinor);
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
        // Student loans the government took over under its guarantee are repaid to the treasury.
        var government = world.Agents[nation.Id + "_government"];
        foreach (var loan in society.Credit.Where(credit => credit.BankId == government.Id && credit.Status == "active").ToArray())
            Collect(world, nation, government, loan);
        // State continuation preserves existing claims; exhausted capital cannot fund new interest.
        bank.DepositInterestSuspended = Equity(world, bank) <= 0;
        // Interest is added to deposits: a cost to the bank that moves no cash.
        foreach (var depositor in world.Agents.Values.Where(agent => !bank.DepositInterestSuspended && agent.CountryId == nation.Id
                     && agent.Role != "government" && agent.DepositMinor > 0))
        {
            var interest = (long)Math.Round(depositor.DepositMinor * bank.DepositRatePct / (100.0 * WeeksPerYear));
            depositor.DepositMinor += interest;
            if (SocietyLabor.IsResident(depositor))
                depositor.WeekDisposableIncomeMinor += interest;
            society.PeriodCostMinor[bank.Id] = society.PeriodCostMinor.GetValueOrDefault(bank.Id) + interest;
            Ledger(world, bank, null, depositor, interest, "deposit_interest");
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
                outcome = "state takeover with nominal domestic deposit continuation guarantee; no recapitalization or new reserves"
            });
        }
        var shortOfReserves = bank.UnmetWithdrawalsMinor > 0;
        foreach (var request in Loans(world, bank, "requested").ToArray())
            Decide(world, nation, bank, request, shortOfReserves);
        world.Emit("bank_week", "rule", [bank.Id, nation.Id], new
        {
            reserves_minor = bank.CashMinor, deposits_minor = Deposits(world, nation.Id), loans_minor = LoansOutstanding(world, bank),
            equity_minor = Equity(world, bank), unmet_withdrawals_minor = bank.UnmetWithdrawalsMinor,
            reserve_shortfall_minor = Math.Max(0, RequiredReserves(world, bank) - bank.CashMinor),
            capital_shortfall_minor = Math.Max(0, -Equity(world, bank)),
            continuation_guarantee_exposure_minor = Math.Max(0, -Equity(world, bank)),
            nominal_domestic_claims_guaranteed = bank.OwnerId is null,
            deposit_interest_suspended = bank.DepositInterestSuspended,
            insurance_paid_minor = nation.DepositInsurancePaidMinor, bank.DepositRatePct, bank.LoanRatePct, bank.LendingStandard
        });
        bank.UnmetWithdrawalsMinor = 0;
    }

    // After spending: deposit excess cash, then share spare reserves across automatic buffer requests.
    // Those who explicitly chose to keep savings out request real withdrawals, with insurance if needed.
    // The government then recovers insurance claims from reserves above the requirement.
    public static void Sweep(World world, Nation nation, Agent[] residents)
    {
        var buffers = new List<(Agent Account, long Buffer)>();
        foreach (var resident in residents.OrderBy(resident => resident.Id, StringComparer.Ordinal))
        {
            var buffer = (long)(CashBufferWeeks * SocietyEconomy.WeeklyPay(resident));
            if (!resident.KeepsSavingsInBank)
                Withdraw(world, resident, resident.DepositMinor);
            else
                buffers.Add((resident, buffer));
        }
        // Private firms bank their cash above two weeks of payroll and draw on it when short.
        foreach (var firm in world.InCountry(nation.Id).Where(Banks).ToArray())
        {
            var buffer = FirmBuffer(world, firm);
            buffers.Add((firm, buffer));
        }
        foreach (var (account, buffer) in buffers)
            if (account.CashMinor > buffer)
                Deposit(world, account, account.CashMinor - buffer);
        var bank = BankOf(world, nation.Id);
        var requests = buffers.Select(item => (item.Account,
                Amount: Math.Min(item.Account.DepositMinor, Math.Max(0, item.Buffer - item.Account.CashMinor))))
            .Where(item => item.Amount > 0).OrderBy(item => item.Account.Id, StringComparer.Ordinal).ToArray();
        var total = requests.Sum(item => item.Amount);
        var budget = Math.Min(total, Math.Max(0, bank.CashMinor - RequiredReserves(world, bank)));
        // Cumulative rounding gives proportional allocation with an exact, deterministic total.
        long cumulative = 0, allocated = 0;
        foreach (var (account, amount) in requests)
        {
            cumulative += amount;
            var through = (long)((decimal)budget * cumulative / total);
            WithdrawAvailable(world, account, through - allocated);
            allocated = through;
        }
        var government = world.Agents[nation.Id + "_government"];
        var spare = bank.CashMinor - RequiredReserves(world, bank);
        if (government.DepositMinor > 0 && spare > 0)
            Withdraw(world, government, Math.Min(spare, government.DepositMinor));
    }

    // Firms that bank: privately owned, not the bank itself (state firms hand their surplus to the treasury).
    public static bool Banks(Agent agent) => agent.Role == "business" && agent.OwnerId is not null && !IsBank(agent);

    public static long FirmBuffer(World world, Agent firm) =>
        Math.Max(10000, CashBufferWeeks * SocietyLabor.Staff(world, firm).Sum(worker => worker.SalaryMinor));

    // Students repay once the course ends.
    private static bool Due(World world, Credit loan) =>
        loan.Kind != "student" || !SocietyLabor.IsStudent(world.Agents[loan.BorrowerId]);

    private static void Collect(World world, Nation nation, Agent bank, Credit loan)
    {
        var society = world.Society!;
        var borrower = world.Agents[loan.BorrowerId];
        if (!Due(world, loan))
            return;
        var principal = Math.Min(loan.PrincipalLeftMinor, loan.WeeklyPrincipalMinor);
        var due = principal + loan.WeeklyInterestMinor;
        if (PaymentCapacity(world, borrower, bank) < due)
        {
            if (++loan.MissedPayments >= MissedPaymentsToDefault)
            {
                if (loan.Kind == "treasury")
                {
                    var provision = Math.Min(loan.PrincipalLeftMinor, Math.Max(loan.ProvisionMinor, loan.PrincipalLeftMinor * Math.Min(10, loan.MissedPayments - 2) / 10));
                    if (provision != loan.ProvisionMinor)
                        world.Emit("treasury_credit_impaired", "rule", [loan.Id, bank.Id, borrower.Id], new { provision_minor = provision, loan.MissedPayments });
                    loan.ProvisionMinor = provision;
                }
                else if (loan.Kind == "business")
                    SocietyLabor.Close(world, nation, borrower, $"missed {MissedPaymentsToDefault} loan payments");
                else if (loan.Kind == "student" && bank.Role == "business")
                    Guarantee(world, nation, bank, loan);
            }
            return;
        }
        SocietyEconomy.Transfer(world, borrower, bank, due, loan.Kind == "business" ? "loan_repayment" : $"{loan.Kind}_repayment");
        loan.PrincipalLeftMinor -= principal;
        if (loan.DepositCreated && IsBank(bank))
        {
            society.CreditRetiredMinor += principal;
            Ledger(world, bank, borrower, null, principal, "loan_principal_retired", loan.Id);
        }
        loan.MissedPayments = 0;
        loan.ProvisionMinor = Math.Min(loan.ProvisionMinor, loan.PrincipalLeftMinor); // losses remain reserved until principal is recovered
        society.PeriodRevenueMinor[bank.Id] = society.PeriodRevenueMinor.GetValueOrDefault(bank.Id) + loan.WeeklyInterestMinor;
        society.PeriodCostMinor[borrower.Id] = society.PeriodCostMinor.GetValueOrDefault(borrower.Id) + loan.WeeklyInterestMinor;
        if (loan.PrincipalLeftMinor == 0)
            Settle(world, nation, bank, loan, 0);
    }

    // A closing firm repays its bank before its owner is paid. The owner personally guarantees the loan: their
    // savings cover what the firm cannot, and anything still unpaid becomes their own debt to the bank.
    public static void Recover(World world, Nation nation, Agent firm)
    {
        foreach (var loan in world.Society!.Credit.Where(credit => credit.BorrowerId == firm.Id && credit.Status == "active").ToArray())
        {
            var bank = world.Agents[loan.BankId];
            var paid = Math.Min(PaymentCapacity(world, firm, bank), loan.PrincipalLeftMinor);
            SocietyEconomy.Transfer(world, firm, bank, paid, "loan_recovery");
            loan.PrincipalLeftMinor -= paid;
            if (loan.PrincipalLeftMinor > 0 && firm.OwnerId is { } ownerId && world.Agents[ownerId] is { } owner)
            {
                var guaranteed = Math.Min(PaymentCapacity(world, owner, bank), loan.PrincipalLeftMinor);
                SocietyEconomy.Transfer(world, owner, bank, guaranteed, "loan_guarantee");
                loan.PrincipalLeftMinor -= guaranteed;
                paid += guaranteed;
                if (loan.PrincipalLeftMinor > 0)
                {
                    var debt = Open(world, bank, owner, loan.PrincipalLeftMinor, "owner_debt", loan.RatePct);
                    debt.DepositCreated = loan.DepositCreated;
                    firm.EstateCreditIds.Add(debt.Id);
                    world.Emit("owner_debt", "rule", [debt.Id, owner.Id, bank.Id], new
                    {
                        firm = firm.Id, amount_minor = debt.PrincipalMinor, weekly_payment_minor = debt.WeeklyPrincipalMinor + debt.WeeklyInterestMinor
                    });
                    loan.PrincipalLeftMinor = 0; // moved to the owner's debt, not lost
                }
            }
            if (loan.DepositCreated && IsBank(bank) && paid > 0)
            {
                world.Society.CreditRetiredMinor += paid;
                Ledger(world, bank, firm, null, paid, "loan_principal_retired", loan.Id);
            }
            Settle(world, nation, bank, loan, paid);
        }
    }

    // Estate proceeds pay the existing guaranteed principal, never another copy of the debt.
    public static long RepayPrincipal(World world, Agent payer, Credit loan)
    {
        if (loan.Status != "active")
            return 0;
        var lender = world.Agents[loan.BankId];
        var paid = Math.Min(PaymentCapacity(world, payer, lender), loan.PrincipalLeftMinor);
        if (paid == 0)
            return 0;
        SocietyEconomy.Transfer(world, payer, lender, paid, "loan_recovery");
        loan.PrincipalLeftMinor -= paid;
        if (paid > 0 && loan.DepositCreated && IsBank(lender))
        {
            world.Society!.CreditRetiredMinor += paid;
            Ledger(world, lender, payer, null, paid, "loan_principal_retired", loan.Id);
        }
        if (loan.PrincipalLeftMinor == 0)
            Settle(world, world.Society!.Nations[payer.CountryId], lender, loan, paid);
        return paid;
    }

    // An active loan with no request step: owner debt, a student loan or treasury borrowing.
    private static Credit Open(World world, Agent lender, Agent borrower, long amount, string kind, int ratePct)
    {
        var credit = new Credit
        {
            Id = $"loan_{world.Seq}", Kind = kind, BankId = lender.Id, BorrowerId = borrower.Id, PrincipalMinor = amount,
            PrincipalLeftMinor = amount, RatePct = ratePct, RequestedTick = world.Tick, Status = "active"
        };
        (credit.WeeklyPrincipalMinor, credit.WeeklyInterestMinor) = Installment(amount, ratePct);
        world.Society!.Credit.Add(credit);
        return credit;
    }

    // A loan adds both an asset and a deposit liability; capital backs risk and reserves cover settlement.
    public static long LendingRoom(World world, Agent bank) => Math.Max(0, Math.Min(
        Equity(world, bank) * 100 / CapitalRatioPct - LoansOutstanding(world, bank),
        bank.CashMinor * 100 / ReserveRatioPct - Deposits(world, bank.CountryId)));

    public static bool CanLend(World world, Agent bank, long amount) =>
        amount > 0 && bank.UnmetWithdrawalsMinor == 0 && Loans(world, bank, "active").Count() < LoanCapacity(world, bank)
        && amount <= LendingRoom(world, bank);

    // A student loan for tuition, decided on the spot (the government guarantees it) and repaid after the course.
    public static Credit StudentLoan(World world, Agent student, long amount)
    {
        var bank = BankOf(world, student.CountryId);
        if (!CanLend(world, bank, amount))
            throw new InvalidOperationException("Student loan no longer affordable for bank");
        var loan = Open(world, bank, student, amount, "student", bank.LoanRatePct);
        Issue(world, bank, student, loan, amount);
        world.Emit("loan_funded", "rule", [loan.Id, bank.Id, student.Id], new
        {
            loan.PrincipalMinor, loan.RatePct, loan.Kind, weekly_payment_minor = loan.WeeklyPrincipalMinor + loan.WeeklyInterestMinor,
            weeks = LoanWeeks, settlement = "simulated internal ledger"
        });
        return loan;
    }

    // After three missed payments the government pays the bank what the student still owes and takes over the debt.
    private static void Guarantee(World world, Nation nation, Agent bank, Credit loan)
    {
        var government = world.Agents[nation.Id + "_government"];
        if (SocietyBank.Liquid(world, government) < loan.PrincipalLeftMinor)
            return;
        SocietyEconomy.Transfer(world, government, bank, loan.PrincipalLeftMinor, "student_loan_guarantee");
        loan.BankId = government.Id;
        if (loan.DepositCreated)
        {
            world.Society!.CreditRetiredMinor += loan.PrincipalLeftMinor;
            Ledger(world, bank, government, null, loan.PrincipalLeftMinor, "loan_principal_retired", loan.Id);
        }
        loan.MissedPayments = 0;
        world.Emit("loan_guaranteed", "rule", [loan.Id, loan.BorrowerId, government.Id], new { paid_minor = loan.PrincipalLeftMinor });
    }

    // A planned state short of cash borrows from its state bank, up to the reserves the bank can spare; one
    // loan grows with each draw; new draws add installments without reducing the old schedule.
    // Delinquency or outstanding impairment blocks new advances.
    public static long BorrowForTreasury(World world, Nation nation, long wanted)
    {
        var bank = BankOf(world, nation.Id);
        var government = world.Agents[nation.Id + "_government"];
        if (world.Society!.Credit.Any(credit => credit.Kind == "treasury" && credit.BorrowerId == government.Id
            && credit.Status == "active" && (credit.MissedPayments > 0 || credit.ProvisionMinor > 0))) return 0;
        var amount = Math.Min(wanted, LendingRoom(world, bank));
        if (amount <= 0 || bank.UnmetWithdrawalsMinor > 0)
            return 0;
        if (world.Society!.Credit.FirstOrDefault(credit => credit.Kind == "treasury" && credit.BorrowerId == government.Id && credit.Status == "active") is { } loan)
        {
            loan.PrincipalMinor += amount;
            loan.PrincipalLeftMinor += amount;
            var installment = Installment(amount, loan.RatePct);
            loan.WeeklyPrincipalMinor += installment.Item1;
            loan.WeeklyInterestMinor += installment.Item2;
        }
        else
            loan = Open(world, bank, government, amount, "treasury", bank.LoanRatePct);
        Issue(world, bank, government, loan, amount);
        world.Emit("treasury_borrowed", "rule", [loan.Id, government.Id, bank.Id], new
        {
            amount_minor = amount, owed_minor = loan.PrincipalLeftMinor, weekly_payment_minor = loan.WeeklyPrincipalMinor + loan.WeeklyInterestMinor
        });
        return amount;
    }

    public static long Debt(World world, Agent borrower) => world.Society!.Credit
        .Where(credit => credit.BorrowerId == borrower.Id && credit.Status == "active").Sum(credit => credit.PrincipalLeftMinor);

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
            : Equity(world, bank) * 100 < (LoansOutstanding(world, bank) + request.PrincipalMinor) * CapitalRatioPct ? "capital is needed for loan risk"
            : request.PrincipalMinor > LendingRoom(world, bank) ? "reserves are needed for deposits"
            : request.Sector is { } sector
                ? !SocietyLabor.CanStart(world, borrower, sector, request.PrincipalMinor)
                  || Liquid(world, borrower) + request.PrincipalMinor
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
        Issue(world, bank, borrower, request, request.PrincipalMinor);
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
            reserve_shortfall_minor = Math.Max(0, RequiredReserves(world, bank) - bank.CashMinor),
            capital_shortfall_minor = Math.Max(0, -Equity(world, bank)),
            nominal_domestic_claims_guaranteed = bank.OwnerId is null,
            continuation_guarantee_exposure_minor = bank.OwnerId is null ? Math.Max(0, -Equity(world, bank)) : 0,
            deposit_interest_suspended = bank.DepositInterestSuspended,
            deposits_insured_up_to = limit == Governments.FullDepositInsurance ? "all" : limit.ToString(),
            state_run = bank.OwnerId is null ? true : (bool?)null,
            reserves_minor = manager ? bank.CashMinor : (long?)null,
            deposits_minor = manager ? deposits : (long?)null,
            required_reserves_minor = manager ? RequiredReserves(world, bank) : (long?)null,
            loans_outstanding_minor = manager ? LoansOutstanding(world, bank) : (long?)null,
            loan_loss_provisions_minor = manager ? Loans(world, bank, "active").Sum(loan => loan.ProvisionMinor) : (long?)null,
            equity_minor = manager ? Equity(world, bank) : (long?)null,
            accounting = AccountingVersion, capital_ratio_pct = CapitalRatioPct,
            lending_room_minor = manager ? LendingRoom(world, bank) : (long?)null,
            lending_standard = manager ? bank.LendingStandard : null,
            loans_active = manager ? Loans(world, bank, "active").Count() : (int?)null,
            loans_requested = manager ? Loans(world, bank, "requested").Count() : (int?)null,
            loan_capacity = manager ? LoanCapacity(world, bank) : (int?)null,
            defaulted_loans = manager ? nation.DefaultedLoans : (int?)null
        };
    }
}
