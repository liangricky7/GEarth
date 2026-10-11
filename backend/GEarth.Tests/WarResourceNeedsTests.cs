using GEarth.Core;

namespace GEarth.Tests;

public sealed class WarResourceNeedsTests
{
    private static (World World, Agent Buyer) Shortage()
    {
        var world = SocietyFactory.Create("war_resource_needs", false, 1100);
        foreach (var firm in world.Agents.Values.Where(agent => agent.Role == "business"))
        {
            world.Agents[firm.CountryId + "_government"].Inventory["ore"] += firm.Inventory["ore"];
            firm.Inventory["ore"] = 0;
        }
        foreach (var relation in world.Society!.Relations.Values) relation.Score = 0;
        foreach (var route in world.Routes.Values) route.Open = true;
        return (world, SocietyLabor.Firms(world, "aster", "tools").First());
    }

    private static void Supply(World world, Agent seller, int quantity)
    {
        world.Agents[seller.CountryId + "_government"].Inventory["ore"] -= quantity;
        seller.Inventory["ore"] += quantity;
    }

    private static SocietyWar.ResourceBlock Need(World world, Agent buyer) => Assert.Single(
        SocietyWar.ResourceNeeds(world, world.Society!.Nations[buyer.CountryId]),
        need => need.BuyerId == buyer.Id && need.Good == "ore");

    [Fact]
    public void OneDomesticUnitKeepsTheForecastGapVisibleWithoutCreatingBlockedAccess()
    {
        var (world, buyer) = Shortage();
        var seller = world.Agents["aster_mine"];
        Supply(world, seller, 1);
        Supply(world, world.Agents["beryl_mine"], 10);
        foreach (var relation in world.Society!.Relations.Values) relation.Score = -100;
        var before = Wire.Node(world).ToJsonString();

        var need = Need(world, buyer);

        Assert.True(need.Gap > 1);
        Assert.Equal(1, need.DomesticAffordable);
        Assert.Equal(seller.PriceMinor, need.DomesticCostMinor);
        Assert.Equal(1, Assert.Single(need.DomesticOffers).Stock);
        Assert.All(need.HeldBy, holder => Assert.Equal("closed_route", holder.BlockedBy));
        Assert.False(need.CannotBuyAny);
        Assert.DoesNotContain(SocietyWar.Blocked(world, world.Society.Nations["aster"]), item => item.Good == "ore");
        var view = Wire.Node(SocietyWar.View(world, world.Society.Nations["aster"]));
        Assert.Contains(view["raw_material_needs"]!.AsArray(), item => (string?)item!["buyer_id"] == buyer.Id && (string?)item["good"] == "ore");
        Assert.Equal(before, Wire.Node(world).ToJsonString());
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void DomesticQuotesShareTheBudgetAndDoNotConfuseNationalStockWithBuyerAccess()
    {
        var (world, buyer) = Shortage();
        var seller = world.Agents["aster_mine"];
        Supply(world, seller, 10);
        SocietyEconomy.Transfer(world, buyer, world.Agents["aster_government"], SocietyBank.Liquid(world, buyer) - seller.PriceMinor);
        var need = Need(world, buyer);
        Assert.Equal(1, need.DomesticAffordable);
        Assert.True(need.Gap > need.DomesticAffordable);
        SocietyEconomy.Transfer(world, buyer, world.Agents["aster_government"], SocietyBank.Liquid(world, buyer));
        need = Need(world, buyer);
        Assert.Equal(0, need.DomesticAffordable);
        Assert.True(need.CannotBuyAny);
        Assert.Equal(10, Assert.Single(need.DomesticOffers).Stock);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void ForeignQuotesRespectRouteAroundWarFundingAndIncomingCargo()
    {
        var (world, buyer) = Shortage();
        var seller = world.Agents["beryl_mine"];
        Supply(world, seller, 10);
        var relation = SocietyDiplomacy.Relation(world, "aster", "beryl");
        relation.Score = -60;
        Assert.True(SocietyTrade.CanImport(world, buyer, seller, "ore", 1, "cheapest"));
        var need = Need(world, buyer);
        var holder = Assert.Single(need.HeldBy);
        Assert.Null(holder.BlockedBy);
        Assert.Equal(1, holder.QuoteQuantity);
        Assert.Equal(SocietyTrade.Affordable(world, buyer, seller, "ore", need.Gap), holder.Affordable);
        Assert.Equal(SocietyTrade.Price(world, buyer, seller, need.Gap).AtWorld, holder.FullGapEscrowMinor);
        Assert.True(holder.FullGapFundingMinor >= holder.FullGapEscrowMinor);

        relation.AtWar = true;
        Assert.False(SocietyTrade.CanImport(world, buyer, seller, "ore", 1, "cheapest"));
        Assert.Equal("war", Assert.Single(Need(world, buyer).HeldBy).BlockedBy);
        relation.AtWar = false;
        foreach (var border in world.Society!.Relations.Values) border.Score = -100;
        Assert.Equal("closed_route", Assert.Single(Need(world, buyer).HeldBy).BlockedBy);
        foreach (var border in world.Society.Relations.Values) border.Score = 0;

        var gap = Need(world, buyer).Gap;
        SocietyTrade.Dispatch(world, buyer, seller, "ore", 1, "cheapest", null);
        Assert.Equal(gap - 1, Need(world, buyer).Gap);
        Assert.Equal(1, Need(world, buyer).Incoming);
        SocietyEconomy.Transfer(world, buyer, world.Agents["aster_government"], SocietyBank.Liquid(world, buyer));
        Assert.Equal("cash_escrow", Assert.Single(Need(world, buyer).HeldBy).BlockedBy);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void PlannedSellersOnlyExposeStockAboveTheirDomesticQuota()
    {
        var (world, buyer) = Shortage();
        var seller = world.Agents["cinder_mine"];
        var quota = SocietyPlanning.WeekQuota(world, world.Society!.Nations["cinder"], seller);
        // Move existing ore from other treasuries to fund the fixture without producing goods.
        foreach (var government in world.Agents.Values.Where(agent => agent.Role == "government" && agent.Id != "cinder_government"))
        {
            world.Agents["cinder_government"].Inventory["ore"] += government.Inventory["ore"];
            government.Inventory["ore"] = 0;
        }
        Supply(world, seller, quota);
        Assert.DoesNotContain(Need(world, buyer).HeldBy, holder => holder.SellerId == seller.Id);
        Supply(world, seller, 1);
        Assert.Equal(1, Assert.Single(Need(world, buyer).HeldBy).Exportable);
        SocietyEconomy.AssertConservation(world);
    }
}
