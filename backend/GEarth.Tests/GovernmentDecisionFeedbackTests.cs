using GEarth.Core;
namespace GEarth.Tests;

public sealed class GovernmentDecisionFeedbackTests
{
    [Fact]
    public void ClinicFeedbackPersistsAcrossOtherPoliciesAndCheckpoint()
    {
        var world = SocietyFactory.Create("clinic-feedback", false, 1100);
        for (var i = 0; i < 7; i++) SocietyEngine.Advance(world);
        var nation = world.Society!.Nations["aster"];
        Assert.NotNull(nation.ClinicLastWeek);
        Assert.True(nation.ClinicLastWeek!.Capacity > 0);
        SocietyGovernmentDecisions.ChangeClinicFunding(world, nation, 100);
        nation.LastPolicyAction = "adjust_tax";
        var copy = Wire.Copy(world);
        var job = SocietyEngine.Observe(copy, "aster_government", DateTimeOffset.UtcNow);
        var clinic = job.Observation["country"]!["clinic_funding"]!;
        Assert.Single(clinic["changes"]!.AsArray());
        Assert.NotNull(clinic["last_completed_week"]);
        Assert.All(job.Candidates.Where(c => c.ActionType == "fund_services"), c =>
            Assert.Contains("does not directly add doctors", (string?)c.Params["effect"]));
    }

    [Fact]
    public void OfferIsVisibleWhileDomesticPolicyBlocksResponseAndDeadlineAccountsForDelay()
    {
        var world = SocietyFactory.Create("offers", true, 1100);
        var nation = world.Society!.Nations["aster"];
        var relation = SocietyDiplomacy.Relation(world, "aster", "beryl");
        relation.ProposedBy = "beryl";
        relation.ProposalKind = "trade";
        relation.ProposalExpiresTick = 60;
        world.Society.Policies.Add(new() { CountryId = "aster", ActionType = "adjust_tax", EffectiveTick = 10 });
        var now = DateTimeOffset.UtcNow;
        var job = SocietyEngine.Observe(world, "aster_government", now);
        var pending = Assert.Single(job.Observation["country"]!["pending_diplomatic_offers"]!.AsArray());
        Assert.False(pending!["response_available_now"]!.GetValue<bool>());
        Assert.DoesNotContain(job.Candidates, c => c.ActionType == "respond_agreement");
        world.Society.Policies.Clear();
        job = SocietyEngine.Observe(world, "aster_government", now);
        Assert.Equal(2, job.Candidates.Count(c => c.ActionType == "respond_agreement"));
        Assert.Contains("lose tariff revenue", SocietyGovernmentDecisions.ResponseEffect(world, nation, relation, true));
        world.Tick = 60;
        Assert.DoesNotContain(SocietyActions.Available(world, world.Agents["aster_government"]), c => c.ActionType == "respond_agreement");
        world.Tick = 61;
        SocietyDiplomacy.Advance(world);
        Assert.Null(relation.ProposedBy);
        Assert.Contains(world.Events, e => e.EventType == "diplomatic_offer_expired");
    }

    [Fact]
    public void HoldingLogsTheActualResponseOptionsPresented()
    {
        var world = SocietyFactory.Create("offer-audit", true, 1100);
        var relation = SocietyDiplomacy.Relation(world, "aster", "beryl");
        relation.ProposedBy = "beryl"; relation.ProposalKind = "trade"; relation.ProposalExpiresTick = 60;
        var now = DateTimeOffset.UtcNow;
        var job = SocietyEngine.Observe(world, "aster_government", now);
        var hold = Assert.Single(job.Candidates, c => c.ActionType == "hold");
        var result = new DecisionResult(job.RequestId, new("hold", hold.Params, "Wait", []), "model", "test", 0);
        Assert.True(SocietyEngine.Apply(world, job, result, now).Accepted);
        var evidence = Assert.Single(world.Events, e => e.EventType == "diplomatic_offer_considered");
        Assert.Equal(2, evidence.Payload["response_options"]!.AsArray().Count);
        Assert.Equal("hold", (string?)evidence.Payload["selected_action"]);
    }
}
