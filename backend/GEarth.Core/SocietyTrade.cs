namespace GEarth.Core;

public sealed partial class Nation
{
    // The country's money against the rest of the world (1 at the start); this week's goods ordered across
    // the border at world value, exports positive.
    public double CurrencyIndex { get; set; } = 1;
    public long TradeNetWeekMinor { get; set; }
    public long TradeGrossWeekMinor { get; set; }
}

public static class SocietyTrade
{
    // Everyone holds the same money, so each country's currency works through its exact single-money
    // equivalent: below 1 its treasury levies a surcharge on imports and pays a rebate on exports (the
    // reverse above 1). Each week the index moves by up to CurrencyStep with the trade balance's share of trade.
    public const double CurrencyStep = 0.05; // ponytail: tune after runs
    public const double CurrencyFloor = 0.5;
    public const double CurrencyCeiling = 2;
    public const string Note = "Each country's currency index (1 at the start) falls when it imports more than it exports and rises with a surplus, by up to 5% a week (a planned economy keeps a fixed official rate of 1): below 1 its imports cost more and its exports cost foreign buyers less (the treasury collects the import surcharge and pays the export rebate; the reverse above 1), so the exporter still gets its own price. A state firm in a planned economy exports at the cheapest market price abroad (never below its decreed price), and only stock above a week of its quota. Imports are offered only when they land cheaper than the cheapest local seller. ";

    public static PathPlan? Path(World world, string from, string to, string objective) =>
        RoutePlanner.FindPath(world, objective, from, to, route => SocietyDiplomacy.BorderPermits(world, route));

    public static long Tariff(World world, Agent buyer, Agent seller, long value) =>
        SocietyDiplomacy.Relation(world, buyer.CountryId, seller.CountryId).TradeAgreement ? 0 : value / 20;

    private static double Index(World world, Agent agent) => world.Society!.Nations[agent.CountryId].CurrencyIndex;

    private static bool StateFirm(World world, Agent seller) =>
        seller.OwnerId is null && SocietyLabor.IsPlanned(world.Society!.Nations[seller.CountryId]);

    // What the seller receives per exported unit in its own prices: its shelf price, or for a planned state
    // firm the cheapest market seller's price abroad at world value (never below its decree), as a state
    // trading company matches the world price instead of exporting at home prices. Pricing at the buyer's own
    // market would never land cheaper than a local seller, leaving a planned state unable to pay for imports.
    public static double SellerPrice(World world, Agent seller)
    {
        if (!StateFirm(world, seller))
            return seller.PriceMinor;
        var good = SocietyLabor.SectorOf(seller.Specialty!).Good!;
        var worldPrice = world.Agents.Values.Where(other => other.Role == "business" && other.Specialty == seller.Specialty
                && other.CountryId != seller.CountryId && !StateFirm(world, other) && other.Inventory[good] > 0)
            .Select(other => other.PriceMinor * Index(world, other)).DefaultIfEmpty(0).Min();
        return Math.Max(seller.PriceMinor, worldPrice / Index(world, seller));
    }

    // Per unit before freight and tariff, in the buyer's terms.
    public static double ImportUnitPrice(World world, Agent buyer, Agent seller) =>
        SellerPrice(world, seller) * Index(world, seller) / Index(world, buyer);

    // What the seller receives, the value at world prices, and what the buyer pays for a quantity.
    public readonly record struct Quote(long ToSeller, long AtWorld, long Paid);

    public static Quote Price(World world, Agent buyer, Agent seller, int quantity)
    {
        var toSeller = checked((long)Math.Ceiling(quantity * SellerPrice(world, seller)));
        var atWorld = (long)Math.Round(toSeller * Index(world, seller));
        return new(toSeller, atWorld, (long)Math.Round(atWorld / Index(world, buyer)));
    }

    // A planned state firm keeps a week of its quota at home before anything goes abroad.
    public static int ExportableStock(World world, Agent seller, string good) => Math.Max(0, seller.Inventory[good]
        - (StateFirm(world, seller) ? SocietyPlanning.WeekQuota(world, world.Society!.Nations[seller.CountryId], seller) : 0));

