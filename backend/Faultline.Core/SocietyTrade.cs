namespace Faultline.Core;

public static class SocietyTrade
{
    public static PathPlan? Path(World world, string from, string to, string objective) =>
        RoutePlanner.FindPath(world, objective, from, to, route => SocietyDiplomacy.BorderPermits(world, route));

    public static long Tariff(World world, Agent buyer, Agent seller, long value) =>
        SocietyDiplomacy.Relation(world, buyer.CountryId, seller.CountryId).TradeAgreement ? 0 : value / 20;

    public static bool CanImport(World world, Agent buyer, Agent seller, string good, int quantity, string objective)
    {
        if (buyer.Role != "business" || seller.Role != "business" || buyer.CountryId == seller.CountryId
            || seller.Specialty != good || quantity < 1 || seller.Inventory[good] < quantity
            || SocietyDiplomacy.Relation(world, buyer.CountryId, seller.CountryId).AtWar)
            return false;
        var path = Path(world, seller.LocationId, buyer.LocationId, objective);
        var value = (long)quantity * seller.PriceMinor;
        return path is not null && buyer.CashMinor >= value + path.CostMinor + Tariff(world, buyer, seller, value)
            && !world.Society!.Cargo.Any(cargo => cargo.BuyerId == buyer.Id && cargo.Good == good && cargo.Status == "in_transit");
    }

    public static void Dispatch(World world, Agent buyer, Agent seller, string good, int quantity, string objective, string cause)
    {
        if (!CanImport(world, buyer, seller, good, quantity, objective))
            throw new InvalidOperationException("Trade precondition changed");
        var path = Path(world, seller.LocationId, buyer.LocationId, objective)!;
        var value = (long)quantity * seller.PriceMinor;
        buyer.CashMinor -= value;
        SocietyEconomy.Transfer(buyer, world.Agents[buyer.CountryId + "_bank"], path.CostMinor);
        var tariff = Tariff(world, buyer, seller, value);
        SocietyEconomy.Transfer(buyer, world.Agents[buyer.CountryId + "_government"], tariff);
        seller.Inventory[good] -= quantity;
        var cargo = new Cargo
        {
            Id = $"cargo_{world.Seq}", BuyerId = buyer.Id, SellerId = seller.Id, Good = good,
            Quantity = quantity, EscrowMinor = value, LocationId = seller.LocationId,
            DestinationId = buyer.LocationId, Plan = path, CreatedTick = world.Tick,
            DeadlineTick = world.Tick + 60, CauseId = cause
        };
        world.Society!.Cargo.Add(cargo);
        world.Emit("shipment_dispatched", "rule", [cargo.Id, buyer.Id, seller.Id], new
        {
            good, quantity, escrow_minor = value, shipping_minor = path.CostMinor,
            tariff_minor = tariff, plan = path, settlement = "internal_escrow"
        }, cause);
    }

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
                world.Emit("shipment_rerouted", "rule", [cargo.Id], new { plan = path }, cargo.CauseId);
            }
            cargo.EdgeRemainingTicks = edge.TravelTicks - 1;
            if (cargo.EdgeRemainingTicks == 0)
            {
                cargo.LocationId = edge.To;
                cargo.EdgeIndex++;
            }
        }
    }

    private static void Deliver(World world, Cargo cargo)
    {
        var buyer = world.Agents[cargo.BuyerId];
        var seller = world.Agents[cargo.SellerId];
        buyer.Inventory[cargo.Good] += cargo.Quantity;
        seller.CashMinor += cargo.EscrowMinor;
        var relation = SocietyDiplomacy.Relation(world, buyer.CountryId, seller.CountryId);
        relation.DeliveredUnits += cargo.Quantity;
        relation.DeliveredValueMinor += cargo.EscrowMinor;
        relation.Tension = Math.Max(0, relation.Tension - 1);
        world.Society!.Nations[buyer.CountryId].TradeValueMinor += cargo.EscrowMinor;
        world.Society.Nations[seller.CountryId].TradeValueMinor += cargo.EscrowMinor;
        cargo.EscrowMinor = 0;
        cargo.Status = "delivered";
        world.Society.DeliveredShipments++;
        world.Emit("shipment_delivered", "rule", [cargo.Id, buyer.Id, seller.Id], new
        {
            cargo.Good, cargo.Quantity, travel_ticks = world.Tick - cargo.CreatedTick,
            settlement = "internal_escrow_released"
        }, cargo.CauseId);
    }

    private static void Fail(World world, Cargo cargo)
    {
        world.Agents[cargo.BuyerId].CashMinor += cargo.EscrowMinor;
        cargo.EscrowMinor = 0;
        cargo.Status = "failed";
        world.Society!.LostGoods[cargo.Good] += cargo.Quantity;
        world.Society.FailedShipments++;
        world.Emit("shipment_failed", "rule", [cargo.Id], new
        {
            reason = "DELIVERY_DEADLINE", cargo.Good, cargo.Quantity,
            refund = "goods escrow refunded; transport fees sunk; abandoned cargo counted as lost"
        }, cargo.CauseId);
    }
}
