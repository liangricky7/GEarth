using System.Globalization;
using System.Text.Json.Nodes;

namespace GEarth.Core;

public static class SocietyActions
{
    public const int MaxTaxBasisPoints = 6000;
    public const int BudgetTurnDays = 28; // income tax changes only at a budget turn every four weeks
    public const int TaxStepPoints = 2; // whole percentage points a budget moves income tax

    public static Candidate[] Available(World world, Agent agent)
    {
        var options = new List<Candidate> { Make("hold", new { }) };
        switch (agent.Role)
        {
            case "citizen" or "entrepreneur":
                foreach (var target in new[] { 2, 4, 8, 16 }.Where(target => target != agent.TargetStock))
                    options.Add(Make("household_policy", new { target_stock = target }));
                foreach (var effort in new[] { 0, 1, 2 }.Where(effort => effort != agent.WorkEffort))
                    options.Add(Make("work", new { effort }));
                options.Add(Make("express_support", new { support = !agent.SupportsLeadership }));
                foreach (var threshold in new[] { 0, 50, 70, 85 }.Where(threshold => threshold != agent.HealthThreshold))
                    options.Add(Make("health_policy", new { visit_doctor_below_health = threshold }));
                // Seeing a doctor now, when unwell and a clinic can take the visit.
                if (agent.Health < 70 && SocietyEconomy.OpenClinic(world, world.Society!.Nations[agent.CountryId]) is { } clinic
                    && SocietyBank.Liquid(world, agent) >= clinic.PriceMinor)
                    options.Add(Make("visit_doctor", new { }));
                if (agent.DepositMinor > 0 || !agent.KeepsSavingsInBank)
                    options.Add(Make("bank_savings", agent.KeepsSavingsInBank
                        ? new { keep_in_bank = false, effect = "withdraw all your savings now and stop depositing" }
                        : new { keep_in_bank = true, effect = "deposit your savings above two weeks of pay again" }));
                if (agent.Role == "entrepreneur")
                {
                    OwnerOptions(world, agent, options);
                    break;
                }
                // Full time (an employee leaves their job) or, for an employee, part time; a student loan covers
                // tuition the student cannot pay. Each option shows what it costs and when it pays back.
                foreach (var partTime in agent.EmployerId is null ? [false] : new[] { false, true })
                    if (SocietySchool.Offer(world, agent, partTime) is { } offer)
                        options.Add(Make("enroll_school", offer));
                if (agent.EmployerId is { } job && world.Agents[job].Role == "business" && agent.SeekingUntilTick < world.Tick)
                    options.Add(Make("seek_better_job", new { }));
                // Founders who cannot pay in full can ask the bank for the rest beyond the down payment.
                foreach (var sector in SocietyLabor.StartableSectors())
                {
                    var cost = SocietyLabor.StartupCost(sector);
                    var launch = SocietyLabor.LaunchPrice(world, world.Society!.Nations[agent.CountryId], sector);
                    var loan = SocietyBank.StartupLoan(world, agent, cost);
                    if (SocietyLabor.CanStart(world, agent, sector))
                        options.Add(Make("start_business", new { sector, startup_cost_minor = cost, launch_price_minor = launch }));
                    else if (SocietyBank.LoanOf(world, agent.Id) is null && SocietyLabor.CanStart(world, agent, sector, loan))
                        options.Add(Make("start_business", new { sector, startup_cost_minor = cost, launch_price_minor = launch, loan_minor = loan }));
                }
                if (SocietyBank.Liquid(world, agent) >= 100)
                    foreach (var nation in world.Society!.Nations.Values.Where(nation => nation.Id != agent.CountryId)
                        .Where(nation => SocietyEngine.CanMigrate(world, agent, nation.Id))
                        .OrderByDescending(nation => nation.Approval).Take(2))
                        options.Add(Make("migrate", new { country_id = nation.Id, cost_minor = 100 }));
                break;
            case "planner":
                options.Add(SocietyPlanning.Option(world, world.Society!.Nations[agent.CountryId]));
                break;
            case "business":
                BusinessOptions(world, agent, options);
                break;
            case "government":
                GovernmentOptions(world, agent, options);
                break;
            case "media":
                var reportingNation = world.Society!.Nations[agent.CountryId];
                var bank = SocietyBank.BankOf(world, agent.CountryId);
                var deposits = SocietyBank.Deposits(world, agent.CountryId);
                var text = string.Create(CultureInfo.InvariantCulture,
                    $"{reportingNation.Name}: food stock {reportingNation.Stocks.GetValueOrDefault("food")}; energy stock {reportingNation.Stocks.GetValueOrDefault("energy")}; unmet needs last cycle {reportingNation.UnmetNeedsLastCycle}; approval {reportingNation.Approval:P0}; bank reserves {bank.CashMinor} against deposits {deposits}{(bank.UnmetWithdrawalsMinor > 0 ? $", {bank.UnmetWithdrawalsMinor} in withdrawals unpaid" : "")}.");
                options.Add(Make("publish", new { text, observed_tick = world.Tick }));
                break;
        }
        // People pay whom they choose; a state firm's manager cannot hand out the state's cash (it pays wages
        // and returns surplus to the treasury), so firms are never offered pay.
        if (agent.Role is not ("government" or "defunct" or "planner" or "business") && SocietyLabor.DecidesForItself(agent))
            SocietyPayments.AddOptions(world, agent, options);
        return Shuffle(world, agent, options);
    }

