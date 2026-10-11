using System.Text.Json.Nodes;

namespace GEarth.Core;

// War, unrest and relations (phase 3). Relations run -100..100: trade and gifts raise them, they drift toward 0
// weekly, and every other country turns against an aggressor. Each war week is a contest decided by strength
// (fit soldiers plus missiles); the winner takes tribute and wounds some of the loser's soldiers. A government's
// mission is to keep average quality of life up; long unrest brings an early election or, in an autocracy short of
// paid soldiers, a revolution.
public static class SocietyWar
{
    public const int WarBelow = -20;      // relations at or below this allow declare_conflict (the old tension 60)
    public const int EmbargoBelow = -60;  // relations at or below this close the routes between the pair
    public const int GiftGainCap = 15;
    public const int SoldierStep = 2;
    public const double TributeShare = 0.10, CessionShare = 0.25, ReparationsShare = 0.25, WoundedShare = 0.25;
    public const int SurrenderWeeks = 2;
    public const double WearinessStep = 0.1, MaxWeariness = 0.5, WearySupportLoss = 0.05;
    // ponytail: tune after runs (x, n, y, m in the design)
    public const int UnrestQol = 40;
    public const int UnrestDays = 28;
    public const double RevolutionSoldierShare = 0.05;
    public const int SoldiersPerMissile = 3;

    // Readiness for war by government type; two democracies rarely fight each other.
    public static readonly Dictionary<string, double> Readiness = new()
    {
        ["dictatorship"] = 0.8, ["monarchy"] = 0.6, ["planned_republic"] = 0.5,
        ["market_democracy"] = 0.2, ["social_democracy"] = 0.15
    };

    public static readonly string Note = $"Relations between countries run from -100 to 100: they rise with delivered trade and gifts (send_gift of cash in minor units or raw materials in units, up to +{GiftGainCap} as the gift compares with the recipient's weekly spending), drift toward 0 each week, and fall for every other country toward an aggressor, more when it is bigger than its victim or they are the victim's friends; at {EmbargoBelow} or below the routes between the pair close (embargo). With relations at {WarBelow} or below a government can declare_conflict (5000 upfront plus 1000 per week, paid to the soldiers). Each war week is a contest: strength is fit soldiers plus {SoldiersPerMissile} per missile, and a side wins the week with probability its strength over both; the winner takes tribute ({(int)Math.Round(100 * TributeShare)}% of the loser's weekly taxes and of its ore and fuel stock) and {(int)Math.Round(100 * WoundedShare)}% of the loser's fit soldiers are wounded (too sick to fight until treated). A side with no fit soldiers for {SurrenderWeeks} weeks surrenders to the winner's terms; otherwise a war ends when offer_peace is accepted, its terms being what the offering side demands (none, reparations of {(int)Math.Round(100 * ReparationsShare)}% of the treasury, or {(int)Math.Round(100 * CessionShare)}% of the other's concentration in one raw material). Missiles are ordered from the builders (order_missile) and launched at an enemy (launch_missile); a hit damages one completed project, and a turret (build_turret) stops {(int)Math.Round(100 * PhaseContracts.Spec(PhaseContracts.Turret).Effects["missile_defense"])}% of hits. Soldiers are recruited or discharged {SoldierStep} at a time at the table wage, the unemployed first; discharging is offered only above the soldiers needed to keep order. War weeks lower residents' security and support. A government's mission is average quality of life of at least {UnrestQol}: below it for {UnrestDays} days a democracy holds an early election, and an autocracy whose paid soldiers are fewer than {(int)Math.Round(100 * RevolutionSoldierShare)}% of residents faces a revolution that changes its government. ";

    public static Agent[] Soldiers(World world, Nation nation) => world.InCountry(nation.Id)
        .Where(person => person.EmployerId == nation.Id + "_government" && person.Occupation == "soldier").ToArray();

