namespace Faultline.Core;

// One tick is one day. Daily: production, health, doctor visits and household shopping at the
// cheapest local seller. Weekly: labor market and payroll (SocietyLabor), public payroll, welfare,
// unemployment benefit, public healthcare, discretionary spending and metrics.
public static class SocietyEconomy
{
    // ponytail: fixed economic constants; move to scenario config if experiments need to vary them
    public const int VisitHealthGain = 40;
    public const int PublicCareHealthBelow = 60;
    public const long SavingsBufferMinor = 1000;
    public const int DiscretionaryBasisPoints = 6000;
    public const double DailyIllnessChance = 0.01;

    // Sick people still do some work; output and pay scale from 25% at zero health to 100% when healthy.
    public static double Capacity(Agent citizen) => 0.25 + 0.75 * citizen.Health / 100.0;

    public static void Day(World world)
    {
        var society = world.Society!;
        society.VisitsServedToday.Clear();
        var byCountry = world.Agents.Values.Where(agent => agent.Role != "defunct").GroupBy(agent => agent.CountryId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var nation in society.Nations.Values)
        {
            var agents = byCountry[nation.Id];
            var residents = agents.Where(SocietyLabor.IsResident).ToArray();
            var staff = residents.Where(resident => resident.EmployerId is not null).GroupBy(resident => resident.EmployerId!)
                .ToDictionary(group => group.Key, group => group.ToList());
            Agent[] Workers(Agent firm) => staff.GetValueOrDefault(firm.Id, [])
                .Concat(firm.OwnerId is { } owner && world.Agents[owner].Role == "entrepreneur" ? [world.Agents[owner]] : [])
                .ToArray();
            var firms = agents.Where(agent => agent.Role == "business").ToArray();
            foreach (var firm in firms.OrderBy(firm => firm.Specialty == "energy" ? 0 : 1).ThenBy(firm => firm.Id))
                Produce(world, nation, firm, Workers(firm), residents.Length);
            foreach (var clinic in firms.Where(firm => firm.Specialty == "clinic"))
            {
                var capacity = (int)(Workers(clinic).Sum(Capacity) * SocietyLabor.SectorOf("clinic").DailyOutput);
                society.VisitsServedToday[clinic.Id] = -capacity; // counts up to zero as visits are served
                Acquire(world, clinic, "medicine", Math.Max(0, capacity * 2 - clinic.Inventory["medicine"]), false);
            }

            var start = residents.Length == 0 ? 0 : world.Tick % residents.Length;
            for (var index = 0; index < residents.Length; index++)
            {
                var resident = residents[(index + start) % residents.Length];
                resident.Health = Math.Max(0, resident.Health - 1);
                if (resident.WorkEffort == 2 && resident.EmployerId is not null)
                {
                    resident.Health = Math.Max(0, resident.Health - 1);
                    resident.Wellbeing = Math.Max(0, resident.Wellbeing - 1);
                }
                if (SocietyFactory.Sample(world.Seed, $"{resident.Id}:{world.Tick}", "illness") < DailyIllnessChance)
                    resident.Health = Math.Max(0, resident.Health - 30);
                if (resident.Health < resident.HealthThreshold)
                    Visit(world, nation, resident, resident, true);
                foreach (var good in new[] { "food", "energy" })
                {
                    Acquire(world, resident, good, Math.Max(0, resident.TargetStock - resident.Inventory[good]), true);
                    if (resident.Inventory[good] > 0)
                    {
                        resident.Inventory[good]--;
                        society.ConsumedGoods[good]++;
                        nation.FulfilledNeedsLastCycle++;
                        resident.Wellbeing = Math.Min(100, resident.Wellbeing + 1);
                    }
                    else
                    {
                        nation.UnmetNeedsLastCycle++;
                        resident.Wellbeing = Math.Max(0, resident.Wellbeing - 3);
                        resident.Health = Math.Max(0, resident.Health - 1);
                    }
                }
            }
        }
    }

