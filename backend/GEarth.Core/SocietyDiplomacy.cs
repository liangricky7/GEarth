using System.Text.Json.Nodes;

namespace GEarth.Core;

public static class SocietyDiplomacy
{
    public static string PairId(string first, string second) =>
        string.CompareOrdinal(first, second) < 0 ? first + ":" + second : second + ":" + first;

    public static Relationship Relation(World world, string first, string second) =>
        world.Society!.Relations[PairId(first, second)];

    public static bool BorderPermits(World world, Route route)
    {
        var from = route.From.Replace("_city", "", StringComparison.Ordinal);
        var to = route.To.Replace("_city", "", StringComparison.Ordinal);
        return route.Open && world.Society!.Nations.ContainsKey(from)
            && world.Society.Nations.ContainsKey(to) && !Relation(world, from, to).AtWar;
    }

    public static bool HasAuthority(World world, Nation nation)
    {
        var profile = Governments.Get(nation.GovernmentId);
        if (profile.Elected && nation.Approval * 100 < profile.MinimumApprovalPercent)
            return false;
        if (profile.Id == "dictatorship")
            return world.Agents[nation.Id + "_bank"].CashMinor >= nation.Population * 100;
        if (profile.Id == "monarchy")
            return world.InCountry(nation.Id).Count(agent => agent.Role == "business" && agent.CashMinor > 1000) >= 2;
        return true;
    }

    public static void Propose(World world, Agent government, ActionProposal action, string cause)
    {
        var nation = world.Society!.Nations[government.CountryId];
        var profile = Governments.Get(nation.GovernmentId);
        var policy = new PolicyProposal
        {
            Id = $"policy_{world.NextEntityNumber()}", CountryId = nation.Id, ActionType = action.ActionType,
            Params = (JsonObject)action.Params.DeepClone(), ProposedTick = world.Tick,
            EffectiveTick = world.Tick + profile.PolicyDelay, CauseId = cause
        };
        world.Society.Policies.Add(policy);
        world.Emit("policy_proposed", "rule", [nation.Id, policy.Id], new
        {
            action = action.ActionType, policy.EffectiveTick, profile.PolicyDelay,
            institution = profile.Name
        }, cause);
        if (profile.PolicyDelay == 0)
            ProcessPolicies(world);
    }

    public static void ProcessPolicies(World world)
    {
        foreach (var policy in world.Society!.Policies.Where(policy => policy.EffectiveTick <= world.Tick).ToArray())
        {
            var nation = world.Society.Nations[policy.CountryId];
            var government = world.Agents[nation.Id + "_government"];
            var netBefore = government.LastWeekInMinor - government.LastWeekOutMinor;
            using var auditContext = RunAudit.Context(world, new(EventId: policy.CauseId));
            using var effects = RunAudit.Effects(world, government,
                new ActionProposal(policy.ActionType, policy.Params, "policy enactment", []), null, policy.CauseId);
            var accepted = HasAuthority(world, nation) && Enact(world, nation, policy);
            if (accepted)
                (nation.LastPolicyAction, nation.LastPolicyParams, nation.LastPolicyTick, nation.NetBeforeLastPolicyMinor) =
                    (policy.ActionType, (JsonObject)policy.Params.DeepClone(), world.Tick, netBefore);
            world.Emit(accepted ? "policy_enacted" : "policy_blocked", "rule", [nation.Id, policy.Id], new
            {
                action = policy.ActionType, policy.Params,
                response_ticks = world.Tick - policy.ProposedTick,
                reason = accepted ? "INSTITUTIONAL_APPROVAL_AND_FUNDING" : "AUTHORITY_OR_CURRENT_PRECONDITION_FAILED"
            }, policy.CauseId);
            world.Society.Policies.Remove(policy);
        }
    }