    // Strikers and the wounded do not fight; unpaid soldiers (wages owed) do not defend the regime.
    private static bool Fit(Agent soldier) => !SocietyEconomy.TooSick(soldier) && !SocietyLabor.OnArrearsStrike(soldier);
    private static bool Paid(Agent soldier) => soldier.ArrearsMinor == 0;

    public static int Strength(World world, Nation nation) =>
        Soldiers(world, nation).Count(Fit) + SoldiersPerMissile * nation.Missiles;

    // The contest function: the side wins the week with probability own / (own + enemy); roll is uniform in 0-1.
    public static bool Wins(double roll, int own, int enemy) => own > 0 && (enemy == 0 || roll * (own + enemy) < own);

    public static double ReadinessFor(Nation nation, Nation target) => Readiness[nation.GovernmentId]
        * (Governments.Get(nation.GovernmentId).Elected && Governments.Get(target.GovernmentId).Elected ? 0.25 : 1);

    private static bool HasBuilders => SocietyLabor.Sectors.Any(sector => sector.Id == PhaseContracts.Construction);

    private static Relationship Relation(World world, string first, string second) => SocietyDiplomacy.Relation(world, first, second);

    private static IEnumerable<Relationship> RelationsOf(World world, string countryId) =>
        world.Society!.Relations.Values.Where(relation => relation.First == countryId || relation.Second == countryId);

    private static string Other(Relationship relation, string countryId) => relation.First == countryId ? relation.Second : relation.First;

    public static void Day(World world)
    {
        var society = world.Society!;
        foreach (var nation in society.Nations.Values)
            Arm(world, nation);
        if (world.Tick % SocietyLabor.DaysPerWeek != 0)
            return;
        var wars = society.Relations.Values.Where(relation => relation.AtWar).OrderBy(relation => relation.Id).ToArray();
        foreach (var nation in society.Nations.Values.Where(nation => wars.Any(war => war.First == nation.Id || war.Second == nation.Id)))
            nation.NoFitSoldierWeeks = Soldiers(world, nation).Any(Fit) || nation.Missiles > 0 ? 0 : nation.NoFitSoldierWeeks + 1;
        foreach (var war in wars)
            Contest(world, war);
        foreach (var relation in society.Relations.Values.Where(relation => relation.AtWar && relation.Aggressor is not null))
            Condemn(world, society.Nations[relation.Aggressor!], society.Nations[Other(relation, relation.Aggressor!)], 2);
        foreach (var relation in society.Relations.Values.Where(relation => !relation.AtWar && relation.Score != 0))
            relation.Score -= Math.Sign(relation.Score) * Math.Max(1, Math.Abs(relation.Score) / 10);
        foreach (var nation in society.Nations.Values)
        {
            Weariness(world, nation, wars.Any(war => war.First == nation.Id || war.Second == nation.Id));
            Unrest(world, nation);
            nation.TaxMarkMinor = nation.TaxesCollectedMinor;
        }
    }

    // Missiles and turrets are projects. Built alone (no construction firm) they were paid for upfront and finish
    // after their build ticks; a finished missile joins the stockpile and leaves the project list.
    private static void Arm(World world, Nation nation)
    {
        if (!HasBuilders)
            foreach (var project in nation.Infrastructure.Where(project => !project.Complete && world.Tick >= project.ReadyTick
                         && project.Type is PhaseContracts.Missile or PhaseContracts.Turret && project.PaidMinor >= project.CostMinor))
            {
                project.Complete = true;
                world.Emit("infrastructure_completed", "rule", [nation.Id, project.Id], new { project.Type, project.Level });
            }
        foreach (var missile in nation.Infrastructure.Where(project => project.Complete && project.Type == PhaseContracts.Missile).ToArray())
        {
            nation.Infrastructure.Remove(missile);
            nation.Missiles++;
            world.Emit("missile_ready", "rule", [nation.Id, missile.Id], new { nation.Missiles });
        }
    }