    public static void Week(World world)
    {
        var society = world.Society!;
        var byCountry = world.Agents.Values.Where(agent => agent.Role != "defunct").GroupBy(agent => agent.CountryId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var nation in society.Nations.Values)
        {
            var agents = byCountry[nation.Id];
            var residents = agents.Where(SocietyLabor.IsResident).ToArray();
            var government = world.Agents[nation.Id + "_government"];
            // Daily activity (visits, purchases) counts toward the week, so deltas run from last week's totals.
            long Since(string key, long total) => total - nation.WeekStartTotals.GetValueOrDefault(key);

            // Planned economies own every firm: the state takes surplus above four weeks of payroll and covers shortfalls.
            if (SocietyLabor.IsPlanned(nation))
                foreach (var firm in agents.Where(agent => agent.Role == "business"))
                {
                    var payroll = SocietyLabor.Staff(world, firm).Sum(worker => worker.SalaryMinor);
                    if (firm.CashMinor > payroll * 4)
                        Transfer(firm, government, firm.CashMinor - payroll * 4);
                    else if (firm.CashMinor < payroll)
                        Transfer(government, firm, Math.Min(government.CashMinor, payroll - firm.CashMinor));
                }

            SocietyLabor.Week(world, nation, government, residents);
            var teachers = PublicPayroll(nation, government,
                residents.Where(resident => resident.EmployerId == government.Id).ToArray());
            // Schools run at full strength only when teachers are paid and healthy enough to work.
            nation.EducationFactor = teachers.Length == 0 ? 1
                : Math.Round(0.7 + 0.3 * teachers.Average(teacher => teacher.PaidShare * Capacity(teacher.Teacher)), 3);

            var citizens = residents.Where(resident => resident.Role == "citizen").ToArray();
            var welfare = citizens.Length == 0 ? 0 : Math.Min(government.CashMinor, (long)nation.WelfareMinor * citizens.Length) / citizens.Length;
            foreach (var citizen in citizens)
            {
                Transfer(government, citizen, welfare);
                nation.WelfarePaidMinor += welfare;
            }
            foreach (var jobless in citizens.Where(citizen => citizen.EmployerId is null))
            {
                var benefit = Math.Min(government.CashMinor, nation.UnemploymentBenefitMinor);
                Transfer(government, jobless, benefit);
                nation.UnemploymentPaidMinor += benefit;
            }

            // Public healthcare: the weekly service budget buys visits for the sickest residents first.
            var careBudget = Math.Min(government.CashMinor, (long)nation.ServiceBudgetPerResidentMinor * residents.Length);
            foreach (var patient in residents.Where(resident => resident.Health < PublicCareHealthBelow)
                .OrderBy(resident => resident.Health).ThenBy(resident => resident.Id))
            {
                var clinic = OpenClinic(world, nation);
                if (clinic is null || careBudget < clinic.PriceMinor || !Visit(world, nation, patient, government, false))
                    break;
                careBudget -= clinic.PriceMinor;
                nation.PublicDoctorVisits++;
            }

            // Households spend part of their savings above a buffer on household goods (tools).
            foreach (var resident in residents)
            {
                var spend = Math.Max(0, resident.CashMinor - SavingsBufferMinor) * DiscretionaryBasisPoints / 10000;
                var cheapest = Sellers(world, nation, "tools").FirstOrDefault();
                if (cheapest is null || spend < cheapest.PriceMinor)
                    continue;
                var units = Acquire(world, resident, "tools", (int)(spend / cheapest.PriceMinor), true);
                resident.Inventory["tools"] -= units;
                society.ConsumedGoods["tools"] += units;
                if (units > 0)
                    resident.Wellbeing = Math.Min(100, resident.Wellbeing + 2);
            }

            var tensions = society.Relations.Values.Where(relation => relation.AtWar
                && (relation.First == nation.Id || relation.Second == nation.Id)).ToArray();
            if (tensions.Length > 0)
                ConflictCosts(world, nation, government, agents.Where(agent => agent.Role == "business").ToArray(), tensions.Length);

            nation.UnmetNeeds += nation.UnmetNeedsLastCycle;
            world.UnmetNeeds += nation.UnmetNeedsLastCycle;
            world.Emit("economy_cycle", "rule", [nation.Id], new
            {
                nation.UnmetNeedsLastCycle, nation.FulfilledNeedsLastCycle,
                wages_paid_minor = Since("wages", nation.WagesPaidMinor),
                sales_tax_minor = Since("sales_tax", nation.SalesTaxCollectedMinor),
                profit_tax_minor = Since("profit_tax", nation.ProfitTaxCollectedMinor),
                doctor_visits = Since("visits", nation.DoctorVisits),
                public_doctor_visits = Since("public_visits", nation.PublicDoctorVisits),
                nation.EducationFactor, nation.Unemployed,
                government_income_minor = government.WeekInMinor, government_spending_minor = government.WeekOutMinor,
                funded_welfare_per_person_minor = welfare,
                production_units = nation.ProducedUnits,
                period = "week"
            });
            nation.WeekStartTotals = new()
            {
                ["wages"] = nation.WagesPaidMinor, ["sales_tax"] = nation.SalesTaxCollectedMinor,
                ["profit_tax"] = nation.ProfitTaxCollectedMinor, ["visits"] = nation.DoctorVisits,
                ["public_visits"] = nation.PublicDoctorVisits
            };
        }
        SettleCredit(world);
        UpdateMetrics(world);
        foreach (var nation in society.Nations.Values)
        {
            nation.UnmetNeedsLastCycle = 0;
            nation.FulfilledNeedsLastCycle = 0;
        }
        foreach (var agent in world.Agents.Values)
        {
            agent.LastWeekInMinor = agent.WeekInMinor;
            agent.LastWeekOutMinor = agent.WeekOutMinor;
            agent.WeekInMinor = 0;
            agent.WeekOutMinor = 0;
        }
    }

