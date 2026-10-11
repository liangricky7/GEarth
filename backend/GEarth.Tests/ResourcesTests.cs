using System.Text.Json;
using GEarth.Core;

namespace GEarth.Tests;

public sealed class ResourcesTests
{
    private static World Create(int population = 1100) => SocietyFactory.Create("resources", false, population);

    // Remove market supply without destroying stock: governments hold it, outside firms' shelves.
    private static void WithdrawSupply(World world, string good, string? country = null)
    {
        foreach (var agent in world.Agents.Values.Where(agent => agent.Role == "business" && (country is null || agent.CountryId == country)))
        {
            world.Agents[agent.CountryId + "_government"].Inventory[good] += agent.Inventory[good];
            agent.Inventory[good] = 0;
        }
    }

    [Fact]
    public void EndowmentsAreDeterministicBalancedAndOpposedToFarmland()
    {
        var world = Create();
        var rotated = SocietyFactory.Create("rotated", false, 1100, seed: 42, rotation: 3);
        foreach (var good in PhaseContracts.RawMaterials)
        {
            var values = world.Society!.Nations.Values.Select(nation => nation.Endowment[good]).ToArray();
            Assert.All(values, value => Assert.InRange(value, 0.25, 2.5));
            Assert.Equal(1, values.Average(), precision: 10);
            Assert.Equal(3, values.Count(value => value >= 1.8));
            Assert.Equal(4, values.Count(value => value is >= 0.8 and <= 1.2));
            Assert.Equal(3, values.Count(value => value <= 0.5));
        }
        foreach (var nation in world.Society!.Nations.Values)
        {
            Assert.Equal(nation.Endowment, rotated.Society!.Nations[nation.Id].Endowment);
            Assert.True(nation.FoodMultiplier >= 1.4 || nation.Endowment.Values.Any(value => value >= 1.8), nation.Id);
        }
        Assert.All(world.Society.Nations.Values.Where(nation => nation.Endowment["ore"] >= 1.8),
            nation => Assert.True(nation.Endowment["fuel"] <= 0.5));
    }