    // A planned economy's currency is not convertible: it keeps a fixed official rate of 1, since its
    // world-priced exports and quota-driven imports would not respond to one.
    public static void ReviewCurrencies(World world)
    {
        foreach (var nation in world.Society!.Nations.Values)
        {
            if (nation.TradeGrossWeekMinor > 0 && !SocietyLabor.IsPlanned(nation))
                nation.CurrencyIndex = Math.Clamp(nation.CurrencyIndex
                    * (1 + CurrencyStep * nation.TradeNetWeekMinor / nation.TradeGrossWeekMinor), CurrencyFloor, CurrencyCeiling);
            (nation.TradeNetWeekMinor, nation.TradeGrossWeekMinor) = (0, 0);
        }
    }

    public static object CurrencyView(Nation nation) => new
    {
        index = Math.Round(nation.CurrencyIndex, 3),
        effect = "below 1 your imports cost more and your exports are cheaper abroad; it moves weekly with the trade balance"
    };

    public static bool CanImport(World world, Agent buyer, Agent seller, string good, int quantity, string objective)
    {
        if (buyer.Role != "business" || seller.Role != "business" || buyer.CountryId == seller.CountryId
            || SocietyLabor.SectorOf(seller.Specialty!).Good != good || quantity < 1 || ExportableStock(world, seller, good) < quantity
            || SocietyDiplomacy.Relation(world, buyer.CountryId, seller.CountryId).AtWar)
            return false;
        var path = Path(world, seller.LocationId, buyer.LocationId, objective);
        var price = Price(world, buyer, seller, quantity);
        return path is not null && SocietyBank.CashAvailable(world, buyer) >= price.AtWorld
            && SocietyBank.Liquid(world, buyer) >= Math.Max(price.Paid, price.AtWorld)
                + SocietyResources.ShippingCost(world, buyer, path, quantity) + Tariff(world, buyer, seller, price.Paid)
            && world.Society!.Cargo.Count(cargo => cargo.BuyerId == buyer.Id && cargo.Good == good && cargo.Status == "in_transit") < 2;
    }

    // A firm with no domestic supplier left imports an input from the cheapest foreign seller, as much as it
    // needs and can pay for, one shipment at a time.
    public static void AutoImport(World world, Agent buyer, string good, int wanted)
    {
        if (world.Society!.Cargo.Any(cargo => cargo.BuyerId == buyer.Id && cargo.Good == good && cargo.Status == "in_transit"))
            return;
        foreach (var seller in ForeignSellers(world, buyer, good, wanted))
        {
            var quantity = Affordable(world, buyer, seller, good, wanted);
            if (quantity > 0)
            {
                Dispatch(world, buyer, seller, good, quantity, "cheapest", null);
                return;
            }
        }
    }

    // When the country's food shelves hold under three days of meals, each farm (or the state food firm) buys
    // food abroad: a market farm only when the landed cost undercuts its own price, the planned state always.
    public static void ImportFood(World world, Nation nation, int residents)
    {
        var farms = SocietyLabor.Firms(world, nation.Id, "food").OrderBy(farm => farm.Id).ToArray();
        if (farms.Length == 0 || SocietyEconomy.Offers(world, nation, "food").Sum(offer => offer.Seller.Inventory["food"]) >= residents * 3)
            return;
        foreach (var farm in farms)
        {
            if (world.Society!.Cargo.Any(cargo => cargo.BuyerId == farm.Id && cargo.Good == "food" && cargo.Status == "in_transit"))
                continue;
            foreach (var seller in ForeignSellers(world, farm, "food", residents * SocietyLabor.DaysPerWeek / farms.Length))
            {
                var quantity = Affordable(world, farm, seller, "food", residents * SocietyLabor.DaysPerWeek / farms.Length);
                if (quantity <= 0)
                    continue;
                var path = Path(world, seller.LocationId, farm.LocationId, "cheapest")!;
                var landed = SocietyResources.LandedUnitCost(world, farm, seller, path, quantity);
                if (farm.OwnerId is null && SocietyLabor.IsPlanned(nation) || landed < farm.PriceMinor)
                    Dispatch(world, farm, seller, "food", quantity, "cheapest", null);
                break;
            }
        }
    }

