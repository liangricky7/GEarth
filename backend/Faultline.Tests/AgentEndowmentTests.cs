using Faultline.Core;

namespace Faultline.Tests;

public sealed class AgentEndowmentTests
{
    [Fact]
    public void SeededProfilesAreBoundedIndependentAndCountryMatched()
    {
        var world = SocietyFactory.Create("weights", false, 1100);
        var repeat = SocietyFactory.Create("repeat", false, 1100);
        var rotated = SocietyFactory.Create("rotated", false, 1100, rotation: 1);
        var residents = world.Agents.Values.Where(agent => agent.Role == "citizen").ToArray();
        Assert.True(residents.Select(agent => agent.CashMinor).Distinct().Count() > 20);
        foreach (var resident in residents)
        {
            Assert.InRange(resident.Weights.Wealth, 0.25, 4);
            Assert.InRange(resident.Weights.Productivity, 0.6, 1.4);
            Assert.InRange(resident.Weights.BargainingPower, 0.75, 1.25);
            Assert.Equal(1, resident.Weights.Preferences.Values.Sum(), 10);
            Assert.Equal(Wire.Node(resident.Weights).ToJsonString(), Wire.Node(repeat.Agents[resident.Id].Weights).ToJsonString());
            Assert.Equal(Wire.Node(resident.Weights).ToJsonString(), Wire.Node(rotated.Agents[resident.Id].Weights).ToJsonString());
        }
        Assert.Equal(Wire.Node(world.Agents["aster_citizen_00001"].Weights).ToJsonString(),
            Wire.Node(world.Agents["jade_citizen_00001"].Weights).ToJsonString());
        foreach (var country in residents.GroupBy(agent => agent.CountryId))
        {
            var allResidents = world.Agents.Values.Where(agent => agent.CountryId == country.Key && SocietyLabor.IsResident(agent)).ToArray();
            Assert.Equal(allResidents.Sum(agent => 2L * SocietyLabor.TableWage[agent.FormerOccupation ?? agent.Occupation]),
                allResidents.Sum(agent => agent.CashMinor));
        }
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void EqualWeightsRetainLegacyBalancesAndOldSavedAgentsGetNeutralDefaults()
    {
        var citizens = new[] { new Agent(), new Agent(), new Agent() };
        AgentEndowments.AllocateCash(citizens);
        Assert.All(citizens, citizen => Assert.Equal(2000, citizen.CashMinor));
        var old = System.Text.Json.JsonSerializer.Deserialize<Agent>("{\"id\":\"old\"}", Wire.Json)!;
        Assert.Equal(1, old.Weights.Productivity);
        Assert.Equal(1, old.Weights.BargainingPower);
    }

    [Fact]
    public void FixedSalaryPayrollDoesNotChangeWithBargainingWeights()
    {
        var world = SocietyFactory.Create("wages", false, 1100);
        var alternate = Wire.Copy(world);
        foreach (var worker in world.Agents.Values.Where(SocietyLabor.IsResident))
            worker.Weights.BargainingPower = 0.75;
        foreach (var worker in alternate.Agents.Values.Where(SocietyLabor.IsResident))
            worker.Weights.BargainingPower = 1.25;
        SocietyEconomy.Week(world);
        SocietyEconomy.Week(alternate);
        foreach (var agent in world.Agents.Values)
        {
            Assert.Equal(agent.SalaryMinor, alternate.Agents[agent.Id].SalaryMinor);
            Assert.Equal(agent.CashMinor, alternate.Agents[agent.Id].CashMinor);
        }
        SocietyEconomy.AssertConservation(world);
        SocietyEconomy.AssertConservation(alternate);
    }

    [Fact]
    public void ProductivityChangesOutputWhileBargainingPowerDoesNot()
    {
        World Create(double productivity, double bargaining)
        {
            var world = SocietyFactory.Create("production", false, 1100);
            foreach (var agent in world.Agents.Values.Where(SocietyLabor.IsResident))
            {
                agent.Weights = new AgentWeights { Productivity = productivity, BargainingPower = bargaining };
                agent.TargetStock = 2;
            }
            foreach (var firm in world.Agents.Values.Where(agent => agent.Specialty == "food"))
            {
                world.Agents[firm.CountryId + "_government"].Inventory["food"] += firm.Inventory["food"];
                firm.Inventory["food"] = 0;
                firm.LastWeekUnitsSold = 1000;
            }
            SocietyEconomy.Day(world);
            SocietyEconomy.AssertConservation(world);
            return world;
        }
        var low = Create(0.6, 1);
        var high = Create(1.4, 1);
        Assert.True(high.Society!.ProducedGoods["food"] > low.Society!.ProducedGoods["food"]);
        Assert.Equal(high.Society.ProducedGoods["food"], Create(1.4, 0.75).Society!.ProducedGoods["food"]);
    }

    [Fact]
    public void ProfilesAppearInObservationAndNotesWithoutChangingTurnEligibility()
    {
        var world = SocietyFactory.Create("observations", true, 1100);
        var citizen = world.Agents["aster_citizen_00001"];
        var now = world.Society!.StartedAt;
        var due = SocietyScheduler.NextTurnTick(world, citizen);
        var job = SocietyEngine.Observe(world, citizen.Id, now);
        Assert.NotNull(job.Observation["self"]!["weights"]!["preferences"]);
        citizen.Weights.Wealth = 4;
        citizen.Weights.BargainingPower = 1.25;
        Assert.Equal(due, SocietyScheduler.NextTurnTick(world, citizen));
        Assert.Contains(RunAnalysis.Create(world, world.Events).Observations, note => note.Category == "inequality");
        var preserved = Wire.Copy(world);
        Assert.Equal(citizen.Weights.Wealth, preserved.Agents[citizen.Id].Weights.Wealth);
    }
}
