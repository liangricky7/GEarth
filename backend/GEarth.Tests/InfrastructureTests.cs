using GEarth.Core;

namespace GEarth.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public void SharedContractScalesCompletedProjectsByLevelAndCondition()
    {
        var nation = new Nation();
        nation.Infrastructure.Add(new Project { Type = "roads", Level = 2, Condition = 50, Complete = true });
        nation.Infrastructure.Add(new Project { Type = "irrigation" });
        Assert.Equal(0.04, PhaseContracts.Boost(nation, "food"), 6);
        Assert.Equal(46500, PhaseContracts.CostOf("roads", 2));
        Assert.Equal(1, PhaseContracts.Endowment(nation, "ore"));
    }

    [Fact]
    public void PopulationMinimumReservesRoomForAllPhases()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SocietyFactory.Create("too_small", false, 199));
        var world = SocietyFactory.Create("minimum", false, 200);
        Assert.Equal(200, world.Agents.Count);
        SocietyEconomy.AssertConservation(world);
    }
}