    private static void Contest(World world, Relationship war)
    {
        var society = world.Society!;
        var (first, second) = (society.Nations[war.First], society.Nations[war.Second]);
        first.WarWeeks++;
        second.WarWeeks++;
        var (firstOut, secondOut) = (first.NoFitSoldierWeeks >= SurrenderWeeks, second.NoFitSoldierWeeks >= SurrenderWeeks);
        if (firstOut || secondOut)
        {
            // Both unable to fight: the war peters out with no terms.
            if (firstOut && secondOut)
                EndWar(world, war, first, second, "none", "both_exhausted");
            else
                EndWar(world, war, firstOut ? second : first, firstOut ? first : second, ImposedTerms(firstOut ? second : first, firstOut ? first : second), "surrender");
            return;
        }
        var (a, b) = (Strength(world, first), Strength(world, second));
        if (a + b == 0)
            return;
        var firstWins = Wins(SocietyFactory.Sample(world.Seed, $"{war.Id}:{world.Tick}", "war_week"), a, b);
        var (winner, loser) = firstWins ? (first, second) : (second, first);
        var winnerGovernment = world.Agents[winner.Id + "_government"];
        var loserGovernment = world.Agents[loser.Id + "_government"];
        var tribute = Math.Min(loserGovernment.CashMinor, (long)((loser.TaxesCollectedMinor - loser.TaxMarkMinor) * TributeShare));
        SocietyEconomy.Transfer(world, loserGovernment, winnerGovernment, Math.Max(0, tribute), "war_tribute");
        var goods = new Dictionary<string, int>();
        if (PhaseContracts.HasRawMaterials)
            foreach (var good in PhaseContracts.RawMaterials)
                goods[good] = MoveGoods(world, loser, winner, good, TributeShare);
        var fit = Soldiers(world, loser).Where(Fit).OrderBy(soldier => soldier.Id).ToArray();
        var wounded = fit.Take((int)Math.Ceiling(fit.Length * WoundedShare)).ToArray();
        foreach (var soldier in wounded)
            soldier.Health = SocietyEconomy.SickBelow - 10;
        world.Emit("war_week", "rule", [war.Id, winner.Id, loser.Id], new
        {
            winner = winner.Id, loser = loser.Id, strength = new Dictionary<string, int> { [first.Id] = a, [second.Id] = b },
            tribute_minor = Math.Max(0, tribute), tribute_goods = goods, wounded = wounded.Length
        });
    }

    // A surrendering side gets the winner's default terms: its richest raw material when there are any, else reparations.
    private static string ImposedTerms(Nation winner, Nation loser) => PhaseContracts.HasRawMaterials
        ? "cede_" + PhaseContracts.RawMaterials.OrderByDescending(good => PhaseContracts.Endowment(loser, good)).First()
        : "reparations";

    // Peace: the terms are what the winner (or the side whose offer was accepted) takes from the other.
    public static void EndWar(World world, Relationship war, Nation winner, Nation loser, string terms, string reason)
    {
        var paid = 0L;
        if (terms == "reparations")
        {
            var loserGovernment = world.Agents[loser.Id + "_government"];
            paid = (long)(loserGovernment.CashMinor * ReparationsShare);
            SocietyEconomy.Transfer(world, loserGovernment, world.Agents[winner.Id + "_government"], paid, "war_reparations");
        }
        else if (terms.StartsWith("cede_", StringComparison.Ordinal))
        {
            var good = terms["cede_".Length..];
            var ceded = PhaseContracts.Endowment(loser, good) * CessionShare;
            loser.Endowment[good] = PhaseContracts.Endowment(loser, good) - ceded;
            winner.Endowment[good] = PhaseContracts.Endowment(winner, good) + ceded;
        }
        war.AtWar = false;
        war.Aggressor = null;
        (winner.NoFitSoldierWeeks, loser.NoFitSoldierWeeks) = (0, 0);
        world.Emit("war_ended", "rule", [war.Id, winner.Id, loser.Id], new
        {
            winner = winner.Id, loser = loser.Id, terms, reason, reparations_minor = paid,
            loser_endowment = loser.Endowment, winner_endowment = winner.Endowment
        });
    }

