namespace GEarth.Core;

public sealed record ClinicWeek(int Tick, int Capacity, long Visits, int UntreatedPatientDays, long PaidMinor);
public sealed record ClinicFundingChange(int Tick, int FromMinor, int ToMinor, ClinicWeek? Before);

public sealed partial class Nation
{
    public int ClinicCapacityThisWeek { get; set; }
    public int UntreatedPatientDaysThisWeek { get; set; }
    public ClinicWeek? ClinicLastWeek { get; set; }
    public List<ClinicFundingChange> ClinicFundingHistory { get; set; } = [];
}

// Compact, measured feedback for decisions that otherwise invite repeated generic adjustments.
public static class SocietyGovernmentDecisions
{
    public static void CloseClinicWeek(World world, Nation nation)
    {
        nation.ClinicLastWeek = new(world.Tick, nation.ClinicCapacityThisWeek,
            nation.DoctorVisits - nation.WeekStartTotals.GetValueOrDefault("visits"),
            nation.UntreatedPatientDaysThisWeek,
            nation.CapitationPaidMinor - nation.WeekStartTotals.GetValueOrDefault("capitation"));
        nation.ClinicCapacityThisWeek = nation.UntreatedPatientDaysThisWeek = 0;
    }

    public static void ChangeClinicFunding(World world, Nation nation, int level)
    {
        if (level == nation.ServiceBudgetPerResidentMinor) return;
        nation.ClinicFundingHistory.Add(new(world.Tick, nation.ServiceBudgetPerResidentMinor, level, nation.ClinicLastWeek));
        if (nation.ClinicFundingHistory.Count > 3) nation.ClinicFundingHistory.RemoveAt(0);
        nation.ServiceBudgetPerResidentMinor = level;
    }

    public static string ClinicEffect(World world, Nation nation, int residents, int level)
    {
        var last = nation.ClinicLastWeek;
        return $"Clinic funding only: {nation.ServiceBudgetPerResidentMinor} -> {level} minor per resident weekly; budget change {(long)residents * (level - nation.ServiceBudgetPerResidentMinor):+0;-0;0} minor a week. "
            + (last is null ? "No completed treatment week yet. "
                : $"Last week: {last.Visits}/{last.Capacity} visit slots used, {last.UntreatedPatientDays} patient-days below personal treatment threshold after care, {last.PaidMinor} minor actually paid. ")
            + "Funds clinic operations; extra money does not directly add doctors, medicine or health. Check clinic_funding history before reversing a recent change.";
    }

    public static object ClinicView(World world, Nation nation)
    {
        var clinics = SocietyLabor.Firms(world, nation.Id, "clinic").ToArray();
        var lastChange = nation.ClinicFundingHistory.LastOrDefault();
        return new
        {
            budget_per_resident_minor = nation.ServiceBudgetPerResidentMinor,
            last_completed_week = nation.ClinicLastWeek,
            doctors_now = clinics.Sum(clinic => SocietyLabor.Workers(world, clinic).Length),
            medicine_in_clinics_now = clinics.Sum(clinic => clinic.Inventory.GetValueOrDefault("medicine")),
            cheapest_visit_minor = clinics.Select(clinic => (int?)clinic.PriceMinor).Min(),
            changes = nation.ClinicFundingHistory,
            full_weeks_since_change = lastChange is null ? (int?)null : Math.Max(0, world.Tick / 7 - (lastChange.Tick + 6) / 7),
            guidance = "Compare paid funding, unused slots, medicine and untreated patient-days. Hold while a recent change takes effect unless access or funding has deteriorated. Before/after outcomes are descriptive, not proof the funding change caused them."
        };
    }

    public static Relationship[] Pending(World world, Nation nation) => world.Society!.Relations.Values
        .Where(r => (r.First == nation.Id || r.Second == nation.Id) && r.ProposedBy is not null
            && r.ProposedBy != nation.Id && r.ProposalExpiresTick >= world.Tick)
        .OrderBy(r => r.ProposalExpiresTick).ThenBy(r => r.Id).ToArray();

    public static string ResponseEffect(World world, Nation nation, Relationship relation, bool accept)
    {
        var delay = Governments.Get(nation.GovernmentId).PolicyDelay;
        var consequence = !accept ? "Reject: relations -5 and diplomacy counts as failed; trade and war status stay unchanged."
            : relation.ProposalKind == "peace" ? SocietyWar.PeaceEffect(world, relation.ProposedBy!, nation.Id, relation.ProposalTerms ?? "none")
            : "Accept: bilateral import tariffs fall from 5% to zero where not already exempt. Importers save and their governments lose tariff revenue; goods still require funded purchases and open routes.";
        return $"{consequence} Offer expires day {relation.ProposalExpiresTick}; policy takes {delay} days. Acceptance raises relations by up to 25. Ignoring until expiry counts as failed diplomacy.";
    }

    public static object[] PendingView(World world, Nation nation) => Pending(world, nation).Select(relation => (object)new
    {
        country_id = relation.ProposedBy, kind = relation.ProposalKind, terms = relation.ProposalTerms,
        expires_tick = relation.ProposalExpiresTick,
        latest_response_tick = relation.ProposalExpiresTick - Governments.Get(nation.GovernmentId).PolicyDelay,
        response_available_now = SocietyDiplomacy.HasAuthority(world, nation)
            && !world.Society!.Policies.Any(p => p.CountryId == nation.Id)
            && world.Tick + Governments.Get(nation.GovernmentId).PolicyDelay <= relation.ProposalExpiresTick,
        accept_effect = ResponseEffect(world, nation, relation, true),
        reject_effect = ResponseEffect(world, nation, relation, false)
    }).ToArray();
}
