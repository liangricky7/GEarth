namespace GEarth.Core;

public sealed record GovernmentProfile(string Id, string Name, int PolicyDelay, bool Elected,
    int MinimumApprovalPercent, int PublicOwnershipPercent, int DefaultTaxBasisPoints,
    int DefaultWelfareMinor, long DepositInsuranceMinor);

public static class Governments
{
    public const long FullDepositInsurance = long.MaxValue;

    // Deposit insurance per depositor: democracies insure most household savings (a capped scheme in the
    // market democracy, a blanket guarantee in the social democracy), the planned state guarantees its own
    // bank, the monarchy offers a small scheme and the dictatorship none.
    public static readonly GovernmentProfile[] Profiles =
    [
        new("market_democracy", "Market democracy", 3, true, 45, 0, 1500, 0, 50000),
        new("social_democracy", "Social democracy", 2, true, 45, 0, 3000, 100, FullDepositInsurance),
        new("planned_republic", "Single-party planned economy", 1, false, 0, 100, 2500, 150, FullDepositInsurance),
        new("dictatorship", "Personal dictatorship", 0, false, 0, 0, 1000, 0, 0),
        new("monarchy", "Absolute monarchy", 1, false, 0, 0, 2000, 50, 10000)
    ];

    public static GovernmentProfile Get(string id) => Profiles.Single(profile => profile.Id == id);
}

public sealed partial class Nation
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string GovernmentId { get; set; } = "";
    public int TaxBasisPoints { get; set; }
    public int WelfareMinor { get; set; }
    public int ServiceBudgetPerResidentMinor { get; set; } = 50;
    public int LeadershipTerm { get; set; }
    public int NextElectionTick { get; set; } = 100;
    public int Population { get; set; }
    public int UnmetNeeds { get; set; }
    public int UnmetNeedsLastCycle { get; set; }
    public int FulfilledNeedsLastCycle { get; set; }
    public long TaxesCollectedMinor { get; set; }
    public long WelfarePaidMinor { get; set; }
    public long WagesPaidMinor { get; set; }
    public long TradeValueMinor { get; set; }
    public long ProducedUnits { get; set; }
    public long LostUnits { get; set; }
    public int DefaultedLoans { get; set; }
    public long DepositInsurancePaidMinor { get; set; }
    public int BankFailures { get; set; }
    public double Approval { get; set; } = 0.75;
    public double GiniCash { get; set; }
    public double FoodMultiplier { get; set; } = 1;
    public double EnergyMultiplier { get; set; } = 1;
    public double Productivity { get; set; } = 1;
    // A week of food and energy at average shelf prices when pay was last indexed (0: not yet measured).
    public double LivingCostAtReview { get; set; }
    // Government levers added for famine, failed sectors and schooling.
    public int FoodSubsidyPct { get; set; }
    public long FoodSubsidyPaidMinor { get; set; }
    public long StartupGrantsPaidMinor { get; set; }
    public long CapitationPaidMinor { get; set; }
    public int TuitionSubsidyPct { get; set; }
    public bool StudentStipend { get; set; }
    public int HiringBonusMinor { get; set; }
    // Hunger last week: residents who missed a meal, those who missed one while food was on the shelves
    // (priced out), and the share of working days lost to it.
    public int HungryResidentsLastWeek { get; set; }
    public int PricedOutLastWeek { get; set; }
    public int HungryDays { get; set; }
    public int WorkerDays { get; set; }
    public double OutputLostToHungerPct { get; set; }
    // Essential sectors with no working firm, and since which tick (startup grants fire after two weeks).
    public Dictionary<string, int> NeededSince { get; set; } = new();
    // Planned economies: weekly output quota per sector (goods in units, services in visits or sessions).
    public Dictionary<string, int> Plan { get; set; } = new();
    // National average of each quality-of-life part (0-1), which founders read as market gaps.
    public Dictionary<string, double> QolParts { get; set; } = new();
    // The last enacted policy and the government's weekly net budget just before it, so its effect shows.
    public string? LastPolicyAction { get; set; }
    public System.Text.Json.Nodes.JsonObject? LastPolicyParams { get; set; }
    public int LastPolicyTick { get; set; } = -1;
    // Income tax changes only at a budget turn, every four weeks.
    public int TaxChangedTick { get; set; } = -SocietyActions.BudgetTurnDays;
    public long NetBeforeLastPolicyMinor { get; set; }
    public int SalesTaxBasisPoints { get; set; } = 500;
    public int ProfitTaxBasisPoints { get; set; } = 2000;
    public long SalesTaxCollectedMinor { get; set; }
    public long ProfitTaxCollectedMinor { get; set; }
    public long DoctorVisits { get; set; }
    public long PublicDoctorVisits { get; set; }
    public int UnemploymentBenefitMinor { get; set; }
    public long DividendsPaidMinor { get; set; }
    public long UnemploymentPaidMinor { get; set; }
    public int Unemployed { get; set; }
    public int EmployedResidents { get; set; }
    public int LaborForce { get; set; }
    public int Nonparticipants { get; set; }
    public int JoblessResidents { get; set; }
    public int Students { get; set; }
    public double? GiniNetWealth { get; set; }
    public double GiniDisposableIncome { get; set; }
    public long EssentialsBasketMinor { get; set; }
    public long MedianRealDisposableIncomeMinor { get; set; }
    public int BusinessesStarted { get; set; }
    public int BusinessesClosed { get; set; }
    public Dictionary<string, long> WeekStartTotals { get; set; } = new();
    public int CrisisUntilTick { get; set; }
    public string? CrisisGood { get; set; }
    public Dictionary<string, int> Stocks { get; set; } = new();
    public List<CountryMetric> History { get; set; } = [];
}

