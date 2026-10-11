namespace GEarth.Core;

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
            && world.Society!.Cargo.Count(cargo => cargo.BuyerId == buyer.Id && cargo.Good == good && cargo.Status == "in_transit") < 2;
    }

    // A firm with no domestic supplier left imports an input from the cheapest foreign seller, as much as it
    // needs and can pay for, one shipment at a time.
    public static void AutoImport(World world, Agent buyer, string good, int wanted)
    {
        if (world.Society!.Cargo.Any(cargo => cargo.BuyerId == buyer.Id && cargo.Good == good && cargo.Status == "in_transit"))
            return;
        foreach (var seller in ForeignSellers(world, buyer, good))
        {
            var quantity = Affordable(world, buyer, seller, good, Math.Min(wanted, seller.Inventory[good]));
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
        if (farms.Length == 0 || farms.Sum(farm => farm.Inventory["food"]) >= residents * 3)
            return;
        foreach (var farm in farms)
        {
            if (world.Society!.Cargo.Any(cargo => cargo.BuyerId == farm.Id && cargo.Good == "food" && cargo.Status == "in_transit"))
                continue;
            foreach (var seller in ForeignSellers(world, farm, "food"))
            {
                var quantity = Affordable(world, farm, seller, "food", Math.Min(residents * SocietyLabor.DaysPerWeek / farms.Length, seller.Inventory["food"]));
                if (quantity <= 0)
                    continue;
                var path = Path(world, seller.LocationId, farm.LocationId, "cheapest")!;
                var landed = seller.PriceMinor + (path.CostMinor + Tariff(world, farm, seller, (long)quantity * seller.PriceMinor)) / (double)quantity;
                if (farm.OwnerId is null && SocietyLabor.IsPlanned(nation) || landed < farm.PriceMinor)
                    Dispatch(world, farm, seller, "food", quantity, "cheapest", null);
                break;
            }
        }
    }

    // Foreign firms selling the good, reachable and not at war with the buyer, cheapest first.
    private static IEnumerable<Agent> ForeignSellers(World world, Agent buyer, string good) =>
        world.Agents.Values.Where(seller => seller.Role == "business" && seller.Specialty == good && seller.CountryId != buyer.CountryId
                && seller.Inventory[good] > 0 && !SocietyDiplomacy.Relation(world, buyer.CountryId, seller.CountryId).AtWar
                && Path(world, seller.LocationId, buyer.LocationId, "cheapest") is not null)
            .OrderBy(seller => seller.PriceMinor).ThenBy(seller => seller.Id).Take(3);

    // The largest quantity (up to the wanted amount) the buyer can pay for, shipping and tariff included.
    private static int Affordable(World world, Agent buyer, Agent seller, string good, int wanted)
    {
        for (var quantity = wanted; quantity > 0; quantity = quantity * 3 / 4)
            if (CanImport(world, buyer, seller, good, quantity, "cheapest"))
                return quantity;
        return 0;
    }

    public static void Dispatch(World world, Agent buyer, Agent seller, string good, int quantity, string objective, string? cause)
    {
        if (!CanImport(world, buyer, seller, good, quantity, objective))
            throw new InvalidOperationException("Trade precondition changed");
        var cargoId = $"cargo_{world.NextEntityNumber()}";
        using var auditContext = RunAudit.Context(world, (world.CurrentAuditContext ?? new()) with { EventId = cause });
        using var auditGroup = RunAudit.Group(world, good, quantity, seller.PriceMinor, cargoId);
        var path = Path(world, seller.LocationId, buyer.LocationId, objective)!;
        var value = (long)quantity * seller.PriceMinor;
        buyer.CashMinor -= value;
        // A domestic freight firm (dockworkers) carries the goods; without one the port authority collects the fee.
        var carrier = SocietyLabor.Firms(world, buyer.CountryId, "freight").OrderBy(agent => agent.Id).FirstOrDefault()
            ?? world.Agents[buyer.CountryId + "_government"];
        SocietyEconomy.Sell(world, buyer, carrier, path.CostMinor, false, 1, "shipping");
        var tariff = Tariff(world, buyer, seller, value);
        SocietyEconomy.Transfer(world, buyer, world.Agents[buyer.CountryId + "_government"], tariff, "tariff");
        var costs = world.Society!.PeriodCostMinor;
        costs[buyer.Id] = costs.GetValueOrDefault(buyer.Id) + value + tariff;
        seller.Inventory[good] -= quantity;
        var cargo = new Cargo
        {
            Id = cargoId, BuyerId = buyer.Id, SellerId = seller.Id, Good = good,
            Quantity = quantity, EscrowMinor = value, LocationId = seller.LocationId,
            DestinationId = buyer.LocationId, Plan = path, CreatedTick = world.Tick,
            DeadlineTick = world.Tick + 60, CauseId = cause ?? ""
        };
        world.Society!.Cargo.Add(cargo);
        SocietyEconomy.RecordFlow(world, buyer.Id, cargo.Id, value, "import_escrow");
        world.Emit("shipment_dispatched", "rule", [cargo.Id, buyer.Id, seller.Id], new
        {
            good, quantity, escrow_minor = value, shipping_minor = path.CostMinor,
            tariff_minor = tariff, carrier_id = carrier.Id, plan = path, settlement = "internal_escrow",
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
        relation.Score = Math.Min(100, relation.Score + 1);
        world.Society!.Nations[buyer.CountryId].TradeValueMinor += cargo.EscrowMinor;
        world.Society.Nations[seller.CountryId].TradeValueMinor += cargo.EscrowMinor;
        cargo.EscrowMinor = 0;
        cargo.Status = "delivered";
        world.Society.DeliveredShipments++;
        world.Emit("shipment_delivered", "rule", [cargo.Id, buyer.Id, seller.Id], new
        {
            cargo.Good, cargo.Quantity, travel_ticks = world.Tick - cargo.CreatedTick,
            settlement = "internal_escrow_released"
        }, Causes(cargo));
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
    }
}