    // Moves a share of the loser's stock of a good (held by its firms) to the winner's firms in the same sector.
    private static int MoveGoods(World world, Nation from, Nation to, string good, double share)
    {
        var sector = SocietyLabor.Sectors.FirstOrDefault(sector => sector.Good == good)?.Id;
        var receiver = (sector is null ? null : SocietyLabor.Firms(world, to.Id, sector).OrderBy(firm => firm.Id).FirstOrDefault())
            ?? world.Agents[to.Id + "_government"];
        var moved = 0;
        foreach (var holder in world.InCountry(from.Id).Where(agent => agent.Role == "business" && agent.Inventory.GetValueOrDefault(good) > 0))
        {
            var units = (int)(holder.Inventory[good] * share);
            holder.Inventory[good] -= units;
            receiver.Inventory[good] = receiver.Inventory.GetValueOrDefault(good) + units;
            moved += units;
        }
        return moved;
    }

    // Every third country turns against an aggressor, more when it is bigger than its victim (bullying) and when
    // they are friends of the victim (who may then embargo it or join the war).
    public static void Condemn(World world, Nation aggressor, Nation victim, int points)
    {
        var ratio = Math.Clamp(aggressor.Population / (double)Math.Max(1, victim.Population), 0.5, 3);
        foreach (var third in world.Society!.Nations.Values.Where(nation => nation.Id != aggressor.Id && nation.Id != victim.Id))
        {
            var friendship = 1 + Math.Max(0, Relation(world, third.Id, victim.Id).Score) / 50.0;
            var relation = Relation(world, third.Id, aggressor.Id);
            relation.Score = Math.Max(-100, relation.Score - (int)Math.Round(points * ratio * friendship));
        }
    }

    private static void Weariness(World world, Nation nation, bool atWar)
    {
        nation.WarWeariness = Math.Round(Math.Clamp(nation.WarWeariness + (atWar ? WearinessStep : -WearinessStep), 0, MaxWeariness), 2);
        foreach (var resident in world.InCountry(nation.Id).Where(SocietyLabor.IsResident))
        {
            resident.WarWeariness = nation.WarWeariness;
            if (atWar && resident.SupportsLeadership && SocietyFactory.Sample(world.Seed, $"{resident.Id}:{world.Tick}", "war_weary") < WearySupportLoss)
                resident.SupportsLeadership = false;
        }
    }

    private static void Unrest(World world, Nation nation)
    {
        var residents = world.InCountry(nation.Id).Where(SocietyLabor.IsResident).ToArray();
        var quality = residents.Length == 0 ? 100 : residents.Average(resident => resident.Wellbeing);
        if (quality >= UnrestQol)
        {
            nation.UnrestSinceTick = -1;
            return;
        }
        if (nation.UnrestSinceTick < 0)
        {
            nation.UnrestSinceTick = world.Tick;
            world.Emit("unrest_started", "rule", [nation.Id], new { average_quality_of_life = Math.Round(quality, 1), threshold = UnrestQol });
            return;
        }
        if (world.Tick - nation.UnrestSinceTick < UnrestDays)
            return;
        nation.UnrestSinceTick = world.Tick; // the clock restarts after the crisis comes to a head
        var paid = Soldiers(world, nation).Count(Paid);
        if (Governments.Get(nation.GovernmentId).Elected)
        {
            nation.NextElectionTick = world.Tick + 1;
            world.Emit("early_election_called", "rule", [nation.Id], new { average_quality_of_life = Math.Round(quality, 1), nation.NextElectionTick });
        }
        else if (paid < residents.Length * RevolutionSoldierShare)
        {
            var choices = Governments.Profiles.Where(profile => profile.Id != nation.GovernmentId).ToArray();
            var next = choices[(int)(SocietyFactory.Sample(world.Seed, $"{nation.Id}:{world.Tick}", "revolution") * choices.Length) % choices.Length];
            var previous = nation.GovernmentId;
            SocietyFactory.ChangeGovernment(world, nation.Id, next.Id);
            world.Emit("revolution", "rule", [nation.Id], new { from = previous, to = next.Id, paid_soldiers = paid, residents = residents.Length });
        }
        else
            world.Emit("unrest_suppressed", "rule", [nation.Id], new { paid_soldiers = paid, residents = residents.Length });
    }