    // Domestic sellers of a good, cheapest first.
    public static IEnumerable<Agent> Sellers(World world, Nation nation, string good) =>
        world.Agents.Values.Where(agent => agent.Role == "business" && agent.CountryId == nation.Id
                && agent.Specialty == good && agent.Inventory[good] > 0)
            .OrderBy(agent => agent.PriceMinor).ThenBy(agent => agent.Id);

    // Buys up to the wanted quantity from the cheapest domestic sellers the buyer can afford.
    public static int Acquire(World world, Agent buyer, string good, int wanted, bool household)
    {
        var nation = world.Society!.Nations[buyer.CountryId];
        var bought = 0;
        foreach (var seller in Sellers(world, nation, good).Where(seller => seller != buyer).ToArray())
        {
            if (bought >= wanted)
                break;
            var units = (int)Math.Min(Math.Min(wanted - bought, seller.Inventory[good]), buyer.CashMinor / Math.Max(1, seller.PriceMinor));
            if (units <= 0)
                continue;
            Sell(world, buyer, seller, (long)units * seller.PriceMinor, household, units);
            seller.Inventory[good] -= units;
            buyer.Inventory[good] += units;
            bought += units;
        }
        return bought;
    }

    // A sale between agents. Household purchases carry sales tax inside the price; business
    // revenue, costs and units sold accumulate for the weekly payroll, profit tax and price review.
    public static void Sell(World world, Agent buyer, Agent seller, long amount, bool household = false, int units = 0)
    {
        var society = world.Society!;
        var nation = society.Nations[seller.CountryId];
        var tax = household ? amount * nation.SalesTaxBasisPoints / 10000 : 0;
        Transfer(buyer, seller, amount - tax);
        Transfer(buyer, world.Agents[seller.CountryId + "_government"], tax);
        nation.SalesTaxCollectedMinor += tax;
        nation.TaxesCollectedMinor += tax;
        if (seller.Role == "business")
        {
            society.PeriodRevenueMinor[seller.Id] = society.PeriodRevenueMinor.GetValueOrDefault(seller.Id) + amount - tax;
            society.PeriodUnitsSold[seller.Id] = society.PeriodUnitsSold.GetValueOrDefault(seller.Id) + units;
        }
        if (buyer.Role == "business")
            society.PeriodCostMinor[buyer.Id] = society.PeriodCostMinor.GetValueOrDefault(buyer.Id) + amount;
    }

    // The cheapest clinic with a doctor free today and medicine supplies on hand.
    public static Agent? OpenClinic(World world, Nation nation) =>
        world.Agents.Values.Where(agent => agent.Role == "business" && agent.CountryId == nation.Id && agent.Specialty == "clinic"
                && world.Society!.VisitsServedToday.GetValueOrDefault(agent.Id) < 0 && agent.Inventory["medicine"] > 0)
            .OrderBy(agent => agent.PriceMinor).ThenBy(agent => agent.Id).FirstOrDefault();

