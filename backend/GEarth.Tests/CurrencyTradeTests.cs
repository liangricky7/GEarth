using GEarth.Core;

namespace GEarth.Tests;

// Trade fixes from the resources run: a country's currency falls with a trade deficit (imports dearer,
// exports cheaper abroad, settled through treasuries), planned states export at foreign market prices and
// only their surplus, and nothing is imported that a local seller sells for less.
public sealed class CurrencyTradeTests
{
    private static World NewWorld() => SocietyFactory.Create("currency_trade", false, 1100);

    [Fact]
    public void ADeficitWeakensTheCurrencyAndASurplusStrengthensIt()
    {
        var world = NewWorld();
        var (aster, dune) = (world.Society!.Nations["aster"], world.Society.Nations["dune"]);
        (aster.TradeNetWeekMinor, aster.TradeGrossWeekMinor) = (-1000, 1000); // imports only
        (dune.TradeNetWeekMinor, dune.TradeGrossWeekMinor) = (500, 1000);

        SocietyTrade.ReviewCurrencies(world);

        Assert.Equal(1 - SocietyTrade.CurrencyStep, aster.CurrencyIndex, 6);
        Assert.Equal(1 + SocietyTrade.CurrencyStep / 2, dune.CurrencyIndex, 6);
        Assert.Equal(0, aster.TradeGrossWeekMinor);
        Assert.Equal(1, world.Society.Nations["beryl"].CurrencyIndex); // no trade, no change
        var cinder = world.Society.Nations["cinder"]; // a planned economy keeps its official rate
        (cinder.TradeNetWeekMinor, cinder.TradeGrossWeekMinor) = (-1000, 1000);
        SocietyTrade.ReviewCurrencies(world);
        Assert.Equal(1, cinder.CurrencyIndex);
        Assert.Contains(SocietyTrade.Note, SocietyEngine.RulesNote);
    }

    [Fact]
    public void AWeakBuyerPaysItsTreasuryASurchargeAndAWeakSellerIsToppedUpByItsTreasury()
    {
        var world = NewWorld();
        var buyer = world.Agents["aster_tools"];
        var seller = world.Agents["dune_mine"];
        SocietyEconomy.Transfer(world, world.Agents["aster_bank"], buyer, 100_000);
        world.Society!.Nations["aster"].CurrencyIndex = 0.8;
        var asterTreasury = world.Agents["aster_government"].CashMinor;

        var price = SocietyTrade.Price(world, buyer, seller, 50);
        Assert.Equal(price.ToSeller, price.AtWorld); // dune at 1: no rebate or levy on its side
        Assert.Equal((long)Math.Round(price.AtWorld / 0.8), price.Paid);
        SocietyTrade.Dispatch(world, buyer, seller, "ore", 50, "cheapest", null);
        var cargo = world.Society.Cargo.Last();
        Assert.Equal(price.ToSeller, cargo.EscrowMinor);
        Assert.True(world.Agents["aster_government"].CashMinor - asterTreasury >= price.Paid - price.AtWorld);
        Assert.True(world.Society.Nations["aster"].TradeNetWeekMinor < 0 && world.Society.Nations["dune"].TradeNetWeekMinor > 0);
        SocietyEconomy.AssertConservation(world);

        // Now the seller's currency is weak: the foreign buyer pays less, and dune's treasury makes up the price.
        world.Society.Nations["aster"].CurrencyIndex = 1;
        world.Society.Nations["dune"].CurrencyIndex = 0.8;
        var weak = SocietyTrade.Price(world, buyer, seller, 50);
        Assert.True(weak.Paid < weak.ToSeller);
        var duneTreasury = world.Agents["dune_government"].CashMinor;
        SocietyTrade.Dispatch(world, buyer, seller, "ore", 50, "cheapest", null);
        Assert.Equal(weak.ToSeller, world.Society.Cargo.Last().EscrowMinor);
        Assert.Equal(weak.ToSeller - weak.AtWorld, duneTreasury - world.Agents["dune_government"].CashMinor);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void APlannedStateFirmExportsAtTheWorldPriceAndKeepsAWeekOfItsQuota()
    {
        var world = NewWorld();
        var cinder = world.Society!.Nations["cinder"];
        var stateFarm = SocietyLabor.Firms(world, "cinder", "food").First();
        foreach (var farm in world.Agents.Values.Where(agent => agent.Role == "business" && agent.Specialty == "food" && agent.OwnerId is not null))
            farm.PriceMinor = 80;
        SocietyLabor.Firms(world, "aster", "food").Last().PriceMinor = 70; // the cheapest market seller abroad
        var buyer = SocietyLabor.Firms(world, "aster", "food").First();

        Assert.Equal(70, SocietyTrade.SellerPrice(world, stateFarm), 6); // not the decreed 40
        world.Society.Nations["aster"].CurrencyIndex = 0.5; // that seller's 70 is worth 35 abroad: the decree floor holds
        Assert.Equal(stateFarm.PriceMinor, SocietyTrade.SellerPrice(world, stateFarm), 6);
        var privateMine = world.Agents["dune_mine"]; // a market seller exports at its own price
        Assert.Equal(privateMine.PriceMinor, SocietyTrade.SellerPrice(world, privateMine), 6);

        cinder.Plan["food"] = 700;
        stateFarm.Inventory["food"] = 1000;
        Assert.Equal(1000 - SocietyPlanning.WeekQuota(world, cinder, stateFarm), SocietyTrade.ExportableStock(world, stateFarm, "food"));
        Assert.True(SocietyPlanning.WeekQuota(world, cinder, stateFarm) > 0);
        stateFarm.Inventory["food"] = SocietyPlanning.WeekQuota(world, cinder, stateFarm);
        Assert.False(SocietyTrade.CanImport(world, buyer, stateFarm, "food", 1, "cheapest"));
    }

    [Fact]
    public void NoImportIsOfferedWhenALocalSellerIsCheaper()
    {
        var world = NewWorld();
        var workshop = world.Agents["aster_tools"];
        var owner = world.Agents[workshop.OwnerId!];
        workshop.Inventory["ore"] = 0;
        SocietyEconomy.Transfer(world, world.Agents["aster_bank"], workshop, 100_000);
        var mine = SocietyLabor.Firms(world, "aster", "mine").First();
        mine.Inventory["ore"] = 500;
        bool OreImportOffered() => SocietyActions.Available(world, owner).Any(candidate => candidate.ActionType == "import_goods"
            && candidate.Params["good"]!.GetValue<string>() == "ore");

        mine.PriceMinor = 1; // far below any landed import
        Assert.False(OreImportOffered());
        mine.PriceMinor = 45; // three times base: imports land cheaper
        Assert.True(OreImportOffered());
    }
}