    // Goods the country has none of whose foreign holders it cannot reach (war, embargo or no open route).
    public static (string Good, (string CountryId, string Reason)[] HeldBy)[] Blocked(World world, Nation nation) =>
        SocietyFactory.Goods.Where(good => nation.Stocks.GetValueOrDefault(good) == 0)
            .Select(good => (good, world.Society!.Nations.Values.Where(other => other.Id != nation.Id && other.Stocks.GetValueOrDefault(good) > 0)
                .Select(other => (other.Id, Reason: BlockedBy(world, nation, other))).ToArray()))
            .Where(item => item.Item2.Length > 0 && item.Item2.All(holder => holder.Reason is not null))
            .Select(item => (item.good, item.Item2.Select(holder => (holder.Id, holder.Reason!)).ToArray())).ToArray();

    private static string? BlockedBy(World world, Nation nation, Nation holder)
    {
        var relation = Relation(world, nation.Id, holder.Id);
        if (relation.AtWar)
            return "war";
        if (relation.Score <= EmbargoBelow)
            return "embargo";
        return SocietyTrade.Path(world, holder.Id + "_city", nation.Id + "_city", "cautious") is null ? "no_route" : null;
    }

    // Rule default: trade first. Without a model, war is offered only when the target holds a good the country
    // cannot buy, the country is stronger, and a draw weighted by its readiness passes. A model always sees it.
    public static bool OffersWar(World world, Nation nation, Nation target) => world.Mode == "LIVE"
        || Blocked(world, nation).Any(item => item.HeldBy.Any(holder => holder.CountryId == target.Id))
        && Strength(world, nation) > Strength(world, target)
        && SocietyFactory.Sample(world.Seed, $"{nation.Id}:{target.Id}:{world.Tick}", "war_readiness") < ReadinessFor(nation, target);

    // Paid soldiers an autocracy needs to put down a revolution; every government keeps at least this many by default.
    public static int OrderLine(World world, Nation nation) =>
        (int)Math.Ceiling(world.InCountry(nation.Id).Count(SocietyLabor.IsResident) * RevolutionSoldierShare);

    private static bool Threatened(World world, Nation nation) =>
        RelationsOf(world, nation.Id).Any(relation => relation.AtWar || relation.Score <= WarBelow);

    // Recruits come from the unemployed first, then from unskilled jobs at the lowest pay.
    private static Agent[] Recruits(World world, Nation nation) => world.InCountry(nation.Id)
        .Where(person => person.Role == "citizen" && !SocietyLabor.IsStudent(person) && (person.EmployerId is null
            || SocietyLabor.Unskilled.Contains(person.Occupation) && world.Agents[person.EmployerId].Role == "business"))
        .OrderBy(person => person.EmployerId is null ? 0 : 1).ThenBy(person => person.SalaryMinor).ThenBy(person => person.Id)
        .Take(SoldierStep).ToArray();