    // A doctor visit: a clinic's doctor sees the patient and uses one unit of medicine as supplies.
    public static bool Visit(World world, Nation nation, Agent patient, Agent payer, bool household)
    {
        var society = world.Society!;
        var clinic = OpenClinic(world, nation);
        if (clinic is null)
        {
            foreach (var busy in world.Agents.Values.Where(agent => agent.Role == "business" && agent.CountryId == nation.Id
                         && agent.Specialty == "clinic"))
                society.TurnedAwayThisWeek[busy.Id] = society.TurnedAwayThisWeek.GetValueOrDefault(busy.Id) + 1;
            return false;
        }
        if (payer.CashMinor < clinic.PriceMinor)
            return false;
        Sell(world, payer, clinic, clinic.PriceMinor, household, 1);
        clinic.Inventory["medicine"]--;
        society.ConsumedGoods["medicine"]++;
        society.VisitsServedToday[clinic.Id]++;
        patient.Health = Math.Min(100, patient.Health + VisitHealthGain);
        nation.DoctorVisits++;
        return true;
    }

    // Public salaries come from the government's pay scale, not effort; when the treasury falls
    // short, everyone is paid the same fraction.
    private static (Agent Teacher, double PaidShare)[] PublicPayroll(Nation nation, Agent government, Agent[] staff)
    {
        var working = staff.Where(worker => worker.WorkEffort > 0).ToArray(); // strikers forfeit the week's pay
        var bill = working.Sum(worker => worker.SalaryMinor);
        var share = bill == 0 ? 1 : Math.Min(1, government.CashMinor / (double)bill);
        foreach (var worker in working)
            PayWage(nation, government, government, worker, (long)(worker.SalaryMinor * share));
        return staff.Where(worker => worker.Occupation == "teacher").Select(worker => (worker, share)).ToArray();
    }

    public static long PayWage(Nation nation, Agent government, Agent employer, Agent worker, long wage)
    {
        Transfer(employer, worker, wage);
        var tax = wage * nation.TaxBasisPoints / 10000;
        Transfer(worker, government, tax);
        nation.TaxesCollectedMinor += tax;
        nation.WagesPaidMinor += wage;
        return wage;
    }

    private static void Produce(World world, Nation nation, Agent producer, Agent[] workers, int residents)
    {
        var sector = SocietyLabor.SectorOf(producer.Specialty!);
        if (sector.Good is not { } good)
            return;
        var multiplier = good switch
        {
            "food" => nation.FoodMultiplier,
            "energy" => nation.EnergyMultiplier,
            _ => 1
        };
        if (nation.CrisisUntilTick > world.Tick && nation.CrisisGood == good)
            multiplier *= 0.35;
        // Health drives output; wellbeing (morale) has a milder effect.
        var potential = (int)(workers.Sum(worker => worker.WorkEffort * (0.75 + 0.25 * worker.Wellbeing / 100.0) * Capacity(worker))
            * sector.DailyOutput * multiplier * nation.Productivity * nation.EducationFactor);
        var stockLimit = residents * good switch { "food" or "energy" => 30, "tools" => 15, _ => 10 };
        var quantity = Math.Min(potential, Math.Max(0, stockLimit - producer.Inventory[good]));
        if (quantity <= 0)
            return;
        var inputs = good switch { "energy" => [], "medicine" => new[] { "energy", "tools" }, _ => new[] { "energy" } };
        foreach (var input in inputs)
        {
            var needed = (quantity + 9) / 10;
            Acquire(world, producer, input, Math.Max(0, needed - producer.Inventory[input]), false);
            quantity = Math.Min(quantity, producer.Inventory[input] * 10);
        }
        foreach (var input in inputs)
        {
            var used = (quantity + 9) / 10;
            producer.Inventory[input] -= used;
            world.Society!.ConsumedGoods[input] += used;
        }
        producer.Inventory[good] += quantity;
        nation.ProducedUnits += quantity;
        world.Society!.ProducedGoods[good] += quantity;
    }

    private static void ConflictCosts(World world, Nation nation, Agent government, Agent[] businesses, int conflicts)
    {
        var bank = world.Agents[nation.Id + "_bank"];
        Transfer(government, bank, Math.Min(government.CashMinor, 1000L * conflicts));
        foreach (var business in businesses.Where(business => SocietyLabor.SectorOf(business.Specialty!).Good is not null))
        {
            var good = business.Specialty!;
            var destroyed = Math.Min(business.Inventory[good], Math.Max(1, nation.Population / 40) * conflicts);
            business.Inventory[good] -= destroyed;
            nation.LostUnits += destroyed;
            world.Society!.LostGoods[good] += destroyed;
        }
        world.Emit("conflict_costs", "rule", [nation.Id], new { conflicts, nation.LostUnits });
    }

