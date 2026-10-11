using System.Text.Json.Nodes;

namespace GEarth.Core;

// Agent-chosen money movement inside a country: direct payments and on-demand household purchases.
// Routine wages, taxes and restocking stay in SocietyEconomy; these are the deliberate choices on top.
public static class SocietyPayments
{
    public const int BuyQuantity = 4;

    // ponytail: one fixed amount per role; add size tiers if models need finer choices
    public static long PayAmount(Agent payer) => SocietyLabor.IsResident(payer) ? 100 : 1000;

    public static void AddOptions(World world, Agent agent, List<Candidate> options)
    {
        var amount = PayAmount(agent);
        if (SocietyBank.Liquid(world, agent) >= amount)
            foreach (var recipient in Recipients(world, agent))
                options.Add(SocietyActions.Make("pay", new { recipient_id = recipient.Id, amount_minor = amount }));
        if (!SocietyLabor.IsResident(agent))
            return;
        foreach (var good in new[] { "food", "energy" })
            if (SocietyEconomy.Offers(world, world.Society!.Nations[agent.CountryId], good).FirstOrDefault() is { } offer
                && CanBuy(world, agent, offer.Seller.Id, good, BuyQuantity, offer.PriceMinor))
                options.Add(SocietyActions.Make("buy_goods", new
                {
                    seller_id = offer.Seller.Id, good, quantity = BuyQuantity, price_minor = offer.PriceMinor
                }));
    }

    // Recipients shift as wellbeing and cash change, so validation checks legality, not menu membership.
    public static bool StillValid(World world, Agent agent, JsonObject parameters) =>
        parameters.ContainsKey("recipient_id")
            ? CanPay(world, agent, parameters["recipient_id"]!.GetValue<string>(), parameters["amount_minor"]!.GetValue<long>())
            : CanBuy(world, agent, parameters["seller_id"]!.GetValue<string>(), parameters["good"]!.GetValue<string>(),
                parameters["quantity"]!.GetValue<int>(), parameters["price_minor"]!.GetValue<int>());

    public static bool Pay(World world, Agent payer, JsonObject parameters, string cause)
    {
        var recipientId = parameters["recipient_id"]!.GetValue<string>();
        var amount = parameters["amount_minor"]!.GetValue<long>();
        if (!CanPay(world, payer, recipientId, amount))
            return false;
        var recipient = world.Agents[recipientId];
        SocietyEconomy.Transfer(world, payer, recipient, amount, "payment");
        recipient.Remember($"T{world.Tick}: received {amount} minor from {payer.Id} ({payer.Role})");
        world.Emit("payment_sent", "rule", [payer.Id, recipient.Id], new
        {
            from = payer.Id, to = recipient.Id, from_role = payer.Role, to_role = recipient.Role,
            amount_minor = amount, settlement = "simulated internal ledger"
        }, cause);
        return true;
    }

    public static void Buy(World world, Agent buyer, JsonObject parameters, string cause)
    {
        var seller = world.Agents[parameters["seller_id"]!.GetValue<string>()];
        var good = parameters["good"]!.GetValue<string>();
        var quantity = parameters["quantity"]!.GetValue<int>();
        var price = parameters["price_minor"]!.GetValue<int>();
        var total = (long)quantity * price;
        using var auditGroup = RunAudit.Group(world, good, quantity, price);
        SocietyEconomy.Sell(world, buyer, seller, total, true, quantity, seller.Role == "defunct" ? "liquidation_sale" : "purchase");
        seller.Inventory[good] -= quantity;
        buyer.Inventory[good] += quantity;
        world.Emit("goods_purchased", "rule", [buyer.Id, seller.Id], new
        {
            buyer = buyer.Id, seller = seller.Id, good, quantity, price_minor = price, total_minor = total
        }, cause);
        if (seller.Role == "defunct") SocietyEstates.Settle(world, seller);
    }

    private static bool CanPay(World world, Agent payer, string recipientId, long amount) =>
        amount > 0 && SocietyBank.Liquid(world, payer) >= amount && recipientId != payer.Id
        && world.Agents.TryGetValue(recipientId, out var recipient) && recipient.CountryId == payer.CountryId;

    // The quoted price is part of the choice: a repriced seller invalidates it rather than charging more.
    private static bool CanBuy(World world, Agent buyer, string sellerId, string good, int quantity, int price) =>
        SocietyLabor.IsResident(buyer) && world.Agents.TryGetValue(sellerId, out var seller)
        && good is "food" or "energy" && quantity > 0
        && SocietyEconomy.Offers(world, world.Society!.Nations[buyer.CountryId], good)
            .Any(offer => offer.Seller == seller && offer.PriceMinor == price)
        && seller.Inventory[good] >= quantity && SocietyBank.Liquid(world, buyer) >= (long)quantity * price;

    private static IEnumerable<Agent> Recipients(World world, Agent payer)
    {
        var locals = world.InCountry(payer.CountryId).Where(agent => agent.Id != payer.Id).ToArray();
        var neediest = locals.Where(SocietyLabor.IsResident)
            .OrderBy(agent => agent.Wellbeing).ThenBy(agent => agent.CashMinor).ThenBy(agent => agent.Id).FirstOrDefault();
        var poorestBusiness = locals.Where(agent => agent.Role == "business")
            .OrderBy(agent => agent.CashMinor).ThenBy(agent => agent.Id).FirstOrDefault();
        var government = locals.FirstOrDefault(agent => agent.Role == "government");
        Agent?[] picks = payer.Role switch
        {
            "citizen" or "entrepreneur" => [neediest, government, payer.EmployerId is null ? null : world.Agents.GetValueOrDefault(payer.EmployerId)],
            "government" => [neediest, poorestBusiness, locals.FirstOrDefault(SocietyBank.IsBank)],
            _ => [neediest, government]
        };
        return picks.OfType<Agent>().Where(agent => agent.Id != payer.Id && agent.CountryId == payer.CountryId)
            .DistinctBy(agent => agent.Id);
    }
}