    public static void AddOptions(World world, Nation nation, Agent government, List<Candidate> options)
    {
        var atWar = RelationsOf(world, nation.Id).Where(relation => relation.AtWar).Select(relation => Other(relation, nation.Id)).Order().ToArray();
        var threatened = Threatened(world, nation);
        var soldiers = Soldiers(world, nation);
        var pay = SocietyLabor.TableWage["soldier"];
        var orderLine = OrderLine(world, nation);
        if ((threatened || soldiers.Length < orderLine) && Recruits(world, nation).Length == SoldierStep && government.CashMinor >= 8 * SoldierStep * pay)
            options.Add(SocietyActions.Make("recruit_soldiers", new { count = SoldierStep, weekly_pay_minor = pay }));
        if (!threatened && soldiers.Length - SoldierStep >= orderLine)
            options.Add(SocietyActions.Make("discharge_soldiers", new { count = SoldierStep }));
        var missileCost = PhaseContracts.CostOf(PhaseContracts.Missile, 1);
        if (threatened && government.CashMinor >= missileCost
            && !nation.Infrastructure.Any(project => project.Type == PhaseContracts.Missile && !project.Complete))
            options.Add(SocietyActions.Make("order_missile", new { cost_minor = missileCost, ticks = PhaseContracts.Spec(PhaseContracts.Missile).Ticks }));
        var turretCost = PhaseContracts.CostOf(PhaseContracts.Turret, 1);
        if (threatened && government.CashMinor >= turretCost && !nation.Infrastructure.Any(project => project.Type == PhaseContracts.Turret))
            options.Add(SocietyActions.Make("build_turret", new { cost_minor = turretCost, ticks = PhaseContracts.Spec(PhaseContracts.Turret).Ticks }));
        if (nation.Missiles > 0)
            foreach (var enemy in atWar)
                options.Add(SocietyActions.Make("launch_missile", new { country_id = enemy }));
        // A gift to the country with the worst relations not at war, when the treasury can spare it.
        if (RelationsOf(world, nation.Id).Where(relation => !relation.AtWar).OrderBy(relation => relation.Score).ThenBy(relation => relation.Id)
                .FirstOrDefault() is { } coldest && government.CashMinor >= 20000)
        {
            options.Add(new("send_gift", new JsonObject
            {
                ["country_id"] = Other(coldest, nation.Id), ["kind"] = "cash",
                ["amount"] = SocietyActions.Range(500, (int)Math.Min(10000, government.CashMinor / 4), 1000) // minor units
            }));
            if (PhaseContracts.HasRawMaterials)
                foreach (var good in PhaseContracts.RawMaterials.Where(good => nation.Stocks.GetValueOrDefault(good) >= 20))
                    options.Add(new("send_gift", new JsonObject
                    {
                        ["country_id"] = Other(coldest, nation.Id), ["kind"] = good,
                        ["amount"] = SocietyActions.Range(10, nation.Stocks[good] / 2, 10) // units
                    }));
        }
    }

    public static string[] PeaceTerms => PhaseContracts.HasRawMaterials
        ? ["none", "reparations", .. PhaseContracts.RawMaterials.Select(good => "cede_" + good)]
        : ["none", "reparations"];

