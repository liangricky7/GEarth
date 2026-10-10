using Faultline.Core;

namespace Faultline.Tests;

public sealed class SocietyTests
{
    [Fact]
    public void ThousandAgentWorldHasTenCountriesAndSeventyInstitutions()
    {
        var world = SocietyFactory.Create("society_test", false, 1000);

        Assert.Equal(1000, world.Agents.Count);
        Assert.Equal(10, world.Society!.Nations.Count);
        Assert.Equal(70, world.Agents.Values.Count(agent => agent.Role != "citizen"));
        Assert.Equal(2, Wire.Node(world.Snapshot())["run"]!["stage"]!.GetValue<int>());
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void EconomicCyclesPreserveCashAndAccountForAllGoods()
    {
        var world = SocietyFactory.Create("economy_test", false, 1000);

        for (var tick = 0; tick < 120; tick++)
            SocietyEngine.Advance(world);

        SocietyEconomy.AssertConservation(world);
        Assert.True(world.Society!.ProducedGoods["food"] > 0);
        Assert.True(world.Society.ConsumedGoods["energy"] > 0);
        Assert.Contains(world.Events, item => item.EventType == "resource_crisis");
        Assert.Contains(world.Events, item => item.EventType == "election_result");
    }
}
