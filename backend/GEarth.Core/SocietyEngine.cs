using System.Text.Json.Nodes;

namespace GEarth.Core;

public static class SocietyEngine
{
    // A decision made on a view older than a week no longer fits the world and expires.
    public const int DecisionMaxAgeDays = 7;

    // Numbers come from the engine constants so the model is never told a stale rule.
    public static readonly string RulesNote = $"One tick is one day. Daily: everyone uses 1 food and 1 energy, bought automatically up to your target stock, mostly from cheaper local sellers (a seller 10% dearer than the cheapest gets about two-thirds of the custom, 50% dearer about a fifth; above {SocietyEconomy.PriceLeanAbove.ToString(System.Globalization.CultureInfo.InvariantCulture)}x the base price shoppers lean hard away, and a seller 10% dearer than a rival there gets about a third of the rival's custom). Health falls 1 every {SocietyEconomy.HealthDecayDays} days, more with illness (a 1% daily chance of losing 30), overtime or unmet needs. Below your health threshold you automatically see the cheapest open clinic if you can pay (+{SocietyEconomy.VisitHealthGain} health; visit_doctor goes now); with no visit possible you buy medicine at the pharmacy (+{SocietyEconomy.MedicineHealthGain}). Below health {SocietyEconomy.SickBelow} you are too sick to work: no output and no pay for those days (sick pay equals the unemployment benefit where one is paid), and if you cannot pay, the clinic sees you free. Lower health lowers your output, and a missed meal cuts your output by 99% until you eat again; farm workers eat first from their employer's stock (paid from cash or from the next wage). quality_of_life (0-100) is scored from outcomes with diminishing returns: needs met (up to 35), health (+20 when healthy, falling ever faster to -50 when very sick), household goods and entertainment bought last week (20), security from a job or business plus savings (15) and free time (10); quality_of_life_parts shows yours. Weekly: households automatically buy household goods (tools) and entertainment while the next unit adds more quality of life than the savings it costs. Employees receive their agreed salary; effort 1 is normal hours; effort 2 means you accept overtime when your employer offers it (overtime_offered), working a quarter more hours for 1.5x pay on those hours at the cost of free time and some health, and it does nothing when no overtime is offered; effort 0 is a strike and forfeits that week's pay. Firms short of cash lay off staff first and cut the remaining crew's pay 10% only when that is not enough; every {SocietyLabor.IndexationDays} days firms with positive average operating cash surplus across up to four completed payroll weeks raise pay and wage offers by {(int)(SocietyLabor.IndexationShare * 100)}% of the rise in the cost of a week of food and energy. Unemployed residents are hired automatically by firms with openings in their occupation; after {SocietyLabor.RelatedJobAfterDays} days anyone can take an unskilled job (skilled workers keep their qualification and return when a matching job pays 10% more), and doctors and researchers can swap; employed workers switch for at least 10% more pay (for any raise for {SocietyLabor.CourseDays} days after seek_better_job). School: a citizen can enroll_school for a fixed tuition, full time for {SocietyLabor.CourseDays} days without pay (an employee leaves their job; a stipend if the government offers one) or, keeping their job, part time for {SocietyLabor.PartTimeCourseDays} days at half hours and half pay; a student who cannot pay the tuition borrows it from the bank (government-guaranteed, repaid weekly after the course). Graduates produce {(int)Math.Round((SocietyLabor.GraduateOutputBoost - 1) * 100)}% more, are hired first and are paid {SocietyLabor.GraduatePayPremiumPct}% more; each school option shows its cost, the raise and the weeks to pay it back. Several firms compete in most sectors. A citizen with savings, or with a bank loan for all but a down payment, can start any business (it costs {SocietyLabor.StartupWeeks} weeks of its workers' pay, {SocietyLabor.ServiceStartupWeeks} for services) where the market shows a gap: no firm yet (unless it is a mine, well or pharma whose goods import for less than a local firm would need to charge), or incumbents charging on average at least {SocietyLabor.EntryMarkup.ToString(System.Globalization.CultureInfo.InvariantCulture)}x what a unit costs to make while buyers go without (priced out, a tenth of the market's buyers turned away, under a week of stock, or a service near capacity) and someone out of work could staff it; a clinic, pharma or school needs someone qualified to work in it. New firms launch 10% below the cheapest competitor; entrepreneurs are paid from operating cash surplus after tax (up to their former trade's wage, then half the rest; nothing in a losing week), retaining next week's staff payroll, receive as dividends any business cash above {SocietyLabor.DividendBufferWeeks} weeks of payroll, and set price, wage offer and headcount; each price option shows the weekly sales and margin it should bring if competitors hold their prices. A business that cannot cover costs for {SocietyLabor.DistressWeeksToClose} weeks, or a small firm with at most one employee whose owner goes hungry with no pay for {SocietyLabor.OwnerGivesUpWeeks} weeks while its cash plus deposits cannot cover another week of staff pay plus the owner's former table wage, closes, repays what it can of its bank loan, and retains its account as a liquidation estate: late cargo and discounted stock sales repay existing guaranteed debt first, then pay the owner. Prices adjust weekly in proportion to shortages or gluts, up to {SocietyLabor.MaxPriceStepPct}%, and come down when more than a tenth of shoppers could not afford them; an owner sees their sales, customers lost, operating cash surplus (input purchases expensed when paid), competitors' prices and sales, and what their last price change did; in a shortage, farms, power, pharma and clinics raise wage offers as fast (up to twice the usual pay). Firms with no domestic supplier import inputs automatically, and when food shelves run low farms import food that lands cheaper than their price. {SocietyResources.Note}Banking: each country has one bank, run by its owner (by the state in planned economies). Each week residents target two weeks of their pay as cash and private firms two weeks of payroll. Excess cash is deposited; automatic cash top-ups share spare reserves and never invoke insurance. Deposits earn the bank's deposit rate while capital is positive; bank_savings with keep_in_bank=false withdraws everything and keeps savings as cash until you choose to bank again. Bank loans create matching deposits for businesses (request_loan), founders and students; domestic deposit payments do not use reserves, cross-country payments settle in reserves, and principal repayment retires money. Loans are repaid weekly over 26 weeks at the loan rate when funded. Requests are decided weekly against the owner's lending standard (founder down payment strict 50%, normal 25%, loose 10%; installments at most 10/20/35% of weekly revenue), the reserve rule (reserves of 10% of deposits), capital of at least 10% of outstanding loans, and banker capacity (10 loans each, owner included). Three missed installments close the borrowing firm; the bank recovers what it can, the owner's savings cover the rest, and anything still unpaid becomes the owner's personal debt, collected weekly. When withdrawals exceed reserves the bank cannot pay them all: government deposit insurance (by government type, up to a limit per depositor) pays the insured part and the bank lends nothing that week. A bank owing depositors more than its reserves and loans are worth is taken over by the state and the owner loses their stake. State continuation guarantees existing nominal domestic deposits without new reserves or recapitalization; nonpositive capital suspends interest credits and prevents new lending. Bank operating wages still require its own reserve cash. Migration requires full settlement of personal savings; unavailable settlement blocks migration and leaves the claim intact. The bank's owner sets deposit and loan rates (deposit rates only up to what the loan book earns), lending standard, headcount (another banker only once loans fill 80% of what the staff can service) and wage, and receives dividends only from equity above their capital. Household prices include sales tax, wages carry income tax and firms pay profit tax. Governments pay public salaries on a fixed scale (soldiers, a planned economy's planner), welfare, unemployment and sick benefit (outside planned economies, welfare and the unemployment benefit rise by the full rise in the cost of a week of food and energy every {SocietyLabor.IndexationDays} days, and are never cut by it); a budget lever set to its current level is a hold; when the treasury cannot pay, public and state-firm wages are owed as arrears (two weeks owed and the worker strikes until paid) and welfare, benefits and capitation stop, and a planned state first borrows from its state bank. Each week they pay clinics a capitation from the services budget (fund_services, per resident) so visits stay cheap; budget_last_week shows income and spending and last_policy what the last change did. Income tax is set at a budget turn every {SocietyActions.BudgetTurnDays} days (adjust_tax with a new whole-percent rate within {SocietyActions.MaxTaxStepPoints} points of the current one). Food, energy, clinics and pharma never stay without workers: an idle firm takes back someone from the trade, and the government can grant the startup cost of a new firm (subsidize_startup; automatic after {SocietyLabor.GrantAfterDays} days). Governments can also subsidize food when residents go hungry with food on the shelves, tuition, a student stipend and a hiring bonus for graduates. {SocietyInfrastructure.Note}In a planned economy a planner sets each sector's weekly output quota every {SocietyPlanning.PlanningWeeks} weeks (set_plan, any numbers); state firms make their share at prices fixed by decree, so a quota too low leaves shortages and one too high leaves unsold stock. Residents whose quality of life falls sharply withdraw support from the leadership. {SocietyTrade.Note}Each import option shows what a unit lands for and the cheapest local price; an order whose seller has sold some stock meanwhile fills what is left. Imports require cash-backed goods escrow and transit; domestic freight fees and tariffs can be paid with deposits. Failed deliveries refund goods escrow, while freight and tariffs remain sunk costs. Conflict (declare_conflict, offered only after a trade or peace offer between the two expired or was refused) costs 5000 upfront plus 1000 per week, paid to the soldiers; trade between the two stops and both lose stock every week until peace; it never lowers tension. Decisions apply only within {DecisionMaxAgeDays} days of the observation they were made on.";