    public static bool Enact(World world, Nation nation, PolicyProposal policy)
    {
        var government = world.Agents[nation.Id + "_government"];
        var parameters = policy.Params;
        switch (policy.ActionType)
        {
            case "recruit_soldiers":
                var recruits = Recruits(world, nation);
                foreach (var person in recruits)
                {
                    (person.EmployerId, person.Occupation, person.SalaryMinor) = (government.Id, "soldier", SocietyLabor.TableWage["soldier"]);
                    (person.UnemployedSinceTick, person.OvertimeDays) = (-1, 0);
                }
                world.Emit("soldiers_recruited", "rule", [nation.Id], new { recruited = recruits.Length, soldiers = Soldiers(world, nation).Length }, policy.CauseId);
                return recruits.Length > 0;
            case "discharge_soldiers":
                var discharged = Soldiers(world, nation).OrderBy(soldier => soldier.Health).ThenBy(soldier => soldier.Id).Take(SoldierStep).ToArray();
                foreach (var soldier in discharged)
                    SocietyLabor.Fire(world, soldier);
                world.Emit("soldiers_discharged", "rule", [nation.Id], new { discharged = discharged.Length, soldiers = Soldiers(world, nation).Length }, policy.CauseId);
                return discharged.Length > 0;
            case "order_missile":
            case "build_turret":
                return Order(world, nation, government, policy.ActionType == "order_missile" ? PhaseContracts.Missile : PhaseContracts.Turret, policy.CauseId);
            case "launch_missile":
                var targetId = parameters["country_id"]!.GetValue<string>();
                if (nation.Missiles == 0 || !Relation(world, nation.Id, targetId).AtWar)
                    return false;
                nation.Missiles--;
                Strike(world, nation, world.Society!.Nations[targetId], policy.Id, policy.CauseId);
                return true;
            case "send_gift":
                return Gift(world, nation, world.Society!.Nations[parameters["country_id"]!.GetValue<string>()],
                    parameters["kind"]!.GetValue<string>(), parameters["amount"]!.GetValue<int>(), policy.CauseId);
            default:
                return false;
        }
    }

    // Phase 2's construction firm builds missiles and turrets like any project; built alone, the country's
    // tools workshop is paid the full cost upfront and the project finishes after its build ticks.
    // ponytail: built alone, no materials are used; phase 2's path buys them.
    private static bool Order(World world, Nation nation, Agent government, string type, string cause)
    {
        var cost = PhaseContracts.CostOf(type, 1);
        if (government.CashMinor < cost || type == PhaseContracts.Turret && nation.Infrastructure.Any(project => project.Type == type))
            return false;
        var project = new Project
        {
            Id = $"project_{world.NextEntityNumber()}", Type = type, CostMinor = cost,
            StartedTick = world.Tick, ReadyTick = world.Tick + PhaseContracts.Spec(type).Ticks
        };
        if (!HasBuilders)
        {
            if (SocietyLabor.Firms(world, nation.Id, "tools").OrderBy(firm => firm.Id).FirstOrDefault() is not { } workshop)
                return false;
            SocietyEconomy.Sell(world, government, workshop, cost, kind: "military_procurement");
            project.PaidMinor = cost;
        }
        nation.Infrastructure.Add(project);
        world.Emit("infrastructure_started", "rule", [nation.Id, project.Id], new { type, project.CostMinor, project.ReadyTick }, cause);
        return true;
    }

    // A missile hits unless the target's turret stops it; a hit halves one completed project's condition, picked
    // at random weighted by build cost.
    public static void Strike(World world, Nation attacker, Nation target, string key, string cause)
    {
        var hit = SocietyFactory.Sample(world.Seed, key, "missile_hit") >= PhaseContracts.Boost(target, "missile_defense");
        Project? damaged = null;
        var projects = target.Infrastructure.Where(project => project.Complete && project.Type != PhaseContracts.Missile).ToArray();
        if (hit && projects.Length > 0)
        {
            var pick = SocietyFactory.Sample(world.Seed, key, "missile_target") * projects.Sum(project => project.CostMinor);
            damaged = projects.FirstOrDefault(project => (pick -= project.CostMinor) < 0) ?? projects[^1];
            damaged.Condition = Math.Max(0, damaged.Condition - 50);
        }
        world.Emit("missile_strike", "rule", [attacker.Id, target.Id], new
        {
            attacker = attacker.Id, target = target.Id, hit, damaged = damaged?.Type, condition = damaged?.Condition
        }, cause);
    }

