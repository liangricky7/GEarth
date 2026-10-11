using GEarth.Api;
using GEarth.Core;
using Xunit.Abstractions;

namespace GEarth.Tests;

public sealed class Phase3YearTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RuleDecisionYearConservesMoneyAndGoodsWithMilitaryPoliciesAndWar()
    {
        var world = SocietyFactory.Create("phase3_rule_year", false, 1100);
        var war = SocietyDiplomacy.Relation(world, "aster", "beryl");
        war.AtWar = true;
        war.Score = -100;
        war.Aggressor = "aster";
        world.Society!.Nations["aster"].Missiles = 1;
        foreach (var soldier in SocietyWar.Soldiers(world, world.Society.Nations["aster"])) soldier.WorkEffort = 0;
        SocietyDiplomacy.Relation(world, "dune", "haven").Score = -30;
        // Seed explicit available policies so annual coverage does not depend on random selection of rare actions.
        foreach (var action in new[] { "order_missile", "send_gift" })
        {
            var job = SocietyEngine.Observe(world, "dune_government", DateTimeOffset.UnixEpoch);
            var candidate = job.Candidates.First(item => item.ActionType == action);
            var record = SocietyEngine.Apply(world, job, new(job.RequestId,
                new(candidate.ActionType, SocietyActions.Resolve(candidate), "phase3 annual scenario", []), "rule", "none", 0),
                DateTimeOffset.UnixEpoch);
            Assert.True(record.Accepted, record.Code);
        }
        using var client = new HttpClient();
        for (var day = 1; day <= 365; day++)
        {
            SocietyEngine.Advance(world);
            if (day == 1 || day % SocietyLabor.DaysPerWeek == 0)
                foreach (var agent in SocietyScheduler.WeeklyCohort(world).Where(SocietyLabor.DecidesForItself).ToArray())
                {
                    var job = SocietyEngine.Observe(world, agent.Id, DateTimeOffset.UnixEpoch);
                    var result = await WorkerClient.Decide(client, job, false, CancellationToken.None);
                    SocietyEngine.Apply(world, job, result, DateTimeOffset.UnixEpoch);
                }
            SocietyEconomy.AssertConservation(world);
        }
        Assert.Equal(365, world.Tick);
        foreach (var type in new[] { "war_week", "war_ended", "gift_sent", "missile_ready" })
            Assert.Contains(world.Events, item => item.EventType == type);
        var notes = RunAnalysis.Create(world, world.Events, "completed");
        Assert.Contains(notes.Observations, item => item.Category == "war_and_unrest");
        Assert.All(notes.Observations.Where(item => item.Category == "war_and_unrest").SelectMany(item => item.EvidenceIds),
            id => Assert.Contains(world.Events, item => item.EventId == id));
        Assert.Equal(0, notes.AcceptedModelDecisions);
        output.WriteLine(Wire.Node(new
        {
            world.Tick, population = world.Agents.Count, mode = world.Mode,
            decisions = world.Society.RuleDecisionCount,
            phase3_events = world.Events.Where(item => item.EventType is "war_week" or "war_ended" or "gift_sent"
                or "missile_ready" or "missile_strike" or "revolution" or "revolution_deferred" or "early_election_called")
                .GroupBy(item => item.EventType).ToDictionary(group => group.Key, group => group.Count()),
            countries = notes.Countries
        }).ToJsonString());
    }
}
