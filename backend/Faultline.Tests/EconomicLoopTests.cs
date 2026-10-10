using Faultline.Core;

namespace Faultline.Tests;

public sealed class EconomicLoopTests
{
    // A full simulated year (365 daily ticks) with no agent decisions: wages, taxes, public services,
    // household spending, hiring and price drift alone must keep every country's economy running.
    [Fact]
    public void MoneyKeepsCirculatingForAFullYear()
    {
        var world = SocietyFactory.Create("loop_test", false, 1100);
        var start = CashByCountryAndRole(world);

        for (var tick = 0; tick < 365; tick++)
            SocietyEngine.Advance(world); // asserts money and goods conservation every day

        var end = CashByCountryAndRole(world);
        foreach (var nation in world.Society!.Nations.Values)
        {
            double Ratio(params string[] roles) => roles.Sum(role => end.GetValueOrDefault((nation.Id, role)))
                / (double)roles.Sum(role => start.GetValueOrDefault((nation.Id, role)));
            var producers = Ratio("government", "business");
            var households = Ratio("citizen", "entrepreneur");
            Assert.True(producers is > 0.3 and < 3, $"{nation.Id} government+business cash ratio {producers:F2}");
            Assert.True(households is > 0.3 and < 3, $"{nation.Id} household cash ratio {households:F2}");
            var residents = world.Agents.Values.Where(agent => agent.CountryId == nation.Id && SocietyLabor.IsResident(agent)).ToArray();
            Assert.True(residents.Average(resident => resident.Health) > 60, $"{nation.Id} health");
            Assert.True(nation.Unemployed < residents.Length * 0.3, $"{nation.Id} unemployed {nation.Unemployed}");
            foreach (var sector in SocietyLabor.Sectors.Where(sector => sector.Id != "freight"))
                Assert.True(SocietyLabor.Firms(world, nation.Id, sector.Id).Any(), $"{nation.Id} has no {sector.Id} firm");
            var lastWeek = world.Events.Last(item => item.EventType == "economy_cycle" && item.EntityIds.Contains(nation.Id));
            Assert.True(lastWeek.Payload["wages_paid_minor"]!.GetValue<long>() > 0, $"{nation.Id} wages");
        }
    }

    [Fact]
    public void SickResidentSeesTheCheapestDoctorAndClinicUsesSupplies()
    {
        var world = SocietyFactory.Create("health_test", false, 300);
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

    private static Dictionary<(string Country, string Role), long> CashByCountryAndRole(World world) =>
        world.Agents.Values.GroupBy(agent => (agent.CountryId, agent.Role))
            .ToDictionary(group => group.Key, group => group.Sum(agent => agent.CashMinor));
}