    // The menu is shown in a seeded order per agent and day, so no option wins by always being listed first.
    private static Candidate[] Shuffle(World world, Agent agent, List<Candidate> options)
    {
        var shuffled = options.ToArray();
        for (var index = shuffled.Length - 1; index > 0; index--)
        {
            var pick = (int)(SocietyFactory.Sample(world.Seed, $"{agent.Id}:{world.Tick}:{index}", "menu") * (index + 1)) % (index + 1);
            (shuffled[index], shuffled[pick]) = (shuffled[pick], shuffled[index]);
        }
        return shuffled;
    }

    // A free numeric parameter: the model picks any integer in [min, max]; rule decisions use the default.
    public static JsonObject Range(int min, int max, int @default) => new() { ["min"] = min, ["max"] = max, ["default"] = @default };

    private static bool IsRange(JsonNode? node) => node is JsonObject range && range.ContainsKey("min") && range.ContainsKey("max");

    // An action matches a candidate when every fixed parameter is equal and every free one is an integer in range.
    public static bool Matches(Candidate candidate, string actionType, JsonObject parameters) =>
        candidate.ActionType == actionType && candidate.Params.Count == parameters.Count
        && candidate.Params.All(pair => parameters.TryGetPropertyValue(pair.Key, out var value) && (IsRange(pair.Value)
            ? value is JsonValue number && number.TryGetValue<int>(out var chosen)
                && chosen >= pair.Value!["min"]!.GetValue<int>() && chosen <= pair.Value!["max"]!.GetValue<int>()
            : JsonNode.DeepEquals(pair.Value, value)));

    // The candidate's parameters with each free number at its default, for rule-driven choices.
    public static JsonObject Resolve(Candidate candidate) => new(candidate.Params.Select(pair =>
        KeyValuePair.Create(pair.Key, IsRange(pair.Value) ? pair.Value!["default"]!.DeepClone() : pair.Value?.DeepClone())));

    // An entrepreneur steers their one business; the engine runs it daily between turns.
    private static void OwnerOptions(World world, Agent owner, List<Candidate> options)
    {
        if (owner.OwnedBusinessId is not { } id || world.Agents[id] is not { Role: "business" } firm)
            return;
        if (SocietyBank.IsBank(firm))
        {
            BankOptions(world, firm, options);
            // Another banker only when the loans already fill most of what the current staff can service.
            var busy = SocietyBank.LoanBook(world, firm) * 10 >= SocietyBank.LoanCapacity(world, firm) * 8;
            foreach (var change in new[] { -1, 1 }.Where(change => firm.TargetHeadcount + change >= 0 && (change < 0 || busy)))
                options.Add(Make("set_headcount", new { business_id = id, change }));
            foreach (var change in new[] { -10, 10 })
                options.Add(Make("set_wage", new { business_id = id, change_pct = change }));
            return;
        }
        LoanOptions(world, firm, options);
        // A change that the price limits (half to three times the base price) would undo is not offered, nor one
        // forecast to sell no more and earn less than holding. Each option says what it should do to weekly sales
        // and margin if competitors keep their prices, and how that margin compares with now.
        var spec = SocietyLabor.SectorOf(firm.Specialty!);
        if (spec.BasePrice > 0 && spec.Id != "school")
        {
            var unitCost = SocietyLabor.UnitCost(world, world.Society!.Nations[firm.CountryId], firm);
            (long Units, long Margin) Forecast(int price) =>
                (SocietyEconomy.ExpectedUnits(world, firm, price), (long)(SocietyEconomy.ExpectedUnits(world, firm, price) * (price - unitCost)));
            var now = Forecast(firm.PriceMinor);
            foreach (var change in new[] { -10, 10 })
            {
                var price = SocietyLabor.ClampPrice(spec, firm.PriceMinor * (100 + change) / 100);
                var (units, margin) = Forecast(price);
                if (price == firm.PriceMinor || units <= now.Units && margin < now.Margin)
                    continue;
                var versus = margin == now.Margin ? "the same as now" : $"{Math.Abs(margin - now.Margin)} {(margin > now.Margin ? "more" : "less")} than now";
                options.Add(Make("set_price", new { business_id = id, change_pct = change,
                    effect = $"price {firm.PriceMinor} -> {price}: about {units} sold a week (now {now.Units}), margin about {margin} a week, {versus}" }));
            }
        }
        foreach (var change in new[] { -10, 10 })
            options.Add(Make("set_wage", new { business_id = id, change_pct = change }));
        foreach (var change in new[] { -1, 1, 2 }.Where(change => firm.TargetHeadcount + change >= 0))
            options.Add(Make("set_headcount", new { business_id = id, change }));
        var imports = new List<Candidate>();
        ImportOptions(world, firm, imports);
        foreach (var candidate in imports)
        {
            candidate.Params["business_id"] = id;
            options.Add(candidate);
        }
    }

