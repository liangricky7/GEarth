namespace Faultline.Core;

// One tick is one day. Daily: production, health, doctor visits and household shopping at the
// cheapest local seller. Weekly: banking (SocietyBank), labor market and payroll (SocietyLabor), public
// payroll, welfare, unemployment benefit, public healthcare, discretionary spending, the savings sweep
// and metrics.
public static class SocietyEconomy
{
    // ponytail: fixed economic constants; move to scenario config if experiments need to vary them
    public const int VisitHealthGain = 40;
    public const int PublicCareHealthBelow = 60;
    public const double DailyIllnessChance = 0.01;

    // Quality of life (stored as Wellbeing, 0-100) is scored from outcomes, each with diminishing returns:
    // needs met today, health, discretionary goods and services bought last week, security (a job or
    // business plus savings) and free time (overtime costs some).
    public const double NeedsWeight = 0.35, HealthWeight = 0.20, ComfortWeight = 0.20, SecurityWeight = 0.15, LeisureWeight = 0.10;
    public const double ComfortUnitsAtFull = 40; // weekly units of goods (or of services) that count as full comfort
    public const int SecurityWeeksOfPay = 8; // savings of this many weeks' pay count as fully secure
    // ponytail: households weigh a coin saved as lasting this many weeks; tune if saving rates look wrong
    public const double SavingsHorizonWeeks = 12;

    // Sick people still do some work; output and pay scale from 25% at zero health to 100% when healthy.
    public static double Capacity(Agent citizen) => 0.25 + 0.75 * citizen.Health / 100.0;

    public static double WeeklyPay(Agent resident) =>
        SocietyLabor.TableWage.GetValueOrDefault(resident.FormerOccupation ?? resident.Occupation, 1000);

    // Savings (cash and deposits) in weeks of one's own pay, with log utility: the first weeks matter most.
    private static double SavingsSecurity(Agent resident, double cash) =>
        Math.Min(1, Math.Log(1 + Math.Max(0, cash) / WeeklyPay(resident)) / Math.Log(1 + SecurityWeeksOfPay));

    // Log utility: every extra unit adds less, but something, so spending rises with income.
    private static double ComfortOf(int units) => Math.Min(1, Math.Log(1 + units) / Math.Log(1 + ComfortUnitsAtFull));

    private static double Comfort(int goods, int services) => 0.5 * ComfortOf(goods) + 0.5 * ComfortOf(services);

    private static double Security(Agent resident, long cash) =>
        0.5 * (resident.EmployerId is not null || resident.OwnedBusinessId is not null ? 1 : 0)
        + 0.5 * SavingsSecurity(resident, cash);

    public static int QualityOfLife(Agent resident, int needsMet, bool overtime) => (int)Math.Round(100 * (
        NeedsWeight * needsMet / 2.0 + HealthWeight * resident.Health / 100.0
        + ComfortWeight * Comfort(resident.GoodsBoughtLastWeek, resident.ServicesBoughtLastWeek)
        + SecurityWeight * Security(resident, SocietyBank.Savings(resident)) + LeisureWeight * (overtime ? 0.5 : 1)));

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
            var firms = agents.Where(agent => agent.Role == "business").ToArray();
            foreach (var firm in firms.OrderBy(firm => firm.Specialty == "energy" ? 0 : 1).ThenBy(firm => firm.Id))
                Produce(world, nation, firm, SocietyLabor.Workers(world, firm), residents.Length);
            foreach (var clinic in firms.Where(firm => firm.Specialty == "clinic"))
            {
                var capacity = (int)(SocietyLabor.ServiceCapacity(world, clinic) / SocietyLabor.DaysPerWeek);
                society.VisitsServedToday[clinic.Id] = -capacity; // counts up to zero as visits are served
                Acquire(world, clinic, "medicine", Math.Max(0, capacity * 2 - clinic.Inventory["medicine"]), false);
            }

