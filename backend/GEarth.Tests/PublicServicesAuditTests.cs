using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Tests;

public sealed class PublicServicesAuditTests
{
    private static World NewWorld() => SocietyFactory.Create("public_services_audit", false, 1100);

    private static Agent Employee(World world) => world.InCountry("aster").First(agent => agent.Role == "citizen"
        && agent.EmployerId is { } employer && world.Agents[employer].Specialty == "tools");

    private static DecisionRecord Decide(World world, Agent agent, string action, Func<JsonObject, JsonObject>? edit = null)
    {
        var now = DateTimeOffset.UtcNow;
        var job = SocietyEngine.Observe(world, agent.Id, now);
        var candidate = job.Candidates.First(option => option.ActionType == action);
        var parameters = SocietyActions.Resolve(candidate);
        RunAudit.Request(world, job, now);
        return SocietyEngine.Apply(world, job, new(job.RequestId,
            new(action, edit?.Invoke(parameters) ?? parameters, "test", []), "model", "fixture", 0), now) with { Job = job };
    }

    [Fact]
    public void StateFirmGrantsCaptureEveryNewAccountBeforeFundingWithoutChangingTheSimulation()
    {
        var world = NewWorld();
        world.Tick = 14;
        var plain = Wire.Copy(world);
        var initialPopulation = world.Agents.Count;
        RunAudit.Enable(world);
        foreach (var country in new[] { "cinder", "haven" })
        foreach (var sector in new[] { "clinic", "energy", "medicine" })
        {
            var firm = SocietyLabor.Grant(world, world.Society!.Nations[country], sector, "")!;
            var control = SocietyLabor.Grant(plain, plain.Society!.Nations[country], sector, "")!;
            Assert.Equal(control.Id, firm.Id);
            var opening = Assert.Single(world.PendingAudit, row => row.Kind == "opening_balance"
                && row.Payload["account_id"]!.GetValue<string>() == firm.Id);
            Assert.Equal("created_agent", opening.Payload["account_type"]!.GetValue<string>());
            Assert.Equal(0, opening.Payload["balance_minor"]!.GetValue<long>());
            Assert.Equal(0, opening.Payload["agent"]!["cash_minor"]!.GetValue<long>());
            Assert.Equal(14, opening.Payload["creation_tick"]!.GetValue<int>());
            Assert.Equal("state_firm", opening.Payload["creation_source"]!.GetValue<string>());
            Assert.Null(opening.Payload["agent"]!["owner_id"]);
            var grant = Assert.Single(world.PendingAudit, row => row.Kind == "transaction"
                && row.Payload["recipient_account_id"]!.GetValue<string>() == firm.Id);
            Assert.True(opening.Sequence < grant.Sequence);
            Assert.Equal("startup_grant", grant.Payload["kind"]!.GetValue<string>());
            Assert.Equal(firm.CashMinor, grant.Payload["amount_minor"]!.GetValue<long>());
        }
        RunAudit.Day(world);
        Assert.Equal(initialPopulation, world.PendingAudit.Count(row => row.Kind == "opening_balance"
            && row.Payload["account_type"]!.GetValue<string>() == "agent"));
        Assert.Equal(6, world.PendingAudit.Count(row => row.Kind == "opening_balance"
            && row.Payload["account_type"]!.GetValue<string>() == "created_agent"));
        Assert.Equal(world.InitialCashMinor, world.PendingAudit.Where(row => row.Kind == "opening_balance")
            .Sum(row => row.Payload["balance_minor"]!.GetValue<long>()));
        Assert.True(JsonNode.DeepEquals(Wire.Node(plain.Agents), Wire.Node(world.Agents)));
        Assert.True(JsonNode.DeepEquals(Wire.Node(plain.Society!), Wire.Node(world.Society!)));
        Assert.True(JsonNode.DeepEquals(Wire.Node(plain.Events), Wire.Node(world.Events)));
        Assert.Empty(plain.PendingAudit);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void PrivateFirmCreationHasOneOpeningBeforeFundingAndRetainsItsExistingEvent()
    {
        var world = NewWorld();
        var founder = Employee(world);
        var cost = SocietyLabor.StartupCost("food");
        SocietyEconomy.Transfer(world, world.Agents["aster_government"], founder, cost);
        world.Tick = 2;
        var plain = Wire.Copy(world);
        RunAudit.Enable(world);
        Agent firm;
        using (RunAudit.Context(world, new(DecisionId: "chosen_startup", EventId: "accepted_startup")))
            firm = SocietyLabor.Start(world, founder, "food", 30, "accepted_startup");
        SocietyLabor.Start(plain, plain.Agents[founder.Id], "food", 30, "accepted_startup");
        RunAudit.Day(world);
        var opening = Assert.Single(world.PendingAudit, row => row.Kind == "opening_balance"
            && row.Payload["account_id"]!.GetValue<string>() == firm.Id);
        Assert.Equal("created_agent", opening.Payload["account_type"]!.GetValue<string>());
        Assert.Equal("private_firm", opening.Payload["creation_source"]!.GetValue<string>());
        Assert.Equal(0, opening.Payload["balance_minor"]!.GetValue<long>());
        Assert.Equal("chosen_startup", opening.Payload["decision_id"]!.GetValue<string>());
        var funding = Assert.Single(world.PendingAudit, row => row.Kind == "transaction"
            && row.Payload["recipient_account_id"]!.GetValue<string>() == firm.Id);
        Assert.True(opening.Sequence < funding.Sequence);
        Assert.Equal(cost, funding.Payload["amount_minor"]!.GetValue<long>());
        var started = Assert.Single(world.Events, item => item.EventType == "business_started");
        Assert.Equal(firm.Id, started.Payload["account_id"]!.GetValue<string>());
        Assert.Equal(0, started.Payload["opening_cash_minor"]!.GetValue<long>());
        Assert.True(JsonNode.DeepEquals(Wire.Node(plain.Agents), Wire.Node(world.Agents)));
        Assert.True(JsonNode.DeepEquals(Wire.Node(plain.Events), Wire.Node(world.Events)));
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void DailySnapshotsCapturePublicServicesAndSeparateDepositClaimsFromMoney()
    {
        var world = NewWorld();
        var person = Employee(world);
        SocietyBank.Deposit(world, person, 123);
        person.Hungry = person.HungryThisWeek = person.PricedOutThisWeek = true;
        person.NeedsMet = 1;
        person.SickDays = 3;
        person.InKindMinor = 42;
        person.StudentUntilTick = person.SeekingUntilTick = 28;
        person.SchoolId = "aster_school";
        person.GoodsBoughtLastWeek = 10;
        person.ServicesBoughtLastWeek = 12;
        var nation = world.Society!.Nations[person.CountryId];
        nation.FoodSubsidyPct = 50;
        nation.TuitionSubsidyPct = 100;
        nation.StudentStipend = true;
        nation.HiringBonusMinor = 200;
        nation.HungryResidentsLastWeek = 5;
        nation.PricedOutLastWeek = 4;
        nation.OutputLostToHungerPct = 9.5;
        RunAudit.Enable(world, new { fixture = true, banking_paused = false });
        var metadata = world.PendingAudit[0].Payload;
        Assert.True(metadata["fixture"]!.GetValue<bool>());
        Assert.Equal(SocietyBank.Paused, metadata["banking_paused"]!.GetValue<bool>()); // the engine records its own flag
        Assert.Equal("v1", metadata["features"]!["public_services"]!.GetValue<string>());
        Assert.Equal(SocietyLabor.CourseDays, metadata["economy_configuration"]!["school"]!["course_days"]!.GetValue<int>());
        Assert.Equal("claim_on_bank_cash_not_additional_money", metadata["economy_configuration"]!["accounting"]!["deposit_minor"]!.GetValue<string>());
        var opening = world.PendingAudit.Where(row => row.Kind == "opening_balance").ToArray();
        Assert.Equal(world.InitialCashMinor, opening.Sum(row => row.Payload["balance_minor"]!.GetValue<long>()));
        Assert.Equal(123, opening.Single(row => row.Payload["account_id"]!.GetValue<string>() == person.Id).Payload["agent"]!["deposit_minor"]!.GetValue<long>());
        Assert.All(opening, row => Assert.Equal("agent", row.Payload["account_type"]!.GetValue<string>()));

        world.Tick = 1;
        RunAudit.Day(world);
        var snapshot = world.PendingAudit.Single(row => row.Kind == "agent_day" && row.Payload["agent_id"]!.GetValue<string>() == person.Id).Payload;
        Assert.True(snapshot["is_student"]!.GetValue<bool>());
        Assert.True(snapshot["seeking_better_job"]!.GetValue<bool>());
        Assert.True(snapshot["hungry"]!.GetValue<bool>());
        Assert.Equal(42, snapshot["in_kind_minor"]!.GetValue<long>());
        Assert.Equal(3, snapshot["sick_days"]!.GetValue<int>());
        Assert.Equal("aster_school", snapshot["school_id"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(Wire.Node(SocietyEconomy.QolParts(person, 1, false)), snapshot["qol_parts_points"]));
        Assert.Equal(SocietyEconomy.QualityOfLife(person, 1, false), snapshot["qol_target"]!.GetValue<int>());
        var country = world.PendingAudit.Single(row => row.Kind == "nation_day" && row.Payload["nation_id"]!.GetValue<string>() == nation.Id).Payload;
        Assert.Equal(50, country["food_subsidy_pct"]!.GetValue<int>());
        Assert.Equal(100, country["tuition_subsidy_pct"]!.GetValue<int>());
        Assert.True(country["student_stipend"]!.GetValue<bool>());
        Assert.Equal(200, country["hiring_bonus_minor"]!.GetValue<int>());
        Assert.Equal(123, country["bank_deposits_minor"]!.GetValue<long>());
        Assert.Equal(9.5, country["output_lost_to_hunger_pct"]!.GetValue<double>());
        var planned = world.PendingAudit.Single(row => row.Kind == "nation_day" && row.Payload["nation_id"]!.GetValue<string>() == "cinder").Payload;
        Assert.True(JsonNode.DeepEquals(Wire.Node(world.Society.Nations["cinder"].Plan), planned["plan"]));
        Assert.NotNull(planned["planning_view"]!["food"]!["weekly_capacity"]);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void EnrollmentCapturesTuitionLegsLinkedEffectsAndGraduationBoundary()
    {
        var world = NewWorld();
        var student = Employee(world);
        SocietyEconomy.Transfer(world, world.Agents["aster_government"], student, 3000);
        world.Society!.Nations["aster"].TuitionSubsidyPct = 50;
        RunAudit.Enable(world);
        var enrolled = Decide(world, student, "enroll_school");
        Assert.True(enrolled.Accepted);
        var school = world.Agents[student.SchoolId!];
        var payments = world.PendingAudit.Where(row => row.Kind == "transaction"
            && row.Payload["decision_id"]?.GetValue<string>() == enrolled.Job.RequestId).ToArray();
        Assert.Equal(1000, payments.Single(row => row.Payload["kind"]!.GetValue<string>() == "tuition").Payload["amount_minor"]!.GetValue<long>());
        Assert.Equal(1000, payments.Single(row => row.Payload["kind"]!.GetValue<string>() == "tuition_subsidy").Payload["amount_minor"]!.GetValue<long>());
        Assert.All(payments, row => Assert.Equal(school.Id, row.Payload["recipient_account_id"]!.GetValue<string>()));
        var effects = world.PendingAudit.Where(row => row.Kind == "action_effect"
            && row.Payload["decision_id"]?.GetValue<string>() == enrolled.Job.RequestId).ToArray();
        Assert.Equal(school.Id, effects.Single(row => row.Payload["entity_id"]!.GetValue<string>() == student.Id).Payload["after"]!["school_id"]!.GetValue<string>());
        Assert.True(effects.Single(row => row.Payload["entity_id"]!.GetValue<string>() == student.Id).Payload["after"]!["is_student"]!.GetValue<bool>());
        Assert.Equal(1, effects.Single(row => row.Payload["entity_id"]!.GetValue<string>() == school.Id).Payload["after"]!["enrolled_students"]!.GetValue<int>());
        Assert.Contains(effects, row => row.Payload["entity_id"]!.GetValue<string>() == "aster_government");

        // Start just before the course boundary; the engine performs graduation during its next daily step.
        world.Tick = student.StudentUntilTick - 1;
        SocietyEngine.Advance(world);
        RunAudit.Day(world);
        Assert.True(student.Graduated);
        var final = world.PendingAudit.Last(row => row.Kind == "agent_day" && row.Payload["agent_id"]!.GetValue<string>() == student.Id).Payload;
        Assert.True(final["graduated"]!.GetValue<bool>());
        Assert.False(final["is_student"]!.GetValue<bool>());
        Assert.Null(final["school_id"]);
        Assert.Contains(world.PendingAudit, row => row.Kind == "event" && row.Payload["event_type"]!.GetValue<string>() == "graduated");
        SocietyEconomy.AssertConservation(world);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("1.0", false)]
    [InlineData("true", false)]
    [InlineData("\"1\"", false)]
    [InlineData("-1", false)]
    [InlineData("2147483647", false)]
    [InlineData("2147483648", false)]
    public void NumericPlanMatchingPreservesBoundsAndChosenConcreteValues(string chosen, bool accepted)
    {
        var world = NewWorld();
        var planner = world.InCountry("cinder").Single(agent => agent.Role == "planner");
        RunAudit.Enable(world);
        var decision = Decide(world, planner, "set_plan", values => { values["food"] = JsonNode.Parse(chosen); return values; });
        Assert.Equal(accepted, decision.Accepted);
        var request = world.PendingAudit.Single(row => row.Kind == "decision_request").Payload;
        Assert.Equal("gearth.numbered_menu.v2", request["prompt_contract_version"]!.GetValue<string>());
        Assert.Equal(RunAudit.MenuContract, request["menu_parameter_contract"]!.GetValue<string>());
        var index = Array.FindIndex(decision.Job.Candidates, option => option.ActionType == "set_plan");
        Assert.NotNull(request["job"]!["candidates"]![index]!["params"]!["food"]!["min"]);
        var result = world.PendingAudit.Single(row => row.Kind == "decision_result").Payload;
        Assert.Equal(accepted ? index : (int?)null, result["selected_option_index"]?.GetValue<int>());
        Assert.Equal(accepted ? 1 : 0, result["selection_match_count"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(chosen), result["selected_params"]!["food"]));
        if (accepted)
        {
            var effect = world.PendingAudit.Single(row => row.Kind == "action_effect" && row.Payload["entity_type"]!.GetValue<string>() == "nation");
            Assert.Equal(0, effect.Payload["after"]!["plan"]!["food"]!.GetValue<int>());
            Assert.Equal(decision.Job.RequestId, effect.Payload["decision_id"]!.GetValue<string>());
        }
        else Assert.DoesNotContain(world.PendingAudit, row => row.Kind == "action_effect");
    }

    [Fact]
    public void NumericDefaultsUpperBoundsAndFixedFieldsUseTheOriginalMenu()
    {
        var world = NewWorld();
        var planner = world.InCountry("cinder").Single(agent => agent.Role == "planner");
        var now = DateTimeOffset.UtcNow;
        var original = SocietyEngine.Observe(world, planner.Id, now);
        var range = new Candidate("fixture_choice", new JsonObject
            { ["sector"] = "food", ["amount"] = SocietyActions.Range(0, 100, 25) });
        var job = original with { Candidates = [range] };
        RunAudit.Enable(world);
        foreach (var parameters in new[] { SocietyActions.Resolve(range), new JsonObject { ["sector"] = "food", ["amount"] = 100 } })
        {
            var result = new DecisionResult(job.RequestId, new("fixture_choice", parameters, "fallback default or upper bound", []), "rule_fallback", "none", 0);
            RunAudit.Result(world, job, result, false, "FIXTURE", "fixture_event");
            var payload = world.PendingAudit.Last().Payload;
            Assert.Equal(0, payload["selected_option_index"]!.GetValue<int>());
            Assert.Equal(1, payload["selection_match_count"]!.GetValue<int>());
            Assert.True(payload["fallback"]!.GetValue<bool>());
        }
        var changedFixed = SocietyActions.Resolve(range);
        changedFixed["sector"] = "energy";
        RunAudit.Result(world, job, new(job.RequestId, new("fixture_choice", changedFixed, "invalid fixed field", []), "model", "fixture", 0), false, "UNAVAILABLE_ACTION", "fixture_event");
        Assert.Null(world.PendingAudit.Last().Payload["selected_option_index"]);
        Assert.Equal(0, world.PendingAudit.Last().Payload["selection_match_count"]!.GetValue<int>());
    }

    [Fact]
    public void NumericMatchingRejectsMissingAndExtraKeysAndDoesNotInventAnAmbiguousIndex()
    {
        var world = NewWorld();
        var planner = world.InCountry("cinder").Single(agent => agent.Role == "planner");
        RunAudit.Enable(world);
        var missing = Decide(world, planner, "set_plan", values => { values.Remove("food"); return values; });
        var extra = Decide(world, planner, "set_plan", values => { values["extra"] = 1; return values; });
        Assert.False(missing.Accepted);
        Assert.False(extra.Accepted);
        Assert.All(world.PendingAudit.Where(row => row.Kind == "decision_result"), row => Assert.Null(row.Payload["selected_option_index"]));
        var candidate = missing.Job.Candidates.Single(option => option.ActionType == "set_plan");
        var duplicated = missing.Job with { Candidates = [candidate, candidate] };
        RunAudit.Canceled(world, duplicated, new(duplicated.RequestId,
            new("set_plan", SocietyActions.Resolve(candidate), "returned before shutdown", []), "rule_fallback", "none", 0));
        var canceled = world.PendingAudit.Last().Payload;
        Assert.Null(canceled["selected_option_index"]);
        Assert.Equal(2, canceled["selection_match_count"]!.GetValue<int>());
        Assert.True(canceled["fallback"]!.GetValue<bool>());
        Assert.NotNull(canceled["selected_params"]!["food"]);
    }

    [Theory]
    [InlineData("subsidize_food", "food_subsidy_pct")]
    [InlineData("subsidize_tuition", "tuition_subsidy_pct")]
    [InlineData("student_stipend", "student_stipend")]
    [InlineData("hiring_bonus", "hiring_bonus_minor")]
    public void DelayedPoliciesCaptureNewNationLeversAtEnactment(string action, string field)
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        nation.PricedOutLastWeek = nation.Unemployed = 100;
        RunAudit.Enable(world);
        var record = Decide(world, world.Agents["aster_government"], action);
        Assert.True(record.Accepted);
        Assert.DoesNotContain(world.PendingAudit, row => row.Kind == "action_effect");
        world.Tick = Governments.Get(nation.GovernmentId).PolicyDelay;
        SocietyDiplomacy.ProcessPolicies(world);
        var changed = world.PendingAudit.Single(row => row.Kind == "action_effect" && row.Payload["entity_type"]!.GetValue<string>() == "nation");
        Assert.NotNull(changed.Payload["after"]![field]);
        Assert.False(JsonNode.DeepEquals(changed.Payload["before"]![field], changed.Payload["after"]![field]));
        Assert.Equal(action, changed.Payload["after"]!["last_policy_action"]!.GetValue<string>());
        Assert.Equal(record.Job.ObservationTick + Governments.Get(nation.GovernmentId).PolicyDelay, changed.Tick);
    }
}