    [Fact]
    public void MinersAreSharedByConcentrationAndTheNewMinimumBuilds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(199));
        Assert.Equal(200, Create(200).Agents.Count);
        var world = Create();
        foreach (var nation in world.Society!.Nations.Values)
        {
            var mine = SocietyLabor.Staff(world, world.Agents[nation.Id + "_mine"]).Length;
            var well = SocietyLabor.Staff(world, world.Agents[nation.Id + "_well"]).Length;
            var share = nation.Endowment["ore"] / nation.Endowment.Values.Sum();
            Assert.InRange(Math.Abs(mine - (mine + well) * share), 0, 1);
        }
        Assert.Equal("miner", SocietyLabor.SectorOf("mine").Occupation);
        Assert.Equal("miner", SocietyLabor.SectorOf("well").Occupation);
        Assert.Contains("miner", SocietyLabor.Unskilled);
        Assert.Contains("builder", SocietyLabor.Unskilled);
        Assert.Equal(1000, SocietyLabor.TableWage["miner"]);
    }

    [Fact]
    public void RawMaterialsScaleExtractionButLegacyEnergyMultiplierDoesNotScalePower()
    {
        var world = Create();
        var nation = world.Society!.Nations["aster"];
        nation.Productivity = 1;
        nation.EnergyMultiplier = 0.01;
        Assert.Equal(1, SocietyEconomy.OutputMultiplier(world, nation, "energy"));
        Assert.Equal(nation.Endowment["ore"], SocietyEconomy.OutputMultiplier(world, nation, "ore"));
        Assert.Equal(nation.Endowment["fuel"], SocietyEconomy.OutputMultiplier(world, nation, "fuel"));
        Assert.Equal(nation.FoodMultiplier, SocietyEconomy.OutputMultiplier(world, nation, "food"));
    }

    [Fact]
    public void ToolsRequireOreAndConsumeExactlyTheInputsForActualOutput()
    {
        var world = Create();
        var firm = world.Agents["cinder_tools"];
        var nation = world.Society!.Nations["cinder"];
        foreach (var person in world.Agents.Values.Where(SocietyLabor.IsResident))
            person.WorkEffort = 0;
        var worker = SocietyLabor.Staff(world, firm).First();
        worker.WorkEffort = 1;
        worker.Health = worker.Wellbeing = 100;
        WithdrawSupply(world, "ore");
        WithdrawSupply(world, "tools");
        nation.Plan["tools"] = 70;
        var before = world.Society.ProducedGoods["tools"];
        SocietyEngine.Advance(world);
        Assert.Equal(before, world.Society.ProducedGoods["tools"]);
        Assert.DoesNotContain(world.Society.Cargo, cargo => cargo.Good == "ore");
        var reserve = world.Agents["cinder_government"];
        reserve.Inventory["ore"] -= 1;
        firm.Inventory["ore"] = 1;
        var consumed = world.Society.ConsumedGoods["ore"];
        SocietyEngine.Advance(world);
        Assert.Equal(before + 1, world.Society.ProducedGoods["tools"]);
        Assert.Equal(consumed + 1, world.Society.ConsumedGoods["ore"]);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void PowerRequiresFuelAndCannotConsumeAnInputWithoutProducing()
    {
        var world = Create();
        foreach (var person in world.Agents.Values.Where(SocietyLabor.IsResident))
            person.WorkEffort = 0;
        var firm = world.Agents["cinder_energy"];
        var worker = SocietyLabor.Staff(world, firm).First();
        worker.WorkEffort = 1;
        worker.Health = worker.Wellbeing = 100;
        WithdrawSupply(world, "fuel");
        var before = world.Society!.ProducedGoods["energy"];
        SocietyEngine.Advance(world);
        Assert.Equal(before, world.Society.ProducedGoods["energy"]);
        world.Agents["cinder_government"].Inventory["fuel"] -= 2;
        firm.Inventory["fuel"] = 2;
        var used = world.Society.ConsumedGoods["fuel"];
        SocietyEngine.Advance(world);
        Assert.Equal(8, world.Society.ProducedGoods["energy"] - before);
        Assert.Equal(2, world.Society.ConsumedGoods["fuel"] - used);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void AResourcePoorCountryAutomaticallyImportsAndCountsArrivals()
    {
        var world = Create();
        var buyer = world.Agents["aster_tools"];
        WithdrawSupply(world, "ore", "aster");
        SocietyEconomy.AcquireInput(world, buyer, "ore", 5);
        var cargo = Assert.Single(world.Society!.Cargo);
        Assert.Equal("ore", cargo.Good);
        Assert.Equal(buyer.Id, cargo.BuyerId);
        Assert.True(world.Society.Nations["aster"].Endowment["ore"] <= 0.5);
        var before = JsonSerializer.SerializeToNode(SocietyResources.View(world, world.Society.Nations["aster"]), Wire.Json)!;
        Assert.Equal(0, before["imports_last_week"]!["ore"]!.GetValue<long>());
        while (cargo.Status == "in_transit")
        {
            world.Tick++;
            SocietyTrade.Advance(world);
        }
        world.Tick = (world.Tick + 6) / 7 * 7;
        var view = JsonSerializer.SerializeToNode(SocietyResources.View(world, world.Society.Nations["aster"]), Wire.Json)!;
        Assert.Equal(cargo.Quantity, view["imports_last_week"]!["ore"]!.GetValue<long>());
        Assert.True(buyer.Inventory["ore"] > 0);
        SocietyEconomy.AssertConservation(world);
        world.Tick += 7;
        view = JsonSerializer.SerializeToNode(SocietyResources.View(world, world.Society.Nations["aster"]), Wire.Json)!;
        Assert.Equal(0, view["imports_last_week"]!["ore"]!.GetValue<long>());
    }

    [Fact]
    public void PlannedStateFirmsImportRawInputsAndPlanBothExtractionSectors()
    {
        var world = Create();
        var buyer = world.Agents["cinder_energy"];
        WithdrawSupply(world, "fuel", "cinder");
        SocietyEconomy.AcquireInput(world, buyer, "fuel", 5);
        Assert.Null(buyer.OwnerId);
        Assert.Contains(world.Society!.Cargo, cargo => cargo.BuyerId == buyer.Id && cargo.Good == "fuel");
        var nation = world.Society.Nations["cinder"];
        foreach (var sector in new[] { "mine", "well" })
        {
            Assert.Contains(sector, SocietyPlanning.PlannedSectors);
            Assert.True(SocietyPlanning.Demand(world, nation, sector) > 0);
            Assert.True(SocietyPlanning.Option(world, nation).Params.ContainsKey(sector));
        }
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void ShippingScalesWithQuantityDistanceAndCompletedPortCondition()
    {
        var world = Create();
        var buyer = world.Agents["aster_tools"];
        var shortPath = new PathPlan([], 2, 100, "cheapest");
        var longPath = shortPath with { CostMinor = 200 };
        Assert.Equal(30, SocietyResources.ShippingCost(world, buyer, shortPath, 10));
        Assert.Equal(120, SocietyResources.ShippingCost(world, buyer, shortPath, 100));
        Assert.Equal(40, SocietyResources.ShippingCost(world, buyer, longPath, 10));
        var project = new Project { Type = "port", Level = 1 };
        world.Society!.Nations["aster"].Infrastructure.Add(project);
        Assert.Equal(120, SocietyResources.ShippingCost(world, buyer, shortPath, 100));
        project.Complete = true;
        Assert.Equal(90, SocietyResources.ShippingCost(world, buyer, shortPath, 100));
        project.Condition = 0;
        Assert.Equal(120, SocietyResources.ShippingCost(world, buyer, shortPath, 100));
        project.Condition = 100;
        project.Level = 2;
        Assert.Equal(72, SocietyResources.ShippingCost(world, buyer, shortPath, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => SocietyResources.ShippingCost(world, buyer, shortPath, 0));
    }

    [Fact]
    public void DispatchChargesTheQuoteAndTradeAgreementRemovesOnlyTariff()
    {
        var world = Create();
        var buyer = world.Agents["aster_tools"];
        var seller = world.Agents["cinder_mine"];
        var path = SocietyTrade.Path(world, seller.LocationId, buyer.LocationId, "cheapest")!;
        var quantity = 20;
        var shipping = SocietyResources.ShippingCost(world, buyer, path, quantity);
        var value = seller.PriceMinor * quantity;
        var tariff = SocietyTrade.Tariff(world, buyer, seller, value);
        Assert.Equal((value + shipping + tariff) / (double)quantity, SocietyResources.LandedUnitCost(world, buyer, seller, path, quantity));
        var cash = buyer.CashMinor;
        var freight = world.Agents["aster_freight"].CashMinor;
        SocietyTrade.Dispatch(world, buyer, seller, "ore", quantity, "cheapest", null);
        Assert.Equal(value + shipping + tariff, cash - buyer.CashMinor);
        Assert.Equal(shipping, world.Agents["aster_freight"].CashMinor - freight);
        Assert.Equal(shipping, world.Events.Last().Payload["shipping_minor"]!.GetValue<long>());
        SocietyDiplomacy.Relation(world, buyer.CountryId, seller.CountryId).TradeAgreement = true;
        Assert.Equal(0, SocietyTrade.Tariff(world, buyer, seller, value));
        Assert.Equal((value + shipping) / (double)quantity, SocietyResources.LandedUnitCost(world, buyer, seller, path, quantity));
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void MarginalPricesIncludeDomesticOrLandedInputCost()
    {
        var world = Create();
        var buyer = world.Agents["aster_tools"];
        var mine = world.Agents["aster_mine"];
        mine.PriceMinor = 10;
        var cheap = SocietyResources.InputCostPerOutput(world, buyer);
        mine.PriceMinor = 80;
        Assert.Equal(70, SocietyResources.InputCostPerOutput(world, buyer) - cheap);
        WithdrawSupply(world, "ore", "aster");
        var cost = SocietyResources.InputUnitCost(world, buyer, "ore", 20);
        Assert.True(cost > world.Agents.Values.Where(seller => seller.Role == "business" && seller.Specialty == "mine" && seller.CountryId != "aster").Min(seller => seller.PriceMinor));
    }

    [Fact]
    public void WeeklyPriceReviewPassesOnAnInputCostIncrease()
    {
        int Review(int orePrice)
        {
            var world = Create();
            var firm = world.Agents["aster_tools"];
            firm.PriceMinor = 110;
            world.Agents["aster_mine"].PriceMinor = orePrice;
            world.Society!.PeriodUnitsSold[firm.Id] = 70;
            world.Society.PeriodRevenueMinor[firm.Id] = 1000;
            firm.Inventory["tools"] = 100;
            SocietyLabor.Week(world, world.Society.Nations["aster"], world.Agents["aster_government"],
                world.InCountry("aster").Where(SocietyLabor.IsResident).ToArray());
            return firm.PriceMinor;
        }
        Assert.Equal(110, Review(10));
        Assert.True(Review(80) > Review(10));
    }

    [Fact]
    public void RawMaterialsHaveNoDirectHouseholdUtilityAndMinesCanEnterOnOpportunity()
    {
        var world = Create();
        var nation = world.Society!.Nations["aster"];
        nation.QolParts = new() { ["needs"] = 1, ["health"] = 1, ["comfort"] = 1, ["security"] = 1 };
        nation.Endowment["ore"] = 2; // entry needs a profitable margin, even when stock is empty
        var idle = world.InCountry("aster").First(agent => agent.Role == "citizen");
        idle.EmployerId = null;
        idle.Occupation = "miner";
        WithdrawSupply(world, "ore", "aster");
        Assert.True(SocietyLabor.HasGap(world, nation, "mine"));
        Assert.False(nation.QolParts.ContainsKey(SocietyLabor.QolPartOf("mine")));
        var citizen = world.InCountry("aster").First(agent => agent.Role == "citizen");
        foreach (var good in PhaseContracts.RawMaterials)
        {
            Assert.Equal(0, SocietyEconomy.Acquire(world, citizen, good, 1, true));
            Assert.Equal(0, citizen.Inventory[good]);
        }
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void ServicesConsumeOneEnergyPerTenDeliveredSessionsAndStopWithoutPower()
    {
        var world = Create();
        var firm = world.Agents["aster_entertainment"];
        WithdrawSupply(world, "energy");
        var reserve = world.Agents["aster_government"];
        reserve.Inventory["energy"] -= 2;
        firm.Inventory["energy"] = 2;
        var before = world.Society!.ConsumedGoods["energy"];
        for (var count = 0; count < 20; count++)
            Assert.True(SocietyResources.PowerSession(world, firm));
        Assert.Equal(before + 2, world.Society.ConsumedGoods["energy"]);
        Assert.False(SocietyResources.PowerSession(world, firm));
        SocietyEconomy.AssertConservation(world);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnpoweredSchoolDelaysGraduationUntilTeachingResumes(bool partTime)
    {
        var world = Create();
        var student = world.InCountry("aster").First(agent => agent.Role == "citizen" && agent.Occupation == "factory_worker");
        SocietyEconomy.Transfer(world, world.Agents["aster_bank"], student, 5000);
        var employer = student.EmployerId;
        SocietySchool.Enroll(world, student, partTime, "test");
        Assert.Equal(partTime ? employer : null, student.EmployerId);
        var school = world.Agents[student.SchoolId!];
        var graduation = student.StudentUntilTick;
        WithdrawSupply(world, "energy");
        SocietyResources.SchoolDay(world, school);
        Assert.Equal(graduation + 1, student.StudentUntilTick);
        world.Agents["aster_government"].Inventory["energy"]--;
        school.Inventory["energy"] = 1;
        var used = world.Society!.ConsumedGoods["energy"];
        SocietyResources.SchoolDay(world, school);
        Assert.Equal(graduation + 1, student.StudentUntilTick);
        Assert.Equal(used + 1, world.Society.ConsumedGoods["energy"]);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void EveryRoleCanObserveTheSharedMinerOccupationAndRawImportMenus()
    {
        var world = Create();
        foreach (var agent in world.Agents.Values.Where(SocietyLabor.DecidesForItself))
        {
            var job = SocietyEngine.Observe(world, agent.Id, DateTimeOffset.UtcNow);
            Assert.NotEmpty(job.Candidates);
            if (agent.Role == "government")
            {
                Assert.NotNull(job.Observation["country"]!["resources"]);
                Assert.Contains(SocietyResources.Note, job.Observation["note"]!.GetValue<string>());
            }
        }
        var owner = world.Agents[world.Agents["aster_tools"].OwnerId!];
        var observed = SocietyEngine.Observe(world, owner.Id, DateTimeOffset.UtcNow);
        Assert.Contains(observed.Candidates, option => option.ActionType == "import_goods" && option.Params["good"]!.GetValue<string>() == "ore");
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void SmallProductionBatchesCarryInputFractionsAcrossCheckpoints()
    {
        var world = Create();
        var mine = world.Agents["aster_mine"];
        var energy = mine.Inventory["energy"];
        var consumed = world.Society!.ConsumedGoods["energy"];
        for (var count = 0; count < 10; count++)
            Assert.Equal(1, SocietyResources.SupplyProduction(world, mine, 1));
        Assert.Equal(energy - 1, mine.Inventory["energy"]);
        Assert.Equal(consumed + 1, world.Society.ConsumedGoods["energy"]);
        // These calls only supply inputs; actual production records the ore separately.
        SocietyEconomy.AssertConservation(world);
        Assert.Equal(1, SocietyResources.SupplyProduction(world, mine, 1));
        var saved = JsonSerializer.Deserialize<World>(JsonSerializer.Serialize(world, Wire.Json), Wire.Json)!;
        mine = saved.Agents[mine.Id];
        energy = mine.Inventory["energy"];
        for (var count = 0; count < 9; count++)
            Assert.Equal(1, SocietyResources.SupplyProduction(saved, mine, 1));
        Assert.Equal(energy, mine.Inventory["energy"]);
        SocietyEconomy.AssertConservation(saved);
    }

    [Fact]
    public void AutomaticImportFindsTheLargestAffordableQuantity()
    {
        var world = Create();
        var buyer = world.Agents["aster_tools"];
        var seller = world.Agents["cinder_mine"];
        WithdrawSupply(world, "ore", "aster");
        foreach (var mine in world.Agents.Values.Where(agent => agent.Specialty == "mine"))
            mine.PriceMinor = 100;
        seller.PriceMinor = 15;
        var path = SocietyTrade.Path(world, seller.LocationId, buyer.LocationId, "cheapest")!;
        var value = 3 * seller.PriceMinor;
        var affordable = value + SocietyResources.ShippingCost(world, buyer, path, 3) + SocietyTrade.Tariff(world, buyer, seller, value);
        SocietyEconomy.Transfer(world, buyer, world.Agents["aster_government"], buyer.CashMinor - affordable);
        Assert.True(SocietyTrade.CanImport(world, buyer, seller, "ore", 3, "cheapest"));
        Assert.False(SocietyTrade.CanImport(world, buyer, seller, "ore", 4, "cheapest"));
        SocietyTrade.AutoImport(world, buyer, "ore", 4);
        var cargo = Assert.Single(world.Society!.Cargo);
        Assert.Equal(3, cargo.Quantity);
        Assert.Equal(seller.Id, cargo.SellerId);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void OwnerImportQuotesUseTheFirmAndStatePerUnitShipping()
    {
        var world = Create();
        var buyer = world.Agents["aster_tools"];
        var owner = world.Agents[buyer.OwnerId!];
        owner.LocationId = "beryl_city"; // the firm's freight quote follows its factory, not its owner
        var job = SocietyEngine.Observe(world, owner.Id, DateTimeOffset.UtcNow);
        var offer = job.Observation["import_offers"]!.AsArray().First(node => node!["good"]!.GetValue<string>() == "ore")!;
        var sellerId = offer["seller_id"]!.GetValue<string>();
        var candidate = job.Candidates.First(option => option.ActionType == "import_goods"
            && option.Params["good"]!.GetValue<string>() == "ore" && option.Params["seller_id"]!.GetValue<string>() == sellerId);
        var quantity = candidate.Params["quantity"]!.GetValue<int>();
        var seller = world.Agents[sellerId];
        var path = SocietyTrade.Path(world, seller.LocationId, buyer.LocationId, "cheapest")!;
        var total = SocietyResources.ShippingCost(world, buyer, path, quantity);
        Assert.Equal((long)Math.Ceiling(total / (double)quantity), offer["shipping_minor"]!.GetValue<long>());
        Assert.Equal(total, offer["shipping_total_minor"]!.GetValue<long>());
        Assert.Equal(quantity, offer["quoted_quantity"]!.GetValue<int>());
    }

    [Fact]
    public void RawStockIsVisibleInSnapshotsAndTheDailyHistory()
    {
        var world = Create();
        var snapshot = JsonSerializer.SerializeToNode(world.Snapshot(), Wire.Json)!;
        Assert.True(snapshot["metrics"]!["held_goods"]!["ore"]!.GetValue<long>() > 0);
        Assert.True(snapshot["metrics"]!["held_goods"]!["fuel"]!.GetValue<long>() > 0);
        var directory = Path.Combine(Path.GetTempPath(), "gearth-resources-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "history.gz");
        try
        {
            using (var writer = new HistoryWriter(path, world))
                writer.Day(world);
            using var file = File.OpenRead(path);
            using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            reader.ReadLine(); // header
            var day = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(reader.ReadLine()!)!;
            foreach (var sector in new[] { "mine", "well" })
            {
                var firm = world.Agents["aster_" + sector];
                var row = day["agents"]!.AsArray().First(item => item![0]!.GetValue<string>() == firm.Id)!;
                Assert.Equal(firm.Inventory[SocietyLabor.SectorOf(sector).Good!], row[11]!.GetValue<int>());
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void FullYearRuleEconomyFeedsEveryCountryAndConservesSixGoods()
    {
        var world = Create();
        var meals = world.Society!.Nations.Keys.ToDictionary(country => country, _ => (Fed: 0, Days: 0));
        for (var day = 0; day < 365; day++)
        {
            SocietyEngine.Advance(world); // cash and all six goods checked on every day, including cargo
            if (world.Tick > 365 - 28)
                foreach (var nation in world.Society.Nations.Values)
                {
                    var residents = world.InCountry(nation.Id).Where(SocietyLabor.IsResident).ToArray();
                    var previous = meals[nation.Id];
                    meals[nation.Id] = (previous.Fed + residents.Count(person => !person.Hungry), previous.Days + residents.Length);
                }
        }
        foreach (var nation in world.Society.Nations.Values)
        {
            var residents = world.InCountry(nation.Id).Where(SocietyLabor.IsResident).ToArray();
            var fed = meals[nation.Id].Fed / (double)meals[nation.Id].Days;
            // Keep the existing rule-only calibration's 20% unemployment tolerance: no agent changes
            // the monarchy's missing benefits here. Check actual meals, adequate national food supply
            // and health together; this is not a promise of zero hunger for every individual.
            Assert.True(fed > 0.8, $"{nation.Id}: final-month meals {fed:P1}");
            Assert.True(residents.Average(person => person.Health) > 70, $"{nation.Id}: health");
            Assert.True(SocietyLabor.Firms(world, nation.Id, "food").Sum(firm => firm.Inventory["food"]) >= residents.Length * 3,
                $"{nation.Id}: fewer than three days of food on the shelves");
        }
        foreach (var good in PhaseContracts.RawMaterials)
        {
            Assert.True(world.Society.ProducedGoods[good] > 0);
            Assert.True(world.Society.ConsumedGoods[good] > 0);
        }
        Assert.Contains(world.Society.Cargo, cargo => cargo.Good == "ore" && cargo.Status == "delivered");
        Assert.Contains(world.Society.Cargo, cargo => cargo.Good == "fuel" && cargo.Status == "delivered");
        SocietyEconomy.AssertConservation(world);
    }
}
