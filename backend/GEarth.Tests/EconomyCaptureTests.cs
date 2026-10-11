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
    public void DailyWarStateRetainsMilitaryPayRelationshipsBothSidesAndPoliticalMission()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var soldier = SocietyWar.Soldiers(world, nation).First();
        var relationship = SocietyDiplomacy.Relation(world, "aster", "cinder");
        soldier.WarWeariness = 0.2;
        soldier.MilitaryPaidTick = 11;
        soldier.MilitaryPayMinor = 97;
        nation.Missiles = 2;
        nation.WarWeeks = 4;
        nation.NoFitSoldierWeeks = 1;
        nation.WarWeariness = 0.3;
        nation.UnrestSinceTick = 3;
        nation.TaxMarkMinor = 875;
        nation.WarAccountingInitialized = true;
        nation.LeadershipTerm = 7;
        nation.NextElectionTick = 37;
        relationship.Score = -87;
        relationship.AtWar = true;
        relationship.Aggressor = nation.Id;
        relationship.ProposedBy = "cinder";
        relationship.ProposalKind = "peace";
        relationship.ProposalTerms = "cede_ore";
        relationship.ProposalExpiresTick = 45;
        RunAudit.Enable(world);
        world.Tick = 14;
        RunAudit.Day(world);
        var person = Day(world, "agent_day", "agent_id", soldier.Id);
        Assert.Equal(0.2, person["war_weariness"]!.GetValue<double>());
        Assert.Equal(11, person["military_paid_tick"]!.GetValue<int>());
        Assert.Equal(97, person["military_pay_minor"]!.GetValue<long>());
        var country = Day(world, "nation_day", "nation_id", nation.Id);
        Assert.Equal(2, country["missiles"]!.GetValue<int>());
        Assert.Equal(4, country["war_weeks"]!.GetValue<int>());
        Assert.Equal(1, country["no_fit_soldier_weeks"]!.GetValue<int>());
        Assert.Equal(0.3, country["war_weariness"]!.GetValue<double>());
        Assert.Equal(3, country["unrest_since_tick"]!.GetValue<int>());
        Assert.Equal(875, country["tax_mark_minor"]!.GetValue<long>());
        Assert.True(country["war_accounting_initialized"]!.GetValue<bool>());
        Assert.Equal(nation.GovernmentId, country["government_id"]!.GetValue<string>());
        Assert.Equal(7, country["leadership_term"]!.GetValue<int>());
        Assert.Equal(37, country["next_election_tick"]!.GetValue<int>());
        var original = Assert.Single(country["relationships"]!.AsArray(), node => node!["id"]!.GetValue<string>() == relationship.Id)!;
        Assert.True(JsonNode.DeepEquals(Wire.Node(relationship), original));
        Assert.Equal("cinder", original["proposed_by"]!.GetValue<string>());
        Assert.Equal("cede_ore", original["proposal_terms"]!.GetValue<string>());
        Assert.Equal(SocietyWar.Strength(world, nation), country["military"]!["strength"]!.GetValue<int>());
        Assert.Equal(SocietyWar.Soldiers(world, nation).Count(person => SocietyWar.Paid(world, person)), country["military"]!["soldiers_paid"]!.GetValue<int>());
        Assert.Equal(SocietyWar.UnrestQol, country["mission"]!["average_quality_of_life_at_least"]!.GetValue<int>());
        Assert.Equal(3 + SocietyWar.UnrestDays, country["mission"]!["unrest_comes_to_a_head_tick"]!.GetValue<int>());
        relationship.Score = -22;
        Assert.Equal(-87, original["score"]!.GetValue<int>());
    }

    [Fact]
    public void MissileEffectsCaptureTargetDamageAndRecruitmentCapturesChangedResidents()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var target = world.Society.Nations["cinder"];
        var relationship = SocietyDiplomacy.Relation(world, nation.Id, target.Id);
        relationship.AtWar = true;
        nation.Missiles = 1;
        target.Infrastructure.Add(new Project { Id = "target_road", Type = "roads", Complete = true,
            CostMinor = 31000, PaidMinor = 31000, Condition = 100 });
        var government = world.Agents["aster_government"];
        world.Tick = 4;
        RunAudit.Enable(world);
        var launch = new ActionProposal("launch_missile", new() { ["country_id"] = target.Id }, "fixture", []);
        using (RunAudit.Effects(world, government, launch, "launch", "launch_event"))
            Assert.True(SocietyWar.Enact(world, nation, new()
                { Id = "missile_fixture", ActionType = launch.ActionType, Params = launch.Params, CauseId = "launch_event" }));
        var effects = world.PendingAudit.Where(row => row.Kind == "action_effect" && row.Payload["decision_id"]?.GetValue<string>() == "launch").ToArray();
        var damage = effects.Single(row => row.Payload["entity_type"]!.GetValue<string>() == "nation"
            && row.Payload["entity_id"]!.GetValue<string>() == target.Id).Payload;
        Assert.Equal(100, damage["before"]!["infrastructure"]![0]!["condition"]!.GetValue<int>());
        Assert.Equal(50, damage["after"]!["infrastructure"]![0]!["condition"]!.GetValue<int>());
        var attacker = effects.Single(row => row.Payload["entity_type"]!.GetValue<string>() == "nation"
            && row.Payload["entity_id"]!.GetValue<string>() == nation.Id).Payload;
        Assert.Equal(1, attacker["before"]!["missiles"]!.GetValue<int>());
        Assert.Equal(0, attacker["after"]!["missiles"]!.GetValue<int>());
        var recruit = new ActionProposal("recruit_soldiers", new(), "fixture", []);
        using (RunAudit.Effects(world, government, recruit, "recruit", "recruit_event"))
            Assert.True(SocietyWar.Enact(world, nation, new() { ActionType = recruit.ActionType, Params = recruit.Params, CauseId = "recruit_event" }));
        var residents = world.PendingAudit.Where(row => row.Kind == "action_effect"
            && row.Payload["decision_id"]?.GetValue<string>() == "recruit" && row.Payload["entity_type"]!.GetValue<string>() == "agent"
            && row.Payload["after"]!["occupation"]?.GetValue<string>() == "soldier").ToArray();
        Assert.Equal(SocietyWar.SoldierStep, residents.Length);
        Assert.All(residents, row => Assert.NotEqual("soldier", row.Payload["before"]!["occupation"]!.GetValue<string>()));
    }

    [Fact]
    public void DailyRecoveryClinicAndCreditPositionsRetainRawValuesAndCountryScope()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var person = world.InCountry(nation.Id).First(agent => agent.Role == "citizen");
        person.NutritionDebtDays = 9;
        person.ProductionRemainder = 0.731;
        person.FoodSpoilageRemainder = 57;
        nation.ClinicCapacityThisWeek = 81;
        nation.UntreatedPatientDaysThisWeek = 17;
        nation.ClinicLastWeek = new(7, 93, 42, 11, 1500);
        world.Tick = 8;
        SocietyGovernmentDecisions.ChangeClinicFunding(world, nation, 100);
        world.Tick = 9;
        SocietyGovernmentDecisions.ChangeClinicFunding(world, nation, 200);
        world.Tick = 10;
        SocietyGovernmentDecisions.ChangeClinicFunding(world, nation, 300);
        world.Tick = 11;
        SocietyGovernmentDecisions.ChangeClinicFunding(world, nation, 400);
        var crossCountryLoan = new Credit
        {
            Id = "loan_foreign", Kind = "treasury", BankId = "aster_bank", BorrowerId = "cinder_government",
            PrincipalMinor = 10000, PrincipalLeftMinor = 7500, WeeklyPrincipalMinor = 389,
            WeeklyInterestMinor = 19, RatePct = 6, MissedPayments = 4, ProvisionMinor = 1500,
            Sector = "tools", LaunchPriceMinor = 87, RequestedTick = 1, RequestEventId = "loan_request", Status = "active", DepositCreated = true
        };
        var repaidLoan = new Credit { Id = "loan_repaid", BankId = "aster_bank", BorrowerId = person.Id,
            PrincipalMinor = 130, PrincipalLeftMinor = 0, Status = "repaid" };
        var unrelatedLoan = new Credit { Id = "loan_other", BankId = "cinder_bank", BorrowerId = "cinder_government", PrincipalMinor = 200 };
        world.Society.Credit.AddRange([crossCountryLoan, repaidLoan, unrelatedLoan]);
        RunAudit.Enable(world);
        RunAudit.Day(world);
        var agent = Day(world, "agent_day", "agent_id", person.Id);
        Assert.Equal(9, agent["nutrition_debt_days"]!.GetValue<int>());
        Assert.Equal(0.731, agent["production_remainder"]!.GetValue<double>());
        Assert.Equal(57, agent["food_spoilage_remainder"]!.GetValue<int>());
        var country = Day(world, "nation_day", "nation_id", nation.Id);
        Assert.Equal(81, country["clinic_capacity_this_week"]!.GetValue<int>());
        Assert.Equal(17, country["untreated_patient_days_this_week"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(Wire.Node(nation.ClinicLastWeek), country["clinic_last_week"]));
        Assert.Equal(3, country["clinic_funding_history"]!.AsArray().Count);
        Assert.Equal(9, country["clinic_funding_history"]![0]!["tick"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(Wire.Node(SocietyGovernmentDecisions.ClinicView(world, nation)), country["clinic_funding"]));
        Assert.Null(agent["loan_loss_provisions_minor"]);
        var bank = Day(world, "agent_day", "agent_id", "aster_bank");
        Assert.Equal(1500, bank["loan_loss_provisions_minor"]!.GetValue<long>());
        Assert.Equal(Wire.Node(SocietyBank.View(world, world.Agents["aster_bank"], true))["loan_loss_provisions_minor"]!.GetValue<long>(),
            bank["loan_loss_provisions_minor"]!.GetValue<long>());
        var credits = country["credit_positions"]!.AsArray();
        Assert.Equal(2, credits.Count);
        Assert.DoesNotContain(credits, node => node!["id"]!.GetValue<string>() == unrelatedLoan.Id);
        Assert.Contains(credits, node => node!["status"]!.GetValue<string>() == "repaid");
        var actual = Assert.Single(credits, node => node!["id"]!.GetValue<string>() == crossCountryLoan.Id)!;
        foreach (var field in Wire.Node(crossCountryLoan).AsObject())
            Assert.True(JsonNode.DeepEquals(field.Value, actual[field.Key]), field.Key);
        Assert.Equal("aster", actual["bank_country_id"]!.GetValue<string>());
        Assert.Equal("cinder", actual["borrower_country_id"]!.GetValue<string>());
        var borrowerCountry = Day(world, "nation_day", "nation_id", "cinder");
        Assert.Contains(borrowerCountry["credit_positions"]!.AsArray(), node => node!["id"]!.GetValue<string>() == crossCountryLoan.Id);
        crossCountryLoan.ProvisionMinor = 7500;
        nation.ClinicFundingHistory.Clear();
        Assert.Equal(1500, actual["provision_minor"]!.GetValue<long>());
        Assert.Equal(3, country["clinic_funding_history"]!.AsArray().Count);
    }

    [Fact]
    public void EffectsCaptureActualRecoveryFundingFeedbackAndCreditCreation()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var person = world.InCountry(nation.Id).First(agent => agent.Role == "citizen");
        person.NutritionDebtDays = 9;
        person.Inventory["food"] = 151;
        person.FoodSpoilageRemainder = 57;
        world.Tick = 7;
        RunAudit.Enable(world);
        using (RunAudit.Effects(world, person, new("hold", new(), "routine fixture", []), "recovery", "recovery_event"))
        {
            SocietyEconomy.RecordMeal(person, true);
            SocietyEconomy.SpoilFood(world, person);
        }
        var recovery = world.PendingAudit.Single(row => row.Kind == "action_effect"
            && row.Payload["decision_id"]?.GetValue<string>() == "recovery").Payload;
        Assert.Equal(9, recovery["before"]!["nutrition_debt_days"]!.GetValue<int>());
        Assert.Equal(7, recovery["after"]!["nutrition_debt_days"]!.GetValue<int>());
        Assert.Equal(57, recovery["before"]!["food_spoilage_remainder"]!.GetValue<int>());
        Assert.Equal(8, recovery["after"]!["food_spoilage_remainder"]!.GetValue<int>());
        var government = world.Agents["aster_government"];
        using (RunAudit.Effects(world, government, new("fund_services", new() { ["amount_minor"] = 200 }, "fixture", []), "funding", "funding_event"))
            SocietyGovernmentDecisions.ChangeClinicFunding(world, nation, 200);
        var funding = world.PendingAudit.Single(row => row.Kind == "action_effect"
            && row.Payload["decision_id"]?.GetValue<string>() == "funding" && row.Payload["entity_type"]!.GetValue<string>() == "nation").Payload;
        Assert.Empty(funding["before"]!["clinic_funding_history"]!.AsArray());
        Assert.Equal(200, funding["after"]!["clinic_funding_history"]![0]!["to_minor"]!.GetValue<int>());
        using (RunAudit.Effects(world, person, new("enroll_school", new() { ["part_time"] = true }, "fixture", []), "loan", "loan_event"))
            Assert.NotNull(SocietyBank.StudentLoan(world, person, 120));
        var credit = world.PendingAudit.Single(row => row.Kind == "action_effect"
            && row.Payload["decision_id"]?.GetValue<string>() == "loan" && row.Payload["entity_type"]!.GetValue<string>() == "credit").Payload;
        Assert.Null(credit["before"]);
        Assert.Equal(person.Id, credit["after"]!["borrower_id"]!.GetValue<string>());
        Assert.Equal(120, credit["after"]!["principal_minor"]!.GetValue<long>());
        Assert.Equal(0, credit["after"]!["provision_minor"]!.GetValue<long>());
        Assert.Equal("student", credit["after"]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void RepairQueueIsCapturedAtEnactmentAndInDailyStateWithoutClaimingDamageWasRestored()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var project = new Project { Id = "repair_roads", Type = "roads", Complete = true, CostMinor = 31000,
            PaidMinor = 31000, Condition = 52 };
        nation.Infrastructure.Add(project);
        world.Tick = 6;
        RunAudit.Enable(world);
        var options = new List<Candidate>();
        SocietyInfrastructure.AddOptions(world, nation, world.Agents["aster_government"], options);
        var repair = Assert.Single(options, option => option.ActionType == "repair_infrastructure");
        var action = new ActionProposal(repair.ActionType, SocietyActions.Resolve(repair), "fixture", []);
        using (RunAudit.Effects(world, world.Agents["aster_government"], action, "repair", "repair_event"))
            Assert.True(SocietyInfrastructure.Enact(world, nation, new()
                { ActionType = action.ActionType, Params = action.Params, CauseId = "repair_event" }));
        var queued = world.PendingAudit.Single(row => row.Kind == "action_effect"
            && row.Payload["decision_id"]?.GetValue<string>() == "repair").Payload;
        Assert.Empty(queued["before"]!["repair_orders"]!.AsArray());
        Assert.Equal(project.Id, queued["after"]!["repair_orders"]![0]!.GetValue<string>());
        Assert.Equal(52, project.Condition); // queueing a repair does not complete its work
        Assert.Null(queued["after"]!["infrastructure"]);
        RunAudit.Day(world);
        var daily = Day(world, "nation_day", "nation_id", nation.Id);
        Assert.Equal(project.Id, daily["repair_orders"]![0]!.GetValue<string>());
        Assert.Equal(52, daily["infrastructure"]![0]!["condition"]!.GetValue<int>());
        nation.RepairOrders.Clear();
        Assert.Equal(project.Id, daily["repair_orders"]![0]!.GetValue<string>());
        var metadata = world.PendingAudit[0].Payload;
        Assert.Equal("v1", metadata["features"]!["infrastructure_repairs"]!.GetValue<string>());
        Assert.Equal(10, metadata["economy_configuration"]!["infrastructure"]!["repair_max_condition_per_week"]!.GetValue<int>());
        Assert.Equal(0.005, metadata["economy_configuration"]!["infrastructure"]!["repair_cost_fraction_per_condition"]!.GetValue<double>());
        Assert.Contains("does_not_restore_damage", metadata["economy_configuration"]!["infrastructure"]!["maintenance_basis"]!.GetValue<string>());
    }

    [Fact]
    public void MetadataVersionsCaptureWithoutChangingAuditEnvelopeAndLegacyPayloadAbsence()
    {
        var world = NewWorld();
        RunAudit.Enable(world);
        var metadata = world.PendingAudit[0].Payload;
        Assert.Equal(1, world.AuditSchemaVersion);
        Assert.Equal("gearth.economy_capture.v4", metadata["capture_contract_version"]!.GetValue<string>());
        foreach (var feature in new[] { "infrastructure", "part_time_school", "wage_arrears", "price_response", "currency_trade", "war_diplomacy", "nutrition_recovery", "clinic_feedback", "loan_provisions", "infrastructure_repairs" })
        {
            Assert.NotNull(metadata["features"]![feature]);
            Assert.Contains(metadata["active_features"]!.AsArray(), node => node!.GetValue<string>() == feature);
        }
        Assert.Equal(56, metadata["economy_configuration"]!["school"]!["part_time_course_days"]!.GetValue<int>());
        Assert.Equal(SocietyLabor.ArrearsStrikeWeeks, metadata["economy_configuration"]!["labor"]!["arrears_strike_weeks"]!.GetValue<int>());
        Assert.Equal(SocietyTrade.CurrencyStep, metadata["economy_configuration"]!["currency_trade"]!["currency_step"]!.GetValue<double>());
        Assert.NotEmpty(metadata["economy_configuration"]!["infrastructure"]!["catalog"]!.AsArray());
        Assert.Equal(SocietyEconomy.StarvationDays, metadata["economy_configuration"]!["nutrition_recovery"]!["starvation_days"]!.GetValue<int>());
        Assert.Equal(3, metadata["economy_configuration"]!["clinic_feedback"]!["retained_funding_changes"]!.GetValue<int>());
        Assert.Contains("not_additive", metadata["economy_configuration"]!["loan_provisions"]!["credit_positions_scope"]!.GetValue<string>());
        Assert.Equal(SocietyWar.WarBelow, metadata["economy_configuration"]!["war_diplomacy"]!["war_below"]!.GetValue<int>());
        Assert.Contains(metadata["economy_configuration"]!["infrastructure"]!["catalog"]!.AsArray(),
            node => node!["type"]!.GetValue<string>() == PhaseContracts.Missile);
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
