using System.Text.Json.Nodes;

namespace GEarth.Core;

public sealed partial class Nation
{
    public bool MaintenanceFunded { get; set; } = true;
    public HashSet<string> ConstructionMaterialsUsed { get; set; } = [];
    public Dictionary<string, BorrowedBuilder> BorrowedBuilders { get; set; } = new();
}

public sealed record BorrowedBuilder(string EmployerId, string Occupation, long SalaryMinor);

public static class SocietyInfrastructure
{
    public const string Note = "Governments can build_infrastructure from treasury cash through the state construction contractor: half the cost is advanced when ordered and the rest paid weekly as work progresses, the treasury covers builders' pay when the contractor runs short, and the contractor bids up its wage offer and recruits from non-essential unskilled jobs while builders are missing; shortages of builders or materials delay completion. A port cuts import transit time. Upgrades replace the existing project's boost during construction. Funded upkeep needs builders and costs 1% weekly; set_maintenance can stop it, losing 2 condition per week. ";

    // Share of a project's cost paid to the contractor when it is ordered, so it can hire and buy materials.
    public const double AdvanceShare = 0.5; // ponytail: tune after runs

    private static IEnumerable<Project> Active(Nation nation) => nation.Infrastructure.Where(project => !project.Complete)
        .OrderBy(project => project.StartedTick);

    private static int Crew(Nation nation) => (nation.Infrastructure.Count(project => project.Complete && project.Type != PhaseContracts.Missile) + 3) / 4;

    // shortcut: a closed contractor waits for a new founder; revisit procurement if runs show persistent stalls.
    private static Agent? Firm(World world, Nation nation) => SocietyLabor.Firms(world, nation.Id, PhaseContracts.Construction)
        .OrderBy(firm => firm.Id, StringComparer.Ordinal).FirstOrDefault();

    private static int Working(World world, Agent? firm) => firm is null ? 0
        : SocietyLabor.Workers(world, firm).Count(worker => SocietyLabor.DayEffort(world, worker) > 0);

    public static bool CanOrder(World world, Nation nation, ProjectSpec spec, int level)
    {
        var previous = spec.Type == PhaseContracts.Missile
            ? nation.Infrastructure.FirstOrDefault(project => project.Type == spec.Type && !project.Complete)
            : nation.Infrastructure.SingleOrDefault(project => project.Type == spec.Type);
        return level == (previous?.Level ?? 0) + 1 && level <= spec.MaxLevel
            && (previous is null || previous.Complete) && Firm(world, nation) is not null
            && world.Agents[nation.Id + "_government"].CashMinor >= PhaseContracts.CostOf(spec.Type, level)
                + Active(nation).Sum(project => project.CostMinor - project.PaidMinor);
    }

    public static void AddOptions(World world, Nation nation, Agent government, List<Candidate> options)
    {
        foreach (var spec in PhaseContracts.Catalog.Where(spec => spec.Type is not (PhaseContracts.Missile or PhaseContracts.Turret)))
        {
            var level = (nation.Infrastructure.FirstOrDefault(project => project.Type == spec.Type)?.Level ?? 0) + 1;
            if (CanOrder(world, nation, spec, level))
                options.Add(SocietyActions.Make("build_infrastructure", new { type = spec.Type, level }));
        }
        options.Add(SocietyActions.Make("set_maintenance", new { on = !nation.MaintenanceFunded }));
    }