            var start = residents.Length == 0 ? 0 : world.Tick % residents.Length;
            for (var index = 0; index < residents.Length; index++)
            {
                var resident = residents[(index + start) % residents.Length];
                resident.Health = Math.Max(0, resident.Health - 1);
                // Overtime (offered by the employer, accepted by the worker) costs free time and, every other day, health.
                var overtime = SocietyLabor.WorksOvertime(world, resident);
                if (overtime)
                {
                    resident.OvertimeDays++;
                    if (world.Tick % 2 == 0)
                        resident.Health = Math.Max(0, resident.Health - 1);
                }
                if (SocietyFactory.Sample(world.Seed, $"{resident.Id}:{world.Tick}", "illness") < DailyIllnessChance)
                    resident.Health = Math.Max(0, resident.Health - 30);
                if (resident.Health < resident.HealthThreshold)
                    Visit(world, nation, resident, resident, true);
                var met = 0;
                foreach (var good in new[] { "food", "energy" })
                {
                    Acquire(world, resident, good, Math.Max(0, resident.TargetStock - resident.Inventory[good]), true);
                    if (resident.Inventory[good] > 0)
                    {
                        resident.Inventory[good]--;
                        society.ConsumedGoods[good]++;
                        nation.FulfilledNeedsLastCycle++;
                        met++;
                    }
                    else
                    {
                        nation.UnmetNeedsLastCycle++;
                        resident.Health = Math.Max(0, resident.Health - 1);
                    }
                }
                // Quality of life follows the day's outcomes, a quarter of the way at a time.
                var target = QualityOfLife(resident, met, overtime);
                resident.Wellbeing = Math.Clamp(resident.Wellbeing + (int)Math.Round((target - resident.Wellbeing) / 4.0), 0, 100);
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
            // The state bank is left alone: its cash is depositors' reserves.
            if (SocietyLabor.IsPlanned(nation))
                foreach (var firm in agents.Where(agent => agent.Role == "business" && !SocietyBank.IsBank(agent)))
                {
                    var payroll = SocietyLabor.Staff(world, firm).Sum(worker => worker.SalaryMinor);
                    if (firm.CashMinor > payroll * 4)
                        Transfer(firm, government, firm.CashMinor - payroll * 4);
                    else if (firm.CashMinor < payroll)
                        Transfer(government, firm, Math.Min(government.CashMinor, payroll - firm.CashMinor));
                }

            SocietyBank.Week(world, nation);
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

            Discretionary(world, nation, residents);
            // Retrospective support: a sharp fall in quality of life costs the leadership support, a sharp
            // rise wins it back, and deep misery withdraws it.
            foreach (var resident in residents)
            {
                var change = resident.Wellbeing - resident.LastWeekWellbeing;
                if (change <= -10 || resident.Wellbeing < 30)
                    resident.SupportsLeadership = false;
                else if (change >= 10)
                    resident.SupportsLeadership = true;
                resident.LastWeekWellbeing = resident.Wellbeing;
            }
            SocietyBank.Sweep(world, nation, residents);

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

    // Households buy household goods (tools) and entertainment one unit at a time while the next unit adds
    // more quality of life than the savings it uses up. Each kind saturates, so spending spreads across both
    // and grows with income (Engel's law) instead of following a fixed share.
    private static void Discretionary(World world, Nation nation, Agent[] residents)
    {
        var society = world.Society!;
        var shops = Sellers(world, nation, "tools").ToArray();
        var venues = SocietyLabor.Firms(world, nation.Id, "entertainment").OrderBy(venue => venue.PriceMinor).ThenBy(venue => venue.Id).ToArray();
        var seats = venues.ToDictionary(venue => venue.Id, venue => (int)SocietyLabor.ServiceCapacity(world, venue));
        var basePrice = SocietyLabor.SectorOf("entertainment").BasePrice;
        // Necessities come first: a week of food and energy at today's cheapest prices is set aside.
        long Cheapest(string good) => Sellers(world, nation, good).Select(seller => (long)seller.PriceMinor).DefaultIfEmpty(SocietyFactory.BasePrice(good)).First();
        var essentials = SocietyLabor.DaysPerWeek * (Cheapest("food") + Cheapest("energy"));
        foreach (var resident in residents)
        {
            int goods = 0, services = 0;
            var turnedAway = false;
            // Comfort gained from one more unit, less the security lost by spending its price.
            double Net(int have, long price) =>
                ComfortWeight * 0.5 * (ComfortOf(have + 1) - ComfortOf(have))
                - SavingsHorizonWeeks * SecurityWeight * 0.5
                * (SavingsSecurity(resident, SocietyBank.Savings(resident)) - SavingsSecurity(resident, SocietyBank.Savings(resident) - price));
            for (var round = 0; round < 2 * ComfortUnitsAtFull; round++)
            {
                var spendable = SocietyBank.Liquid(world, resident) - essentials; // savings in the bank can be spent too
                var shop = PickSeller(shops.Where(seller => seller.Inventory["tools"] > 0 && seller.PriceMinor <= spendable).ToArray(),
                    world.Seed, $"{resident.Id}:{world.Tick}:tools:{round}");
                var venue = PickSeller(venues.Where(seller => seats[seller.Id] > 0 && seller.PriceMinor <= spendable).ToArray(),
                    world.Seed, $"{resident.Id}:{world.Tick}:entertainment:{round}");
                var goodsGain = shop is null ? 0 : Net(goods, shop.PriceMinor);
                var serviceGain = venue is null ? 0 : Net(services, venue.PriceMinor);
                if (venue is null && spendable >= basePrice && Net(services, basePrice) > 0)
                    turnedAway = true;
                if (goodsGain <= 0 && serviceGain <= 0)
                    break;
                if (goodsGain >= serviceGain)
                {
                    SocietyBank.Draw(world, resident, shop!.PriceMinor);
                    Sell(world, resident, shop, shop.PriceMinor, true, 1);
                    shop.Inventory["tools"]--;
                    society.ConsumedGoods["tools"]++;
                    goods++;
                }
                else
                {
                    SocietyBank.Draw(world, resident, venue!.PriceMinor);
                    Sell(world, resident, venue, venue.PriceMinor, true, 1);
                    seats[venue.Id]--;
                    services++;
                }
            }
            resident.GoodsBoughtLastWeek = goods;
            resident.ServicesBoughtLastWeek = services;
            if (turnedAway)
                foreach (var full in venues)
                    society.TurnedAwayThisWeek[full.Id] = society.TurnedAwayThisWeek.GetValueOrDefault(full.Id) + 1;
        }
    }

    // Shoppers favour cheaper sellers without all going to the cheapest: location, habit and search costs
    // spread custom. Weight falls with the fourth power of the price ratio (10% dearer: 0.68, 50%: 0.20).
    public static Agent? PickSeller(Agent[] sellers, int seed, string key)
    {
        if (sellers.Length <= 1)
            return sellers.FirstOrDefault();
        var cheapest = sellers.Min(seller => seller.PriceMinor);
        var weights = sellers.Select(seller => Math.Pow(cheapest / (double)Math.Max(1, seller.PriceMinor), 4)).ToArray();
        var draw = SocietyFactory.Sample(seed, key, "seller") * weights.Sum();
        for (var index = 0; index < sellers.Length; index++)
        {
            draw -= weights[index];
            if (draw <= 0)
                return sellers[index];
        }
        return sellers[^1];
    }

    // Domestic sellers of a good, cheapest first.
    public static IEnumerable<Agent> Sellers(World world, Nation nation, string good) =>
        world.Agents.Values.Where(agent => agent.Role == "business" && agent.CountryId == nation.Id
                && agent.Specialty == good && agent.Inventory[good] > 0)
            .OrderBy(agent => agent.PriceMinor).ThenBy(agent => agent.Id);

    // Buys up to the wanted quantity from domestic sellers the buyer can afford. Firms buy inputs from the
    // cheapest seller; households start at a price-weighted pick and fall back to the cheapest.
    public static int Acquire(World world, Agent buyer, string good, int wanted, bool household)
    {
        var nation = world.Society!.Nations[buyer.CountryId];
        var bought = 0;
        var sellers = Sellers(world, nation, good).Where(seller => seller != buyer).ToArray();
        if (household && wanted > 0 && PickSeller(sellers, world.Seed, $"{buyer.Id}:{world.Tick}:{good}") is { } first)
            sellers = [first, .. sellers.Where(seller => seller != first)];
        foreach (var seller in sellers)
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

    // Land and resources, crises, productivity investment and schooling scale what each worker makes.
    public static double OutputMultiplier(World world, Nation nation, string good)
    {
        var multiplier = good switch
        {
            "food" => nation.FoodMultiplier,
            "energy" => nation.EnergyMultiplier,
            _ => 1
        };
        if (nation.CrisisUntilTick > world.Tick && nation.CrisisGood == good)
            multiplier *= 0.35;
        return multiplier * nation.Productivity * nation.EducationFactor;
    }

    private static void Produce(World world, Nation nation, Agent producer, Agent[] workers, int residents)
    {
        var sector = SocietyLabor.SectorOf(producer.Specialty!);
        if (sector.Good is not { } good)
            return;
        // Health drives output; wellbeing (morale) has a milder effect.
        var potential = (int)(workers.Sum(worker => SocietyLabor.DayEffort(world, worker) * (0.75 + 0.25 * worker.Wellbeing / 100.0) * Capacity(worker))
            * sector.DailyOutput * OutputMultiplier(world, nation, good));
        // Firms plan output toward two weeks of their own recent sales, below the three-week glut line.
        var stockLimit = Math.Max(residents / 4, 2 * producer.LastWeekUnitsSold);
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
            nation.GiniCash = Math.Round(Gini(citizens.Select(SocietyBank.Savings).ToArray()), 4);
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
