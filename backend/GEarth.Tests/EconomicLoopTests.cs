using GEarth.Core;

namespace GEarth.Tests;

public sealed class EconomicLoopTests
{
    // A full simulated year (365 daily ticks) with no agent decisions: wages, taxes, public services,
    // household spending, hiring and price drift alone must keep every country that can feed and power
    // itself running. Resource-poor countries depend on imports, which only agent decisions arrange.
    [Fact]
    public void MoneyKeepsCirculatingForAFullYear()
    {
        var world = SocietyFactory.Create("loop_test", false, 1100);
        var start = CashByCountryAndRole(world);

        for (var tick = 0; tick < 365; tick++)
            SocietyEngine.Advance(world); // asserts money and goods conservation every day

        var end = CashByCountryAndRole(world);
        foreach (var nation in world.Society!.Nations.Values.Where(nation => nation.FoodMultiplier >= 0.8 && nation.EnergyMultiplier >= 0.8))
        {
            double Ratio(params string[] roles) => roles.Sum(role => end.GetValueOrDefault((nation.Id, role)))
                / (double)roles.Sum(role => start.GetValueOrDefault((nation.Id, role)));
            var producers = Ratio("government", "business");
            var households = Ratio("citizen", "entrepreneur");
            // State firms now buy ore and fuel, including from foreign firms: part of public capital leaves
            // the country. Keep the health/QoL and employment bounds while allowing that planned outflow.
            var minimumProducerRatio = SocietyLabor.IsPlanned(nation) ? 0.15 : 0.3;
            Assert.True(producers > minimumProducerRatio && producers < 3, $"{nation.Id} government+business cash ratio {producers:F2}");
            // Fixed state prices now leave more savings with households as industrial input costs
            // draw down public capital; resource delivery and resident health are checked separately.
            // Banks-on resources move more opening public capital to households at fixed state prices;
            // this is a cash-distribution bound, not a productivity target. Health, food and jobs stay bounded.
            var maximumHouseholdRatio = SocietyLabor.IsPlanned(nation) ? 6.5 : 3;
            Assert.True(households > 0.3 && households < maximumHouseholdRatio, $"{nation.Id} household cash ratio {households:F2}");
            var residents = world.Agents.Values.Where(agent => agent.CountryId == nation.Id && SocietyLabor.IsResident(agent)).ToArray();
            Assert.True(residents.Average(resident => resident.Health) > 70, $"{nation.Id} health");
            Assert.True(residents.Average(resident => resident.Wellbeing) > 60, $"{nation.Id} quality of life");
            // With no decisions, a monarchy paying no unemployment benefit idles up to a fifth of its factory workers.
            Assert.True(nation.Unemployed < residents.Length * 0.2, $"{nation.Id} unemployed {nation.Unemployed}");
            // A poor raw deposit or a pharma may close and be replaced by imports: those follow comparative
            // advantage. Still require every sector households buy from at home.
            foreach (var sector in SocietyLabor.Sectors.Where(sector => sector.Id != "freight" && !SocietyLabor.Importable.Contains(sector.Id)))
                Assert.True(SocietyLabor.Firms(world, nation.Id, sector.Id).Any(), $"{nation.Id} has no {sector.Id} firm");
            var lastWeek = world.Events.Last(item => item.EventType == "economy_cycle" && item.EntityIds.Contains(nation.Id));
            Assert.True(lastWeek.Payload["wages_paid_minor"]!.GetValue<long>() > 0, $"{nation.Id} wages");
        }
    }

    [Fact]
    public void SickResidentSeesTheCheapestDoctorAndClinicUsesSupplies()
    {
        var world = SocietyFactory.Create("health_test", false, 400);
        var patient = world.Agents.Values.First(agent => agent.CountryId == "aster" && agent.Role == "citizen");
        patient.Health = 20;
        var clinic = world.Agents["aster_clinic"];
        var (cash, supplies) = (patient.CashMinor, clinic.Inventory["medicine"]);
        var price = clinic.PriceMinor;

        SocietyEngine.Advance(world);

        Assert.True(patient.Health >= 50);
        Assert.True(cash - patient.CashMinor >= price); // the visit, plus any daily shopping
        Assert.True(world.Society!.PeriodRevenueMinor[clinic.Id] > 0); // net cash can fall: it also buys supplies
        Assert.True(world.Society!.Nations["aster"].DoctorVisits >= 1);
        Assert.True(world.Society.ConsumedGoods["medicine"] >= 1 || clinic.Inventory["medicine"] < supplies);
    }

    // Utility from outcomes with diminishing returns: each extra unit adds less, so richer households buy
    // more but spread it across goods and entertainment.
    [Fact]
    public void RicherHouseholdsBuyMoreAndEachExtraUnitAddsLess()
    {
        var world = SocietyFactory.Create("qol_test", false, 1100);
        var farmers = world.Agents.Values.Where(agent => agent.CountryId == "aster" && agent.Role == "citizen"
            && agent.Occupation == "farmer").OrderBy(agent => agent.Id).ToArray();
        var (rich, modest) = (farmers[0], farmers[1]);
        SocietyEconomy.Transfer(world, world.Agents["aster_bank"], rich, 20000);

        for (var day = 0; day < SocietyLabor.DaysPerWeek; day++)
            SocietyEngine.Advance(world);

        Assert.True(rich.GoodsBoughtLastWeek + rich.ServicesBoughtLastWeek > modest.GoodsBoughtLastWeek + modest.ServicesBoughtLastWeek);
        Assert.True(rich.GoodsBoughtLastWeek > 0 && rich.ServicesBoughtLastWeek > 0);
        int Score(int goods) { modest.GoodsBoughtLastWeek = goods; return SocietyEconomy.QualityOfLife(modest, 2, false); }
        Assert.True(Score(1) - Score(0) > Score(10) - Score(9));
        Assert.True(Score(10) > Score(9) || Score(20) > Score(10)); // but more is still better
    }

    // Shoppers lean toward cheaper sellers without all going to the cheapest.
    [Fact]
    public void HouseholdsFavourCheaperSellersWithoutBuyingOnlyFromThem()
    {
        var cheap = new Agent { Id = "cheap", PriceMinor = 100 };
        var dearer = new Agent { Id = "dearer", PriceMinor = 110 };
        var expensive = new Agent { Id = "expensive", PriceMinor = 200 };
        double CheapShare(Agent other) => Enumerable.Range(0, 2000)
            .Count(index => SocietyEconomy.PickSeller([cheap, other], 7, $"buyer:{index}") == cheap) / 2000.0;

        Assert.InRange(CheapShare(dearer), 0.5, 0.7); // 10% dearer keeps about 40% of custom
        Assert.InRange(CheapShare(expensive), 0.9, 1.0); // twice the price keeps almost none
    }

    // Deposits count as their holders' money; the bank's reserves (mostly owed to depositors) stand apart.
    private static Dictionary<(string Country, string Role), long> CashByCountryAndRole(World world) =>
        world.Agents.Values.GroupBy(agent => (agent.CountryId, SocietyBank.IsBank(agent) ? "bank" : agent.Role))
            .ToDictionary(group => group.Key, group => group.Sum(agent => agent.CashMinor + agent.DepositMinor));
}