public sealed record CountryMetric(int Tick, double NeedsMet, double Approval, double GiniCash,
    long TreasuryMinor, long TradeValueMinor, long ProducedUnits);

public sealed class Relationship
{
    public string Id { get; set; } = "";
    public string First { get; set; } = "";
    public string Second { get; set; } = "";
    public int Tension { get; set; }
    public bool TradeAgreement { get; set; }
    public bool AtWar { get; set; }
    public string? ProposedBy { get; set; }
    public string? ProposalKind { get; set; }
    public int ProposalExpiresTick { get; set; }
    // When an offer between the two last expired or was refused (-1: never); conflict is offered only after that.
    public int DiplomacyFailedTick { get; set; } = -1;
    public long DeliveredUnits { get; set; }
    public long DeliveredValueMinor { get; set; }
}

public sealed class PolicyProposal
{
    public string Id { get; set; } = "";
    public string CountryId { get; set; } = "";
    public string ActionType { get; set; } = "";
    public System.Text.Json.Nodes.JsonObject Params { get; set; } = new();
    public int ProposedTick { get; set; }
    public int EffectiveTick { get; set; }
    public string CauseId { get; set; } = "";
}

public sealed class Cargo
{
    public string Id { get; set; } = "";
    public string BuyerId { get; set; } = "";
    public string SellerId { get; set; } = "";
    public string Good { get; set; } = "";
    public int Quantity { get; set; }
    public long EscrowMinor { get; set; }
    public string LocationId { get; set; } = "";
    public string DestinationId { get; set; } = "";
    public PathPlan? Plan { get; set; }
    public int EdgeIndex { get; set; }
    public int EdgeRemainingTicks { get; set; }
    public int CreatedTick { get; set; }
    public int DeadlineTick { get; set; }
    public string Status { get; set; } = "in_transit";
    public string CauseId { get; set; } = "";
}

// A bank loan: requested, then active (repaid in weekly installments of principal plus flat interest),
// then repaid or defaulted; a request can also be rejected. Startup loans name the sector to open. Kind is
// "business" (a firm, or a founder's startup), "student" (tuition, government-guaranteed), "owner_debt" (what
// a closed firm's owner still owes) or "treasury" (a planned state borrowing from its state bank). Loans to
// people and the state never close anything when payments are missed; they are collected when there is cash.
public sealed class Credit
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "business";
    public string BankId { get; set; } = "";
    public string BorrowerId { get; set; } = "";
    public long PrincipalMinor { get; set; }
    public long PrincipalLeftMinor { get; set; }
    public long WeeklyPrincipalMinor { get; set; }
    public long WeeklyInterestMinor { get; set; }
    public int RatePct { get; set; }
    public int MissedPayments { get; set; }
    public string? Sector { get; set; }
    public int LaunchPriceMinor { get; set; }
    public int RequestedTick { get; set; }
    public string RequestEventId { get; set; } = "";
    public string Status { get; set; } = "requested";
    public bool DepositCreated { get; set; }
}

public sealed record ComputeProvider(string Name, string Model, int Capacity, string DecisionGroup = "all");

public sealed class SocietyState
{
    public Dictionary<string, Nation> Nations { get; set; } = new();
    public Dictionary<string, Relationship> Relations { get; set; } = new();
    public List<PolicyProposal> Policies { get; set; } = [];
    public List<Cargo> Cargo { get; set; } = [];
    public List<Credit> Credit { get; set; } = [];
    public long CreditCreatedMinor { get; set; }
    public long CreditRetiredMinor { get; set; }
    public Dictionary<string, long> InitialGoods { get; set; } = new();
    public Dictionary<string, long> ProducedGoods { get; set; } = new();
    public Dictionary<string, long> ConsumedGoods { get; set; } = new();
    public Dictionary<string, long> LostGoods { get; set; } = new();
    public long ModelDecisionCount { get; set; }
    public long FallbackDecisionCount { get; set; }
    public long RejectedDecisionCount { get; set; }
    public long RuleDecisionCount { get; set; }
    public long DeliveredShipments { get; set; }
    public long FailedShipments { get; set; }
    public ComputeProvider[] ComputeProviders { get; set; } = [];
    public int WorkerCapacity { get; set; }
    public int PendingDecisions { get; set; }
    public int DueAgents { get; set; }
    public int OldestWaitTicks { get; set; }
    public Dictionary<string, int> PendingByRole { get; set; } = new();
    public string[] QueuedAgentIds { get; set; } = [];
    public int TickIntervalMilliseconds { get; set; } = 2000;
    public double RecentModelDecisionsPerSecond { get; set; }
    public List<double> LatenciesMs { get; set; } = [];
    public DateTimeOffset StartedAt { get; set; }
    public int GovernmentRotation { get; set; }
    public Dictionary<string, long> PeriodRevenueMinor { get; set; } = new();
    public Dictionary<string, long> PeriodCostMinor { get; set; } = new();
    public Dictionary<string, long> PeriodUnitsSold { get; set; } = new();
    public Dictionary<string, int> VisitsServedToday { get; set; } = new();
    public Dictionary<string, int> TurnedAwayThisWeek { get; set; } = new();
    public Dictionary<string, int> IncomeConstrainedThisWeek { get; set; } = new();
    public int MaxPendingObserved { get; set; }
    public DateTimeOffset? CoverageCompletedAt { get; set; }
}
