namespace GEarth.Core;

// Closed firms keep fixed-country accounts until stock, cargo and creditor claims are resolved.
// They never re-enter the labor market, production or model decision scheduling.
public static class SocietyEstates
{
    public static int Price(string good) => Math.Max(1, SocietyFactory.BasePrice(good) / 2);

    public static IEnumerable<Agent> Sellers(World world, string country, string good) =>
        world.InCountry(country).Where(agent => agent.Role == "defunct"
            && agent.EstateBeneficiaryId is not null && agent.Inventory.GetValueOrDefault(good) > 0);

    public static long Settle(World world, Agent estate)
    {
        if (estate.Role != "defunct" || estate.EstateBeneficiaryId is not { } beneficiaryId)
            return 0;
        foreach (var creditId in estate.EstateCreditIds)
        {
            var credit = world.Society!.Credit.Single(credit => credit.Id == creditId);
            SocietyBank.RepayPrincipal(world, estate, credit);
        }
        var beneficiary = world.Agents[beneficiaryId];
        // Unsettled creditor claims retain priority over a migrated owner's payout.
        var owes = estate.EstateCreditIds.Any(id => world.Society!.Credit.Any(credit => credit.Id == id
            && credit.Status == "active" && credit.PrincipalLeftMinor > 0));
        var paid = owes ? 0 : SocietyBank.PaymentCapacity(world, estate, beneficiary);
        SocietyEconomy.Transfer(world, estate, beneficiary, paid, "closure_payout");
        estate.EstateFinalized = estate.CashMinor == 0 && estate.DepositMinor == 0
            && estate.Inventory.Values.All(quantity => quantity == 0)
            && !world.Society!.Cargo.Any(cargo => cargo.Status == "in_transit"
                && (cargo.BuyerId == estate.Id || cargo.SellerId == estate.Id));
        estate.EstateUnreportedPayoutMinor += paid;
        return paid;
    }

    // Late receipts are paid out as they arrive (under their own cause), but reported once a week per estate,
    // and when it is wound up, rather than one event per sale.
    public static void Report(World world, Agent estate)
    {
        if (estate.EstateUnreportedPayoutMinor == 0 || estate.EstateBeneficiaryId is not { } beneficiaryId
            || world.Tick % SocietyLabor.DaysPerWeek != 0 && !estate.EstateFinalized)
            return;
        world.Emit("estate_settled", "rule", [estate.Id, beneficiaryId], new
        {
            payout_minor = estate.EstateUnreportedPayoutMinor, estate.EstateFinalized,
            retained_assets_minor = estate.CashMinor + estate.DepositMinor
        });
        estate.EstateUnreportedPayoutMinor = 0;
    }
}

public sealed record DomesticOffer(Agent Seller, int PriceMinor, bool Liquidation = false);
