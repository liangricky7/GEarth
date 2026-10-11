using GEarth.Core;
using System.Text.Json;

namespace GEarth.Tests;

public sealed class InfrastructureTests
{
    private static World NewWorld() => SocietyFactory.Create("infrastructure", false, 1100);

    private static PolicyProposal Build(string type = "roads", int level = 1) => new()
    {
        ActionType = "build_infrastructure", Params = SocietyActions.Make("build_infrastructure", new { type, level }).Params
    };

    private static Project Start(World world, string type = "roads", string country = "aster")
    {
        var nation = world.Society!.Nations[country];
        Assert.True(SocietyInfrastructure.Enact(world, nation, Build(type)));
        return nation.Infrastructure.Single(project => project.Type == type);
    }

    private static void Staff(World world, int count, string country = "aster")
    {
        var firm = world.Agents[country + "_construction"];
        foreach (var worker in SocietyLabor.Workers(world, firm))
            worker.WorkEffort = 0;
        foreach (var worker in world.InCountry(country).Where(person => person.Role == "citizen" && !SocietyLabor.IsStudent(person)).Take(count))
        {
            SocietyLabor.Hire(world, firm, worker, "builder");
            worker.WorkEffort = 1;
            worker.Health = 100;
            worker.ArrearsWeeks = 0;
        }
    }

    private static void Days(World world, int count)
    {
        for (var day = 0; day < count; day++)
        {
            world.Tick++;
            SocietyInfrastructure.Day(world);
            SocietyEconomy.AssertConservation(world);
        }
    }

    [Fact]
    public void SetupMovesThreePercentOfResidentsAndKeepsPlannedFirmPublic()
    {
        var world = NewWorld();
        foreach (var nation in world.Society!.Nations.Values)
        {
            var firm = Assert.Single(SocietyLabor.Firms(world, nation.Id, PhaseContracts.Construction));
            Assert.Equal(3, SocietyLabor.Workers(world, firm).Length);
            Assert.Equal(1300, firm.WageOfferMinor);
            if (SocietyLabor.IsPlanned(nation))
                Assert.Null(firm.OwnerId);
        }
    }

    [Fact]
    public void ProjectWaitsForBuildersThenPaysExactlyItsCostAndConsumesMaterialsOnce()
    {
        var world = NewWorld();
        var project = Start(world);
        var nation = world.Society!.Nations["aster"];
        nation.MaintenanceFunded = false;
        Staff(world, 0);
        Days(world, 3);
        Assert.Equal(31, project.ReadyTick);
        Assert.Equal(0, project.PaidMinor);
        Assert.False(project.Complete);
        Staff(world, 4);
        var treasury = world.Agents["aster_government"].CashMinor;
        Days(world, 3);
        Assert.Equal(0, project.PaidMinor);
        Days(world, 1);
        Assert.Equal(project.CostMinor * 4 / 28, project.PaidMinor);
        Days(world, 24);
        Assert.True(project.Complete);
        Assert.Equal(project.CostMinor, project.PaidMinor);
        Assert.Equal(project.CostMinor, treasury - world.Agents["aster_government"].CashMinor);
        Assert.Equal(PhaseContracts.Materials("roads", 1)["tools"], world.Society.ConsumedGoods["tools"]);
        Assert.Equal(PhaseContracts.Materials("roads", 1)["energy"], world.Society.ConsumedGoods["energy"]);
        Assert.Single(world.Events, item => item.EventType == "infrastructure_started");
        Assert.Single(world.Events, item => item.EventType == "infrastructure_completed");
    }

    [Fact]
    public void MissingMaterialsDelayWorkWithoutConsumingPartialSupplies()
    {
        var world = NewWorld();
        var project = Start(world);
        Staff(world, 4);
        foreach (var agent in world.Agents.Values)
        {
            world.Society!.LostGoods["tools"] += agent.Inventory["tools"];
            agent.Inventory["tools"] = 0;
        }
        Days(world, 7);
        Assert.Equal(35, project.ReadyTick);
        Assert.Equal(0, project.PaidMinor);
        Assert.Equal(0, world.Society!.ConsumedGoods["energy"]);
        Assert.False(project.Complete);
    }

    [Fact]
    public void TreasuryShortfallPausesInstallmentsWithoutBorrowing()
    {
        var world = NewWorld();
        var project = Start(world);
        Staff(world, 4);
        var government = world.Agents["aster_government"];
        var bank = world.Agents["aster_bank"];
        SocietyEconomy.Transfer(world, government, bank, government.CashMinor);
        Days(world, 7);
        Assert.Equal(29, project.ReadyTick);
        Assert.Equal(0, project.PaidMinor);
        Assert.Equal(0, government.CashMinor);
        SocietyEconomy.Transfer(world, bank, government, project.CostMinor);
        Days(world, 22);
        Assert.True(project.Complete);
        Assert.Equal(project.CostMinor, project.PaidMinor);
    }

