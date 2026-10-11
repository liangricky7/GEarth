using System.Text.Json;
using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Tests;

public sealed class EconomyCaptureTests
{
    private static World NewWorld() => SocietyFactory.Create("economy_capture", false, 1100);
    private static JsonObject Day(World world, string kind, string key, string id) =>
        world.PendingAudit.Last(row => row.Kind == kind && row.Payload[key]!.GetValue<string>() == id).Payload;

    [Fact]
    public void DailyCaptureRetainsChangedLaborPricesTradeAndFullProjectsWithoutMutatingPastDays()
    {
        var world = NewWorld();
        var person = world.InCountry("aster").First(agent => agent.Role == "citizen");
        var nation = world.Society!.Nations["aster"];
        person.PartTimeStudent = true;
        person.ArrearsMinor = 173;
        person.ArrearsWeeks = 2;
        person.UnpaidHungryWeeks = 3;
        person.InsuredMinor = 401;
        person.PriceChangeTick = 3;
        person.PriceChangePct = -15;
        person.UnitsBeforePriceChange = 9;
        person.ProfitBeforePriceChangeMinor = -83;
        person.PriceBeforePriceChangeMinor = 27;
        person.WeekDisposableIncomeMinor = 59;
        person.WeekWorkDays = 2.5;
        nation.MaintenanceFunded = false;
        nation.CurrencyIndex = 0.987654321;
        nation.TradeNetWeekMinor = -117;
        nation.TradeGrossWeekMinor = 508;
        nation.Endowment["ore"] = 1.7;
        nation.ResourceImports["ore"] = new() { [12] = 31 };
        nation.PoweredSessionsLeft["clinic"] = 2;
        nation.ProductionInputCredits["ore"] = new() { ["energy"] = 4 };
        nation.BorrowedBuilders[person.Id] = new("aster_tools", "factory_worker", 1300);
        var project = new Project { Id = "roads_fixture", Type = "roads", Level = 2, Condition = 76,
            CostMinor = 46500, PaidMinor = 23250, StartedTick = 2, ReadyTick = 35, Complete = false };
        nation.Infrastructure.Add(project);
        nation.ConstructionMaterialsUsed.Add(project.Id);
        RunAudit.Enable(world);
        world.Tick = 14;
        RunAudit.Day(world);
        var firstAgent = Day(world, "agent_day", "agent_id", person.Id);
        var firstNation = Day(world, "nation_day", "nation_id", nation.Id);
        var expected = new JsonObject
        {
            ["part_time_student"] = true, ["arrears_minor"] = 173, ["arrears_weeks"] = 2,
            ["unpaid_hungry_weeks"] = 3, ["insured_minor"] = 401, ["price_change_tick"] = 3,
            ["price_change_pct"] = -15, ["units_before_price_change"] = 9,
            ["profit_before_price_change_minor"] = -83, ["price_before_price_change_minor"] = 27
        };
        foreach (var field in expected)
            Assert.True(JsonNode.DeepEquals(field.Value, firstAgent[field.Key]), field.Key);
        Assert.Equal(2.5, firstAgent["week_work_days"]!.GetValue<double>());
        Assert.False(firstNation["maintenance_funded"]!.GetValue<bool>());
        Assert.Equal(0.987654321, firstNation["currency_index"]!.GetValue<double>());
        Assert.Equal(-117, firstNation["trade_net_week_minor"]!.GetValue<long>());
        Assert.Equal(508, firstNation["trade_gross_week_minor"]!.GetValue<long>());
        Assert.Equal(1.7, firstNation["endowment"]!["ore"]!.GetValue<double>());
        Assert.Equal(31, firstNation["resource_imports"]!["ore"]!["12"]!.GetValue<long>());
        Assert.Equal(1300, firstNation["borrowed_builders"]![person.Id]!["salary_minor"]!.GetValue<long>());
        Assert.Equal(project.Id, firstNation["construction_materials_used"]![0]!.GetValue<string>());
        var capturedProject = firstNation["infrastructure"]![0]!;
        foreach (var field in Wire.Node(project).AsObject())
            Assert.True(JsonNode.DeepEquals(field.Value, capturedProject[field.Key]), field.Key);
        Assert.Equal(7, capturedProject["progress_days"]!.GetValue<int>());
        Assert.Equal(28, capturedProject["required_days"]!.GetValue<int>());
        Assert.Equal(0.25, capturedProject["progress_fraction"]!.GetValue<double>());
        Assert.Equal("engine_remaining_schedule_ticks", capturedProject["progress_basis"]!.GetValue<string>());
        Assert.Equal(4, capturedProject["required_builders"]!.GetValue<int>());

        person.PartTimeStudent = false;
        person.ArrearsMinor = 0;
        nation.ResourceImports["ore"][12] = 99;
        nation.MaintenanceFunded = true;
        project.PaidMinor = project.CostMinor;
        project.Complete = true;
        world.Tick = 15;
        RunAudit.Day(world);
        RunAudit.Day(world); // a second boundary call must not duplicate the daily observation
        var secondAgent = Day(world, "agent_day", "agent_id", person.Id);
        var secondNation = Day(world, "nation_day", "nation_id", nation.Id);
        Assert.False(secondAgent["part_time_student"]!.GetValue<bool>());
        Assert.Equal(0, secondAgent["arrears_minor"]!.GetValue<long>());
        Assert.Equal(1, secondNation["infrastructure"]![0]!["progress_fraction"]!.GetValue<double>());
        Assert.Equal(99, secondNation["resource_imports"]!["ore"]!["12"]!.GetValue<long>());
        Assert.True(firstAgent["part_time_student"]!.GetValue<bool>());
        Assert.Equal(173, firstAgent["arrears_minor"]!.GetValue<long>());
        Assert.False(capturedProject["complete"]!.GetValue<bool>());
        Assert.Equal(31, firstNation["resource_imports"]!["ore"]!["12"]!.GetValue<long>());
        Assert.Equal(2, world.PendingAudit.Count(row => row.Kind == "nation_day" && row.Payload["nation_id"]!.GetValue<string>() == nation.Id));
    }