    private static void SettleCredit(World world)
    {
        foreach (var credit in world.Society!.Credit.Where(credit => credit.Status == "active" && credit.DueTick <= world.Tick))
        {
            var borrower = world.Agents[credit.BorrowerId];
            var bank = world.Agents[credit.BankId];
            var paid = Math.Min(borrower.CashMinor, credit.DueMinor);
            Transfer(borrower, bank, paid);
            credit.Status = paid == credit.DueMinor ? "repaid" : "defaulted";
            if (credit.Status == "defaulted")
                world.Society.Nations[borrower.CountryId].DefaultedLoans++;
            world.Emit("loan_settled", "rule", [credit.Id, borrower.Id, bank.Id], new
            {
                credit.Status, paid_minor = paid, loss_minor = credit.DueMinor - paid,
                settlement = "simulated internal ledger"
            });
        }
    }

    public static void UpdateMetrics(World world)
    {
        var groups = world.Agents.Values.GroupBy(agent => agent.CountryId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var nation in world.Society!.Nations.Values)
        {
            var agents = groups[nation.Id];
            var citizens = agents.Where(SocietyLabor.IsResident).ToArray();
            var support = citizens.Length == 0 ? 0 : citizens.Count(citizen => citizen.SupportsLeadership) / (double)citizens.Length;
            var wellbeing = citizens.Length == 0 ? 0 : citizens.Average(citizen => citizen.Wellbeing) / 100;
            nation.Approval = Math.Round(support * 0.5 + wellbeing * 0.5, 3);
            nation.GiniCash = Math.Round(Gini(citizens.Select(citizen => citizen.CashMinor).ToArray()), 4);
            nation.Stocks = SocietyFactory.Goods.ToDictionary(good => good,
                good => agents.Where(agent => agent.Role == "business").Sum(agent => agent.Inventory[good]));
            var totalNeeds = nation.FulfilledNeedsLastCycle + nation.UnmetNeedsLastCycle;
            nation.History.Add(new CountryMetric(world.Tick,
                totalNeeds == 0 ? 1 : nation.FulfilledNeedsLastCycle / (double)totalNeeds,
                nation.Approval, nation.GiniCash, world.Agents[nation.Id + "_government"].CashMinor,
                nation.TradeValueMinor, nation.ProducedUnits));
            if (nation.History.Count > 100)
                nation.History.RemoveAt(0);
        }
    }

    public static double Gini(long[] balances)
    {
        if (balances.Length == 0 || balances.Sum() == 0)
            return 0;
        var ordered = balances.Order().ToArray();
        var weighted = ordered.Select((balance, index) => (2.0 * (index + 1) - ordered.Length - 1) * balance).Sum();
        return weighted / (ordered.Length * (double)ordered.Sum());
    }

    public static void Transfer(Agent from, Agent to, long amount)
    {
        if (amount < 0 || amount > from.CashMinor)
            throw new InvalidOperationException("Unfunded transfer");
        from.CashMinor -= amount;
        to.CashMinor += amount;
        from.WeekOutMinor += amount;
        to.WeekInMinor += amount;
    }

    public static void AssertConservation(World world)
    {
        var society = world.Society!;
        var money = world.Agents.Values.Sum(agent => agent.CashMinor) + society.Cargo.Sum(cargo => cargo.EscrowMinor);
        if (money != world.InitialCashMinor || world.Agents.Values.Any(agent => agent.CashMinor < 0
            || agent.Inventory.Values.Any(quantity => quantity < 0)))
            throw new InvalidOperationException("Cash or inventory invariant failed");
        foreach (var good in SocietyFactory.Goods)
        {
            var physical = world.Agents.Values.Sum(agent => (long)agent.Inventory[good])
                + society.Cargo.Where(cargo => cargo.Status == "in_transit" && cargo.Good == good).Sum(cargo => (long)cargo.Quantity);
            if (physical != society.InitialGoods[good] + society.ProducedGoods[good]
                - society.ConsumedGoods[good] - society.LostGoods[good])
                throw new InvalidOperationException("Goods conservation failed");
        }
    }
}