    // Foreign firms selling the good, reachable and not at war with the buyer, cheapest first.
    private static IEnumerable<Agent> ForeignSellers(World world, Agent buyer, string good, int wanted) =>
        world.Agents.Values.Where(seller => seller.Role == "business" && SocietyLabor.SectorOf(seller.Specialty!).Good == good && seller.CountryId != buyer.CountryId
                && ExportableStock(world, seller, good) > 0 && !SocietyDiplomacy.Relation(world, buyer.CountryId, seller.CountryId).AtWar
                && Path(world, seller.LocationId, buyer.LocationId, "cheapest") is not null)
            .OrderBy(seller => SocietyResources.LandedUnitCost(world, buyer, seller,
                Path(world, seller.LocationId, buyer.LocationId, "cheapest")!, Math.Max(1, Math.Min(wanted, ExportableStock(world, seller, good)))))
            .ThenBy(seller => seller.Id).Take(3);

    // The largest quantity (up to the wanted amount) the buyer can pay for, shipping and tariff included.
    public static int Affordable(World world, Agent buyer, Agent seller, string good, int wanted)
    {
        var low = 0;
        var high = Math.Min(wanted, ExportableStock(world, seller, good));
        while (low < high)
        {
            var quantity = low + (high - low + 1) / 2;
            if (CanImport(world, buyer, seller, good, quantity, "cheapest"))
                low = quantity;
            else
                high = quantity - 1;
        }
        return low;
    }

    public static void Dispatch(World world, Agent buyer, Agent seller, string good, int quantity, string objective, string? cause)
    {
        if (!CanImport(world, buyer, seller, good, quantity, objective))
            throw new InvalidOperationException("Trade precondition changed");
        var cargoId = $"cargo_{world.NextEntityNumber()}";
        using var auditContext = RunAudit.Context(world, (world.CurrentAuditContext ?? new()) with { EventId = cause });
        var price = Price(world, buyer, seller, quantity);
        using var auditGroup = RunAudit.Group(world, good, quantity, price.Paid / quantity, cargoId);
        var path = Path(world, seller.LocationId, buyer.LocationId, objective)!;
        SocietyBank.Draw(world, buyer, price.AtWorld);
        buyer.CashMinor -= price.AtWorld;
        // A domestic freight firm (dockworkers) carries the goods; without one the port authority collects the fee.
        var buyerGovernment = world.Agents[buyer.CountryId + "_government"];
        var carrier = SocietyLabor.Firms(world, buyer.CountryId, "freight").OrderBy(agent => agent.Id).FirstOrDefault() ?? buyerGovernment;
        var shipping = SocietyResources.ShippingCost(world, buyer, path, quantity);
        SocietyEconomy.Sell(world, buyer, carrier, shipping, false, 1, "shipping");
        var tariff = Tariff(world, buyer, seller, price.Paid);
        SocietyEconomy.Transfer(world, buyer, buyerGovernment, tariff, "tariff");
        // The buyer's currency: a weak one pays its treasury a surcharge, a strong one gets a rebate it can fund.
        if (price.Paid > price.AtWorld)
            SocietyEconomy.Transfer(world, buyer, buyerGovernment, price.Paid - price.AtWorld, "currency_surcharge");
        else if (price.AtWorld > price.Paid)
            SocietyEconomy.Transfer(world, buyerGovernment, buyer, Math.Min(price.AtWorld - price.Paid,
                SocietyBank.PaymentCapacity(world, buyerGovernment, buyer)), "currency_rebate");
        var costs = world.Society!.PeriodCostMinor;
        costs[buyer.Id] = costs.GetValueOrDefault(buyer.Id) + price.Paid + tariff;
        seller.Inventory[good] -= quantity;
        var cargo = new Cargo
        {
            Id = cargoId, BuyerId = buyer.Id, SellerId = seller.Id, Good = good,
            Quantity = quantity, EscrowMinor = price.AtWorld, LocationId = seller.LocationId,
            DestinationId = buyer.LocationId, Plan = path, CreatedTick = world.Tick,
            DeadlineTick = world.Tick + 60, CauseId = cause ?? ""
        };
        world.Society!.Cargo.Add(cargo);
        SocietyEconomy.RecordFlow(world, buyer.Id, cargo.Id, price.AtWorld, "import_escrow");
        // The seller's currency: its treasury tops a weak one's exports up to the seller's own price (as far as
        // it can pay) or takes a strong one's excess.
        var sellerGovernment = world.Agents[seller.CountryId + "_government"];
        if (price.ToSeller > price.AtWorld)
        {
            var rebate = Math.Min(price.ToSeller - price.AtWorld, SocietyBank.CashAvailable(world, sellerGovernment));
            SocietyBank.Draw(world, sellerGovernment, rebate);
            sellerGovernment.CashMinor -= rebate;
            sellerGovernment.WeekOutMinor += rebate;
            cargo.EscrowMinor += rebate;
            SocietyEconomy.RecordFlow(world, sellerGovernment.Id, cargo.Id, rebate, "export_rebate");
        }
        else if (price.AtWorld > price.ToSeller)
        {
            var levy = price.AtWorld - price.ToSeller;
            cargo.EscrowMinor -= levy;
            sellerGovernment.CashMinor += levy;
            sellerGovernment.WeekInMinor += levy;
            SocietyEconomy.RecordFlow(world, cargo.Id, sellerGovernment.Id, levy, "export_levy");
        }
        var (buyerNation, sellerNation) = (world.Society.Nations[buyer.CountryId], world.Society.Nations[seller.CountryId]);
        buyerNation.TradeNetWeekMinor -= price.AtWorld;
        sellerNation.TradeNetWeekMinor += price.AtWorld;
        buyerNation.TradeGrossWeekMinor += price.AtWorld;
        sellerNation.TradeGrossWeekMinor += price.AtWorld;
        world.Emit("shipment_dispatched", "rule", [cargo.Id, buyer.Id, seller.Id], new
        {
            good, quantity, escrow_minor = cargo.EscrowMinor, paid_minor = price.Paid, world_value_minor = price.AtWorld,
            currency_index = new { buyer = Math.Round(buyerNation.CurrencyIndex, 3), seller = Math.Round(sellerNation.CurrencyIndex, 3) },
            shipping_minor = shipping, tariff_minor = tariff, carrier_id = carrier.Id, plan = path, settlement = "internal_escrow",
            ordered_by = cause is null ? "rule: no domestic supply" : "agent decision"
        }, cause is null ? [] : [cause]);
    }

