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
        var benefits = SocietyActions.Available(world, world.Agents["aster_government"])
            .Where(candidate => candidate.ActionType == "set_unemployment_benefit")
            .Select(candidate => candidate.Params["amount_minor"]!.GetValue<int>()).Order().ToArray();
        Assert.Equal([aster.UnemploymentBenefitMinor - 300, aster.UnemploymentBenefitMinor + 300], benefits); // around the indexed level
    }

    [Fact]
    public void LeversOfferACutAndARaiseWithTheirWeeklyEffectNotTheCurrentLevel()
    {
        var world = NewWorld();
        var government = world.Agents["aster_government"];
        var nation = world.Society!.Nations["aster"];
        var job = SocietyEngine.Observe(world, government.Id, DateTimeOffset.UtcNow);
        var services = job.Candidates.Where(candidate => candidate.ActionType == "fund_services").ToArray();
        Assert.Equal([nation.ServiceBudgetPerResidentMinor - 50, nation.ServiceBudgetPerResidentMinor + 50],
            services.Select(candidate => candidate.Params["amount_minor"]!.GetValue<int>()).Order());
        Assert.All(services, candidate => Assert.Contains("a week", candidate.Params["effect"]!.GetValue<string>()));
        var current = new System.Text.Json.Nodes.JsonObject { ["amount_minor"] = nation.ServiceBudgetPerResidentMinor };
        Assert.DoesNotContain(job.Candidates, candidate => SocietyActions.Matches(candidate, "fund_services", current));

        var raise = services.Single(candidate => candidate.Params["amount_minor"]!.GetValue<int>() > nation.ServiceBudgetPerResidentMinor);
        var action = new ActionProposal("fund_services", raise.Params, "test", []);
        Assert.True(SocietyEngine.Apply(world, job, new DecisionResult(job.RequestId, action, "rule", "test", 0), DateTimeOffset.UtcNow).Accepted);
        Assert.Single(world.Society.Policies);
    }

    [Fact]
    public void GovernmentsSeeWhatIsGoingWrongInPlainWords()
    {
        var world = NewWorld();
        var government = world.Agents["aster_government"];
        var nation = world.Society!.Nations["aster"];
        (nation.LaborForce, nation.Unemployed, nation.UnemploymentBenefitMinor, nation.HungryResidentsLastWeek) = (90, 25, 0, 4);
        (government.LastWeekInMinor, government.LastWeekOutMinor) = (2000, 1000);
        var problems = SocietyEngine.Observe(world, government.Id, DateTimeOffset.UtcNow).Observation["country"]!["problems"]!
            .AsArray().Select(item => item!.GetValue<string>()).ToArray();
        Assert.Contains("27% of the labor force is out of work with no unemployment benefit", problems);
        Assert.Contains("4 residents went hungry last week", problems);
        Assert.Contains(problems, problem => problem.StartsWith("the treasury grew by 1000 last week"));
    }

    // At twice the base price shoppers lean hard away from the dearer seller, so a 10% raise loses more custom
    // than it gains in margin: that move is not offered, while the cut says how much more it should earn.
    [Fact]
    public void OwnersAreNotOfferedAPriceMoveThatSellsNoMoreAndEarnsLess()
    {
        var world = NewWorld();
        var firms = SocietyLabor.Firms(world, "aster", "food").ToArray();
        var firm = firms.First(farm => farm.OwnerId is { } owner && world.Agents[owner].Role == "entrepreneur");
        foreach (var farm in firms)
            (farm.PriceMinor, farm.LastWeekUnitsSold) = (80, 100);
        var prices = SocietyActions.Available(world, world.Agents[firm.OwnerId!]).Where(candidate => candidate.ActionType == "set_price").ToArray();
        var cut = Assert.Single(prices);
        Assert.Equal(-10, cut.Params["change_pct"]!.GetValue<int>());
        Assert.Contains("more than now", cut.Params["effect"]!.GetValue<string>());
    }

    [Fact]
    public void AClosedFirmsLateReceiptsAreReportedOnceAWeek()
    {
        var world = NewWorld();
        var firm = world.Agents["aster_tools"];
        SocietyLabor.Close(world, world.Society!.Nations["aster"], firm, "test");
        int Reports() => world.Events.Count(item => item.EventType == "estate_settled");
        firm.Inventory["tools"] = Math.Max(1, firm.Inventory["tools"]); // stock left to sell keeps the estate open
        world.Tick = 8;
        foreach (var amount in new[] { 100, 200 })
        {
            SocietyEconomy.Transfer(world, world.Agents["aster_government"], firm, amount);
            SocietyEstates.Settle(world, firm);
            SocietyEstates.Report(world, firm);
        }
        Assert.Equal(0, Reports());
        world.Tick = 14;
        SocietyEstates.Report(world, firm);
        Assert.Equal(1, Reports());
        Assert.Equal(300, world.Events.Last(item => item.EventType == "estate_settled").Payload["payout_minor"]!.GetValue<long>());
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void StateFirmsAreNotOfferedPayments()
    {
        var world = NewWorld();
        var stateFirm = world.Agents["cinder_food"];
        Assert.True(SocietyLabor.DecidesForItself(stateFirm));
        Assert.DoesNotContain(SocietyActions.Available(world, stateFirm), candidate => candidate.ActionType == "pay");
        Assert.Contains(SocietyActions.Available(world, world.Agents["aster_citizen_00001"]), candidate => candidate.ActionType == "pay");
    }
}