    private static bool Enact(World world, Nation nation, PolicyProposal policy)
    {
        var government = world.Agents[nation.Id + "_government"];
        var parameters = policy.Params;
        switch (policy.ActionType)
        {
            case "adjust_tax":
                nation.TaxBasisPoints = Math.Clamp(parameters["income_tax_percent"]!.GetValue<int>() * 100, 0, SocietyActions.MaxTaxBasisPoints);
                nation.TaxChangedTick = world.Tick;
                return true;
            case "subsidize_startup":
                return nation.NeededSince.ContainsKey(parameters["sector"]!.GetValue<string>())
                    && SocietyLabor.Grant(world, nation, parameters["sector"]!.GetValue<string>(), policy.CauseId) is not null;
            case "subsidize_food":
                nation.FoodSubsidyPct = parameters["percent"]!.GetValue<int>();
                return true;
            case "subsidize_tuition":
                nation.TuitionSubsidyPct = parameters["percent"]!.GetValue<int>();
                return true;
            case "student_stipend":
                nation.StudentStipend = parameters["on"]!.GetValue<bool>();
                return true;
            case "hiring_bonus":
                nation.HiringBonusMinor = parameters["amount_minor"]!.GetValue<int>();
                return true;
            case "set_welfare":
                nation.WelfareMinor = parameters["amount_minor"]!.GetValue<int>();
                return true;
            case "set_unemployment_benefit":
                nation.UnemploymentBenefitMinor = parameters["amount_minor"]!.GetValue<int>();
                return true;
            case "set_sales_tax":
                nation.SalesTaxBasisPoints = parameters["sales_tax_basis_points"]!.GetValue<int>();
                return true;
            case "fund_services":
                nation.ServiceBudgetPerResidentMinor = parameters["amount_minor"]!.GetValue<int>();
                return true;
            case "pay":
                return SocietyPayments.Pay(world, government, parameters, policy.CauseId);
            case "invest":
                var amount = parameters["amount_minor"]!.GetValue<int>();
                if (government.CashMinor < amount)
                    return false;
                var workshop = SocietyLabor.Firms(world, nation.Id, "tools").OrderBy(firm => firm.Id).FirstOrDefault();
                if (workshop is null)
                    return false;
                SocietyEconomy.Sell(world, government, workshop, amount, kind: "public_investment");
                nation.Productivity = Math.Min(2, nation.Productivity + 0.05);
                return true;
            case "set_border":
                var route = world.Routes[parameters["edge_id"]!.GetValue<string>()];
                if (route.ControllerCountryId != nation.Id)
                    return false;
                route.Open = parameters["open"]!.GetValue<bool>();
                route.ExpiresTick = route.Open ? null : world.Tick + 15;
                route.Version++;
                world.Emit("border_changed", "rule", [route.Id, nation.Id], new
                {
                    edge_id = route.Id, route.Open, route.ExpiresTick
                }, policy.CauseId);
                return true;
            case "propose_agreement":
            case "offer_peace":
                var offered = Relation(world, nation.Id, parameters["country_id"]!.GetValue<string>());
                if (offered.ProposedBy is not null)
                    return false;
                offered.ProposedBy = nation.Id;
                offered.ProposalKind = policy.ActionType == "offer_peace" ? "peace" : "trade";
                offered.ProposalExpiresTick = world.Tick + 60;
                world.Emit("diplomatic_offer", "rule", [nation.Id, offered.Id], new
                {
                    offered.ProposalKind, offered.ProposalExpiresTick, offered.ProposedBy
                }, policy.CauseId);
                return true;
            case "respond_agreement":
                var relationship = Relation(world, nation.Id, parameters["country_id"]!.GetValue<string>());
                if (relationship.ProposedBy is null || relationship.ProposedBy == nation.Id
                    || relationship.ProposalExpiresTick < world.Tick)
                    return false;
                if (parameters["accept"]!.GetValue<bool>())
                {
                    if (relationship.ProposalKind == "peace")
                        relationship.AtWar = false;
                    else if (!relationship.AtWar)
                        relationship.TradeAgreement = true;
                    relationship.Tension = Math.Max(0, relationship.Tension - 25);
                }
                else
                    relationship.Tension = Math.Min(100, relationship.Tension + 5);
                relationship.ProposedBy = null;
                relationship.ProposalKind = null;
                world.Emit("diplomatic_response", "rule", [relationship.Id], new
                {
                    accept = parameters["accept"]!.GetValue<bool>(), relationship.AtWar,
                    relationship.TradeAgreement, relationship.Tension
                }, policy.CauseId);
                return true;
            case "declare_conflict":
                var opponent = Relation(world, nation.Id, parameters["country_id"]!.GetValue<string>());
                if (government.CashMinor < 5000 || opponent.Tension < 60 || opponent.AtWar)
                    return false;
                SocietyEconomy.PaySoldiers(world, nation, government, 5000);
                opponent.AtWar = true;
                opponent.TradeAgreement = false;
                opponent.Tension = 100;
                world.Emit("conflict_started", "rule", [opponent.Id, nation.Id], new
                {
                    opponent.First, opponent.Second,
                    scope = "abstract economic conflict: borders, inventory losses and funded upkeep",
                    no_territorial_conquest = true
                }, policy.CauseId);
                return true;
            default:
                return false;
        }
    }

    public static void Advance(World world)
    {
        ProcessPolicies(world);
        foreach (var route in world.Routes.Values.Where(route => !route.Open && route.ExpiresTick <= world.Tick))
        {
            route.Open = true;
            route.ExpiresTick = null;
            route.Version++;
            world.Emit("border_changed", "rule", [route.Id], new { edge_id = route.Id, route.Open });
        }
        foreach (var relation in world.Society!.Relations.Values.Where(relation => relation.ProposedBy is not null
            && relation.ProposalExpiresTick < world.Tick))
        {
            relation.ProposedBy = null;
            relation.ProposalKind = null;
        }
        foreach (var nation in world.Society.Nations.Values)
        {
            var profile = Governments.Get(nation.GovernmentId);
            if (!profile.Elected || nation.NextElectionTick > world.Tick)
                continue;
            var replaced = nation.Approval < 0.55;
            nation.NextElectionTick += 100;
            if (replaced)
            {
                nation.LeadershipTerm++;
                var leader = world.Agents[nation.Id + "_government"];
                foreach (var trait in leader.Traits.Keys.ToArray())
                    leader.Traits[trait] = Math.Round(SocietyFactory.Sample(world.Seed,
                        leader.Id + ":term" + nation.LeadershipTerm, trait), 2);
            }
            world.Emit("election_result", "rule", [nation.Id], new
            {
                nation.Approval, replaced, nation.LeadershipTerm, nation.NextElectionTick,
                mechanism = "synthetic approval threshold, not a real-world election model"
            });
        }
        if (world.Tick == 40)
        {
            var nation = world.Society.Nations["beryl"];
            nation.CrisisGood = "food";
            nation.CrisisUntilTick = 90;
            world.Emit("resource_crisis", "scenario", [nation.Id], new
            {
                good = "food", production_multiplier = 0.35, until_tick = 90,
                reason = "synthetic drought"
            });
            var relationship = Relation(world, "dune", "haven");
            relationship.Tension = Math.Min(100, relationship.Tension + 20);
            world.Emit("diplomatic_tension", "scenario", [relationship.Id], new
            {
                relationship.Tension, reason = "synthetic resource-access dispute"
            });
        }
    }
}
