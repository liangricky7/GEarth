namespace GEarth.Core;

// One tick is one day. Daily: production, health, doctor visits and household shopping at the
// cheapest local seller. Weekly: banking (SocietyBank), labor market and payroll (SocietyLabor), public
// payroll, welfare, unemployment and sick benefit, clinic capitation, discretionary spending, the savings
// sweep and metrics.
public static class SocietyEconomy
{
    // ponytail: fixed economic constants; move to scenario config if experiments need to vary them
    public const int VisitHealthGain = 40;
    public const int MedicineHealthGain = 15; // medicine bought at the pharmacy for a minor illness
    public const int HealthDecayDays = 4; // health falls 1 point every this many days
    public const int SickBelow = 30; // below this, too sick to work (and unpaid)
    public const double DailyIllnessChance = 0.01;
    public const double HungerOutputFactor = 0.01; // someone who missed their last meal produces 99% less

    // Quality of life (stored as Wellbeing, 0-100) is scored from outcomes, each with diminishing returns:
    // needs met today, health, discretionary goods and services bought last week, security (a job or
    // business plus savings) and free time (overtime costs some). Good health adds up to 20 points; bad
    // health takes away up to 50, faster the lower it gets.
    public const double NeedsWeight = 0.35, HealthBonus = 0.20, SickPenalty = 0.50, ComfortWeight = 0.20, SecurityWeight = 0.15, LeisureWeight = 0.10;
    public const double ComfortUnitsAtFull = 40; // weekly units of goods (or of services) that count as full comfort
    public const int SecurityWeeksOfPay = 8; // savings of this many weeks' pay count as fully secure
    // ponytail: households weigh a coin saved as lasting this many weeks; tune if saving rates look wrong
    public const double SavingsHorizonWeeks = 12;

    public static bool TooSick(Agent person) => person.Health < SickBelow;

    // Sick people still do some work (until too sick to work at all); output scales from 25% at zero health
    // to 100% when healthy, and drops 99% after a missed meal until the next one.
    public static double Capacity(Agent citizen) => (0.25 + 0.75 * citizen.Health / 100.0) * (citizen.Hungry ? HungerOutputFactor : 1);

    // The week's pay for days actually worked (sick days are unpaid; a part-time student earns half), less food
    // already taken as pay in kind.
    public static long EarnedWage(Agent worker, long salary)
    {
        var earned = salary * (SocietyLabor.DaysPerWeek - Math.Min(SocietyLabor.DaysPerWeek, worker.SickDays)) / SocietyLabor.DaysPerWeek
            / (worker.PartTimeStudent && SocietyLabor.IsStudent(worker) ? 2 : 1);
        var inKind = Math.Min(earned, worker.InKindMinor);
        worker.InKindMinor -= inKind;
        return earned - inKind;
    }

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

    // Health's contribution in points: +20 at full health, about +13 at 70, +4 at 50, -11 at 30, -50 at zero.
    public static double HealthPoints(int health) =>
        100 * (HealthBonus * health / 100.0 - SickPenalty * Math.Pow((100 - health) / 100.0, 3));

    // Each part in points (they sum to the score before clamping), for the resident's own view.
    public static Dictionary<string, double> QolParts(Agent resident, int needsMet, bool overtime) => new()
    {
        ["needs"] = 100 * NeedsWeight * needsMet / 2.0,
        ["health"] = HealthPoints(resident.Health),
        ["comfort"] = 100 * ComfortWeight * Comfort(resident.GoodsBoughtLastWeek, resident.ServicesBoughtLastWeek),
        ["security"] = 100 * SecurityWeight * Security(resident, SocietyBank.Savings(resident)),
        ["free_time"] = 100 * LeisureWeight * (overtime ? 0.5 : 1)
    };

    // Each part as a share of its best (0-1), which founders compare against the gap threshold.
    public static Dictionary<string, double> QolShares(Agent resident, bool overtime)
    {
        var parts = QolParts(resident, resident.NeedsMet, overtime);
        return new()
        {
            ["needs"] = parts["needs"] / (100 * NeedsWeight),
            ["health"] = (parts["health"] + 100 * SickPenalty) / (100 * (HealthBonus + SickPenalty)),
            ["comfort"] = parts["comfort"] / (100 * ComfortWeight),
            ["security"] = parts["security"] / (100 * SecurityWeight)
        };
    }

    public static int QualityOfLife(Agent resident, int needsMet, bool overtime) =>
        Math.Clamp((int)Math.Round(QolParts(resident, needsMet, overtime).Values.Sum()), 0, 100);