    private static void DropNulls(JsonNode? node)
    {
        if (node is JsonObject fields)
            foreach (var (key, value) in fields.ToArray())
            {
                if (value is null)
                    fields.Remove(key);
                else
                    DropNulls(value);
            }
        else if (node is JsonArray items)
            foreach (var item in items)
                DropNulls(item);
    }

    public static void Advance(World world)
    {
        world.Tick++;
        using var routine = RunAudit.Context(world, new(RoutineId: $"{world.Id}:day:{world.Tick}"));
        SocietyDiplomacy.Advance(world);
        SocietyTrade.Advance(world);
        SocietyEconomy.Day(world);
        SocietyInfrastructure.Day(world);
        if (world.Tick % SocietyLabor.DaysPerWeek == 0)
            SocietyEconomy.Week(world);
        SocietyEconomy.AssertConservation(world);
    }

    public static DecisionJob Observe(World world, string agentId, DateTimeOffset now)
    {
        var agent = world.Agents[agentId];
        var nation = world.Society!.Nations[agent.CountryId];
        var profile = Governments.Get(nation.GovernmentId);
        var neighbors = world.Society.Relations.Values.Where(relation => relation.First == nation.Id || relation.Second == nation.Id)
            .OrderByDescending(relation => relation.ProposedBy is not null).ThenByDescending(relation => relation.AtWar)
            .ThenByDescending(relation => relation.Tension).Take(4)
            .Select(relation => new
            {
                country_id = relation.First == nation.Id ? relation.Second : relation.First,
                relation.Tension, relation.TradeAgreement, relation.AtWar, relation.ProposedBy, relation.ProposalKind
            }).ToArray();
        var actions = SocietyActions.Available(world, agent);
        var offers = actions.Where(action => action.ActionType == "import_goods").Select(action =>
        {
            var seller = world.Agents[action.Params["seller_id"]!.GetValue<string>()];
            var good = action.Params["good"]!.GetValue<string>();
            var buyer = action.Params["business_id"] is { } businessId ? world.Agents[businessId.GetValue<string>()] : agent;
            var quantity = action.Params["quantity"]!.GetValue<int>();
            var path = SocietyTrade.Path(world, seller.LocationId, buyer.LocationId, action.Params["objective"]!.GetValue<string>())!;
            return new { seller_id = seller.Id, good, stock = SocietyTrade.ExportableStock(world, seller, good),
                price_minor = Math.Round(SocietyTrade.ImportUnitPrice(world, buyer, seller), 1),
                path.TravelTicks, quoted_quantity = quantity,
                shipping_minor = (long)Math.Ceiling(SocietyResources.ShippingCost(world, buyer, path, quantity) / (double)quantity),
                shipping_total_minor = SocietyResources.ShippingCost(world, buyer, path, quantity) };
        }).DistinctBy(offer => (offer.seller_id, offer.good)).ToArray(); // one row per seller; sizes are in the menu
        // Weekly labor summaries are already in the market block, so they are not repeated as events.
        var observedEvents = world.Events.Where(item => item.EntityIds.Contains(nation.Id)
            && item.EventType is not ("action_accepted" or "action_rejected" or "economy_cycle" or "labor_market" or "bank_week")).TakeLast(2).ToArray();
        var evidence = new[] { $"country:{nation.Id}:tick{world.Tick}" }
            .Concat(observedEvents.Select(item => item.EventId)).ToArray();
        var firm = agent.OwnedBusinessId is { } owned ? world.Agents[owned] : agent.Role == "business" ? agent : null;
        var runsBank = firm is not null && SocietyBank.IsBank(firm);
        var loan = SocietyBank.LoanOf(world, firm?.Id ?? agent.Id);
        var firmGood = firm is null ? null : SocietyLabor.SectorOf(firm.Specialty!).Good;
        var rivals = firm is null || runsBank ? [] : SocietyLabor.Firms(world, nation.Id, firm.Specialty!).Where(other => other != firm).ToArray();
        var sectorSales = firm is null ? 0 : firm.LastWeekUnitsSold + rivals.Sum(other => other.LastWeekUnitsSold);
        var spec = firm is null ? null : SocietyLabor.SectorOf(firm.Specialty!);
        var business = firm is null ? null : new
        {
            firm.Id, sector = firm.Specialty, status = firm.Role, firm.CashMinor,
            deposit_minor = firm.DepositMinor > 0 ? firm.DepositMinor : (long?)null, firm.PriceMinor,
            price_limits_minor = spec!.BasePrice > 0 ? new[] { spec.BasePrice / 2, spec.BasePrice * 3 } : null,
            wage_offer_minor_per_week = firm.WageOfferMinor, staff = SocietyLabor.Staff(world, firm).Length,
            firm.TargetHeadcount, stock = firmGood is null ? (int?)null : firm.Inventory[firmGood],
            // Operating cash surplus expenses purchased inputs immediately; it is not accrual profit.
            units_sold_last_week = firm.LastWeekUnitsSold,
            replacement_cost_per_unit_minor = runsBank || spec!.DailyOutput == 0 ? (long?)null : (long)Math.Ceiling(SocietyLabor.UnitCost(world, nation, firm)),
            pending_wage_catch_up_price_reference = firm.WageIndexReference > 0 ? firm.WageIndexReference : (double?)null,
            customers_lost_last_week = firm.LastWeekTurnedAway > 0 ? firm.LastWeekTurnedAway : (int?)null,
            unable_to_afford_even_twenty_percent_less_last_week = firm.LastWeekIncomeConstrained > 0 ? firm.LastWeekIncomeConstrained : (int?)null,
            share_of_sector_sales_pct = sectorSales == 0 ? (int?)null : 100 * firm.LastWeekUnitsSold / sectorSales,
            last_week_income_minor = firm.LastWeekInMinor, last_week_spending_minor = firm.LastWeekOutMinor,
            last_week_operating_cash_surplus_minor = firm.LastWeekOperatingProfitMinor,
            operating_cash_surplus_average_minor = firm.OperatingSurplusWeeksMinor.Count == 0
                ? (long?)null : (long)firm.OperatingSurplusWeeksMinor.Average(),
            operating_cash_surplus_observed_weeks = firm.OperatingSurplusWeeksMinor.Count,
            weeks_unable_to_cover_costs = firm.DistressWeeks,
            competitors = runsBank ? null : rivals.OrderBy(other => other.PriceMinor).Take(5)
                .Select(other => new { price_minor = other.PriceMinor, units_sold_last_week = other.LastWeekUnitsSold }),
            last_price_change = firm.PriceChangeTick < 0 ? null : new
            {
                change_pct = firm.PriceChangePct, tick = firm.PriceChangeTick,
                price_before_minor = firm.PriceBeforePriceChangeMinor, operating_cash_surplus_before_minor = firm.ProfitBeforePriceChangeMinor,
                operating_cash_surplus_last_week_minor = firm.LastWeekOperatingProfitMinor,
                units_sold_week_before = firm.UnitsBeforePriceChange, units_sold_last_week = firm.LastWeekUnitsSold
            }
        };
        // Each role sees what its choices depend on; prompts stay small so the model keeps up with the weekly economy.
        var resident = SocietyLabor.IsResident(agent);
        var governing = agent.Role == "government";
        var market = agent.Role == "media" || agent.Role == "business" && runsBank ? null : new
        {
            cheapest_price_minor = SocietyLabor.Sectors.Where(sector => sector.BasePrice > 0).ToDictionary(sector => sector.Id,
                sector => SocietyLabor.Firms(world, nation.Id, sector.Id).Select(other => (int?)other.PriceMinor).Min()),
            best_wage_offer_minor_per_week = resident || governing ? SocietyLabor.Sectors.GroupBy(sector => sector.Occupation).ToDictionary(group => group.Key,
                group => group.SelectMany(sector => SocietyLabor.Firms(world, nation.Id, sector.Id)).Select(other => (long?)other.WageOfferMinor).Max()) : null,
            unemployed_residents = nation.Unemployed, nation.UnemploymentBenefitMinor,
            // Founders read the country's quality of life by part (0-1) and the sectors where the market shows a gap.
            quality_of_life_by_part = resident || governing ? nation.QolParts : null,
            startup_cost_minor = agent.Role == "citizen"
                ? SocietyLabor.StartableSectors().ToDictionary(sector => sector, SocietyLabor.StartupCost) : null,
            business_opportunities = agent.Role == "citizen"
                ? SocietyLabor.StartableSectors().Where(sector => !SocietyLabor.Firms(world, nation.Id, sector).Any()
                    || SocietyLabor.HasGap(world, nation, sector)).ToArray() : null,
            school = agent.Role == "citizen" && SocietySchool.OpenSchool(world, nation.Id) is { } school
                ? new { tuition_minor = school.PriceMinor, you_pay_minor = SocietySchool.OwnShare(nation, school),
                    course_days = SocietyLabor.CourseDays, part_time_course_days = SocietyLabor.PartTimeCourseDays,
                    graduate_pay_premium_pct = SocietyLabor.GraduatePayPremiumPct,
                    stipend_while_studying_minor = nation.StudentStipend ? nation.UnemploymentBenefitMinor : 0 } : null
        };
        var treasury = world.Agents[nation.Id + "_government"].CashMinor;
        object country = governing
            ? new
            {
                nation.Id, government = profile.Name, profile.PolicyDelay, profile.PublicOwnershipPercent,
                income_tax_percent = Math.Round(nation.TaxBasisPoints / 100.0, 2),
                next_budget_tick = Math.Max(world.Tick, nation.TaxChangedTick + SocietyActions.BudgetTurnDays),
                nation.WelfareMinor, nation.ServiceBudgetPerResidentMinor,
                nation.Approval, nation.Population, nation.Stocks, nation.UnmetNeedsLastCycle, treasury_minor = treasury,
                treasury_weeks_of_spending = agent.LastWeekOutMinor > 0 ? Math.Round(treasury / (double)agent.LastWeekOutMinor, 1) : (double?)null,
                treasury_weeks_left_at_this_rate = agent.LastWeekOutMinor > agent.LastWeekInMinor
                    ? Math.Round(treasury / (double)(agent.LastWeekOutMinor - agent.LastWeekInMinor), 1) : (double?)null,
                if_the_treasury_runs_out = "public and state-firm wages go unpaid and are owed (workers strike after two weeks unpaid); welfare, benefits and clinic capitation stop",
                public_wage_arrears_minor = world.InCountry(nation.Id).Sum(person => person.ArrearsMinor) is var arrears and > 0 ? arrears : (long?)null,
                state_bank_debt_minor = SocietyBank.Debt(world, agent) is var debt and > 0 ? debt : (long?)null,
                hungry_residents_last_week = nation.HungryResidentsLastWeek,
                priced_out_of_food_last_week = nation.PricedOutLastWeek,
                output_lost_to_hunger_pct = nation.OutputLostToHungerPct,
                essential_sectors_without_workers = nation.NeededSince.Keys.Order().ToArray(),
                resources = SocietyResources.View(world, nation),
                currency = SocietyTrade.CurrencyView(nation),
                labor_and_distribution = SocietyMetrics.View(nation),
                nation.FoodSubsidyPct, nation.TuitionSubsidyPct, nation.StudentStipend, nation.HiringBonusMinor,
                infrastructure = SocietyInfrastructure.View(world, nation),
                last_policy = nation.LastPolicyAction is null ? null : new
                {
                    action = nation.LastPolicyAction, @params = nation.LastPolicyParams, enacted_tick = nation.LastPolicyTick,
                    weekly_net_before_minor = nation.NetBeforeLastPolicyMinor,
                    weekly_net_now_minor = agent.LastWeekInMinor - agent.LastWeekOutMinor
                },
                nation.Productivity, nation.SalesTaxBasisPoints, nation.ProfitTaxBasisPoints,
                nation.CrisisGood, crisis_until_tick = nation.CrisisGood is null ? (int?)null : nation.CrisisUntilTick,
                pending_policy = world.Society.Policies.FirstOrDefault(policy => policy.CountryId == nation.Id)?.ActionType,
                government_authority_available = SocietyDiplomacy.HasAuthority(world, nation),
                authority_asset_proxy = profile.Id == "monarchy" ? "business cash plus deposits"
                    : profile.Id == "dictatorship" ? "bank settlement reserves" : null
            }
            : new
            {
                nation.Id, government = profile.Name, nation.Approval, nation.Stocks, nation.UnmetNeedsLastCycle,
                nation.TaxBasisPoints, nation.WelfareMinor, treasury_minor = treasury, nation.CrisisGood
            };
        var observation = Wire.Node(new
        {
            world_stage = 2, tick = world.Tick,
            self = new
            {
                agent.Id, agent.Role, agent.Occupation, specialty = agent.Role == "business" ? agent.Specialty : null,
                agent.Goal, agent.Traits, agent.Weights, agent.CashMinor,
                inventory = resident ? agent.Inventory.Where(item => item.Value > 0).ToDictionary() : null,
                deposit_minor = agent.DepositMinor > 0 ? agent.DepositMinor : (long?)null,
                keeps_savings_in_bank = resident && !agent.KeepsSavingsInBank ? false : (bool?)null,
                work_effort = resident ? agent.WorkEffort : (int?)null, target_stock = resident ? agent.TargetStock : (int?)null,
                quality_of_life = resident ? agent.Wellbeing : (int?)null,
                quality_of_life_parts = resident ? SocietyEconomy.QolParts(agent, agent.NeedsMet, SocietyLabor.WorksOvertime(world, agent))
                    .ToDictionary(part => part.Key, part => Math.Round(part.Value, 1)) : null,
                health = resident ? agent.Health : (int?)null,
                too_sick_to_work = resident && SocietyEconomy.TooSick(agent) ? true : (bool?)null,
                hungry = agent.Hungry ? true : (bool?)null,
                student_until_tick = SocietyLabor.IsStudent(agent) ? agent.StudentUntilTick : (int?)null,
                part_time_student = agent.PartTimeStudent && SocietyLabor.IsStudent(agent) ? true : (bool?)null,
                wages_owed_to_you_minor = agent.ArrearsMinor > 0 ? agent.ArrearsMinor : (long?)null,
                graduated = agent.Graduated ? true : (bool?)null,
                seeking_better_job_until_tick = agent.SeekingUntilTick >= world.Tick ? agent.SeekingUntilTick : (int?)null,
                health_threshold = resident ? agent.HealthThreshold : (int?)null,
                supports_leadership = resident ? agent.SupportsLeadership : (bool?)null,
                memories = agent.Memories.TakeLast(3).Select(memory => memory.Length > 100 ? memory[..100] : memory),
                employer_id = agent.EmployerId,
                salary_minor_per_week = agent.EmployerId is null ? (long?)null : agent.SalaryMinor,
                disposable_income_last_week_minor = resident ? agent.LastWeekDisposableIncomeMinor : (long?)null,
                real_disposable_income_last_week_minor = resident ? agent.LastWeekRealIncomeMinor : (long?)null,
                worked_days_last_week = resident ? agent.LastWeekWorkDays : (double?)null,
                overtime_offered = agent.EmployerId is { } employer && world.Agents[employer].OvertimeOffered ? true : (bool?)null,
                unemployed_days = agent.EmployerId is null && agent.UnemployedSinceTick >= 0 ? world.Tick - agent.UnemployedSinceTick : (int?)null
            },
            business, market,
            plan = agent.Role == "planner" ? SocietyPlanning.View(world, nation) : null,
            bank = resident || runsBank ? SocietyBank.View(world, SocietyBank.BankOf(world, nation.Id), runsBank) : null,
            loan = loan is null ? null : new
            {
                loan.Kind, loan.Status, loan.PrincipalMinor, principal_left_minor = loan.Status == "active" ? loan.PrincipalLeftMinor : (long?)null,
                weekly_payment_minor = loan.Status == "active" ? loan.WeeklyPrincipalMinor + loan.WeeklyInterestMinor : (long?)null,
                missed_payments = loan.MissedPayments > 0 ? loan.MissedPayments : (int?)null
            },
            budget_last_week = governing
                ? new { income_minor = agent.LastWeekInMinor, spending_minor = agent.LastWeekOutMinor,
                    net_minor = agent.LastWeekInMinor - agent.LastWeekOutMinor } : null,
            country,
            neighbors = governing ? neighbors : null,
            import_offers = offers.Length > 0 ? offers : null,
            public_events = observedEvents.Select(item => new { item.EventType, item.Payload }),
            // The rules are identical for every agent; the worker sends them as a shared, cacheable system prompt.
            note = RulesNote
        });
        DropNulls(observation);
        return new DecisionJob(Guid.NewGuid().ToString("N"), world.Id, agent.Id, world.Tick,
            now.AddSeconds(65), new Dictionary<string, int> { [agent.Id] = agent.Version }, evidence, observation, actions);
    }