    private static string[] Causes(Cargo cargo) => cargo.CauseId == "" ? [] : [cargo.CauseId];

    public static void Advance(World world)
    {
        foreach (var cargo in world.Society!.Cargo.Where(cargo => cargo.Status == "in_transit"))
        {
            if (world.Tick >= cargo.DeadlineTick)
            {
                Fail(world, cargo);
                continue;
            }
            if (cargo.LocationId == cargo.DestinationId)
            {
                Deliver(world, cargo);
                continue;
            }
            if (cargo.EdgeRemainingTicks > 0)
            {
                cargo.EdgeRemainingTicks--;
                if (cargo.EdgeRemainingTicks == 0)
                {
                    cargo.LocationId = world.Routes[cargo.Plan!.EdgeIds[cargo.EdgeIndex]].To;
                    cargo.EdgeIndex++;
                    if (cargo.LocationId == cargo.DestinationId)
                        Deliver(world, cargo);
                }
                continue;
            }
            var edge = cargo.Plan is not null && cargo.EdgeIndex < cargo.Plan.EdgeIds.Length
                ? world.Routes[cargo.Plan.EdgeIds[cargo.EdgeIndex]] : null;
            var buyer = world.Agents[cargo.BuyerId];
            var seller = world.Agents[cargo.SellerId];
            if (SocietyDiplomacy.Relation(world, buyer.CountryId, seller.CountryId).AtWar)
                continue;
            if (edge is null || !SocietyDiplomacy.BorderPermits(world, edge))
            {
                var path = Path(world, cargo.LocationId, cargo.DestinationId, "cautious");
                if (path is null)
                    continue;
                cargo.Plan = path;
                cargo.EdgeIndex = 0;
                edge = world.Routes[path.EdgeIds[0]];
                world.Emit("shipment_rerouted", "rule", [cargo.Id], new { plan = path }, Causes(cargo));
            }
            cargo.EdgeRemainingTicks = edge.TravelTicks - 1;
            if (cargo.EdgeRemainingTicks == 0)
            {
                cargo.LocationId = edge.To;
                cargo.EdgeIndex++;
            }
        }
        // Reserves or asset-sale proceeds can become available after closure, including after resume.
        foreach (var estate in world.Agents.Values.Where(agent => agent.Role == "defunct"))
            SocietyEstates.Settle(world, estate);
    }