    public static bool? Enact(World world, Nation nation, PolicyProposal policy)
    {
        if (policy.ActionType == "set_maintenance")
        {
            if (policy.Params["on"] is not JsonValue on || !on.TryGetValue<bool>(out var funded))
                return false;
            nation.MaintenanceFunded = funded;
            return true;
        }
        if (policy.ActionType != "build_infrastructure")
            return null;
        if (policy.Params["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type)
            || policy.Params["level"] is not JsonValue levelValue || !levelValue.TryGetValue<int>(out var level))
            return false;
        if (type is PhaseContracts.Missile or PhaseContracts.Turret)
            return false;
        return Order(world, nation, type, level, policy.CauseId);
    }

    // Civilian and military orders share commitments, advance funding and the same construction lifecycle.
    public static bool Order(World world, Nation nation, string type, int level, string cause)
    {
        var spec = PhaseContracts.Catalog.FirstOrDefault(item => item.Type == type);
        if (spec is null || !CanOrder(world, nation, spec, level))
            return false;
        var previous = nation.Infrastructure.FirstOrDefault(project => project.Type == type);
        if (previous is not null && type != PhaseContracts.Missile)
        {
            nation.Infrastructure.Remove(previous);
            nation.ConstructionMaterialsUsed.Remove(previous.Id);
        }
        var project = new Project
        {
            Id = $"project_{world.NextEntityNumber()}", Type = type, Level = level,
            CostMinor = PhaseContracts.CostOf(type, level), StartedTick = world.Tick, ReadyTick = world.Tick + spec.Ticks
        };
        nation.Infrastructure.Add(project);
        project.PaidMinor = (long)(project.CostMinor * AdvanceShare);
        SocietyEconomy.Transfer(world, world.Agents[nation.Id + "_government"], Firm(world, nation)!, project.PaidMinor, "construction_advance");
        world.Emit("infrastructure_started", "rule", [nation.Id, project.Id], new
        {
            project.Type, project.Level, project.CostMinor, advance_minor = project.PaidMinor, project.ReadyTick, spec.Builders
        }, cause);
        return true;
    }

    public static void Autopilot(World world, Nation nation, Agent firm)
    {
        var needed = Active(nation).Sum(project => PhaseContracts.Spec(project.Type).Builders)
            + (nation.MaintenanceFunded ? Crew(nation) : 0);
        firm.TargetHeadcount = Math.Max(0, needed - (firm.OwnerId is { } owner && SocietyLabor.OwnerWorks(world.Agents[owner], firm) ? 1 : 0));
        firm.OvertimeOffered = false;
    }

    private static void AssignBuilders(World world, Nation nation, Agent firm)
    {
        Autopilot(world, nation, firm);
        foreach (var (id, previous) in nation.BorrowedBuilders.ToArray())
        {
            var worker = world.Agents[id];
            if (SocietyLabor.IsPlanned(nation) && worker.EmployerId == firm.Id
                && SocietyLabor.Staff(world, firm).Length <= firm.TargetHeadcount)
                continue;
            if (worker.EmployerId == firm.Id)
            {
                if (world.Agents[previous.EmployerId].Role == "business")
                    (worker.EmployerId, worker.Occupation, worker.SalaryMinor) = (previous.EmployerId, previous.Occupation, previous.SalaryMinor);
                else
                    SocietyLabor.Fire(world, worker);
            }
            nation.BorrowedBuilders.Remove(id);
        }
        if (!SocietyLabor.IsPlanned(nation))
            return;
        while (SocietyLabor.Staff(world, firm).Length < firm.TargetHeadcount)
        {
            var donor = world.InCountry(nation.Id).Where(other => other.Role == "business" && other != firm && other.OwnerId is null)
                .OrderByDescending(other => SocietyLabor.Staff(world, other).Length).ThenBy(other => other.Id)
                .FirstOrDefault(other => SocietyLabor.Staff(world, other).Length > 1);
            var worker = donor is null ? null : SocietyLabor.Staff(world, donor)
                .Where(person => person.Role == "citizen" && !SocietyLabor.IsStudent(person))
                .OrderBy(person => person.Id).FirstOrDefault();
            if (worker is null)
                break;
            nation.BorrowedBuilders.Add(worker.Id, new(donor!.Id, worker.Occupation, worker.SalaryMinor));
            SocietyLabor.Hire(world, firm, worker, "builder");
        }
    }

    public static void Day(World world)
    {
        foreach (var nation in world.Society!.Nations.Values)
        {
            var firm = Firm(world, nation);
            if (firm is not null)
                AssignBuilders(world, nation, firm);
            var builders = Working(world, firm);
            var government = world.Agents[nation.Id + "_government"];
            foreach (var project in Active(nation))
            {
                if (world.Tick <= project.StartedTick)
                    continue;
                var spec = PhaseContracts.Spec(project.Type);
                var days = Math.Clamp(spec.Ticks - (project.ReadyTick - world.Tick), 0, spec.Ticks);
                var due = world.Tick % SocietyLabor.DaysPerWeek == 0 || days == spec.Ticks
                    ? project.CostMinor * days / spec.Ticks - project.PaidMinor : 0;
                if (firm is null || builders < spec.Builders || government.CashMinor < due)
                {
                    project.ReadyTick++;
                    continue;
                }
                builders -= spec.Builders;
                if (!nation.ConstructionMaterialsUsed.Contains(project.Id))
                {
                    var materials = PhaseContracts.Materials(project.Type, project.Level)
                        .Where(pair => pair.Key != "ore" || PhaseContracts.HasRawMaterials).ToArray();
                    foreach (var (good, quantity) in materials)
                        SocietyEconomy.AcquireInput(world, firm, good, Math.Max(0, quantity - firm.Inventory[good]));
                    if (materials.Any(pair => firm.Inventory[pair.Key] < pair.Value))
                    {
                        project.ReadyTick++;
                        continue;
                    }
                    foreach (var (good, quantity) in materials)
                    {
                        firm.Inventory[good] -= quantity;
                        world.Society.ConsumedGoods[good] += quantity;
                    }
                    nation.ConstructionMaterialsUsed.Add(project.Id);
                }
                if (due > 0)
                {
                    SocietyEconomy.Sell(world, government, firm, due, kind: "construction");
                    project.PaidMinor += due;
                }
                if (world.Tick >= project.ReadyTick && project.PaidMinor == project.CostMinor)
                {
                    project.Complete = true;
                    nation.ConstructionMaterialsUsed.Remove(project.Id);
                    world.Emit("infrastructure_completed", "rule", [nation.Id, project.Id], new { project.Type, project.Level, project.PaidMinor });
                }
            }
            if (world.Tick % SocietyLabor.DaysPerWeek == 0)
            {
                var maintained = 0;
                foreach (var project in nation.Infrastructure.Where(project => project.Complete && project.Type != PhaseContracts.Missile))
                {
                    var before = project.Condition;
                    project.Condition = Math.Max(0, project.Condition - 2);
                    var upkeep = project.CostMinor / 100;
                    if (nation.MaintenanceFunded && firm is not null && maintained < builders * 4 && government.CashMinor >= upkeep)
                    {
                        SocietyEconomy.Sell(world, government, firm, upkeep, kind: "infrastructure_maintenance");
                        project.Condition = 100;
                        maintained++;
                    }
                    if (before >= 50 && project.Condition < 50)
                        world.Emit("infrastructure_worn", "rule", [nation.Id, project.Id], new { project.Type, project.Condition });
                }
                if (firm is not null)
                    Settle(world, nation, firm, government);
            }
            if (firm is not null)
                AssignBuilders(world, nation, firm);
        }
    }

    // Before payroll, the treasury tops the state contractor up to a week of its builders' pay; with no project
    // under way it takes back anything above two weeks of pay.
    private static void Settle(World world, Nation nation, Agent firm, Agent government)
    {
        var payroll = SocietyLabor.Staff(world, firm).Sum(worker => worker.SalaryMinor);
        var liquid = SocietyBank.Liquid(world, firm);
        if (liquid < payroll)
            SocietyEconomy.Transfer(world, government, firm, Math.Min(SocietyBank.Liquid(world, government), payroll - liquid), "state_subsidy");
        else if (!Active(nation).Any() && liquid > payroll * 2)
            SocietyEconomy.Transfer(world, firm, government, liquid - payroll * 2, "state_surplus");
    }

    public static object View(World world, Nation nation)
    {
        var builders = Working(world, Firm(world, nation));
        var shortages = new Dictionary<string, int>();
        foreach (var project in Active(nation))
        {
            var needed = PhaseContracts.Spec(project.Type).Builders;
            shortages[project.Id] = Math.Max(0, needed - builders);
            if (builders >= needed)
                builders -= needed;
        }
        return new
        {
            nation.MaintenanceFunded,
            projects = nation.Infrastructure.Select(project => new
            {
                project.Id, project.Type, project.Level, project.Condition, project.Complete, project.ReadyTick,
                project.CostMinor, project.PaidMinor, builders_short = shortages.GetValueOrDefault(project.Id)
            }).ToArray(),
            upkeep_per_week_minor = nation.Infrastructure.Where(project => project.Complete && project.Type != PhaseContracts.Missile).Sum(project => project.CostMinor / 100),
            catalog = PhaseContracts.Catalog.Where(spec => spec.Type is not (PhaseContracts.Missile or PhaseContracts.Turret)).Select(spec => new
            {
                spec.Type, spec.Builders, spec.Ticks,
                levels = Enumerable.Range(1, spec.MaxLevel).Select(level => new
                {
                    level, cost_minor = PhaseContracts.CostOf(spec.Type, level),
                    effects = spec.Effects.ToDictionary(pair => pair.Key, pair => pair.Value * PhaseContracts.LevelGain[level - 1])
                }).ToArray()
            }).ToArray()
        };
    }
}
