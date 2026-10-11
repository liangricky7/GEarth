using GEarth.Core;

namespace GEarth.Tests;

// Fixes from the first resources run: shoppers lean away from prices far above base and owners see what a
// price change should do, empty sectors refound only where imports are not cheaper, a gap needs demand
// across the market, banks hire and pay depositors only as their loans allow, and imports fill what is left.
public sealed class PostResourcePatchTests
{
    private static World NewWorld() => SocietyFactory.Create("post_resource_patch", false, 1100);

    [Fact]
    public void ShoppersLeanHardAwayFromPricesFarAboveBase()
    {
        // Below the reference only the price ratio counts: 10% dearer keeps about two-thirds of the weight.
        Assert.InRange(SocietyEconomy.ShopperWeight(44, 40, 40), 0.65, 0.7);
        // At twice base, 10% dearer than a rival keeps about a third, and a seller back near base wins almost everything.
        Assert.InRange(SocietyEconomy.ShopperWeight(88, 80, 40) / SocietyEconomy.ShopperWeight(80, 80, 40), 0.3, 0.35);
        Assert.True(SocietyEconomy.ShopperWeight(80, 52, 40) < 0.01);
    }

    [Fact]
    public void AnOwnerSeesThatUndercuttingADearMarketWinsSales()
    {
        var world = NewWorld();
        var farms = SocietyLabor.Firms(world, "aster", "food").ToArray();
        foreach (var farm in farms)
            (farm.PriceMinor, farm.LastWeekUnitsSold) = (80, 100);
        var farm0 = farms[0];
        var cut = SocietyEconomy.ExpectedUnits(world, farm0, 72);
        var hold = SocietyEconomy.ExpectedUnits(world, farm0, 80);
        Assert.True(cut > 1.5 * hold, $"cut {cut} hold {hold}"); // 10% off nearly doubles sales

        var owner = world.Agents.Values.First(agent => agent.OwnedBusinessId is { } id && world.Agents[id].Specialty == "food"
            && world.Agents[id].Role == "business");
        var priceOption = SocietyActions.Available(world, owner).First(candidate => candidate.ActionType == "set_price");
        Assert.Contains("sold a week", priceOption.Params["effect"]!.GetValue<string>());
    }

    [Fact]
    public void ARawMaterialThatImportsCheaperIsNotRefounded()
    {
        var world = NewWorld();
        var aster = world.Society!.Nations["aster"]; // ore 0.25
        var dune = world.Society.Nations["dune"]; // ore 2.0
        Assert.True(SocietyLabor.ImportsCheaper(world, aster, "mine"));
        Assert.False(SocietyLabor.ImportsCheaper(world, dune, "mine"));
        Assert.False(SocietyLabor.ImportsCheaper(world, aster, "food")); // households only buy food at home

        foreach (var mine in SocietyLabor.Firms(world, "aster", "mine").ToArray())
            SocietyLabor.Close(world, aster, mine, "test");
        Assert.False(SocietyLabor.HasGap(world, aster, "mine"));
    }

    [Fact]
    public void OneSellerTurningABuyerAwayIsNotAGapWhileTheMarketHasStock()
    {
        var world = NewWorld();
        var aster = world.Society!.Nations["aster"];
        var firms = SocietyLabor.Firms(world, "aster", "energy").ToArray();
        foreach (var firm in firms)
            (firm.PriceMinor, firm.LastWeekUnitsSold, firm.Inventory["energy"], firm.LastWeekTurnedAway) = (75, 100, 500, 0);
        var idle = world.Agents.Values.First(agent => agent.CountryId == "aster" && agent.Role == "citizen" && agent.Occupation == "energy_worker");
        (idle.EmployerId, idle.UnemployedSinceTick) = (null, 0);

        firms[0].LastWeekTurnedAway = 1;
        Assert.False(SocietyLabor.HasGap(world, aster, "energy"));
        firms[0].LastWeekTurnedAway = 30; // a tenth of the market's buyers
        Assert.True(SocietyLabor.HasGap(world, aster, "energy"));
    }

    [Fact]
    public void ABankWithFewLoansCannotHireOrOutbidItsLoansForDeposits()
    {
        var world = NewWorld();
        var bank = world.Agents["aster_bank"];
        var owner = world.Agents[bank.OwnerId!];
        bank.DepositRatePct = SocietyBank.DepositRates.Min();
        var options = SocietyActions.Available(world, owner);

        Assert.DoesNotContain(options, candidate => candidate.ActionType == "set_headcount" && candidate.Params["change"]!.GetValue<int>() > 0);
        Assert.DoesNotContain(options, candidate => candidate.ActionType == "set_deposit_rate");
    }