    public static DecisionRecord Apply(World world, DecisionJob job, DecisionResult result, DateTimeOffset now)
    {
        if (world.Decisions.TryGetValue(job.RequestId, out var existing))
            return existing;
        var code = Validate(world, job, result, now);
        var accepted = code == "OK";
        // Where the chosen option sat in the (shuffled) menu the agent saw, to check for position bias.
        var choiceIndex = result.Action is { Params: not null } chosen
            ? Array.FindIndex(job.Candidates, candidate => SocietyActions.Matches(candidate, chosen.ActionType, chosen.Params)) : -1;
        var compactJob = job with { Candidates = [] };
        var record = new DecisionRecord(compactJob, result, accepted, code);
        world.Decisions[job.RequestId] = record;
        while (world.Decisions.Count > 256)
            world.Decisions.Remove(world.Decisions.Keys.First());
        var society = world.Society!;
        if (!accepted)
            society.RejectedDecisionCount++;
        if (result.Source == "rule_fallback")
            society.FallbackDecisionCount++;
        if (accepted && result.Source == "model")
            society.ModelDecisionCount++;
        if (accepted && result.Source == "rule")
            society.RuleDecisionCount++;
        if (result.LatencyMs > 0)
        {
            society.LatenciesMs.Add(result.LatencyMs);
            if (society.LatenciesMs.Count > 200)
                society.LatenciesMs.RemoveAt(0);
        }
        // The full observation stays only in the recent-decisions window; each agent keeps a slim copy.
        if (world.Agents.TryGetValue(job.AgentId, out var decidingAgent))
            decidingAgent.LastDecision = record with { Job = compactJob with { Observation = new JsonObject() } };
        var decisionEvent = world.Emit(accepted ? "action_accepted" : "action_rejected",
            result.Source is "model" or "rule" or "rule_fallback" ? result.Source : "rule",
            [job.AgentId], new
            {
                decision_id = job.RequestId, code, action = result.Action, result.Model, result.Backend,
                // A lever left where it was is recorded as chosen but counted as a hold.
                unchanged_lever = accepted && world.Agents[job.AgentId].Role == "government"
                    && SocietyDiplomacy.Unchanged(world.Society!.Nations[world.Agents[job.AgentId].CountryId], result.Action) ? true : (bool?)null,
                role_at_decision = job.Observation["self"]?["role"]?.GetValue<string>() ?? world.Agents[job.AgentId].Role,
                choice_index = choiceIndex >= 0 ? choiceIndex : (int?)null, menu_size = job.Candidates.Length,
                result.LatencyMs, result.Error, result.CoordinatorLatencyMs,
                result.ErrorCategory, result.ErrorHttpStatus, result_request_id = result.RequestId,
                result.ErrorCode, result.ErrorDetail, result.ErrorField, result.ValidationRetries,
                applied_at = now, observation_tick = job.ObservationTick
            });
        RunAudit.Result(world, job, result, accepted, code, decisionEvent.EventId);
        if (!accepted)
            return record;
        var agent = world.Agents[job.AgentId];
        using var auditContext = RunAudit.Context(world, new(DecisionId: job.RequestId, EventId: decisionEvent.EventId));
        // Government effects are captured when the policy actually enacts (which may be delayed).
        using var effects = agent.Role == "government" ? null
            : RunAudit.Effects(world, agent, result.Action, job.RequestId, decisionEvent.EventId);
        Execute(world, agent, result.Action, decisionEvent.EventId);
        agent.LastDecisionTick = world.Tick;
        agent.LastRequestId = job.RequestId;
        if (result.Source == "model")
        {
            agent.ModelDecisions++;
            agent.LastModelDecisionAt = now;
        }
        agent.Version++;
        agent.Remember($"T{world.Tick}: {result.Action.ActionType}: {result.Action.Reason}");
        SocietyEconomy.AssertConservation(world);
        return record;
    }

