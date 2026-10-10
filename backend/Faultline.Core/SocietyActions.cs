using System.Globalization;
using System.Text.Json.Nodes;

namespace Faultline.Core;

public static class SocietyActions
{
    public static Candidate[] Available(World world, Agent agent)
    {
        var options = new List<Candidate> { Make("hold", new { }) };
        switch (agent.Role)
        {
            case "citizen":
                foreach (var target in new[] { 2, 4, 8, 16 }.Where(target => target != agent.TargetStock))
                    options.Add(Make("household_policy", new { target_stock = target }));
                foreach (var effort in new[] { 0, 1, 2 }.Where(effort => effort != agent.WorkEffort))
                    options.Add(Make("work", new { effort }));
                options.Add(Make("express_support", new { support = !agent.SupportsLeadership }));
                foreach (var threshold in new[] { 0, 30, 50, 70 }.Where(threshold => threshold != agent.HealthThreshold))
                    options.Add(Make("health_policy", new { visit_doctor_below_health = threshold }));
                if (agent.CashMinor >= 100)
                    foreach (var nation in world.Society!.Nations.Values.Where(nation => nation.Id != agent.CountryId)
                        .Where(nation => !SocietyDiplomacy.Relation(world, agent.CountryId, nation.Id).AtWar)
                        .OrderByDescending(nation => nation.Approval).Take(2))
                        options.Add(Make("migrate", new { country_id = nation.Id, cost_minor = 100 }));
                break;
            case "business":
                BusinessOptions(world, agent, options);
                break;
            case "government":
                GovernmentOptions(world, agent, options);
                break;
            case "bank":
                foreach (var credit in world.Society!.Credit.Where(credit => credit.BankId == agent.Id
                    && credit.Status == "requested" && agent.CashMinor >= credit.PrincipalMinor).Take(4))
                    options.Add(Make("approve_loan", new { loan_id = credit.Id }));
                break;
            case "media":
                var reportingNation = world.Society!.Nations[agent.CountryId];
                var text = string.Create(CultureInfo.InvariantCulture,
                    $"{reportingNation.Name}: food stock {reportingNation.Stocks.GetValueOrDefault("food")}; energy stock {reportingNation.Stocks.GetValueOrDefault("energy")}; unmet needs last cycle {reportingNation.UnmetNeedsLastCycle}; approval {reportingNation.Approval:P0}.");
                options.Add(Make("publish", new { text, observed_tick = world.Tick }));
                break;
        }
        if (agent.Role != "government")
            SocietyPayments.AddOptions(world, agent, options);
        return options.ToArray();
    }

    private static void BusinessOptions(World world, Agent agent, List<Candidate> options)
    {
        var basePrice = SocietyFactory.BasePrice(agent.Specialty!);
        foreach (var price in new[] { basePrice * 8 / 10, basePrice, basePrice * 12 / 10 }.Where(price => price != agent.PriceMinor))
            options.Add(Make("set_price", new { price_minor = price }));

        var nation = world.Society!.Nations[agent.CountryId];
        var quantity = Math.Clamp(nation.Population / 2, 10, 300);
        var requestedGoods = new[] { agent.Specialty!, "energy" }.Distinct();
        foreach (var good in requestedGoods)
        {
            if (agent.Inventory[good] > nation.Population * 3)
                continue;
            var sellers = world.Agents.Values.Where(seller => seller.Role == "business" && seller.Specialty == good
                && seller.CountryId != agent.CountryId && seller.Inventory[good] >= quantity)
                .OrderBy(seller => seller.PriceMinor).ThenBy(seller => seller.Id).Take(3);
            foreach (var seller in sellers)
                foreach (var objective in new[] { "cheapest", "fastest" })
                    if (SocietyTrade.CanImport(world, agent, seller, good, quantity, objective))
                        options.Add(Make("import_goods", new { seller_id = seller.Id, good, quantity, objective }));
        }
        if (agent.CashMinor < nation.Population * 200L
            && !world.Society.Credit.Any(credit => credit.BorrowerId == agent.Id && credit.Status is "active" or "requested"))
            options.Add(Make("request_loan", new { amount_minor = Math.Clamp(nation.Population * 100, 1000, 30000) }));
    }

    private static void GovernmentOptions(World world, Agent agent, List<Candidate> options)
    {
        var society = world.Society!;
        var nation = society.Nations[agent.CountryId];
        if (society.Policies.Any(policy => policy.CountryId == nation.Id) || !SocietyDiplomacy.HasAuthority(world, nation))
            return;
        foreach (var tax in new[] { 500, 1500, 3000 }.Where(tax => tax != nation.TaxBasisPoints))
            options.Add(Make("set_tax", new { tax_basis_points = tax }));
        foreach (var welfare in new[] { 0, 40, 80 }.Where(welfare => welfare != nation.WelfareMinor))
            options.Add(Make("set_welfare", new { amount_minor = welfare }));
        foreach (var salesTax in new[] { 0, 500, 1000 }.Where(salesTax => salesTax != nation.SalesTaxBasisPoints))
            options.Add(Make("set_sales_tax", new { sales_tax_basis_points = salesTax }));
        foreach (var services in new[] { 0, 10, 30 }.Where(services => services != nation.ServiceBudgetPerResidentMinor))
            options.Add(Make("fund_services", new { amount_minor = services }));
        if (agent.CashMinor >= 1500 && nation.Productivity < 2)
            options.Add(Make("invest", new { amount_minor = 1500 }));
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
            if (!relation.AtWar && relation.Tension >= 60 && agent.CashMinor >= 5000)
                options.Add(Make("declare_conflict", new { country_id = countryId }));
        }
        foreach (var route in world.Routes.Values.Where(route => route.ControllerCountryId == nation.Id)
            .OrderBy(route => route.Open).ThenBy(route => route.Id).Take(2))
            options.Add(Make("set_border", new { edge_id = route.Id, open = !route.Open }));
        SocietyPayments.AddOptions(world, agent, options);
    }

    public static Candidate Make(string action, object parameters) => new(action, Wire.Node(parameters).AsObject());
}