    // The bank's owner (or the state) sets the annual rates and the standard each loan request must meet.
    // Deposit rates go no higher than the loan book can pay for (the loan rate times loans over deposits).
    private static void BankOptions(World world, Agent bank, List<Candidate> options)
    {
        var deposits = SocietyBank.Deposits(world, bank.CountryId);
        var affordable = deposits == 0 ? SocietyBank.DepositRates.Max()
            : (double)bank.LoanRatePct * SocietyBank.LoansOutstanding(world, bank) / deposits;
        foreach (var rate in SocietyBank.DepositRates.Where(rate => rate != bank.DepositRatePct
                     && (rate <= affordable || rate == SocietyBank.DepositRates.Min())))
            options.Add(Make("set_deposit_rate", new { business_id = bank.Id, rate_pct = rate }));
        foreach (var rate in SocietyBank.LoanRates.Where(rate => rate != bank.LoanRatePct))
            options.Add(Make("set_loan_rate", new { business_id = bank.Id, rate_pct = rate }));
        foreach (var standard in SocietyBank.Standards.Keys.Where(standard => standard != bank.LendingStandard))
            options.Add(Make("set_lending_standard", new { business_id = bank.Id, standard }));
    }

    // A firm with no loan open can borrow four or eight weeks of its payroll (or its owner's pay) over 26 weeks.
    private static void LoanOptions(World world, Agent firm, List<Candidate> options)
    {
        if (SocietyBank.LoanOf(world, firm.Id) is not null)
            return;
        var bank = SocietyBank.BankOf(world, firm.CountryId);
        var weekly = Math.Max(SocietyLabor.Staff(world, firm).Sum(worker => worker.SalaryMinor),
            SocietyLabor.TableWage[SocietyLabor.SectorOf(firm.Specialty!).Occupation]);
        foreach (var weeks in new[] { 4, 8 })
        {
            var amount = weekly * weeks / 100 * 100;
            var (principal, interest) = SocietyBank.Installment(amount, bank.LoanRatePct);
            options.Add(Make("request_loan", new { business_id = firm.Id, amount_minor = amount, weekly_payment_minor = principal + interest }));
        }
    }

    // Imports of the firm's output and production/service inputs, in three sizes, each from the seller whose
    // goods land cheapest, showing that landed cost beside the cheapest local price, and only when it beats it.
    private static void ImportOptions(World world, Agent buyer, List<Candidate> options)
    {
        var nation = world.Society!.Nations[buyer.CountryId];
        var own = buyer.Specialty == "clinic" ? "medicine" : SocietyLabor.SectorOf(buyer.Specialty!).Good;
        foreach (var good in new[] { own }.OfType<string>()
                     .Concat(SocietyResources.Inputs(buyer.Specialty!).Select(input => input.Good)).Distinct())
        {
            if (buyer.Inventory[good] > nation.Population * 3)
                continue;
            var local = SocietyEconomy.Offers(world, nation, good).FirstOrDefault(offer => offer.Seller != buyer);
            foreach (var quantity in new[] { nation.Population / 2, nation.Population, nation.Population * 2 }
                         .Select(size => Math.Clamp(size, 10, 600)).Distinct())
            {
                var best = SocietyResources.Quotes(world, buyer, good, quantity)
                    .Select(quote => (quote.Seller, Landed: SocietyResources.LandedUnitCost(world, buyer, quote.Seller, quote.Path, quantity)))
                    .OrderBy(quote => quote.Landed).ThenBy(quote => quote.Seller.Id, StringComparer.Ordinal)
                    .FirstOrDefault(quote => SocietyTrade.CanImport(world, buyer, quote.Seller, good, quantity, "cheapest"));
                // No import that lands dearer than a local seller: goods do not cross the border both ways.
                if (best.Seller is not { } seller || local is not null && best.Landed >= local.PriceMinor)
                    continue;
                var landed = best.Landed;
                options.Add(Make("import_goods", new
                {
                    seller_id = seller.Id, good, quantity, objective = "cheapest",
                    effect = $"lands at about {Math.Round(landed)} a unit; cheapest here {(local is null ? "none" : local.PriceMinor.ToString())}"
                }));
            }
        }
    }