    // A concurrent buyer can consume a quoted stock before this model returns; an import fills what is left,
    // and an order with nothing left to fill (or a household purchase) gets one fresh menu within the deadline.
    public static DecisionJob? Refresh(World world, DecisionRecord record, DateTimeOffset now)
    {
        if (record.Accepted || record.Code != "CURRENT_PRECONDITION_FAILED" || record.Result.Source != "model"
            || record.Job.RefreshAttempt != 0 || record.Result.Action.ActionType is not ("import_goods" or "buy_goods")
            || record.Job.Deadline <= now.AddSeconds(5)
            || !world.Agents.TryGetValue(record.Job.AgentId, out var agent) || !SocietyLabor.DecidesForItself(agent)
            || record.Job.Observation["self"]?["role"]?.GetValue<string>() != agent.Role)
            return null;
        return Observe(world, record.Job.AgentId, now) with { Deadline = record.Job.Deadline, RefreshAttempt = 1 };
    }

    private static string Validate(World world, DecisionJob job, DecisionResult result, DateTimeOffset now)
    {
        if (job.RunId != world.Id || job.RequestId != result.RequestId || !world.Agents.TryGetValue(job.AgentId, out var agent))
            return "UNKNOWN_ENTITY";
        if (agent.LastRequestId == job.RequestId)
            return "DUPLICATE_REQUEST";
        if (job.Dependencies.GetValueOrDefault(agent.Id, -1) != agent.Version)
            return "STALE_AGENT_INTENT";
        if (result.Source is not ("model" or "rule" or "rule_fallback"))
            return "INVALID_SOURCE";
        if (job.ObservationTick > world.Tick || now > job.Deadline || world.Tick - job.ObservationTick > DecisionMaxAgeDays)
            return "EXPIRED_PROPOSAL";
        var action = result.Action;
        if (action is null || action.Params is null || string.IsNullOrWhiteSpace(action.Reason)
            || action.Reason.Length > 240 || action.EvidenceIds is null || action.EvidenceIds.Length > 8)
            return "INVALID_SCHEMA";
        if (action.EvidenceIds.Distinct().Count() != action.EvidenceIds.Length
            || action.EvidenceIds.Any(evidence => !job.EvidenceIds.Contains(evidence)))
            return "UNKNOWN_EVIDENCE";
        if (!job.Candidates.Any(candidate => SocietyActions.Matches(candidate, action.ActionType, action.Params)))
            return "UNAVAILABLE_ACTION";
        if (action.ActionType == "publish" && agent.Role == "media")
            return "OK";
        if (agent.Role == "entrepreneur" && action.Params.ContainsKey("business_id"))
            return OwnerActionValid(world, agent, action) ? "OK" : "CURRENT_PRECONDITION_FAILED";
        if (action.ActionType == "start_business")
        {
            var loan = action.Params["loan_minor"]?.GetValue<long>() ?? 0;
            return SocietyLabor.CanStart(world, agent, action.Params["sector"]!.GetValue<string>(), loan)
                && (loan == 0 || SocietyBank.LoanOf(world, agent.Id) is null) ? "OK" : "CURRENT_PRECONDITION_FAILED";
        }
        // The cost and payback shown with a school option move with savings and pay; only eligibility must still hold.
        if (action.ActionType == "enroll_school")
            return SocietySchool.Offer(world, agent, action.Params["part_time"]?.GetValue<bool>() == true) is not null
                ? "OK" : "CURRENT_PRECONDITION_FAILED";
        if (action.ActionType is "pay" or "buy_goods" && agent.Role != "government")
            return SocietyPayments.StillValid(world, agent, action.Params) ? "OK" : "CURRENT_PRECONDITION_FAILED";
        if (action.ActionType == "migrate" && agent.Role == "citizen")
        {
            var destination = action.Params["country_id"]!.GetValue<string>();
            return CanMigrate(world, agent, destination)
                ? "OK" : "CURRENT_PRECONDITION_FAILED";
        }
        if (action.ActionType == "import_goods" && agent.Role == "business")
            return ImportQuantity(world, agent, action.Params) > 0 ? "OK" : "CURRENT_PRECONDITION_FAILED";
        if (!SocietyActions.Available(world, agent).Any(candidate => SocietyActions.Matches(candidate, action.ActionType, action.Params)))
            return "CURRENT_PRECONDITION_FAILED";
        return "OK";
    }

