using Faultline.Core;

namespace Faultline.Tests;

public sealed class EconomicLoopTests
{
    // 120 economic cycles with no agent decisions: wages, taxes, public services and household
    // spending alone must keep money moving in countries that can feed and power themselves.
    [Fact]
    public void MoneyKeepsCirculatingInSelfSufficientCountries()
    {
        var world = SocietyFactory.Create("loop_test", false, 1100);
        var start = CashByCountryAndRole(world);

        for (var tick = 0; tick < 600; tick++)
            SocietyEngine.Advance(world);

        var end = CashByCountryAndRole(world);
        var selfSufficient = world.Society!.Nations.Values
            .Where(nation => nation.FoodMultiplier >= 0.8 && nation.EnergyMultiplier >= 0.8).ToArray();
        Assert.NotEmpty(selfSufficient);
        foreach (var nation in selfSufficient)
        {
            // Planned economies move business cash to the state, so producers are judged with the treasury.
            double Ratio(params string[] roles) => roles.Sum(role => end[(nation.Id, role)])
                / (double)roles.Sum(role => start[(nation.Id, role)]);
            var producers = Ratio("government", "business");
            var households = Ratio("citizen");
            Assert.True(producers is > 0.3 and < 3, $"{nation.Id} government+business cash ratio {producers:F2}");
            Assert.True(households is > 0.3 and < 3, $"{nation.Id} household cash ratio {households:F2}");
            Assert.True(nation.UnmetNeedsLastCycle < nation.Population / 2, $"{nation.Id} unmet {nation.UnmetNeedsLastCycle}");
            Assert.True(nation.SalesTaxCollectedMinor > 0 && nation.ProfitTaxCollectedMinor > 0, $"{nation.Id} taxes");
            Assert.True(nation.PublicDoctorVisits > 0, $"{nation.Id} public care");
        }
        var lastCycle = world.Events.Last(item => item.EventType == "economy_cycle" && item.EntityIds.Contains(selfSufficient[0].Id));
        Assert.True(lastCycle.Payload["wages_paid_minor"]!.GetValue<long>() > 0);
    }

    [Fact]
    public void SickWorkersEarnLessAndDoctorVisitsRestoreHealth()
    {
        var world = SocietyFactory.Create("health_test", false, 170);
        var sick = world.Agents["aster_citizen_00001"];
        var healthy = world.Agents["aster_citizen_00009"]; // same employer: farmers are 1, 9, 17…
        Assert.Equal(sick.EmployerId, healthy.EmployerId);
        sick.Health = 10;
        sick.HealthThreshold = 0; // no private visit, so the cycle's pay reflects the illness
        world.Society!.Nations["aster"].ServiceBudgetPerResidentMinor = 0;
        var (sickCash, healthyCash) = (sick.CashMinor, healthy.CashMinor);

        while (world.Tick < 5)
            SocietyEngine.Advance(world);

        Assert.True(sick.CashMinor - sickCash < healthy.CashMinor - healthyCash);

        sick.HealthThreshold = 50;
        var visits = world.Society.Nations["aster"].DoctorVisits;
        while (world.Tick < 10)
            SocietyEngine.Advance(world);

        Assert.True(world.Society.Nations["aster"].DoctorVisits > visits);
        Assert.True(sick.Health >= 50);
    }

    private static Dictionary<(string Country, string Role), long> CashByCountryAndRole(World world) =>
        world.Agents.Values.GroupBy(agent => (agent.CountryId, agent.Role))
            .ToDictionary(group => group.Key, group => group.Sum(agent => agent.CashMinor));
}