    public static void Day(World world)
    {
        var society = world.Society!;
        society.VisitsServedToday.Clear();
        foreach (var nation in society.Nations.Values)
        {
            var agents = world.InCountry(nation.Id).Where(agent => agent.Role != "defunct").ToArray();
            var residents = agents.Where(SocietyLabor.IsResident).ToArray();
            var firms = agents.Where(agent => agent.Role == "business").ToArray();
            foreach (var firm in firms.OrderBy(firm => firm.Specialty == "energy" ? 0 : 1).ThenBy(firm => firm.Id))
                Produce(world, nation, firm, SocietyLabor.Workers(world, firm), residents.Length);
            foreach (var school in firms.Where(firm => firm.Specialty == "school"))
                SocietyResources.SchoolDay(world, school);
            foreach (var clinic in firms.Where(firm => firm.Specialty == "clinic"))
            {
                var capacity = SocietyPlanning.Cap(world, nation, clinic, (int)(SocietyLabor.ServiceCapacity(world, clinic) / SocietyLabor.DaysPerWeek * (1 + PhaseContracts.Boost(nation, "clinic_capacity"))), 1);
                society.VisitsServedToday[clinic.Id] = -capacity; // counts up to zero as visits are served
                AcquireInput(world, clinic, "medicine", Math.Max(0, capacity * 2 - clinic.Inventory["medicine"]));
            }
            SocietyTrade.ImportFood(world, nation, residents.Length);
            // Farm workers eat first, from their employer's stock: bought if they can pay, otherwise as pay in kind.
            foreach (var farm in firms.Where(firm => firm.Specialty == "food"))
                foreach (var worker in SocietyLabor.Workers(world, farm).Where(worker => worker.Inventory["food"] == 0))
                {
                    if (farm.Inventory["food"] == 0)
                        break;
                    if (SocietyBank.Liquid(world, worker) >= farm.PriceMinor)
                        Sell(world, worker, farm, farm.PriceMinor, true, 1);
                    else
                    {
                        worker.InKindMinor += farm.PriceMinor;
                        society.PeriodUnitsSold[farm.Id] = society.PeriodUnitsSold.GetValueOrDefault(farm.Id) + 1;
                    }
                    farm.Inventory["food"]--;
                    worker.Inventory["food"]++;
                }

            var start = residents.Length == 0 ? 0 : world.Tick % residents.Length;
            var foodOnShelves = Offers(world, nation, "food").Any();
            for (var index = 0; index < residents.Length; index++)
            {
                var resident = residents[(index + start) % residents.Length];
                if (SocietyMetrics.Employed(resident) && resident.WorkEffort > 0 && !TooSick(resident)
                    && !SocietyLabor.OnArrearsStrike(resident) && (!SocietyLabor.IsStudent(resident) || resident.PartTimeStudent))
                    resident.WeekWorkDays += (resident.PartTimeStudent && SocietyLabor.IsStudent(resident) ? 0.5 : 1)
                        * (SocietyLabor.WorksOvertime(world, resident) ? 1.25 : 1);
                if (world.Tick % HealthDecayDays == 0)
                    resident.Health = Math.Max(0, resident.Health - 1);
                if (SocietyLabor.IsStudent(resident) && world.Tick >= resident.StudentUntilTick)
                    SocietySchool.Graduate(world, resident);
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
                // Below their threshold residents see a doctor; someone too sick to work who cannot pay is seen free
                // (the clinic's capitation covers it); with no visit possible they buy medicine at the pharmacy.
                if (resident.Health < resident.HealthThreshold
                    && !Visit(world, nation, resident, resident, true)
                    && !(TooSick(resident) && Visit(world, nation, resident, null, false)))
                    TakeMedicine(world, resident);
                if (TooSick(resident))
                    resident.SickDays++;
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
                        if (good == "food")
                            resident.Hungry = false;
                    }
                    else
                    {
                        nation.UnmetNeedsLastCycle++;
                        resident.Health = Math.Max(0, resident.Health - 1);
                        if (good == "food")
                        {
                            resident.Hungry = resident.HungryThisWeek = true;
                            resident.PricedOutThisWeek |= foodOnShelves;
                        }
                    }
                }
                if (resident.EmployerId is not null || resident.OwnedBusinessId is not null)
                {
                    nation.WorkerDays++;
                    if (resident.Hungry)
                        nation.HungryDays++;
                }
                resident.NeedsMet = met;
                // Quality of life follows the day's outcomes, a quarter of the way at a time.
                var target = QualityOfLife(resident, met, overtime);
                resident.Wellbeing = Math.Clamp(resident.Wellbeing + (int)Math.Round((target - resident.Wellbeing) / 4.0), 0, 100);
            }
        }
    }

    public static void Week(World world)
    {
        var society = world.Society!;
        SocietyTrade.ReviewCurrencies(world);
        // Each country's agents are listed before any country's weekly changes, as one snapshot.
        var byCountry = society.Nations.Keys.ToDictionary(id => id,
            id => world.InCountry(id).Where(agent => agent.Role != "defunct").ToArray());
        foreach (var nation in society.Nations.Values)
        {
            var agents = byCountry[nation.Id];
            var residents = agents.Where(SocietyLabor.IsResident).ToArray();
            var government = world.Agents[nation.Id + "_government"];
            // Daily activity (visits, purchases) counts toward the week, so deltas run from last week's totals.
            long Since(string key, long total) => total - nation.WeekStartTotals.GetValueOrDefault(key);

            // A planned state short of a week's spending borrows from its state bank before wages go unpaid.
            if (SocietyLabor.IsPlanned(nation) && SocietyBank.Liquid(world, government) < government.LastWeekOutMinor)
                SocietyBank.BorrowForTreasury(world, nation, government.LastWeekOutMinor - SocietyBank.Liquid(world, government));
            // The state owns every firm in a planned economy, and the school everywhere: it takes surplus above four
            // weeks of payroll and covers shortfalls (the contractor settles in SocietyInfrastructure). The state bank
            // is left alone: its cash is depositors' reserves.
            foreach (var firm in agents.Where(agent => agent.Role == "business" && !SocietyBank.IsBank(agent) && agent.OwnerId is null
                         && agent.Specialty != PhaseContracts.Construction && (SocietyLabor.IsPlanned(nation) || agent.Specialty == "school")))
                {
                    var payroll = SocietyLabor.Staff(world, firm).Sum(worker => worker.SalaryMinor);
                    var operations = payroll + SocietyResources.InputWorkingCapital(world, nation, firm);
                    if (SocietyBank.Liquid(world, firm) > operations * 4)
                        Transfer(world, firm, government, SocietyBank.Liquid(world, firm) - operations * 4, "state_surplus");
                    else if (SocietyBank.Liquid(world, firm) < operations)
                        Transfer(world, government, firm, Math.Min(SocietyBank.Liquid(world, government), operations - SocietyBank.Liquid(world, firm)), "state_subsidy");
                }

            SocietyBank.Week(world, nation);
            SocietyLabor.Week(world, nation, government, residents);
            PublicPayroll(world, nation, government, residents.Where(resident => resident.EmployerId == government.Id).ToArray());

            var citizens = residents.Where(resident => resident.Role == "citizen").ToArray();
            var welfare = citizens.Length == 0 ? 0 : Math.Min(SocietyBank.Liquid(world, government), (long)nation.WelfareMinor * citizens.Length) / citizens.Length;
            foreach (var citizen in citizens)
            {
                Transfer(world, government, citizen, welfare, "welfare");
                nation.WelfarePaidMinor += welfare;
            }
            // Unemployment benefit (students only with a stipend), and the same rate as sick pay for days too ill to work.
            foreach (var jobless in citizens.Where(citizen => citizen.EmployerId is null && (!SocietyLabor.IsStudent(citizen) || nation.StudentStipend)))
            {
                var benefit = Math.Min(SocietyBank.Liquid(world, government), nation.UnemploymentBenefitMinor);
                Transfer(world, government, jobless, benefit, "unemployment_benefit");
                nation.UnemploymentPaidMinor += benefit;
            }
            foreach (var sick in residents.Where(resident => resident.EmployerId is not null && resident.SickDays > 0))
            {
                var pay = Math.Min(SocietyBank.Liquid(world, government), (long)nation.UnemploymentBenefitMinor * Math.Min(SocietyLabor.DaysPerWeek, sick.SickDays) / SocietyLabor.DaysPerWeek);
                Transfer(world, government, sick, pay, "sick_pay");
                nation.UnemploymentPaidMinor += pay;
            }

            // Public healthcare: the weekly services budget pays clinics a capitation for every resident, shared by
            // the doctors working, so visits stay cheap and the sickest are seen free.
            var clinics = SocietyLabor.Firms(world, nation.Id, "clinic").Select(clinic => (Clinic: clinic, Doctors: SocietyLabor.Workers(world, clinic).Length))
                .Where(item => item.Doctors > 0).ToArray();
            var capitation = clinics.Length == 0 ? 0 : Math.Min(SocietyBank.Liquid(world, government), (long)nation.ServiceBudgetPerResidentMinor * residents.Length);
            foreach (var (clinic, doctors) in clinics)
            {
                var share = capitation * doctors / clinics.Sum(item => item.Doctors);
                Sell(world, government, clinic, share, kind: "capitation");
                nation.CapitationPaidMinor += share;
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
                capitation_minor = Since("capitation", nation.CapitationPaidMinor),
                food_subsidy_minor = Since("food_subsidy", nation.FoodSubsidyPaidMinor),
                nation.Unemployed,
                hungry_residents = residents.Count(resident => resident.HungryThisWeek),
                priced_out_residents = residents.Count(resident => resident.PricedOutThisWeek),
                sick_residents = residents.Count(resident => resident.SickDays > 0),
                students = residents.Count(SocietyLabor.IsStudent),
                government_income_minor = government.WeekInMinor, government_spending_minor = government.WeekOutMinor,
                funded_welfare_per_person_minor = welfare,
                production_units = nation.ProducedUnits,
                currency_index = Math.Round(nation.CurrencyIndex, 3),
                period = "week"
            });
            nation.WeekStartTotals = new()
            {
                ["wages"] = nation.WagesPaidMinor, ["sales_tax"] = nation.SalesTaxCollectedMinor,
                ["profit_tax"] = nation.ProfitTaxCollectedMinor, ["visits"] = nation.DoctorVisits,
                ["public_visits"] = nation.PublicDoctorVisits, ["capitation"] = nation.CapitationPaidMinor,
                ["food_subsidy"] = nation.FoodSubsidyPaidMinor
            };
            // Last week's hunger, for the government's view; the weekly flags start over.
            nation.HungryResidentsLastWeek = residents.Count(resident => resident.HungryThisWeek);
            nation.PricedOutLastWeek = residents.Count(resident => resident.PricedOutThisWeek);
            nation.OutputLostToHungerPct = nation.WorkerDays == 0 ? 0 : Math.Round(100 * (1 - HungerOutputFactor) * nation.HungryDays / nation.WorkerDays, 1);
            (nation.HungryDays, nation.WorkerDays) = (0, 0);
        }
        foreach (var resident in world.Agents.Values.Where(SocietyLabor.IsResident))
        {
            var nation = society.Nations[resident.CountryId];
            var basket = SocietyLabor.LivingCost(world, nation) * (1 + nation.SalesTaxBasisPoints / 10000.0);
            var baseBasket = SocietyLabor.DaysPerWeek * (SocietyLabor.SectorOf("food").BasePrice + SocietyLabor.SectorOf("energy").BasePrice);
            resident.LastWeekDisposableIncomeMinor = resident.WeekDisposableIncomeMinor;
            resident.LastWeekRealIncomeMinor = basket <= 0 ? 0 : (long)Math.Round(resident.WeekDisposableIncomeMinor * baseBasket / basket);
            resident.LastWeekWorkDays = resident.WeekWorkDays;
            resident.LastWeekIncomeTick = world.Tick;
            resident.WeekDisposableIncomeMinor = 0;
            resident.WeekWorkDays = 0;
        }
        UpdateMetrics(world);
        foreach (var nation in society.Nations.Values)
        {
            nation.UnmetNeedsLastCycle = 0;
            nation.FulfilledNeedsLastCycle = 0;
        }
        foreach (var resident in world.Agents.Values.Where(SocietyLabor.IsResident))
            (resident.SickDays, resident.HungryThisWeek, resident.PricedOutThisWeek) = (0, false, false);
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
        var shops = Offers(world, nation, "tools").ToArray();
        var venues = SocietyLabor.Firms(world, nation.Id, "entertainment").OrderBy(venue => venue.PriceMinor).ThenBy(venue => venue.Id).ToArray();
        var seats = venues.ToDictionary(venue => venue.Id, venue => SocietyPlanning.Cap(world, nation, venue, (int)SocietyLabor.ServiceCapacity(world, venue), SocietyLabor.DaysPerWeek));
        foreach (var venue in venues)
            SocietyResources.PrepareServices(world, venue, seats[venue.Id]);
        var basePrice = SocietyLabor.SectorOf("entertainment").BasePrice;
        // Necessities come first: a week of food and energy at today's cheapest prices is set aside.
        long Cheapest(string good) => Offers(world, nation, good).Select(offer => (long)offer.PriceMinor).DefaultIfEmpty(SocietyFactory.BasePrice(good)).First();
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
                var shop = PickOffer(shops.Where(offer => offer.Seller.Inventory["tools"] > 0 && offer.PriceMinor <= spendable).ToArray(),
                    world.Seed, $"{resident.Id}:{world.Tick}:tools:{round}", SocietyFactory.BasePrice("tools"));
                var venue = PickSeller(venues.Where(seller => seats[seller.Id] > 0 && SocietyResources.CanPowerSession(world, seller) && seller.PriceMinor <= spendable).ToArray(),
                    world.Seed, $"{resident.Id}:{world.Tick}:entertainment:{round}", basePrice);
                var goodsGain = shop is null ? 0 : Net(goods, shop.PriceMinor);
                var serviceGain = venue is null ? 0 : Net(services, venue.PriceMinor);
                if (venue is null && spendable >= basePrice && Net(services, basePrice) > 0)
                    turnedAway = true;
                if (goodsGain <= 0 && serviceGain <= 0)
                    break;
                if (goodsGain >= serviceGain)
                {
                    using var auditGroup = RunAudit.Group(world, "tools", 1, shop!.PriceMinor);
                    Sell(world, resident, shop.Seller, shop.PriceMinor, true, 1, shop.Liquidation ? "liquidation_sale" : "purchase");
                    shop.Seller.Inventory["tools"]--;
                    if (shop.Liquidation) SocietyEstates.Settle(world, shop.Seller);
                    society.ConsumedGoods["tools"]++;
                    goods++;
                }
                else
                {
                    if (!SocietyResources.PowerSession(world, venue!))
                        break;
                    Sell(world, resident, venue!, venue!.PriceMinor, true, 1);
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
    // Above a reference of PriceLeanAbove x the base price, shoppers also lean hard away from each seller
    // (another PriceLeanPower power of reference/price), so undercutting a dear market wins most of the custom.
    // ponytail: 1.3 and 8 are arbitrary; tune after runs
    public const double PriceLeanAbove = 1.3;
    public const int PriceLeanPower = 8;

    public static double ShopperWeight(int price, int cheapest, int basePrice)
    {
        var weight = Math.Pow(cheapest / (double)Math.Max(1, price), 4);
        var reference = PriceLeanAbove * basePrice;
        return basePrice > 0 && price > reference ? weight * Math.Pow(reference / price, PriceLeanPower) : weight;
    }

    public static Agent? PickSeller(Agent[] sellers, int seed, string key, int basePrice = 0)
    {
        if (sellers.Length <= 1)
            return sellers.FirstOrDefault();
        var cheapest = sellers.Min(seller => seller.PriceMinor);
        return Pick(sellers, sellers.Select(seller => ShopperWeight(seller.PriceMinor, cheapest, basePrice)).ToArray(), seed, key);
    }

    private static DomesticOffer? PickOffer(DomesticOffer[] offers, int seed, string key, int basePrice)
    {
        if (offers.Length <= 1)
            return offers.FirstOrDefault();
        var cheapest = offers.Min(offer => offer.PriceMinor);
        return Pick(offers, offers.Select(offer => ShopperWeight(offer.PriceMinor, cheapest, basePrice)).ToArray(), seed, key);
    }

    private static T Pick<T>(T[] items, double[] weights, int seed, string key)
    {
        var draw = SocietyFactory.Sample(seed, key, "seller") * weights.Sum();
        for (var index = 0; index < items.Length; index++)
        {
            draw -= weights[index];
            if (draw <= 0)
                return items[index];
        }
        return items[^1];
    }

    // What an owner can expect to sell in a week at a new price if competitors hold theirs: last week's
    // market split by the shopper weights, capped by what the firm has and can make (or serve) in a week.
    public static int ExpectedUnits(World world, Agent firm, int price)
    {
        var spec = SocietyLabor.SectorOf(firm.Specialty!);
        var rivals = SocietyLabor.Firms(world, firm.CountryId, spec.Id).Where(other => other != firm).ToArray();
        var market = firm.LastWeekUnitsSold + rivals.Sum(other => other.LastWeekUnitsSold);
        var cheapest = rivals.Select(other => other.PriceMinor).Append(price).Min();
        var own = ShopperWeight(price, cheapest, spec.BasePrice);
        var share = own / (own + rivals.Sum(other => ShopperWeight(other.PriceMinor, cheapest, spec.BasePrice)));
        var capacity = SocietyLabor.ServiceCapacity(world, firm) * (spec.Good is { } good
            ? OutputMultiplier(world, world.Society!.Nations[firm.CountryId], good) : 1) + (spec.Good is { } made ? firm.Inventory[made] : 0);
        return (int)Math.Round(Math.Min(market * share, capacity));
    }

    // Domestic sellers of a good, cheapest first.
    public static IEnumerable<Agent> Sellers(World world, Nation nation, string good) =>
        SocietyLabor.Firms(world, nation.Id, SocietyResources.SectorForGood(good).Id).Where(agent => agent.Inventory[good] > 0)
            .OrderBy(agent => agent.PriceMinor).ThenBy(agent => agent.Id);

    public static IEnumerable<DomesticOffer> Offers(World world, Nation nation, string good) =>
        Sellers(world, nation, good).Select(seller => new DomesticOffer(seller, seller.PriceMinor))
            .Concat(SocietyEstates.Sellers(world, nation.Id, good)
                .Select(seller => new DomesticOffer(seller, SocietyEstates.Price(good), true)))
            .OrderBy(offer => offer.PriceMinor).ThenBy(offer => offer.Seller.Id, StringComparer.Ordinal);

    // Buys up to the wanted quantity from domestic sellers the buyer can afford. Firms buy inputs from the
    // cheapest seller; households start at a price-weighted pick and fall back to the cheapest. A food subsidy
    // has the treasury pay its share of a household's food, so the farm still receives the full price.
    public static int Acquire(World world, Agent buyer, string good, int wanted, bool household)
    {
        if (household && PhaseContracts.RawMaterials.Contains(good))
            return 0; // households demand the finished goods, never raw industrial inputs
        var nation = world.Society!.Nations[buyer.CountryId];
        var bought = 0;
        var offers = Offers(world, nation, good).Where(offer => offer.Seller != buyer).ToArray();
        if (household && wanted > 0 && PickOffer(offers, world.Seed, $"{buyer.Id}:{world.Tick}:{good}", SocietyFactory.BasePrice(good)) is { } first)
            offers = [first, .. offers.Where(offer => offer != first)];
        var subsidyPct = household && good == "food" ? nation.FoodSubsidyPct : 0;
        var government = world.Agents[nation.Id + "_government"];
        foreach (var offer in offers)
        {
            var seller = offer.Seller;
            if (bought >= wanted)
                break;
            var share = (long)offer.PriceMinor * subsidyPct / 100;
            var own = offer.PriceMinor - share;
            var units = (int)Math.Min(Math.Min(wanted - bought, seller.Inventory[good]), SocietyBank.Liquid(world, buyer) / Math.Max(1, own));
            if (share > 0)
                units = (int)Math.Min(units, SocietyBank.Liquid(world, government) / share);
            if (units <= 0)
            {
                // A household that could not afford this seller's price but could pay one a fifth lower is a sale
                // lost to price (someone with almost no money is not won back by a cheaper price).
                if (household && SocietyBank.Liquid(world, buyer) < own && SocietyBank.Liquid(world, buyer) * 5 >= own * 4)
                    world.Society.TurnedAwayThisWeek[seller.Id] = world.Society.TurnedAwayThisWeek.GetValueOrDefault(seller.Id) + 1;
                else if (household && SocietyBank.Liquid(world, buyer) * 5 < own * 4)
                    world.Society.IncomeConstrainedThisWeek[seller.Id] = world.Society.IncomeConstrainedThisWeek.GetValueOrDefault(seller.Id) + 1;
                continue;
            }
            using var auditGroup = RunAudit.Group(world, good, units, offer.PriceMinor);
            Sell(world, buyer, seller, units * own, household, units, offer.Liquidation ? "liquidation_sale" : "purchase");
            if (share > 0)
            {
                Sell(world, government, seller, units * share, kind: "food_subsidy");
                nation.FoodSubsidyPaidMinor += units * share;
            }
            seller.Inventory[good] -= units;
            buyer.Inventory[good] += units;
            bought += units;
            if (offer.Liquidation)
                SocietyEstates.Settle(world, seller);
        }
        return bought;
    }

    // Firms buy inputs at home; with no domestic seller left they import from the cheapest foreign one.
    public static void AcquireInput(World world, Agent buyer, string good, int wanted)
    {
        if (wanted <= 0)
            return;
        var bought = Acquire(world, buyer, good, wanted, false);
        if (bought < wanted && !Offers(world, world.Society!.Nations[buyer.CountryId], good).Any(offer => offer.Seller != buyer))
            SocietyTrade.AutoImport(world, buyer, good, (wanted - bought) * 14);
    }

    // Medicine from the pharmacy for a minor illness, when no doctor's visit is possible.
    private static void TakeMedicine(World world, Agent resident)
    {
        if (Acquire(world, resident, "medicine", 1, true) == 0)
            return;
        resident.Inventory["medicine"]--;
        world.Society!.ConsumedGoods["medicine"]++;
        resident.Health = Math.Min(100, resident.Health + MedicineHealthGain);
    }

    // A sale between agents. Household purchases carry sales tax inside the price; business
    // revenue, costs and units sold accumulate for the weekly payroll, profit tax and price review.
    public static void Sell(World world, Agent buyer, Agent seller, long amount, bool household = false, int units = 0,
        string kind = "purchase")
    {
        using var auditGroup = RunAudit.Group(world, world.CurrentAuditContext?.Good ?? seller.Specialty, units > 0 ? units : null,
            world.CurrentAuditContext?.UnitPriceMinor ?? seller.PriceMinor);
        var society = world.Society!;
        var nation = society.Nations[seller.CountryId];
        var tax = household ? amount * nation.SalesTaxBasisPoints / 10000 : 0;
        Transfer(world, buyer, seller, amount - tax, kind);
        Transfer(world, buyer, world.Agents[seller.CountryId + "_government"], tax, "sales_tax");
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
        SocietyLabor.Firms(world, nation.Id, "clinic")
            .Where(agent => world.Society!.VisitsServedToday.GetValueOrDefault(agent.Id) < 0 && agent.Inventory["medicine"] > 0)
            .OrderBy(agent => agent.PriceMinor).ThenBy(agent => agent.Id).FirstOrDefault();

    // A doctor visit: a clinic's doctor sees the patient and uses one unit of medicine as supplies. With no
    // payer the visit is free public care, already paid for by the clinic's capitation.
    public static bool Visit(World world, Nation nation, Agent patient, Agent? payer, bool household)
    {
        var society = world.Society!;
        var clinic = OpenClinic(world, nation);
        if (clinic is null)
        {
            foreach (var busy in SocietyLabor.Firms(world, nation.Id, "clinic"))
                society.TurnedAwayThisWeek[busy.Id] = society.TurnedAwayThisWeek.GetValueOrDefault(busy.Id) + 1;
            return false;
        }
        if (payer is not null && SocietyBank.Liquid(world, payer) < clinic.PriceMinor)
            return false;
        if (payer is null)
            nation.PublicDoctorVisits++;
        else
            Sell(world, payer, clinic, clinic.PriceMinor, household, 1);
        clinic.Inventory["medicine"]--;
        society.ConsumedGoods["medicine"]++;
        society.VisitsServedToday[clinic.Id]++;
        patient.Health = Math.Min(100, patient.Health + VisitHealthGain);
        nation.DoctorVisits++;
        return true;
    }

    // Public salaries (soldiers, the planner) come from the government's pay scale, not effort.
    private static void PublicPayroll(World world, Nation nation, Agent government, Agent[] staff) =>
        PayOwed(world, nation, government, government, staff.Where(worker => worker.WorkEffort > 0) // strikers forfeit the week's pay
            .Select(worker => (worker, SocietyLabor.OnArrearsStrike(worker) ? 0 : EarnedWage(worker, worker.SalaryMinor))).ToList());

    // A public or state employer pays this week's wages and any arrears; when short, everyone gets the same
    // fraction and the rest is owed. Two weeks owed and the worker strikes until paid.
    public static long PayOwed(World world, Nation nation, Agent government, Agent employer, List<(Agent Worker, long Wage)> dues)
    {
        var bill = dues.Sum(due => due.Worker.ArrearsMinor + due.Wage);
        var share = bill == 0 ? 1 : Math.Min(1, SocietyBank.Liquid(world, employer) / (double)bill);
        var paid = 0L;
        foreach (var (worker, wage) in dues)
        {
            var owed = worker.ArrearsMinor + wage;
            var pay = (long)(owed * share);
            paid += PayWage(world, nation, government, employer, worker, pay);
            worker.ArrearsMinor = owed - pay;
            worker.ArrearsWeeks = worker.ArrearsMinor > 0 ? worker.ArrearsWeeks + 1 : 0;
        }
        return paid;
    }

    public static long PayWage(World world, Nation nation, Agent government, Agent employer, Agent worker, long wage)
    {
        using var auditGroup = RunAudit.Group(world);
        Transfer(world, employer, worker, wage, "wage");
        var tax = wage * nation.TaxBasisPoints / 10000;
        Transfer(world, worker, government, tax, "income_tax");
        nation.TaxesCollectedMinor += tax;
        nation.WagesPaidMinor += wage;
        return wage;
    }

    // Land and resources, crises and productivity investment scale what each worker makes (graduates add
    // their own boost through their day's effort).
    public static double OutputMultiplier(World world, Nation nation, string good)
    {
        var multiplier = good switch
        {
            "food" => nation.FoodMultiplier,
            "ore" or "fuel" => PhaseContracts.Endowment(nation, good),
            _ => 1
        };
        if (nation.CrisisUntilTick > world.Tick && nation.CrisisGood == good)
            multiplier *= 0.35;
        multiplier *= 1 + PhaseContracts.Boost(nation, good);
        return multiplier * nation.Productivity;
    }

    private static void Produce(World world, Nation nation, Agent producer, Agent[] workers, int residents)
    {
        var sector = SocietyLabor.SectorOf(producer.Specialty!);
        if (sector.Good is not { } good)
            return;
        // Health drives output; wellbeing (morale) has a milder effect.
        var potential = (int)(workers.Sum(worker => SocietyLabor.DayEffort(world, worker) * (0.75 + 0.25 * worker.Wellbeing / 100.0) * Capacity(worker) * worker.Weights.Productivity)
            * sector.DailyOutput * OutputMultiplier(world, nation, good));
        // Firms plan output toward two weeks of their own recent sales, below the three-week glut line; a
        // planned economy's state firms make their share of the plan's quota instead, whatever is selling.
        var stockLimit = Math.Max(residents / 4, 2 * producer.LastWeekUnitsSold);
        var quantity = SocietyPlanning.Quota(world, nation, producer) is { } quota
            ? Math.Min(potential, quota)
            : Math.Min(potential, Math.Max(0, stockLimit - producer.Inventory[good]));
        if (quantity <= 0)
            return;
        quantity = SocietyResources.SupplyProduction(world, producer, quantity);
        producer.Inventory[good] += quantity;
        nation.ProducedUnits += quantity;
        world.Society!.ProducedGoods[good] += quantity;
    }

    // War spending goes to the soldiers who fight it, shared equally; with no soldiers it stays in the treasury.
    public static void PaySoldiers(World world, Nation nation, Agent government, long amount)
    {
        var soldiers = world.InCountry(nation.Id).Where(person => person.EmployerId == government.Id && person.Occupation == "soldier")
            .OrderBy(person => person.Id).ToArray();
        amount = Math.Min(amount, SocietyBank.Liquid(world, government));
        if (soldiers.Length == 0 || amount <= 0)
            return;
        for (var index = 0; index < soldiers.Length; index++)
            PayWage(world, nation, government, government, soldiers[index], amount / soldiers.Length + (index < amount % soldiers.Length ? 1 : 0));
    }

    private static void ConflictCosts(World world, Nation nation, Agent government, Agent[] businesses, int conflicts)
    {
        PaySoldiers(world, nation, government, 1000L * conflicts);
        foreach (var business in businesses.Where(business => SocietyLabor.SectorOf(business.Specialty!).Good is not null))
        {
            var good = SocietyLabor.SectorOf(business.Specialty!).Good!;
            var destroyed = Math.Min(business.Inventory[good], Math.Max(1, nation.Population / 40) * conflicts);
            business.Inventory[good] -= destroyed;
            nation.LostUnits += destroyed;
            world.Society!.LostGoods[good] += destroyed;
        }
        world.Emit("conflict_costs", "rule", [nation.Id], new { conflicts, nation.LostUnits });
    }

    public static void UpdateMetrics(World world)
    {
        foreach (var nation in world.Society!.Nations.Values)
        {
            var agents = world.InCountry(nation.Id);
            var citizens = agents.Where(SocietyLabor.IsResident).ToArray();
            var support = citizens.Length == 0 ? 0 : citizens.Count(citizen => citizen.SupportsLeadership) / (double)citizens.Length;
            var wellbeing = citizens.Length == 0 ? 0 : citizens.Average(citizen => citizen.Wellbeing) / 100;
            nation.Approval = Math.Round(support * 0.5 + wellbeing * 0.5, 3);
            var shares = citizens.Select(citizen => QolShares(citizen, SocietyLabor.WorksOvertime(world, citizen))).ToArray();
            nation.QolParts = shares.Length == 0 ? new() : shares[0].Keys.ToDictionary(part => part,
                part => Math.Round(shares.Average(share => share[part]), 3));
            SocietyMetrics.Labor(world, nation);
            SocietyMetrics.Distribution(world, nation);
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

    public static void Transfer(World world, Agent from, Agent to, long amount, string kind = "transfer")
    {
        if (amount < 0 || amount > SocietyBank.PaymentCapacity(world, from, to))
            throw new InvalidOperationException("Unfunded transfer");
        var repayment = kind is "loan_repayment" or "student_repayment" or "treasury_repayment"
            or "owner_debt_repayment" or "loan_recovery" or "loan_guarantee" or "student_loan_guarantee";
        var depositCapacity = SocietyBank.PaymentCapacity(world, from, to) - from.CashMinor;
        var cash = repayment ? Math.Max(0, amount - depositCapacity) : Math.Min(amount, from.CashMinor);
        from.CashMinor -= cash;
        to.CashMinor += cash;
        SocietyBank.TransferDeposit(world, from, to, amount - cash, kind);
        if (kind is not ("deposit" or "withdrawal")) // moving one's own savings is neither income nor spending
        {
            from.WeekOutMinor += amount;
            to.WeekInMinor += amount;
        }
        if (SocietyLabor.IsResident(to) && kind is "wage" or "owner_draw" or "dividend" or "welfare" or "unemployment_benefit" or "sick_pay")
            to.WeekDisposableIncomeMinor += amount;
        if (SocietyLabor.IsResident(from) && kind == "income_tax")
            from.WeekDisposableIncomeMinor -= amount;
        // Cash journals remain reconstructible; deposit claims are captured in the bank ledger.
        RecordFlow(world, from.Id, to.Id, cash, kind);
    }

    // Verbose runs sum every money movement per giver, recipient and kind for the history file.
    // Shipment escrow uses the cargo ID as the giver or recipient.
    public static void RecordFlow(World world, string from, string to, long amount, string kind)
    {
        RunAudit.Transaction(world, from, to, amount, kind);
        if (world.Flows is not { } flows || amount == 0)
            return;
        var (total, count) = flows.GetValueOrDefault((from, to, kind));
        flows[(from, to, kind)] = (total + amount, count + 1);
    }

    public static void AssertConservation(World world)
    {
        var society = world.Society!;
        var money = world.Agents.Values.Sum(agent => agent.CashMinor) + society.Cargo.Sum(cargo => cargo.EscrowMinor);
        if (money != world.InitialCashMinor || world.Agents.Values.Any(agent => agent.CashMinor < 0 || agent.DepositMinor < 0
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