    // Owner choices act on the owned business; prices and wages move relative to their current level.
    private static bool OwnerActionValid(World world, Agent owner, ActionProposal action)
    {
        var parameters = action.Params;
        if (parameters["business_id"]!.GetValue<string>() != owner.OwnedBusinessId
            || !world.Agents.TryGetValue(owner.OwnedBusinessId!, out var firm) || firm.Role != "business")
            return false;
        if (action.ActionType == "request_loan")
            return SocietyBank.LoanOf(world, firm.Id) is null;
        return action.ActionType != "import_goods" || ImportQuantity(world, firm, parameters) > 0;
    }

    // An order fills as much of the chosen quantity as the seller still has and the buyer can still pay for.
    private static int ImportQuantity(World world, Agent buyer, JsonObject parameters) =>
        SocietyTrade.Affordable(world, buyer, world.Agents[parameters["seller_id"]!.GetValue<string>()],
            parameters["good"]!.GetValue<string>(), parameters["quantity"]!.GetValue<int>());

    private static void Execute(World world, Agent agent, ActionProposal action, string cause)
    {
        var parameters = action.Params;
        if (parameters["business_id"]?.GetValue<string>() is { } businessId)
        {
            var firm = world.Agents[businessId];
            var spec = SocietyLabor.SectorOf(firm.Specialty!);
            switch (action.ActionType)
            {
                case "set_price":
                    // Remember the change and last week's sales, so the owner later sees what it did.
                    firm.PriceChangePct = parameters["change_pct"]!.GetValue<int>();
                    (firm.PriceChangeTick, firm.UnitsBeforePriceChange) = (world.Tick, firm.LastWeekUnitsSold);
                    firm.PriceBeforePriceChangeMinor = firm.PriceMinor;
                    firm.ProfitBeforePriceChangeMinor = firm.LastWeekOperatingProfitMinor;
                    firm.PriceMinor = SocietyLabor.ClampPrice(spec, firm.PriceMinor * (100 + firm.PriceChangePct) / 100);
                    return;
                case "set_wage":
                    var change = parameters["change_pct"]!.GetValue<int>();
                    var table = SocietyLabor.TableWage[spec.Occupation];
                    firm.WageOfferMinor = Math.Clamp(firm.WageOfferMinor * (100 + change) / 100, table / 2, table * 3);
                    foreach (var worker in SocietyLabor.Staff(world, firm))
                        worker.SalaryMinor = worker.SalaryMinor * (100 + change) / 100;
                    return;
                case "set_headcount":
                    firm.TargetHeadcount = Math.Max(0, firm.TargetHeadcount + parameters["change"]!.GetValue<int>());
                    return;
                case "import_goods":
                    SocietyTrade.Dispatch(world, firm, world.Agents[parameters["seller_id"]!.GetValue<string>()],
                        parameters["good"]!.GetValue<string>(), ImportQuantity(world, firm, parameters),
                        parameters["objective"]!.GetValue<string>(), cause);
                    return;
                case "request_loan":
                    SocietyBank.Request(world, firm, parameters["amount_minor"]!.GetValue<long>(), null, 0, cause);
                    return;
                case "set_deposit_rate":
                    firm.DepositRatePct = parameters["rate_pct"]!.GetValue<int>();
                    return;
                case "set_loan_rate":
                    firm.LoanRatePct = parameters["rate_pct"]!.GetValue<int>();
                    return;
                case "set_lending_standard":
                    firm.LendingStandard = parameters["standard"]!.GetValue<string>();
                    return;
            }
        }
        if (agent.Role == "government" && action.ActionType != "hold")
        {
            if (!SocietyDiplomacy.Unchanged(world.Society!.Nations[agent.CountryId], action))
                SocietyDiplomacy.Propose(world, agent, action, cause);
            return;
        }
        switch (action.ActionType)
        {
            case "household_policy":
                agent.TargetStock = parameters["target_stock"]!.GetValue<int>();
                break;
            case "start_business" when parameters["loan_minor"] is { } loan:
                SocietyBank.Request(world, agent, loan.GetValue<long>(), parameters["sector"]!.GetValue<string>(),
                    parameters["launch_price_minor"]!.GetValue<int>(), cause);
                break;
            case "start_business":
                SocietyLabor.Start(world, agent, parameters["sector"]!.GetValue<string>(),
                    parameters["launch_price_minor"]!.GetValue<int>(), cause);
                break;
            case "bank_savings":
                SocietyBank.SetKeepsSavings(world, agent, parameters["keep_in_bank"]!.GetValue<bool>(), cause);
                break;
            case "health_policy":
                agent.HealthThreshold = parameters["visit_doctor_below_health"]!.GetValue<int>();
                break;
            case "visit_doctor":
                SocietyEconomy.Visit(world, world.Society!.Nations[agent.CountryId], agent, agent, true);
                break;
            case "enroll_school":
                SocietySchool.Enroll(world, agent, parameters["part_time"]!.GetValue<bool>(), cause);
                break;
            case "seek_better_job":
                agent.SeekingUntilTick = world.Tick + SocietyLabor.CourseDays;
                break;
            case "set_plan":
                SocietyPlanning.Apply(world, agent, parameters, cause);
                break;
            case "work":
                agent.WorkEffort = parameters["effort"]!.GetValue<int>();
                break;
            case "express_support":
                agent.SupportsLeadership = parameters["support"]!.GetValue<bool>();
                break;
            case "set_price":
                agent.PriceMinor = parameters["price_minor"]!.GetValue<int>();
                break;
            case "publish":
                world.Emit("public_report", "rule", [agent.CountryId, agent.Id], new
                {
                    text = parameters["text"]!.GetValue<string>(),
                    provenance = "engine-generated country aggregate at observation tick",
                    report_tick = parameters["observed_tick"]!.GetValue<int>()
                }, cause);
                break;
            case "migrate":
                Migrate(world, agent, parameters["country_id"]!.GetValue<string>(), cause);
                break;
            case "import_goods":
                SocietyTrade.Dispatch(world, agent, world.Agents[parameters["seller_id"]!.GetValue<string>()],
                    parameters["good"]!.GetValue<string>(), ImportQuantity(world, agent, parameters),
                    parameters["objective"]!.GetValue<string>(), cause);
                break;
            case "pay":
                SocietyPayments.Pay(world, agent, parameters, cause);
                break;
            case "buy_goods":
                SocietyPayments.Buy(world, agent, parameters, cause);
                break;
        }
    }