    [Fact]
    public void BuilderAllocationUsesStartOrderAndReportsShortages()
    {
        var world = NewWorld();
        var roads = Start(world);
        Days(world, 1);
        var irrigation = Start(world, "irrigation");
        Staff(world, 4);
        var view = Wire.Node(SocietyInfrastructure.View(world, world.Society!.Nations["aster"]));
        Assert.Equal(0, view["projects"]![0]!["builders_short"]!.GetValue<int>());
        Assert.Equal(3, view["projects"]![1]!["builders_short"]!.GetValue<int>());
        var before = roads.ReadyTick;
        Days(world, 1);
        Assert.Equal(before, roads.ReadyTick);
        Assert.Equal(30, irrigation.ReadyTick);
    }

    [Fact]
    public void PlannedEconomyBorrowsWorkersAndReturnsThemWhenWorkEnds()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations.Values.First(SocietyLabor.IsPlanned);
        nation.MaintenanceFunded = false;
        var before = world.InCountry(nation.Id).ToDictionary(person => person.Id,
            person => (person.EmployerId, person.Occupation, person.SalaryMinor));
        var project = Start(world, "hospital", nation.Id);
        Days(world, 1);
        var borrowed = nation.BorrowedBuilders.Keys.ToArray();
        Assert.Equal(3, borrowed.Length);
        Days(world, 41);
        Assert.True(project.Complete);
        Assert.Empty(nation.BorrowedBuilders);
        foreach (var id in borrowed)
        {
            var worker = world.Agents[id];
            Assert.Equal(before[id], (worker.EmployerId, worker.Occupation, worker.SalaryMinor));
        }
    }

    [Fact]
    public void UnpaidUpkeepWearsDownOncePerWeekAndFundedCrewRestoresCondition()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var project = new Project { Id = "worn", Type = "roads", Complete = true, CostMinor = 31000 };
        nation.Infrastructure.Add(project);
        nation.MaintenanceFunded = false;
        Days(world, 26 * 7);
        Assert.Equal(48, project.Condition);
        Assert.Single(world.Events, item => item.EventType == "infrastructure_worn");
        Assert.Equal(0.024, PhaseContracts.Boost(nation, "food"), 6);
        nation.MaintenanceFunded = true;
        Staff(world, 0);
        Days(world, 7);
        Assert.Equal(46, project.Condition);
        Staff(world, 1);
        var cash = world.Agents["aster_government"].CashMinor;
        Days(world, 7);
        Assert.Equal(100, project.Condition);
        Assert.Equal(310, cash - world.Agents["aster_government"].CashMinor);
    }

    [Theory]
    [InlineData("missile", 2, 7)]
    [InlineData("turret", 4, 35)]
    public void MilitaryCatalogProjectsUseTheSameConstructionPath(string type, int builders, int ticks)
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        nation.MaintenanceFunded = false;
        var project = new Project { Id = "military", Type = type, CostMinor = PhaseContracts.CostOf(type, 1), ReadyTick = ticks };
        nation.Infrastructure.Add(project);
        Staff(world, builders);
        Days(world, ticks);
        Assert.True(project.Complete);
        Assert.Equal(project.CostMinor, project.PaidMinor);
        Assert.Contains(project, nation.Infrastructure);
        if (type == "missile")
        {
            Days(world, 7);
            Assert.Equal(100, project.Condition);
        }
    }

    [Fact]
    public void UpgradesReplaceRatherThanStackAndPoliciesRecheckFundingAndParameters()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var project = Start(world);
        Assert.False(SocietyInfrastructure.Enact(world, nation, Build()));
        Assert.False(SocietyInfrastructure.Enact(world, nation, Build("missile")));
        Assert.False(SocietyInfrastructure.Enact(world, nation, Build("unknown")));
        Assert.False(SocietyInfrastructure.Enact(world, nation, Build("roads", -1)));
        Assert.False(SocietyInfrastructure.Enact(world, nation, new() { ActionType = "build_infrastructure" }));
        Assert.Null(SocietyInfrastructure.Enact(world, nation, new() { ActionType = "invest" }));
        project.Complete = true;
        project.PaidMinor = project.CostMinor;
        Assert.True(SocietyInfrastructure.Enact(world, nation, Build("roads", 2)));
        project = Assert.Single(nation.Infrastructure);
        Assert.Equal(2, project.Level);
        Assert.Equal(46500, project.CostMinor);
        project.Complete = true;
        Assert.Equal(0.08, PhaseContracts.Boost(nation, "food"), 6);
        project.Level = 3;
        Assert.False(SocietyInfrastructure.Enact(world, nation, Build("roads", 4)));
        var government = world.Agents["aster_government"];
        SocietyEconomy.Transfer(world, government, world.Agents["aster_bank"], government.CashMinor);
        Assert.False(SocietyInfrastructure.Enact(world, nation, Build("hospital")));
    }

    [Fact]
    public void MaintenanceToggleAndMultipleMilitaryOrdersRemainValidGovernmentOptions()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        nation.Infrastructure.Add(new() { Id = "missile_1", Type = PhaseContracts.Missile });
        nation.Infrastructure.Add(new() { Id = "missile_2", Type = PhaseContracts.Missile });
        var options = new List<Candidate>();
        SocietyInfrastructure.AddOptions(world, nation, world.Agents["aster_government"], options);
        var toggle = Assert.Single(options, option => option.ActionType == "set_maintenance");
        Assert.True(SocietyInfrastructure.Enact(world, nation, new() { ActionType = toggle.ActionType, Params = toggle.Params }));
        Assert.False(nation.MaintenanceFunded);
        Assert.False(SocietyInfrastructure.Enact(world, nation, new() { ActionType = "set_maintenance" }));
        Assert.Equal(6, options.Count(option => option.ActionType == "build_infrastructure"));
    }

    [Fact]
    public void AFounderCanReplaceAClosedContractorButCannotCreateASecondOne()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var founder = world.InCountry(nation.Id).First(person => person.Role == "citizen");
        SocietyEconomy.Transfer(world, world.Agents["aster_bank"], founder, 20000);
        Assert.False(SocietyLabor.CanStart(world, founder, PhaseContracts.Construction));
        SocietyLabor.Close(world, nation, world.Agents["aster_construction"], "test");
        Assert.True(SocietyLabor.CanStart(world, founder, PhaseContracts.Construction));
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void OutputAndClinicCapacityUseConditionScaledBoosts()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations["aster"];
        var before = SocietyEconomy.OutputMultiplier(world, nation, "food");
        nation.Infrastructure.Add(new() { Type = "roads", Complete = true, Condition = 50 });
        Assert.Equal(before * 1.025, SocietyEconomy.OutputMultiplier(world, nation, "food"), 6);
        nation.Infrastructure.Add(new() { Type = "hospital", Complete = true, Level = 2 });
        foreach (var person in world.InCountry("aster").Where(SocietyLabor.IsResident))
        {
            person.Health = 100;
            person.HealthThreshold = 0;
        }
        var clinic = world.Agents["aster_clinic"];
        var expected = (int)(SocietyLabor.ServiceCapacity(world, clinic) / 7 * 1.8);
        SocietyEconomy.Day(world);
        Assert.Equal(-expected, world.Society.VisitsServedToday[clinic.Id]);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public void SavedNationRetainsMaterialsProgressAndBorrowedWorkers()
    {
        var world = NewWorld();
        var nation = world.Society!.Nations.Values.First(SocietyLabor.IsPlanned);
        var project = Start(world, "hospital", nation.Id);
        Days(world, 7);
        var consumed = world.Society.ConsumedGoods["tools"];
        var saved = JsonSerializer.Deserialize<Nation>(JsonSerializer.Serialize(nation))!;
        world.Society.Nations[nation.Id] = saved;
        Assert.NotEmpty(saved.BorrowedBuilders);
        Assert.Contains(project.Id, saved.ConstructionMaterialsUsed);
        Days(world, 35);
        Assert.True(saved.Infrastructure.Single().Complete);
        Assert.Equal(consumed, world.Society.ConsumedGoods["tools"]);
    }

    [Theory]
    [InlineData("aster", "irrigation")]
    [InlineData("cinder", "hospital")]
    public void GovernmentDecisionBuildsThroughTheDailyEconomyAndReplaysFromCheckpoint(string country, string type)
    {
        var world = NewWorld();
        var now = DateTimeOffset.UnixEpoch;
        var job = SocietyEngine.Observe(world, country + "_government", now);
        Assert.NotNull(job.Observation["country"]!["infrastructure"]);
        var candidate = job.Candidates.First(option => option.ActionType == "build_infrastructure" && (string?)option.Params["type"] == type);
        Assert.DoesNotContain(job.Candidates, option => option.ActionType == "build_infrastructure" && (string?)option.Params["type"] is "missile" or "turret");
        var result = SocietyEngine.Apply(world, job, new(job.RequestId,
            new(candidate.ActionType, SocietyActions.Resolve(candidate), "build infrastructure", []), "rule", "none", 0), now);
        Assert.True(result.Accepted);
        for (var day = 0; day < 21; day++)
            SocietyEngine.Advance(world);
        var copy = Wire.Copy(world);
        for (var day = 0; day < 99; day++)
        {
            SocietyEngine.Advance(world);
            SocietyEngine.Advance(copy);
        }
        var project = Assert.Single(world.Society!.Nations[country].Infrastructure);
        Assert.True(project.Complete, $"Project stalled until {project.ReadyTick}, paid {project.PaidMinor}");
        Assert.Equal(project.CostMinor, project.PaidMinor);
        Assert.Equal(Wire.Node(world.Society).ToJsonString(), Wire.Node(copy.Society!).ToJsonString());
        SocietyEconomy.AssertConservation(copy);
    }

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