    private static void BusinessOptions(World world, Agent agent, List<Candidate> options)
    {
        var nation = world.Society!.Nations[agent.CountryId];
        var basePrice = SocietyFactory.BasePrice(agent.Specialty!);
        if (!SocietyLabor.IsPlanned(nation) && basePrice > 0) // planned economies fix prices by decree
            foreach (var price in new[] { basePrice * 8 / 10, basePrice, basePrice * 12 / 10 }.Where(price => price != agent.PriceMinor))
                options.Add(Make("set_price", new { price_minor = price }));
        ImportOptions(world, agent, options);
        if (SocietyBank.IsBank(agent)) // state firms draw on the treasury, not loans; the state bank sets rates
            BankOptions(world, agent, options);
    }

    private static void GovernmentOptions(World world, Agent agent, List<Candidate> options)
    {
        var society = world.Society!;
        var nation = society.Nations[agent.CountryId];
        if (society.Policies.Any(policy => policy.CountryId == nation.Id) || !SocietyDiplomacy.HasAuthority(world, nation))
            return;
        // Budget levers are a cut and a raise from the level in force (holding is the hold option), each saying
        // what it should do to the weekly budget, as price options do for owners: a model offered a free range
        // kept echoing the current level. Indexation can carry a benefit past its usual ceiling.
        var people = world.InCountry(nation.Id).Where(SocietyLabor.IsResident).ToArray();
        var citizens = people.Count(person => person.Role == "citizen");
        void Lever(string action, string key, int now, int step, int max, Func<int, string> effect)
        {
            foreach (var level in new[] { Math.Max(0, now - step), Math.Min(Math.Max(max, now), now + step) }.Where(level => level != now).Distinct())
                options.Add(new(action, new JsonObject { [key] = level, ["effect"] = effect(level) }));
        }
        string Weekly(long change, string what) => $"about {Math.Abs(change)} a week {(change >= 0 ? "more" : "less")} {what}";
        // Income tax is set at a budget turn every four weeks, as a new whole-percent rate near the current one.
        if (world.Tick - nation.TaxChangedTick >= BudgetTurnDays)
        {
            var payroll = people.Where(person => person.EmployerId is not null).Sum(person => person.SalaryMinor);
            var now = (int)Math.Round(nation.TaxBasisPoints / 100.0);
            Lever("adjust_tax", "income_tax_percent", now, TaxStepPoints, MaxTaxBasisPoints / 100,
                level => $"income tax {now}% -> {level}%: {Weekly(payroll * (level - now) / 100, "in income tax")}");
        }
        Lever("set_unemployment_benefit", "amount_minor", nation.UnemploymentBenefitMinor, 300, 900, level =>
            $"benefit {nation.UnemploymentBenefitMinor} -> {level} a week each: {Weekly((long)nation.Unemployed * (level - nation.UnemploymentBenefitMinor), $"for the {nation.Unemployed} out of work now")}");
        Lever("set_welfare", "amount_minor", nation.WelfareMinor, 50, 200, level =>
            $"welfare {nation.WelfareMinor} -> {level} a week each: {Weekly((long)citizens * (level - nation.WelfareMinor), $"for {citizens} residents")}");
        var essentials = (long)(people.Length * SocietyLabor.LivingCost(world, nation));
        Lever("set_sales_tax", "sales_tax_basis_points", nation.SalesTaxBasisPoints, 200, 1000, level =>
            $"sales tax {nation.SalesTaxBasisPoints / 100.0:0.#}% -> {level / 100.0:0.#}%: {Weekly(essentials * (level - nation.SalesTaxBasisPoints) / 10000, "in tax on food and energy alone")}, raising shop prices");
        Lever("fund_services", "amount_minor", nation.ServiceBudgetPerResidentMinor, 50, 150, level =>
            SocietyGovernmentDecisions.ClinicEffect(world, nation, people.Length, level));
        if (SocietyBank.Liquid(world, agent) >= 1500 && nation.Productivity < 2 && SocietyLabor.Firms(world, nation.Id, "tools").Any())
            options.Add(Make("invest", new { amount_minor = 1500 }));
        SocietyInfrastructure.AddOptions(world, nation, agent, options);
        // Levers that appear only when they apply: a startup grant for an essential sector nobody works in,
        // a food subsidy when residents went hungry with food on the shelves, and schooling when people are
        // out of work (each also shows while in force, so it can be changed or ended).
        foreach (var sector in nation.NeededSince.Keys.Order().Where(sector => SocietyBank.Liquid(world, agent) >= SocietyLabor.StartupCost(sector)))
            options.Add(Make("subsidize_startup", new { sector, grant_minor = SocietyLabor.StartupCost(sector) }));
        var residents = world.InCountry(nation.Id).Count(SocietyLabor.IsResident);
        if (nation.PricedOutLastWeek * 10 >= residents || nation.FoodSubsidyPct > 0)
            foreach (var percent in new[] { 0, 25, 50, 75 }.Where(percent => percent != nation.FoodSubsidyPct))
                options.Add(Make("subsidize_food", new { percent }));
        if (nation.Unemployed * 20 >= residents || nation.TuitionSubsidyPct > 0 || nation.StudentStipend || nation.HiringBonusMinor > 0)
        {
            foreach (var percent in new[] { 0, 50, 100 }.Where(percent => percent != nation.TuitionSubsidyPct))
                options.Add(Make("subsidize_tuition", new { percent }));
            options.Add(Make("student_stipend", new { on = !nation.StudentStipend }));
            foreach (var amount in new[] { 0, 100, 200 }.Where(amount => amount != nation.HiringBonusMinor))
                options.Add(Make("hiring_bonus", new { amount_minor = amount }));
        }
        var relationships = society.Relations.Values.Where(relation => relation.First == nation.Id || relation.Second == nation.Id)
            .OrderByDescending(relation => relation.ProposedBy is not null && relation.ProposedBy != nation.Id)
            .ThenByDescending(relation => relation.AtWar).ThenBy(relation => relation.Score).ToArray();
        relationships = relationships.Where(relation => relation.AtWar || relation.ProposedBy is not null)
            .Concat(relationships.Where(relation => !relation.AtWar && relation.ProposedBy is null).Take(4)).ToArray();
        foreach (var relation in relationships)
        {
            var countryId = relation.First == nation.Id ? relation.Second : relation.First;
            if (relation.ProposedBy is not null && relation.ProposedBy != nation.Id
                && world.Tick + Governments.Get(nation.GovernmentId).PolicyDelay <= relation.ProposalExpiresTick)
            {
                options.Add(Make("respond_agreement", new { country_id = countryId, accept = true, effect = SocietyGovernmentDecisions.ResponseEffect(world, nation, relation, true) }));
                options.Add(Make("respond_agreement", new { country_id = countryId, accept = false, effect = SocietyGovernmentDecisions.ResponseEffect(world, nation, relation, false) }));
            }
            else if (relation.ProposedBy is null)
            {
                if (relation.AtWar)
                    foreach (var terms in SocietyWar.PeaceTerms)
                        options.Add(Make("offer_peace", new { country_id = countryId, terms,
                            effect = SocietyWar.PeaceEffect(world, nation.Id, countryId, terms) }));
                else if (!relation.TradeAgreement)
                    options.Add(Make("propose_agreement", new { country_id = countryId }));
            }
            // Conflict comes only after diplomacy between the two has failed, and the option says what it does.
            if (!relation.AtWar && relation.Score <= SocietyWar.WarBelow && relation.DiplomacyFailedTick >= 0 && SocietyBank.Liquid(world, agent) >= 5000
                && SocietyWar.OffersWar(world, nation, society.Nations[countryId]))
                options.Add(Make("declare_conflict", new
                {
                    country_id = countryId,
                    effect = "Costs 5000 now and 1000 per war week in soldier pay; closes bilateral trade and sets relations to -100. Weekly contests take 10% of taxes and ore/fuel stocks from the loser and wound 25% of fit soldiers. Third countries condemn the aggressor. Two weeks without fit soldiers forces surrender regardless of missiles."
                }));
        }
        foreach (var route in world.Routes.Values.Where(route => route.ControllerCountryId == nation.Id)
            .OrderBy(route => route.Open).ThenBy(route => route.Id).Take(2))
            options.Add(Make("set_border", new { edge_id = route.Id, open = !route.Open }));
        SocietyWar.AddOptions(world, nation, agent, options);
        SocietyPayments.AddOptions(world, agent, options);
    }

    public static Candidate Make(string action, object parameters) => new(action, Wire.Node(parameters).AsObject());
}