    // Relations gain scales with the gift's value against the recipient's weekly spending, capped per gift.
    private static bool Gift(World world, Nation donor, Nation recipient, string kind, int amount, string cause)
    {
        var giver = world.Agents[donor.Id + "_government"];
        var receiver = world.Agents[recipient.Id + "_government"];
        long value;
        if (kind == "cash")
        {
            if (amount <= 0 || giver.CashMinor < amount)
                return false;
            SocietyEconomy.Transfer(world, giver, receiver, amount, "gift");
            value = amount;
        }
        else
        {
            if (!PhaseContracts.RawMaterials.Contains(kind) || !SocietyFactory.Goods.Contains(kind) || donor.Stocks.GetValueOrDefault(kind) < amount)
                return false;
            var units = MoveGoods(world, donor, recipient, kind, amount / (double)Math.Max(1, donor.Stocks[kind]));
            value = (long)units * SocietyFactory.BasePrice(SocietyLabor.Sectors.First(sector => sector.Good == kind).Id);
        }
        var relation = Relation(world, donor.Id, recipient.Id);
        var gain = (int)Math.Min(GiftGainCap, Math.Round(GiftGainCap * value / (double)Math.Max(1, receiver.LastWeekOutMinor)));
        relation.Score = Math.Min(100, relation.Score + gain);
        world.Emit("gift_sent", "rule", [donor.Id, recipient.Id], new { kind, amount, value_minor = value, relation_gain = gain, relation.Score }, cause);
        return true;
    }

    public static object View(World world, Nation nation)
    {
        var soldiers = Soldiers(world, nation);
        var strength = Strength(world, nation);
        var residents = world.InCountry(nation.Id).Where(SocietyLabor.IsResident).ToArray();
        return new
        {
            strength, soldiers = soldiers.Length, soldiers_fit = soldiers.Count(Fit), soldiers_paid = soldiers.Count(Paid),
            soldiers_to_keep_order = OrderLine(world, nation),
            nation.Missiles,
            missiles_on_order = nation.Infrastructure.Count(project => project.Type == PhaseContracts.Missile && !project.Complete),
            missile_defense_pct = (int)Math.Round(100 * PhaseContracts.Boost(nation, "missile_defense")),
            readiness_for_war = Readiness[nation.GovernmentId], war_weariness = nation.WarWeariness,
            wars = RelationsOf(world, nation.Id).Where(relation => relation.AtWar).Select(relation =>
            {
                var enemy = world.Society!.Nations[Other(relation, nation.Id)];
                var theirs = Strength(world, enemy);
                return new
                {
                    country_id = enemy.Id, enemy_strength = theirs, aggressor = relation.Aggressor,
                    weekly_win_chance_pct = strength + theirs == 0 ? 0 : 100 * strength / (strength + theirs)
                };
            }).ToArray(),
            relations = RelationsOf(world, nation.Id).OrderBy(relation => Other(relation, nation.Id))
                .ToDictionary(relation => Other(relation, nation.Id), relation => relation.Score),
            mission = new
            {
                average_quality_of_life_at_least = UnrestQol,
                average_quality_of_life = residents.Length == 0 ? 0 : Math.Round(residents.Average(resident => resident.Wellbeing), 1),
                unrest_since_tick = nation.UnrestSinceTick >= 0 ? nation.UnrestSinceTick : (int?)null,
                unrest_comes_to_a_head_tick = nation.UnrestSinceTick >= 0 ? nation.UnrestSinceTick + UnrestDays : (int?)null
            },
            needs_but_cannot_buy = Blocked(world, nation).Select(item => new
            {
                good = item.Good, held_by = item.HeldBy.Select(holder => new { country_id = holder.CountryId, blocked_by = holder.Reason })
            }).ToArray()
        };
    }
}

public sealed partial class Nation
{
    // Phase 3: missiles in stock, war weeks fought, consecutive war weeks with no fit soldiers, war weariness (share of
    // residents' security lost), when average quality of life fell below the mission line, and the tax total at the
    // last weekly contest (tribute is a share of the week's taxes).
    public int Missiles { get; set; }
    public int WarWeeks { get; set; }
    public int NoFitSoldierWeeks { get; set; }
    public double WarWeariness { get; set; }
    public int UnrestSinceTick { get; set; } = -1;
    public long TaxMarkMinor { get; set; }
}
