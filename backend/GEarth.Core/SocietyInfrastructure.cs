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
    public const string Note = "Governments can build_infrastructure from treasury cash: builders need materials and are paid weekly as work progresses; shortages delay completion. Upgrades replace the existing project's boost during construction. Funded upkeep needs builders and costs 1% weekly; set_maintenance can stop it, losing 2 condition per week. ";

    private static IEnumerable<Project> Active(Nation nation) => nation.Infrastructure.Where(project => !project.Complete)
        .OrderBy(project => project.StartedTick);

    private static int Crew(Nation nation) => (nation.Infrastructure.Count(project => project.Complete && project.Type != PhaseContracts.Missile) + 3) / 4;

    // shortcut: a closed contractor waits for a new founder; revisit procurement if runs show persistent stalls.
    private static Agent? Firm(World world, Nation nation) => SocietyLabor.Firms(world, nation.Id, PhaseContracts.Construction)
        .OrderBy(firm => firm.Id, StringComparer.Ordinal).FirstOrDefault();

    private static int Working(World world, Agent? firm) => firm is null ? 0
        : SocietyLabor.Workers(world, firm).Count(worker => SocietyLabor.DayEffort(world, worker) > 0);

    public static void Setup(World world)
    {
        foreach (var nation in world.Society!.Nations.Values)
        {
            var firm = Firm(world, nation)!;
            var residents = world.InCountry(nation.Id).Where(SocietyLabor.IsResident).ToArray();
            var wanted = (int)Math.Round(residents.Length * 0.03);
            var owner = firm.OwnerId is null ? 0 : 1;
            foreach (var worker in residents.Where(person => person.Role == "citizen" && person.Occupation == "factory_worker"
                         && person.EmployerId is not null).OrderBy(person => person.Id).Take(Math.Max(0, wanted - owner)))
            {
                var previous = world.Agents[worker.EmployerId!];
                SocietyLabor.Hire(world, firm, worker, "builder");
                previous.TargetHeadcount = SocietyLabor.Staff(world, previous).Length;
            }
            firm.TargetHeadcount = SocietyLabor.Staff(world, firm).Length;
        }
    }

    private static bool CanBuild(World world, Nation nation, ProjectSpec spec, int level)
    {
        if (spec.Type is PhaseContracts.Missile or PhaseContracts.Turret)
            return false;
        var previous = nation.Infrastructure.SingleOrDefault(project => project.Type == spec.Type);
        return level == (previous?.Level ?? 0) + 1 && level <= spec.MaxLevel
            && (previous is null || previous.Complete) && Firm(world, nation) is not null
            && world.Agents[nation.Id + "_government"].CashMinor >= PhaseContracts.CostOf(spec.Type, level)
                + Active(nation).Sum(project => project.CostMinor - project.PaidMinor);
    }

    public static void AddOptions(World world, Nation nation, Agent government, List<Candidate> options)
    {
        foreach (var spec in PhaseContracts.Catalog)
        {
            var level = (nation.Infrastructure.FirstOrDefault(project => project.Type == spec.Type)?.Level ?? 0) + 1;
            if (CanBuild(world, nation, spec, level))
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
        var spec = PhaseContracts.Catalog.FirstOrDefault(item => item.Type == type);
        if (spec is null || !CanBuild(world, nation, spec, level))
            return false;
        var previous = nation.Infrastructure.SingleOrDefault(project => project.Type == type);
        if (previous is not null)
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
        world.Emit("infrastructure_started", "rule", [nation.Id, project.Id], new
        {
            project.Type, project.Level, project.CostMinor, project.ReadyTick, spec.Builders
        }, policy.CauseId);
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
            }
            if (firm is not null)
                AssignBuilders(world, nation, firm);
        }
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