    private static void Deliver(World world, Cargo cargo)
    {
        using var auditContext = RunAudit.Context(world, new(EventId: cargo.CauseId, GroupId: cargo.Id,
            Good: cargo.Good, Quantity: cargo.Quantity));
        var buyer = world.Agents[cargo.BuyerId];
        var seller = world.Agents[cargo.SellerId];
        buyer.Inventory[cargo.Good] += cargo.Quantity;
        seller.CashMinor += cargo.EscrowMinor;
        SocietyEconomy.RecordFlow(world, cargo.Id, seller.Id, cargo.EscrowMinor, "import");
        var revenue = world.Society!.PeriodRevenueMinor;
        revenue[seller.Id] = revenue.GetValueOrDefault(seller.Id) + cargo.EscrowMinor;
        var sold = world.Society.PeriodUnitsSold;
        sold[seller.Id] = sold.GetValueOrDefault(seller.Id) + cargo.Quantity;
        var relation = SocietyDiplomacy.Relation(world, buyer.CountryId, seller.CountryId);
        relation.DeliveredUnits += cargo.Quantity;
        relation.DeliveredValueMinor += cargo.EscrowMinor;
        relation.Tension = Math.Max(0, relation.Tension - 1);
        world.Society!.Nations[buyer.CountryId].TradeValueMinor += cargo.EscrowMinor;
        world.Society.Nations[seller.CountryId].TradeValueMinor += cargo.EscrowMinor;
        cargo.EscrowMinor = 0;
        cargo.Status = "delivered";
        world.Society.DeliveredShipments++;
        SocietyResources.RecordImport(world, cargo);
        world.Emit("shipment_delivered", "rule", [cargo.Id, buyer.Id, seller.Id], new
        {
            cargo.Good, cargo.Quantity, travel_ticks = world.Tick - cargo.CreatedTick,
            settlement = "internal_escrow_released"
        }, Causes(cargo));
        // Keep settlement under the shipment's cause/group, including creditor recoveries.
        SocietyEstates.Settle(world, buyer);
        SocietyEstates.Settle(world, seller);
    }

    private static void Fail(World world, Cargo cargo)
    {
        using var auditContext = RunAudit.Context(world, new(EventId: cargo.CauseId, GroupId: cargo.Id,
            Good: cargo.Good, Quantity: cargo.Quantity));
        world.Agents[cargo.BuyerId].CashMinor += cargo.EscrowMinor;
        SocietyEconomy.RecordFlow(world, cargo.Id, cargo.BuyerId, cargo.EscrowMinor, "import_refund");
        var costs = world.Society!.PeriodCostMinor;
        costs[cargo.BuyerId] = costs.GetValueOrDefault(cargo.BuyerId) - cargo.EscrowMinor;
        cargo.EscrowMinor = 0;
        cargo.Status = "failed";
        world.Society!.LostGoods[cargo.Good] += cargo.Quantity;
        world.Society.FailedShipments++;
        world.Emit("shipment_failed", "rule", [cargo.Id], new
        {
            reason = "DELIVERY_DEADLINE", cargo.Good, cargo.Quantity,
            refund = "goods escrow refunded; transport fees sunk; abandoned cargo counted as lost"
        }, Causes(cargo));
        SocietyEstates.Settle(world, world.Agents[cargo.BuyerId]);
        SocietyEstates.Settle(world, world.Agents[cargo.SellerId]);
    }
}
