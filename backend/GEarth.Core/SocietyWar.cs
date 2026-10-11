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

    public static readonly string Note = "Relations run -100..100, rise with deliveries and gifts, and drift toward zero weekly outside war. Relations at -60 close the direct route; other open routes can still carry trade. After a refused or expired diplomatic offer, relations at -20 permit conflict: 5000 upfront and 1000 per war week paid to soldiers. Rule governments also require a blocked raw-material need, superior strength and a readiness draw; LIVE governments choose freely. Strength is fit non-striking soldiers plus 3 per missile. Each weekly contest awards the winner 10% of the loser's weekly taxes and ore/fuel stock, and wounds 25% of fit losing soldiers; normal treatment restores health. Two weeks without fit soldiers forces surrender even with missiles. Peace offers demand none, 25% of the other treasury, or 25% of one productive raw-material endowment; surrender imposes a raw-material cession. Military construction uses the state contractor, a 50% advance and reserved outstanding commitments. Missiles consume one stockpile unit to strike, removing 50 condition from a cost-weighted completed project; a funded turret blocks 30% at full condition. Ordinary funded upkeep restores damaged condition. Recruit/discharge changes two soldiers at table pay. Gifts use treasury cash or treasury-owned/purchased raw goods; gain is capped at 15 and can round to zero. War lowers security and support. Weekly checks of quality of life below 40 for 28 days call early elections in democracies or, with fewer than 5% recently paid non-striking soldiers, a revolution in autocracies. Revolutions preserve ownership compatibility; planned-to-market transitions are explicitly deferred. ";

    public static Agent[] Soldiers(World world, Nation nation) => world.InCountry(nation.Id)
        .Where(person => person.EmployerId == nation.Id + "_government" && person.Occupation == "soldier").ToArray();

    // Strikers and the wounded do not fight; unpaid soldiers (wages owed) do not defend the regime.
    public static bool Fit(Agent soldier) => soldier.WorkEffort > 0 && !SocietyEconomy.TooSick(soldier) && !SocietyLabor.OnArrearsStrike(soldier);
    public static bool Paid(World world, Agent soldier) => soldier.WorkEffort > 0 && soldier.ArrearsMinor == 0
        && soldier.MilitaryPayMinor > 0 && soldier.MilitaryPaidTick >= 0
        && soldier.MilitaryPaidTick >= world.Tick - SocietyLabor.DaysPerWeek;

    public static int Strength(World world, Nation nation) =>
        Soldiers(world, nation).Count(Fit) + SoldiersPerMissile * nation.Missiles;

    // The contest function: the side wins the week with probability own / (own + enemy); roll is uniform in 0-1.
    public static bool Wins(double roll, int own, int enemy) => own > 0 && (enemy == 0 || roll * (own + enemy) < own);

    public static double ReadinessFor(Nation nation, Nation target) => Readiness[nation.GovernmentId]
        * (Governments.Get(nation.GovernmentId).Elected && Governments.Get(target.GovernmentId).Elected ? 0.25 : 1);

    private static Relationship Relation(World world, string first, string second) => SocietyDiplomacy.Relation(world, first, second);

    private static IEnumerable<Relationship> RelationsOf(World world, string countryId) =>
        world.Society!.Relations.Values.Where(relation => relation.First == countryId || relation.Second == countryId);

    private static string Other(Relationship relation, string countryId) => relation.First == countryId ? relation.Second : relation.First;

    public static void Day(World world)
    {
        var society = world.Society!;
        foreach (var nation in society.Nations.Values)
        {
            // Old checkpoints have cumulative taxes but no war snapshot: never levy tribute on their history.
            if (!nation.WarAccountingInitialized)
            {
                nation.TaxMarkMinor = nation.TaxesCollectedMinor;
                nation.WarAccountingInitialized = true;
            }
            Arm(world, nation);
        }
        if (world.Tick % SocietyLabor.DaysPerWeek != 0)
            return;
        var wars = society.Relations.Values.Where(relation => relation.AtWar).OrderBy(relation => relation.Id).ToArray();
        foreach (var nation in society.Nations.Values.Where(nation => wars.Any(war => war.First == nation.Id || war.Second == nation.Id)))
            nation.NoFitSoldierWeeks = Soldiers(world, nation).Any(Fit) ? 0 : nation.NoFitSoldierWeeks + 1;
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

    // Completed construction is converted to consumable missile stock on the following daily war pass.
    private static void Arm(World world, Nation nation)
    {
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
        if (!PeaceTerms.Contains(terms)) throw new ArgumentException("Unsupported peace terms.", nameof(terms));
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
        foreach (var nation in new[] { winner, loser })
            if (!RelationsOf(world, nation.Id).Any(relation => relation.AtWar))
                nation.NoFitSoldierWeeks = 0;
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
        var paid = Soldiers(world, nation).Count(soldier => Paid(world, soldier));
        if (Governments.Get(nation.GovernmentId).Elected)
        {
            nation.NextElectionTick = world.Tick + 1;
            world.Emit("early_election_called", "rule", [nation.Id], new { average_quality_of_life = Math.Round(quality, 1), nation.NextElectionTick });
        }
        else if (paid < residents.Length * RevolutionSoldierShare)
        {
            // Political changes cannot silently nationalize or privatize existing firms and their creditors.
            var choices = Governments.Profiles.Where(profile => profile.Id != nation.GovernmentId
                && (profile.Id == "planned_republic") == SocietyLabor.IsPlanned(nation)).ToArray();
            if (choices.Length == 0)
            {
                world.Emit("revolution_deferred", "rule", [nation.Id], new
                {
                    reason = "OWNERSHIP_TRANSITION_UNSUPPORTED", paid_soldiers = paid,
                    residents = residents.Length, average_quality_of_life = Math.Round(quality, 1)
                });
                return;
            }
            var next = choices[(int)(SocietyFactory.Sample(world.Seed, $"{nation.Id}:{world.Tick}", "revolution") * choices.Length) % choices.Length];
            var previous = nation.GovernmentId;
            SocietyFactory.ChangeGovernment(world, nation.Id, next.Id, "revolution");
            foreach (var policy in world.Society!.Policies.Where(policy => policy.CountryId == nation.Id).ToArray())
            {
                world.Society!.Policies.Remove(policy);
                world.Emit("policy_blocked", "rule", [nation.Id, policy.Id], new
                {
                    action = policy.ActionType, reason = "LEADERSHIP_CHANGED"
                }, policy.CauseId);
            }
            world.Emit("revolution", "rule", [nation.Id], new { from = previous, to = next.Id, paid_soldiers = paid, residents = residents.Length });
        }
        else
            world.Emit("unrest_suppressed", "rule", [nation.Id], new { paid_soldiers = paid, residents = residents.Length });
    }

    public sealed record ResourceBlock(string BuyerId, string Good, int Needed, int Available, int Incoming,
        long LiquidMinor, long CashAvailableMinor, DomesticResourceOffer[] DomesticOffers, int DomesticAffordable,
        long DomesticCostMinor, ResourceHolder[] HeldBy)
    {
        public int Gap => Needed - Available - Incoming;
        public bool CannotBuyAny => DomesticAffordable == 0 && HeldBy.All(holder => holder.BlockedBy is not null);
    }
    public sealed record DomesticResourceOffer(string SellerId, int Stock, int UnitPriceMinor);
    public sealed record ResourceHolder(string CountryId, string SellerId, int Exportable, string? BlockedBy,
        double? LandedUnitCost, int Affordable, long FullGapEscrowMinor, long? FullGapFundingMinor)
    {
        public int QuoteQuantity => 1;
    }

    // Use actual raw-input consumers, their inventories, inbound cargo and executable seller quotes.
    // National aggregate stocks include other firms' private inputs and cannot establish a buyer's access.
    public static ResourceBlock[] ResourceNeeds(World world, Nation nation)
    {
        var needs = new List<ResourceBlock>();
        foreach (var buyer in world.InCountry(nation.Id).Where(agent => agent.Role == "business"))
        foreach (var input in SocietyResources.Inputs(buyer.Specialty!).Where(input => PhaseContracts.RawMaterials.Contains(input.Good)))
        {
            var spec = SocietyLabor.SectorOf(buyer.Specialty!);
            var output = (int)Math.Ceiling(SocietyLabor.Workers(world, buyer).Sum(worker =>
                SocietyEconomy.Capacity(worker) * SocietyLabor.DayEffort(world, worker)) * spec.DailyOutput
                * SocietyLabor.DaysPerWeek * SocietyEconomy.OutputMultiplier(world, nation, spec.Good!));
            output = SocietyPlanning.Cap(world, nation, buyer, output, SocietyLabor.DaysPerWeek);
            var needed = (output + input.OutputsPerInput - 1) / input.OutputsPerInput;
            var available = buyer.Inventory.GetValueOrDefault(input.Good);
            var incoming = world.Society!.Cargo.Where(cargo => cargo.BuyerId == buyer.Id && cargo.Good == input.Good
                && cargo.Status == "in_transit").Sum(cargo => cargo.Quantity);
            var gap = needed - available - incoming;
            if (gap <= 0) continue;
            var liquid = SocietyBank.Liquid(world, buyer);
            var cash = SocietyBank.CashAvailable(world, buyer);
            var domestic = SocietyEconomy.Offers(world, nation, input.Good).Where(offer => offer.Seller != buyer)
                .Select(offer => new DomesticResourceOffer(offer.Seller.Id, offer.Seller.Inventory[input.Good], offer.PriceMinor)).ToArray();
            var affordable = 0;
            long domesticCost = 0;
            foreach (var offer in domestic)
            {
                var units = (int)Math.Min(Math.Min(gap - affordable, offer.Stock),
                    (liquid - domesticCost) / Math.Max(1, offer.UnitPriceMinor));
                affordable += units;
                domesticCost += (long)units * offer.UnitPriceMinor;
            }
            var holders = world.Agents.Values.Where(seller => seller.Role == "business" && seller.CountryId != nation.Id
                    && SocietyLabor.SectorOf(seller.Specialty!).Good == input.Good
                    && SocietyTrade.ExportableStock(world, seller, input.Good) > 0)
                .OrderBy(seller => seller.Id).Select(seller =>
                {
                    var path = SocietyTrade.Path(world, seller.LocationId, buyer.LocationId, "cheapest");
                    var price = SocietyTrade.Price(world, buyer, seller, 1);
                    var fullPrice = SocietyTrade.Price(world, buyer, seller, gap);
                    string? reason = Relation(world, nation.Id, seller.CountryId).AtWar ? "war"
                        : path is null ? "closed_route"
                        : SocietyBank.CashAvailable(world, buyer) < price.AtWorld ? "cash_escrow"
                        : SocietyBank.Liquid(world, buyer) < Math.Max(price.Paid, price.AtWorld)
                            + SocietyResources.ShippingCost(world, buyer, path, 1) + SocietyTrade.Tariff(world, buyer, seller, price.Paid) ? "unaffordable"
                        : world.Society.Cargo.Count(cargo => cargo.BuyerId == buyer.Id && cargo.Good == input.Good && cargo.Status == "in_transit") >= 2
                            ? "shipment_limit" : null;
                    return new ResourceHolder(seller.CountryId, seller.Id, SocietyTrade.ExportableStock(world, seller, input.Good),
                        reason, path is null ? null : Math.Round(SocietyResources.LandedUnitCost(world, buyer, seller, path, 1), 2),
                        reason is null ? SocietyTrade.Affordable(world, buyer, seller, input.Good, gap) : 0,
                        fullPrice.AtWorld, path is null ? null : Math.Max(fullPrice.Paid, fullPrice.AtWorld)
                            + SocietyResources.ShippingCost(world, buyer, path, gap) + SocietyTrade.Tariff(world, buyer, seller, fullPrice.Paid));
                }).ToArray();
            needs.Add(new(buyer.Id, input.Good, needed, available, incoming, liquid, cash, domestic, affordable, domesticCost, holders));
        }
        return needs.ToArray();
    }

    public static (string Good, (string CountryId, string Reason)[] HeldBy)[] Blocked(World world, Nation nation) =>
        ResourceNeeds(world, nation).Where(need => need.HeldBy.Length > 0 && need.CannotBuyAny)
            .Select(need => (need.Good, need.HeldBy.Select(holder => (holder.CountryId, holder.BlockedBy!)).Distinct().ToArray())).ToArray();

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
            options.Add(SocietyActions.Make("recruit_soldiers", new { count = SoldierStep, weekly_pay_minor = pay, effect = $"Hire two residents, unemployed first, adding {SoldierStep * pay} minor units weekly salary; former job output is lost. Requires eight weeks of salary in treasury cash." }));
        if (!threatened && soldiers.Length - SoldierStep >= orderLine)
            options.Add(SocietyActions.Make("discharge_soldiers", new { count = SoldierStep, effect = $"Discharge two soldiers, reducing weekly salary by {SoldierStep * pay} minor units and strength by up to two; keeps the order floor." }));
        foreach (var type in new[] { PhaseContracts.Missile, PhaseContracts.Turret })
        {
            var spec = PhaseContracts.Spec(type);
            if (threatened && SocietyInfrastructure.CanOrder(world, nation, spec, 1))
                options.Add(SocietyActions.Make(type == PhaseContracts.Missile ? "order_missile" : "build_turret", new
                {
                    cost_minor = PhaseContracts.CostOf(type, 1), ticks = spec.Ticks,
                    effect = $"Commit {PhaseContracts.CostOf(type, 1)} minor units; half paid now, rest reserved; needs {spec.Builders} builders for {spec.Ticks} working ticks plus materials. "
                        + (type == PhaseContracts.Missile ? "Adds one missile (+3 strength) when complete." : "Blocks 30% of missiles at full condition; ordinary upkeep applies.")
                }));
        }
        if (nation.Missiles > 0)
            foreach (var enemy in atWar)
                options.Add(SocietyActions.Make("launch_missile", new { country_id = enemy,
                    effect = $"Consumes one missile (-3 strength); {100 - (int)Math.Round(100 * PhaseContracts.Boost(world.Society!.Nations[enemy], "missile_defense"))}% chance to hit. Subtracts 50 condition from one completed project, chosen by build cost; normal funded upkeep can restore it." }));
        if (RelationsOf(world, nation.Id).Where(relation => !relation.AtWar).OrderBy(relation => relation.Score).ThenBy(relation => relation.Id)
                .FirstOrDefault() is { } coldest && SocietyBank.Liquid(world, government) >= 20000)
        {
            var target = Other(coldest, nation.Id);
            var recipient = world.Agents[target + "_government"];
            options.Add(SocietyActions.Make("send_gift", new { country_id = target, kind = "cash", amount = 1000,
                effect = $"Pay 1000 minor units; relations +{Math.Min(100 - coldest.Score, GiftGain(1000, recipient))} at current weekly spending (may round to zero)." }));
            foreach (var good in PhaseContracts.RawMaterials)
                if (GiftCost(world, government, good, 10) is { } cost)
                {
                    var owned = Math.Min(10, government.Inventory.GetValueOrDefault(good));
                    var value = cost + owned * SocietyFactory.BasePrice(SocietyResources.SectorForGood(good).Id);
                    options.Add(SocietyActions.Make("send_gift", new { country_id = target, kind = good, amount = 10,
                        effect = $"Send exactly 10 {good}; buy treasury shortfall for {cost} minor units at current quotes; relations +{Math.Min(100 - coldest.Score, GiftGain(value, recipient))}. Private goods are paid for; quantities and funding are rechecked on enactment." }));
                }
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
                if (recruits.Length != SoldierStep || government.CashMinor < 8L * SoldierStep * SocietyLabor.TableWage["soldier"]) return false;
                foreach (var person in recruits)
                {
                    (person.EmployerId, person.Occupation, person.SalaryMinor) = (government.Id, "soldier", SocietyLabor.TableWage["soldier"]);
                    (person.UnemployedSinceTick, person.OvertimeDays) = (-1, 0);
                }
                world.Emit("soldiers_recruited", "rule", [nation.Id], new { recruited = recruits.Length, soldiers = Soldiers(world, nation).Length }, policy.CauseId);
                return recruits.Length > 0;
            case "discharge_soldiers":
                if (Threatened(world, nation) || Soldiers(world, nation).Length - SoldierStep < OrderLine(world, nation)) return false;
                var discharged = Soldiers(world, nation).OrderBy(soldier => soldier.Health).ThenBy(soldier => soldier.Id).Take(SoldierStep).ToArray();
                foreach (var soldier in discharged)
                    SocietyLabor.Fire(world, soldier);
                world.Emit("soldiers_discharged", "rule", [nation.Id], new { discharged = discharged.Length, soldiers = Soldiers(world, nation).Length }, policy.CauseId);
                return discharged.Length > 0;
            case "order_missile":
            case "build_turret":
                return SocietyInfrastructure.Order(world, nation,
                    policy.ActionType == "order_missile" ? PhaseContracts.Missile : PhaseContracts.Turret, 1, policy.CauseId);
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

    public static int GiftGain(long value, Agent recipient) => (int)Math.Min(GiftGainCap,
        Math.Round(GiftGainCap * value / (double)Math.Max(1, recipient.LastWeekOutMinor)));

    // Quote a full raw gift before any purchase, so a failed policy cannot leave a partial purchase behind.
    private static long? GiftCost(World world, Agent giver, string good, int amount)
    {
        var remaining = Math.Max(0, amount - giver.Inventory.GetValueOrDefault(good));
        long cost = 0;
        foreach (var offer in SocietyEconomy.Offers(world, world.Society!.Nations[giver.CountryId], good))
        {
            var units = Math.Min(remaining, offer.Seller.Inventory.GetValueOrDefault(good));
            remaining -= units;
            cost += units * offer.PriceMinor;
            if (remaining == 0) break;
        }
        return remaining == 0 && SocietyBank.Liquid(world, giver) >= cost ? cost : null;
    }

    private static bool Gift(World world, Nation donor, Nation recipient, string kind, int amount, string cause)
    {
        if (amount <= 0 || donor == recipient || Relation(world, donor.Id, recipient.Id).AtWar) return false;
        var giver = world.Agents[donor.Id + "_government"];
        var receiver = world.Agents[recipient.Id + "_government"];
        long value;
        if (kind == "cash")
        {
            if (SocietyBank.Liquid(world, giver) < amount) return false;
            SocietyEconomy.Transfer(world, giver, receiver, amount, "gift");
            value = amount;
        }
        else
        {
            if (!PhaseContracts.RawMaterials.Contains(kind) || GiftCost(world, giver, kind, amount) is not { } cost) return false;
            var owned = Math.Min(amount, giver.Inventory.GetValueOrDefault(kind));
            var purchased = SocietyEconomy.Acquire(world, giver, kind, amount - owned, household: false);
            if (purchased + owned != amount) throw new InvalidOperationException("Funded raw gift quote failed to fill.");
            var sector = SocietyResources.SectorForGood(kind).Id;
            var receivingFirm = SocietyLabor.Firms(world, recipient.Id, sector).OrderBy(firm => firm.Id).FirstOrDefault() ?? receiver;
            giver.Inventory[kind] -= amount;
            receivingFirm.Inventory[kind] = receivingFirm.Inventory.GetValueOrDefault(kind) + amount;
            value = cost + owned * SocietyFactory.BasePrice(sector);
        }
        var relation = Relation(world, donor.Id, recipient.Id);
        var gain = GiftGain(value, receiver);
        relation.Score = Math.Min(100, relation.Score + gain);
        world.Emit("gift_sent", "rule", [donor.Id, recipient.Id], new { kind, amount, value_minor = value, relation_gain = gain, relation.Score }, cause);
        return true;
    }

    public static string PeaceEffect(World world, string winner, string loser, string terms)
    {
        if (terms == "none") return "End this war with no transfer; relations and other wars remain in force.";
        if (terms == "reparations") return $"End this war; {loser} pays {winner} 25% of its treasury (currently {(long)(world.Agents[loser + "_government"].CashMinor * ReparationsShare)} minor units), recalculated on acceptance.";
        var good = terms["cede_".Length..];
        return $"End this war; {loser} transfers 25% of productive {good} endowment (currently {PhaseContracts.Endowment(world.Society!.Nations[loser], good) * CessionShare:0.###}) to {winner}, recalculated on acceptance; inventories do not move.";
    }

    public static object View(World world, Nation nation)
    {
        var soldiers = Soldiers(world, nation);
        var strength = Strength(world, nation);
        var residents = world.InCountry(nation.Id).Where(SocietyLabor.IsResident).ToArray();
        var needs = ResourceNeeds(world, nation);
        return new
        {
            strength, soldiers = soldiers.Length, soldiers_fit = soldiers.Count(Fit), soldiers_paid = soldiers.Count(soldier => Paid(world, soldier)),
            soldiers_to_keep_order = OrderLine(world, nation),
            weekly_salary_minor = soldiers.Sum(soldier => soldier.SalaryMinor),
            weekly_conflict_upkeep_minor = 1000L * RelationsOf(world, nation.Id).Count(relation => relation.AtWar),
            construction_commitments_minor = nation.Infrastructure.Where(project => !project.Complete).Sum(project => project.CostMinor - project.PaidMinor),
            tribute_tax_exposure_pct = 10 * RelationsOf(world, nation.Id).Count(relation => relation.AtWar),
            tribute_stock_exposure = "Each lost contest transfers 10% of then-current private ore/fuel inventories, rounded per holder; losses compound across simultaneous wars.",
            paid_soldier_rule = "positive military pay within 7 days, no arrears, non-striking; checked before weekly payroll",
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
            raw_material_quote_basis = "Forecast gap is next week's staffed input need less inventory and incoming cargo. Domestic affordable quantity uses cheapest offers in order. Each foreign affordable quantity is an independent alternative using the same current funds, capped at the gap and exportable stock; do not add alternatives together. Landed unit quotes use quantity one; full-gap funding includes escrow, freight, currency and tariff but does not guarantee stock or delivery within the week.",
            raw_material_needs = needs,
            needs_but_cannot_buy = needs.Where(need => need.CannotBuyAny).ToArray()
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
    public bool WarAccountingInitialized { get; set; }
}
