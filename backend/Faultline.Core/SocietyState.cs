namespace Faultline.Core;

public sealed record GovernmentProfile(string Id, string Name, int PolicyDelay, bool Elected,
    int MinimumApprovalPercent, int PublicOwnershipPercent, int DefaultTaxBasisPoints,
    int DefaultWelfareMinor);

public static class Governments
{
    public static readonly GovernmentProfile[] Profiles =
    [
        new("market_democracy", "Market democracy", 3, true, 45, 0, 1500, 0),
        new("social_democracy", "Social democracy", 2, true, 45, 0, 3000, 100),
        new("planned_republic", "Single-party planned economy", 1, false, 0, 100, 2500, 150),
        new("dictatorship", "Personal dictatorship", 0, false, 0, 0, 1000, 0),
        new("monarchy", "Absolute monarchy", 1, false, 0, 0, 2000, 50)
    ];

    public static GovernmentProfile Get(string id) => Profiles.Single(profile => profile.Id == id);
}

public sealed class Nation
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
    public double Approval { get; set; } = 0.75;
    public double GiniCash { get; set; }
    public double FoodMultiplier { get; set; } = 1;
    public double EnergyMultiplier { get; set; } = 1;
    public double Productivity { get; set; } = 1;
    public double EducationFactor { get; set; } = 1;
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

public sealed class Credit
{
    public string Id { get; set; } = "";
    public string BankId { get; set; } = "";
    public string BorrowerId { get; set; } = "";
    public long PrincipalMinor { get; set; }
    public long DueMinor { get; set; }
    public int DueTick { get; set; }
    public string Status { get; set; } = "requested";
}

public sealed class SocietyState
{
    public Dictionary<string, Nation> Nations { get; set; } = new();
    public Dictionary<string, Relationship> Relations { get; set; } = new();
    public List<PolicyProposal> Policies { get; set; } = [];
    public List<Cargo> Cargo { get; set; } = [];
    public List<Credit> Credit { get; set; } = [];
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
    public int WorkerCapacity { get; set; }
    public int PendingDecisions { get; set; }
    public int DueAgents { get; set; }
    public int OldestWaitTicks { get; set; }
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
    public int MaxPendingObserved { get; set; }
    public DateTimeOffset? CoverageCompletedAt { get; set; }
}