    [Fact]
    public void EffectsRetainRealEnrollmentAndInfrastructureChangesAsBeforeAndAfterState()
    {
        var world = NewWorld();
        var person = world.InCountry("aster").First(agent => agent.Role == "citizen" && agent.EmployerId is not null);
        SocietyEconomy.Transfer(world, world.Agents["aster_government"], person, 3000);
        var originalEmployer = person.EmployerId;
        world.Tick = 4;
        RunAudit.Enable(world);
        var enrollment = new ActionProposal("enroll_school", new() { ["part_time"] = true }, "fixture", []);
        using (RunAudit.Effects(world, person, enrollment, "enrollment", "enrollment_event"))
            SocietySchool.Enroll(world, person, true, "enrollment_event");
        var studentEffect = world.PendingAudit.Single(row => row.Kind == "action_effect"
            && row.Payload["entity_id"]!.GetValue<string>() == person.Id).Payload;
        Assert.False(studentEffect["before"]!["part_time_student"]!.GetValue<bool>());
        Assert.True(studentEffect["after"]!["part_time_student"]!.GetValue<bool>());
        Assert.Equal(originalEmployer, person.EmployerId);
        Assert.Equal(world.Tick + SocietyLabor.PartTimeCourseDays, studentEffect["after"]!["student_until_tick"]!.GetValue<int>());

        var nation = world.Society!.Nations["aster"];
        var government = world.Agents["aster_government"];
        var build = new ActionProposal("build_infrastructure", new() { ["type"] = "roads", ["level"] = 1 }, "fixture", []);
        using (RunAudit.Effects(world, government, build, "build", "build_event"))
            Assert.True(SocietyInfrastructure.Enact(world, nation, new()
                { ActionType = build.ActionType, Params = build.Params, CauseId = "build_event" }));
        var buildEffect = world.PendingAudit.Single(row => row.Kind == "action_effect"
            && row.Payload["decision_id"]!.GetValue<string>() == "build" && row.Payload["entity_type"]!.GetValue<string>() == "nation").Payload;
        Assert.Empty(buildEffect["before"]!["infrastructure"]!.AsArray());
        var project = buildEffect["after"]!["infrastructure"]![0]!;
        Assert.Equal("roads", project["type"]!.GetValue<string>());
        Assert.Equal(15500, project["paid_minor"]!.GetValue<long>());
        Assert.Equal(4, project["started_tick"]!.GetValue<int>());
        Assert.Equal(0, project["progress_days"]!.GetValue<int>());
        var maintenance = new ActionProposal("set_maintenance", new() { ["on"] = false }, "fixture", []);
        using (RunAudit.Effects(world, government, maintenance, "maintenance", "maintenance_event"))
            Assert.True(SocietyInfrastructure.Enact(world, nation, new()
                { ActionType = maintenance.ActionType, Params = maintenance.Params, CauseId = "maintenance_event" }));
        var effect = world.PendingAudit.Single(row => row.Kind == "action_effect"
            && row.Payload["decision_id"]!.GetValue<string>() == "maintenance").Payload;
        Assert.True(effect["before"]!["maintenance_funded"]!.GetValue<bool>());
        Assert.False(effect["after"]!["maintenance_funded"]!.GetValue<bool>());
        Assert.Equal("engine_execution", effect["link_type"]!.GetValue<string>());
    }