    public static bool CanMigrate(World world, Agent agent, string destination) =>
        agent.Role == "citizen" && destination != agent.CountryId && world.Society!.Nations.ContainsKey(destination)
        && SocietyBank.Liquid(world, agent) >= 100
        && SocietyBank.BankOf(world, agent.CountryId).CashMinor >= agent.DepositMinor
        && !SocietyDiplomacy.Relation(world, agent.CountryId, destination).AtWar;

    private static void Migrate(World world, Agent agent, string destination, string cause)
    {
        if (!CanMigrate(world, agent, destination))
            throw new InvalidOperationException("Migration settlement no longer available");
        var previous = agent.CountryId;
        // This release has one personal bank account: fully settle it before changing country.
        SocietyBank.WithdrawAvailable(world, agent, agent.DepositMinor);
        SocietyEconomy.Transfer(world, agent, world.Agents[destination + "_government"], 100, "migration_fee");
        agent.CountryId = destination;
        world.Reindex();
        agent.LocationId = destination + "_city";
        agent.EmployerId = null;
        agent.SalaryMinor = 0;
        agent.UnemployedSinceTick = world.Tick;
        world.Society!.Nations[previous].Population--;
        world.Society.Nations[destination].Population++;
        world.Emit("agent_migrated", "rule", [agent.Id, previous, destination], new
        {
            from = previous, to = destination, cost_minor = 100, occupation = agent.Occupation,
            deposit_left_behind_minor = 0L
        }, cause);
    }
}