    [Fact]
    public void AnImportFillsWhatIsLeftWhenTheSellerSoldSomeMeanwhile()
    {
        var world = NewWorld();
        var buyer = world.Agents["aster_food"];
        foreach (var firm in world.InCountry("aster").Where(agent => agent.Role == "business" && agent.Specialty is "food" or "energy"))
            firm.PriceMinor = SocietyFactory.BasePrice(firm.Specialty!) * 3; // dear at home, so imports land cheaper
        var job = SocietyEngine.Observe(world, buyer.Id, DateTimeOffset.UtcNow);
        var imports = job.Candidates.Where(candidate => candidate.ActionType == "import_goods").ToArray();
        Assert.NotEmpty(imports);
        Assert.All(imports, candidate => Assert.Contains("lands at about", candidate.Params["effect"]!.GetValue<string>()));
        Assert.Equal(imports.Length, imports.DistinctBy(candidate => (candidate.Params["good"]!.GetValue<string>(), candidate.Params["quantity"]!.GetValue<int>())).Count());

        var pick = imports.MaxBy(candidate => candidate.Params["quantity"]!.GetValue<int>())!;
        var seller = world.Agents[pick.Params["seller_id"]!.GetValue<string>()];
        var good = pick.Params["good"]!.GetValue<string>();
        var left = pick.Params["quantity"]!.GetValue<int>() / 2;
        var rival = world.Agents.Values.First(agent => agent.Role == "business" && agent.CountryId == seller.CountryId && agent != seller);
        rival.Inventory[good] += seller.Inventory[good] - left; // sold to someone else before this decision lands
        seller.Inventory[good] = left;
        var record = SocietyEngine.Apply(world, job, new DecisionResult(job.RequestId,
            new ActionProposal(pick.ActionType, pick.Params, "test", []), "rule", "test", 0), DateTimeOffset.UtcNow);

        Assert.True(record.Accepted, record.Code);
        Assert.Equal(left, world.Society!.Cargo.Single().Quantity);
    }

    [Fact]
    public void BenefitsRiseWithTheFullCostOfLiving()
    {
        var world = NewWorld();
        var aster = world.Society!.Nations["aster"];
        (aster.UnemploymentBenefitMinor, aster.WelfareMinor) = (300, 100);
        foreach (var farm in SocietyLabor.Firms(world, "aster", "food"))
            farm.PriceMinor = 80; // food at twice its base price
        world.Tick = SocietyLabor.IndexationDays;

        SocietyLabor.Week(world, aster, world.Agents["aster_government"], world.InCountry("aster").Where(SocietyLabor.IsResident).ToArray());

        // A week of food and energy went from 7 x (40 + 25) to 7 x (80 + 25): benefits rise the full 61.5%.
        Assert.Equal((300 * 735 + 227) / 455, aster.UnemploymentBenefitMinor);
        Assert.Equal((100 * 735 + 227) / 455, aster.WelfareMinor);
        var benefit = SocietyActions.Available(world, world.Agents["aster_government"])
            .Single(candidate => candidate.ActionType == "set_unemployment_benefit").Params["amount_minor"]!;
        Assert.True(benefit["max"]!.GetValue<int>() >= aster.UnemploymentBenefitMinor);
    }

    [Fact]
    public void ALeverLeftAtItsCurrentLevelIsAHoldNotAPolicy()
    {
        var world = NewWorld();
        var government = world.Agents["aster_government"];
        DecisionRecord Choose(int amount)
        {
            var job = SocietyEngine.Observe(world, government.Id, DateTimeOffset.UtcNow);
            var action = new ActionProposal("fund_services", new System.Text.Json.Nodes.JsonObject { ["amount_minor"] = amount }, "test", []);
            return SocietyEngine.Apply(world, job, new DecisionResult(job.RequestId, action, "rule", "test", 0), DateTimeOffset.UtcNow);
        }

        var current = world.Society!.Nations["aster"].ServiceBudgetPerResidentMinor;
        Assert.True(Choose(current).Accepted);
        Assert.Empty(world.Society.Policies);
        Assert.True(world.Events.Last(item => item.EventType == "action_accepted").Payload["unchanged_lever"]!.GetValue<bool>());

        Assert.True(Choose(current == 150 ? 100 : current + 10).Accepted);
        Assert.Single(world.Society.Policies);
    }
}