    [Fact]
    public void NewlyAvailableMenusAndConcreteSelectionsRemainInDecisionAudit()
    {
        var world = NewWorld();
        var person = world.InCountry("aster").First(agent => agent.Role == "citizen" && agent.EmployerId is not null);
        SocietyEconomy.Transfer(world, world.Agents["aster_government"], person, 3000);
        RunAudit.Enable(world);
        var now = DateTimeOffset.UnixEpoch;
        var schoolJob = SocietyEngine.Observe(world, person.Id, now);
        Assert.Contains(schoolJob.Candidates, option => option.ActionType == "enroll_school"
            && option.Params["part_time"]!.GetValue<bool>());
        RunAudit.Request(world, schoolJob, now);
        var schoolRequest = world.PendingAudit.Last(row => row.Kind == "decision_request").Payload;
        Assert.True(JsonNode.DeepEquals(Wire.Node(schoolJob.Candidates), schoolRequest["job"]!["candidates"]));
        var governmentJob = SocietyEngine.Observe(world, "aster_government", now);
        Assert.Contains(governmentJob.Candidates, option => option.ActionType == "build_infrastructure");
        var selected = Array.FindIndex(governmentJob.Candidates, option => option.ActionType == "set_maintenance");
        Assert.True(selected >= 0);
        RunAudit.Request(world, governmentJob, now);
        var governmentRequest = world.PendingAudit.Last(row => row.Kind == "decision_request").Payload;
        Assert.True(JsonNode.DeepEquals(Wire.Node(governmentJob.Candidates), governmentRequest["job"]!["candidates"]));
        var parameters = SocietyActions.Resolve(governmentJob.Candidates[selected]);
        RunAudit.Result(world, governmentJob, new(governmentJob.RequestId,
            new("set_maintenance", parameters, "fixture", []), "model", "fixture", 0), true, "OK", "event");
        var result = world.PendingAudit.Last(row => row.Kind == "decision_result").Payload;
        Assert.Equal(selected, result["selected_option_index"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(parameters, result["selected_params"]));
        Assert.False(result["selected_params"]!["on"]!.GetValue<bool>());
    }

    [Fact]
    public void MetadataVersionsCaptureWithoutChangingAuditEnvelopeAndLegacyPayloadAbsence()
    {
        var world = NewWorld();
        RunAudit.Enable(world);
        var metadata = world.PendingAudit[0].Payload;
        Assert.Equal(1, world.AuditSchemaVersion);
        Assert.Equal("gearth.economy_capture.v2", metadata["capture_contract_version"]!.GetValue<string>());
        foreach (var feature in new[] { "infrastructure", "part_time_school", "wage_arrears", "price_response", "currency_trade" })
        {
            Assert.NotNull(metadata["features"]![feature]);
            Assert.Contains(metadata["active_features"]!.AsArray(), node => node!.GetValue<string>() == feature);
        }
        Assert.Equal(56, metadata["economy_configuration"]!["school"]!["part_time_course_days"]!.GetValue<int>());
        Assert.Equal(SocietyLabor.ArrearsStrikeWeeks, metadata["economy_configuration"]!["labor"]!["arrears_strike_weeks"]!.GetValue<int>());
        Assert.Equal(SocietyTrade.CurrencyStep, metadata["economy_configuration"]!["currency_trade"]!["currency_step"]!.GetValue<double>());
        Assert.NotEmpty(metadata["economy_configuration"]!["infrastructure"]!["catalog"]!.AsArray());
        var serialized = """{"run_id":"legacy","sequence":1,"tick":7,"revision":2,"kind":"agent_day","payload":{"agent_id":"p","frame_kind":"day","cash_minor":0}}""";
        var legacy = JsonSerializer.Deserialize<AuditRecord>(serialized, Wire.Json)!;
        Assert.False(legacy.Payload.ContainsKey("part_time_student"));
        Assert.False(legacy.Payload.ContainsKey("arrears_minor"));
        Assert.Equal(serialized, JsonSerializer.Serialize(legacy, Wire.Json));
        metadata["capture_contract_version"] = RunAudit.PublicServicesContract;
        metadata["features"]!.AsObject().Remove("infrastructure");
        var original = metadata.DeepClone();
        RunAudit.Enable(world); // opening a preexisting captured world is not a backfill or metadata rewrite
        Assert.True(JsonNode.DeepEquals(original, world.PendingAudit[0].Payload));
    }
}
