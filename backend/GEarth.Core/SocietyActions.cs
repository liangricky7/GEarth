using System.Globalization;
using System.Text.Json.Nodes;

namespace GEarth.Core;

public static class SocietyActions
{
    public const int MaxTaxBasisPoints = 6000;
    public const int BudgetTurnDays = 28; // income tax changes only at a budget turn every four weeks
    public const int MaxTaxStepPoints = 5; // whole percentage points a budget can move income tax

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
                    && agent.CashMinor >= clinic.PriceMinor)
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
                if (agent.CashMinor >= 100)
                    foreach (var nation in world.Society!.Nations.Values.Where(nation => nation.Id != agent.CountryId)
                        .Where(nation => !SocietyDiplomacy.Relation(world, agent.CountryId, nation.Id).AtWar)
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
        if (agent.Role is not ("government" or "defunct" or "planner") && SocietyLabor.DecidesForItself(agent))
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
            BankOptions(firm, options);
            foreach (var change in new[] { -1, 1 }.Where(change => firm.TargetHeadcount + change >= 0))
                options.Add(Make("set_headcount", new { business_id = id, change }));
            foreach (var change in new[] { -10, 10 })
                options.Add(Make("set_wage", new { business_id = id, change_pct = change }));
            return;
        }
        LoanOptions(world, firm, options);
        // A change that the price limits (half to three times the base price) would undo is not offered.
        var spec = SocietyLabor.SectorOf(firm.Specialty!);
        if (spec.BasePrice > 0 && spec.Id != "school")
            foreach (var change in new[] { -10, 10 }.Where(change => SocietyLabor.ClampPrice(spec, firm.PriceMinor * (100 + change) / 100) != firm.PriceMinor))
                options.Add(Make("set_price", new { business_id = id, change_pct = change }));
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
    private static void BankOptions(Agent bank, List<Candidate> options)
    {
        foreach (var rate in SocietyBank.DepositRates.Where(rate => rate != bank.DepositRatePct))
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

    // Imports of the firm's own good (or a clinic's medicine supplies) and energy, in three sizes.
    private static void ImportOptions(World world, Agent buyer, List<Candidate> options)
    {
        var nation = world.Society!.Nations[buyer.CountryId];
        var own = buyer.Specialty == "clinic" ? "medicine" : SocietyLabor.SectorOf(buyer.Specialty!).Good;
        if (own is null) // freight and entertainment use no imported inputs
            return;
        foreach (var good in new[] { own, "energy" }.OfType<string>().Distinct())
        {
            if (buyer.Inventory[good] > nation.Population * 3)
                continue;
            foreach (var quantity in new[] { nation.Population / 2, nation.Population, nation.Population * 2 }
                         .Select(size => Math.Clamp(size, 10, 600)).Distinct())
            {
                var sellers = world.Agents.Values.Where(seller => seller.Role == "business" && seller.Specialty == good
                        && seller.CountryId != buyer.CountryId && seller.Inventory[good] >= quantity)
                    .OrderBy(seller => seller.PriceMinor).ThenBy(seller => seller.Id).Take(2);
                foreach (var seller in sellers)
                    if (SocietyTrade.CanImport(world, buyer, seller, good, quantity, "cheapest"))
                        options.Add(Make("import_goods", new { seller_id = seller.Id, good, quantity, objective = "cheapest" }));
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
            BankOptions(agent, options);
    }

    private static void GovernmentOptions(World world, Agent agent, List<Candidate> options)
    {
        var society = world.Society!;
        var nation = society.Nations[agent.CountryId];
        if (society.Policies.Any(policy => policy.CountryId == nation.Id) || !SocietyDiplomacy.HasAuthority(world, nation))
            return;
        // Income tax is set at a budget turn every four weeks, as a new whole-percent rate near the current one.
        if (world.Tick - nation.TaxChangedTick >= BudgetTurnDays)
        {
            var now = (int)Math.Round(nation.TaxBasisPoints / 100.0);
            options.Add(new("adjust_tax", new JsonObject
            {
                ["income_tax_percent"] = Range(Math.Max(0, now - MaxTaxStepPoints), Math.Min(MaxTaxBasisPoints / 100, now + MaxTaxStepPoints), now)
            }));
        }
        // Budget levers are ranges showing the current level, so a raise and a cut are told apart.
        options.Add(new("set_unemployment_benefit", new JsonObject { ["amount_minor"] = Range(0, 900, nation.UnemploymentBenefitMinor) }));
        options.Add(new("set_welfare", new JsonObject { ["amount_minor"] = Range(0, 200, nation.WelfareMinor) }));
        options.Add(new("set_sales_tax", new JsonObject { ["sales_tax_basis_points"] = Range(0, 1000, nation.SalesTaxBasisPoints) }));
        options.Add(new("fund_services", new JsonObject { ["amount_minor"] = Range(0, 150, nation.ServiceBudgetPerResidentMinor) }));
        if (agent.CashMinor >= 1500 && nation.Productivity < 2)
            options.Add(Make("invest", new { amount_minor = 1500 }));
        SocietyInfrastructure.AddOptions(world, nation, agent, options);
        // Levers that appear only when they apply: a startup grant for an essential sector nobody works in,
        // a food subsidy when residents went hungry with food on the shelves, and schooling when people are
        // out of work (each also shows while in force, so it can be changed or ended).
        foreach (var sector in nation.NeededSince.Keys.Order().Where(sector => agent.CashMinor >= SocietyLabor.StartupCost(sector)))
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
            .ThenByDescending(relation => relation.AtWar).ThenByDescending(relation => relation.Tension).Take(4);
        foreach (var relation in relationships)
        {
            var countryId = relation.First == nation.Id ? relation.Second : relation.First;
            if (relation.ProposedBy is not null && relation.ProposedBy != nation.Id)
            {
                options.Add(Make("respond_agreement", new { country_id = countryId, accept = true }));
                options.Add(Make("respond_agreement", new { country_id = countryId, accept = false }));
            }
            else if (relation.ProposedBy is null)
            {
                if (relation.AtWar)
                    options.Add(Make("offer_peace", new { country_id = countryId }));
                else if (!relation.TradeAgreement)
                    options.Add(Make("propose_agreement", new { country_id = countryId }));
            }
            // Conflict comes only after diplomacy between the two has failed, and the option says what it does.
            if (!relation.AtWar && relation.Tension >= 60 && relation.DiplomacyFailedTick >= 0 && agent.CashMinor >= 5000)
                options.Add(Make("declare_conflict", new
                {
                    country_id = countryId,
                    effect = "start an economic conflict: trade with them stops, both sides lose stock every week, costs 5000 now and 1000 a week; it raises tension to 100"
                }));
        }
        foreach (var route in world.Routes.Values.Where(route => route.ControllerCountryId == nation.Id)
            .OrderBy(route => route.Open).ThenBy(route => route.Id).Take(2))
            options.Add(Make("set_border", new { edge_id = route.Id, open = !route.Open }));
        SocietyPayments.AddOptions(world, agent, options);
    }

    public static Candidate Make(string action, object parameters) => new(action, Wire.Node(parameters).AsObject());
}
