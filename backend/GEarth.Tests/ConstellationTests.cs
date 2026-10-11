using GEarth.Core;

namespace GEarth.Tests;

public sealed class ConstellationTests
{
    [Fact]
    public void SnapshotUsesAllCitizensForTotalsAndKeepsAStableBoundedSample()
    {
        var world = SocietyFactory.Create("constellation", false, 1100);
        var citizens = world.Agents.Values.Where(agent => agent.Role == "citizen" && agent.CountryId == "aster")
            .OrderBy(agent => agent.Id, StringComparer.Ordinal).ToArray();
        Assert.True(citizens.Length > 48);
        for (var i = 0; i < citizens.Length; i++) citizens[i].NeedsMet = i % 3;
        citizens[0].NeedsMet = 7; // Unknown persisted values must not be painted as healthy citizens.
        var count = world.Agents.Count;
        var seq = world.Seq;
        var snapshot = Wire.Node(world.Snapshot());
        var country = snapshot["constellation"]!.AsArray().Single(item => item!["country_id"]!.GetValue<string>() == "aster")!;
        Assert.Equal(citizens.Length, country["citizen_count"]!.GetValue<int>());
        Assert.Equal(citizens.Count(agent => agent.NeedsMet == 2), country["met"]!.GetValue<int>());
        Assert.Equal(citizens.Count(agent => agent.NeedsMet == 1), country["partial"]!.GetValue<int>());
        Assert.Equal(citizens.Count(agent => agent.NeedsMet == 0), country["unmet"]!.GetValue<int>());
        Assert.Equal(1, country["unknown"]!.GetValue<int>());
        var sample = country["citizens"]!.AsArray();
        Assert.Equal(48, sample.Count);
        var ids = sample.Select(item => item!["id"]!.GetValue<string>()).ToArray();
        Assert.Equal(48, ids.Distinct().Count());
        Assert.All(sample, item =>
        {
            var actual = world.Agents[item!["id"]!.GetValue<string>()];
            Assert.Equal(actual.CountryId, item["country_id"]!.GetValue<string>());
            Assert.Equal(actual.NeedsMet, item["needs_met"]!.GetValue<int>());
            Assert.Equal(actual.Health, item["health"]!.GetValue<int>());
            Assert.Equal(actual.EmployerId, (string?)item["employer_id"]);
        });
        Assert.Equal(snapshot["constellation"]!.ToJsonString(), Wire.Node(world.Snapshot())["constellation"]!.ToJsonString());
        Assert.Equal(count, world.Agents.Count);
        Assert.Equal(seq, world.Seq);
    }

    [Fact]
    public void EmptyCountriesAndSmallPopulationsDoNotInventCitizenDots()
    {
        var world = SocietyFactory.Create("empty_constellation", false, 200);
        foreach (var citizen in world.Agents.Values.Where(agent => agent.Role == "citizen" && agent.CountryId == "aster"))
            citizen.CountryId = "beryl";
        var countries = Wire.Node(world.Snapshot())["constellation"]!.AsArray();
        var empty = countries.Single(item => item!["country_id"]!.GetValue<string>() == "aster")!;
        Assert.Equal(0, empty["citizen_count"]!.GetValue<int>());
        Assert.Empty(empty["citizens"]!.AsArray());
        foreach (var country in countries)
        {
            var count = country!["citizen_count"]!.GetValue<int>();
            Assert.Equal(Math.Min(48, count), country["citizens"]!.AsArray().Count);
        }
    }
}
