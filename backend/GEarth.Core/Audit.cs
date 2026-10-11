using System.Text.Json.Nodes;

namespace GEarth.Core;

// Independent of the bounded UI event/decision windows. Revision is assigned by the committing store.
public sealed record AuditRecord(string RunId, long Sequence, int Tick, long Revision, string Kind, JsonObject Payload);

public sealed record RunAuditHead(string RunId, long Revision, long AuditSequence, string Status, bool AnalyticsEnabled);

public interface IRunAuditStore
{
    bool SupportsAudit { get; }
    Task<RunAuditHead?> ReadAuditHead(string runId, CancellationToken cancellationToken);
    Task<AuditRecord[]> ReadAudit(string runId, long afterSequence, int limit, CancellationToken cancellationToken);
}

public interface IAnalyticsReportStore
{
    Task<JsonObject?> ReadAnalyticsReport(string runId, CancellationToken cancellationToken);
    Task SaveAnalyticsReport(string runId, JsonObject report, CancellationToken cancellationToken);
}

public sealed record AuditContext(string? DecisionId = null, string? EventId = null, string? RoutineId = null,
    string? GroupId = null, string? Good = null, int? Quantity = null, long? UnitPriceMinor = null);

public sealed class AnalyticsTimelineException() : InvalidOperationException(
    "Analytics capture for resumed/forked timelines requires a distinct dataset identity and is not supported by audit v1; start a fresh analytics run or resume with analytics disabled.");

public static class RunAudit
{
    public const int SchemaVersion = 1;
    public const string PublicServicesContract = "gearth.public_services.v1";
    public const string EconomyCaptureContract = "gearth.economy_capture.v3";
    public const string MenuContract = "fixed_or_bounded_integer.v1";

    public static void PrepareResume(World world, bool enableAnalytics)
    {
        if (enableAnalytics)
            throw new AnalyticsTimelineException();
        if (world.PendingAudit.Count > 0)
            throw new InvalidOperationException("Resume requires a committed checkpoint without pending audit records");
        // A branch inherits its parent's audit prefix, but cannot append using the same dataset identity.
        // Keep the cursor and coverage boundary intact so exporters can identify the incomplete capture.
        world.AnalyticsEnabled = false;
    }

    public static void Enable(World world, object? metadata = null)
    {
        if (world.AnalyticsEnabled)
            return;
        world.AnalyticsEnabled = true;
        world.AuditSchemaVersion = SchemaVersion;
        world.AuditStartTick = world.Tick;
        world.AuditStartedAt = DateTimeOffset.UtcNow;
        var configuration = Wire.Node(metadata ?? new
        {
            world.Seed, world.Mode, initial_population = world.Agents.Count,
            capture_started_at = world.AuditStartedAt, capture_start_tick = world.Tick,
            schema_version = SchemaVersion, engine_commit = (string?)null
        }).AsObject();
        configuration["capture_contract_version"] = EconomyCaptureContract;
        configuration["features"] = Wire.Node(new
        {
            public_services = world.Society is null ? null : "v1",
            bounded_integer_choices = "v1",
            banking = world.Society is null ? null : SocietyBank.AccountingVersion,
            resources = world.Society is null ? null : "phase1_v1",
            labor_distribution = world.Society is null ? null : "v1",
            infrastructure = world.Society is null ? null : "phase2_v1",
            part_time_school = world.Society is null ? null : "v1",
            wage_arrears = world.Society is null ? null : "v1",
            price_response = world.Society is null ? null : "v1",
            currency_trade = world.Society is null ? null : "v1",
            war_diplomacy = world.Society is null ? null : "phase3_v1"
        });
        configuration["active_features"] = Wire.Node(world.Society is null ? new[] { "bounded_integer_choices" }
            : new[] { "public_services", "school", "planner_quotas", "bounded_integer_choices", "deposit_creation", "resources", "labor_distribution",
                "infrastructure", "part_time_school", "wage_arrears", "price_response", "currency_trade", "war_diplomacy" });
        configuration["banking_paused"] = world.Society is null ? null : JsonValue.Create(SocietyBank.Paused);
        configuration["economy_configuration"] = world.Society is null ? null : Wire.Node(EconomyConfiguration());
        Append(world, "run_metadata", configuration);
        foreach (var item in world.Events)
            Append(world, "event", item);
        foreach (var agent in world.Agents.Values)
            Append(world, "opening_balance", new { account_id = agent.Id, account_type = "agent",
                balance_minor = agent.CashMinor, agent = AgentState(world, agent) });
        if (world.Society is { } society)
            foreach (var cargo in society.Cargo)
                Append(world, "opening_balance", new { account_id = cargo.Id, account_type = "escrow",
                    balance_minor = cargo.EscrowMinor });
        world.AuditOpeningBalancesCaptured = true;
    }

    public static void Append(World world, string kind, object payload)
    {
        if (!world.AnalyticsEnabled)
            return;
        world.PendingAudit.Add(new AuditRecord(world.Id, ++world.AuditSequence, world.Tick, 0, kind,
            Wire.Node(payload).AsObject()));
    }

    // Called immediately after insertion, before the new account receives its startup funding.
    // Kept separate from initial accounts so the initial-population denominator remains unchanged.
    public static void Created(World world, Agent agent, string source)
    {
        if (!world.AnalyticsEnabled)
            return;
        var context = world.CurrentAuditContext;
        Append(world, "opening_balance", new
        {
            account_id = agent.Id, account_type = "created_agent", balance_minor = agent.CashMinor,
            agent = AgentState(world, agent), creation_tick = world.Tick,
            creation_basis = "engine_agent_added_before_funding", creation_source = source,
            decision_id = context?.DecisionId, event_id = context?.EventId, routine_id = context?.RoutineId
        });
    }

    private static object EconomyConfiguration() => new
    {
        school = new { SocietyLabor.CourseDays, SocietyLabor.PartTimeCourseDays,
            part_time_work_fraction = 0.5, part_time_pay_fraction = 0.5,
            SocietyLabor.GraduateOutputBoost, SocietyLabor.GraduatePayPremiumPct,
            students_per_teacher = SocietyLabor.SectorOf("school").DailyOutput,
            seeking_better_job_days = SocietyLabor.CourseDays },
        health = new { SocietyEconomy.VisitHealthGain, SocietyEconomy.MedicineHealthGain,
            SocietyEconomy.HealthDecayDays, SocietyEconomy.SickBelow, SocietyEconomy.DailyIllnessChance,
            SocietyEconomy.HungerOutputFactor },
        quality_of_life = new { SocietyEconomy.NeedsWeight, SocietyEconomy.HealthBonus,
            SocietyEconomy.SickPenalty, SocietyEconomy.ComfortWeight, SocietyEconomy.SecurityWeight,
            SocietyEconomy.LeisureWeight, SocietyEconomy.ComfortUnitsAtFull, SocietyEconomy.SecurityWeeksOfPay,
            SocietyEconomy.SavingsHorizonWeeks, parts_basis = "snapshot_state_recomputed",
            wellbeing_basis = "engine_smoothed_score", adjustment_divisor = 4 },
        planning = new { SocietyPlanning.PlanningWeeks, SocietyPlanning.PlannedSectors },
        labor = new { SocietyLabor.DaysPerWeek, SocietyLabor.ArrearsStrikeWeeks, SocietyLabor.OwnerGivesUpWeeks,
            SocietyLabor.MaxPriceStepPct, SocietyLabor.IndexationDays, SocietyLabor.IndexationShare },
        infrastructure = new
        {
            SocietyInfrastructure.AdvanceShare, upkeep_cost_fraction_per_week = 0.01,
            unfunded_condition_loss_per_week = 2,
            progress_basis = "engine_remaining_schedule_ticks",
            catalog = PhaseContracts.Catalog.Select(spec => new { spec.Type, spec.Builders, spec.Ticks, spec.CostMinor, spec.MaxLevel, spec.Effects,
                    levels = Enumerable.Range(1, spec.MaxLevel).Select(level => new
                    { level, cost_minor = PhaseContracts.CostOf(spec.Type, level), materials = PhaseContracts.Materials(spec.Type, level) }) })
        },
        currency_trade = new { SocietyTrade.CurrencyStep, SocietyTrade.CurrencyFloor, SocietyTrade.CurrencyCeiling,
            balance_basis = "world_value_orders_current_week_exports_positive",
            planned_economy_rate = "fixed_official_index_1", rate_basis = "single_money_treasury_surcharge_rebate" },
        war_diplomacy = new
        {
            SocietyWar.WarBelow, SocietyWar.EmbargoBelow, SocietyWar.GiftGainCap, SocietyWar.SoldierStep,
            SocietyWar.TributeShare, SocietyWar.CessionShare, SocietyWar.ReparationsShare, SocietyWar.WoundedShare,
            SocietyWar.SurrenderWeeks, SocietyWar.WearinessStep, SocietyWar.MaxWeariness, SocietyWar.WearySupportLoss,
            SocietyWar.UnrestQol, SocietyWar.UnrestDays, SocietyWar.RevolutionSoldierShare, SocietyWar.SoldiersPerMissile,
            SocietyWar.Readiness, peace_terms = SocietyWar.PeaceTerms,
            relationship_score_basis = "minus_100_to_100; negative_is_hostile",
            military_paid_basis = "positive_military_pay_within_7_days_no_arrears_and_positive_work_effort",
            planned_to_market_ownership_transition = "deferred"
        },
        banking = new { SocietyBank.Paused, SocietyBank.ReserveRatioPct, SocietyBank.CapitalRatioPct, SocietyBank.AccountingVersion, SocietyBank.CashBufferWeeks,
            automatic_topups = "best_effort_proportional_spare_reserves_no_insurance",
            foreign_goods_settlement = "cash_escrow_domestic_fees_deposits",
            state_continuation = "nominal_domestic_claims_no_recapitalization_interest_suspended_at_nonpositive_equity",
            SocietyBank.LoanWeeks, SocietyBank.LoansPerBanker, SocietyBank.MissedPaymentsToDefault,
            SocietyBank.WeeksPerYear, SocietyBank.DepositRates, SocietyBank.LoanRates,
            lending_standards = SocietyBank.Standards.ToDictionary(pair => pair.Key, pair => new
                { pair.Value.DownPaymentPct, pair.Value.RevenueSharePct }) },
        accounting = new
        {
            conserved_money = "base_cash_including_bank_reserves_plus_cargo_escrow",
            deposit_minor = "bank_liability_created_by_cash_deposits_and_credit; broad_money_is_not_conserved",
            in_kind_minor = "wage_deduction_owed_not_money_transfer",
            bank_capture_scope = "cash_transactions_and_bank_ledger_claim_changes; daily_balance_sheets",
            operating_result = "operating_cash_surplus_input_purchases_expensed_when_paid",
            wage_review = "up_to_four_completed_actual_payroll_weeks_effective_following_week",
            closed_firms = "fixed_country_estates_existing_guaranteed_credit_ids",
            state_working_capital = "payroll_plus_feasible_input_purchase_forecast"
        }
    };

    public static object AgentState(World world, Agent agent) => new
    {
        id = agent.Id, agent_id = agent.Id, agent.Role, country = agent.CountryId, country_id = agent.CountryId,
        location_id = agent.LocationId, agent.Occupation, employer_id = agent.EmployerId,
        owner_id = agent.OwnerId, owned_business_id = agent.OwnedBusinessId,
        decision_maker = SocietyLabor.DecidesForItself(agent), agent.CashMinor, agent.Health, agent.Wellbeing,
        agent.Inventory, agent.Specialty, agent.PriceMinor, agent.WageOfferMinor, agent.TargetHeadcount,
        closed = agent.Role == "defunct", agent.SalaryMinor, agent.UnemployedSinceTick, agent.FoundedTick,
        agent.HealthThreshold, agent.TargetStock, agent.WorkEffort, agent.SupportsLeadership,
        agent.OvertimeOffered, agent.ModelDecisions, agent.LastDecisionTick, agent.LastAttemptTick,
        agent.FormerOccupation, agent.InvestedMinor, agent.DistressWeeks,
        agent.LastWeekUnitsSold, agent.LastWeekTurnedAway, agent.OvertimeDays,
        agent.GoodsBoughtLastWeek, agent.ServicesBoughtLastWeek, agent.LastWeekWellbeing,
        agent.SickDays, agent.Hungry, agent.NeedsMet, agent.HungryThisWeek, agent.PricedOutThisWeek, agent.InKindMinor,
        agent.StudentUntilTick, agent.SchoolId, agent.Graduated, agent.SeekingUntilTick,
        agent.PartTimeStudent, agent.ArrearsMinor, agent.ArrearsWeeks, agent.UnpaidHungryWeeks, agent.InsuredMinor,
        agent.PriceChangeTick, agent.PriceChangePct, agent.UnitsBeforePriceChange,
        agent.ProfitBeforePriceChangeMinor, agent.PriceBeforePriceChangeMinor,
        is_student = SocietyLabor.IsStudent(agent), seeking_better_job = agent.SeekingUntilTick >= world.Tick,
        too_sick = SocietyLabor.IsResident(agent) ? (bool?)SocietyEconomy.TooSick(agent) : null,
        works_overtime = SocietyLabor.IsResident(agent) ? (bool?)SocietyLabor.WorksOvertime(world, agent) : null,
        qol_parts_points = SocietyLabor.IsResident(agent)
            ? SocietyEconomy.QolParts(agent, agent.NeedsMet, SocietyLabor.WorksOvertime(world, agent)) : null,
        qol_target = SocietyLabor.IsResident(agent)
            ? (int?)SocietyEconomy.QualityOfLife(agent, agent.NeedsMet, SocietyLabor.WorksOvertime(world, agent)) : null,
        school_seats = agent.Role == "business" && agent.Specialty == "school" ? (int?)SocietyLabor.Seats(world, agent) : null,
        enrolled_students = agent.Role == "business" && agent.Specialty == "school" ? (int?)SocietyLabor.Students(world, agent).Length : null,
        planned_daily_quota = agent.Role == "business" && world.Society is { } society
            ? SocietyPlanning.Quota(world, society.Nations[agent.CountryId], agent) : null,
        agent.DepositMinor, agent.KeepsSavingsInBank, agent.DepositRatePct, agent.LoanRatePct,
        agent.LendingStandard, agent.UnmetWithdrawalsMinor,
        agent.WeekInMinor, agent.WeekOutMinor, agent.LastWeekInMinor, agent.LastWeekOutMinor,
        agent.WageIndexReference, agent.WeekDisposableIncomeMinor, agent.WeekWorkDays,
        agent.LastWeekDisposableIncomeMinor, agent.LastWeekRealIncomeMinor,
        agent.LastWeekWorkDays, agent.LastWeekIncomeTick,
        agent.LastWeekIncomeConstrained, agent.LastWeekOperatingProfitMinor,
        last_week_operating_cash_surplus_minor = agent.LastWeekOperatingProfitMinor,
        operating_cash_surplus_average_minor = agent.OperatingSurplusWeeksMinor.Count == 0 ? (long?)null
            : (long)agent.OperatingSurplusWeeksMinor.Average(),
        operating_cash_surplus_observed_weeks = agent.OperatingSurplusWeeksMinor.Count,
        agent.OperatingSurplusWeeksMinor, agent.EstateBeneficiaryId, agent.EstateCreditIds, agent.EstateFinalized,
        agent.DepositInterestSuspended, agent.EstateUnreportedPayoutMinor,
        agent.WarWeariness, agent.MilitaryPaidTick, agent.MilitaryPayMinor,
        net_wealth_minor = SocietyLabor.IsResident(agent) ? (long?)SocietyMetrics.NetWealth(world, agent) : null
    };

    private static object NationState(World world, Nation nation) => new
    {
        nation.GovernmentId, nation.LeadershipTerm, nation.NextElectionTick,
        nation.TaxBasisPoints, nation.WelfareMinor, nation.UnemploymentBenefitMinor,
        nation.SalesTaxBasisPoints, nation.ProfitTaxBasisPoints, nation.ServiceBudgetPerResidentMinor, nation.Productivity,
        nation.FoodSubsidyPct, nation.FoodSubsidyPaidMinor, nation.StartupGrantsPaidMinor, nation.CapitationPaidMinor,
        nation.TuitionSubsidyPct, nation.StudentStipend, nation.HiringBonusMinor,
        nation.HungryResidentsLastWeek, nation.PricedOutLastWeek, nation.HungryDays, nation.WorkerDays,
        nation.OutputLostToHungerPct, nation.NeededSince, nation.Plan, nation.QolParts,
        nation.LastPolicyAction, nation.LastPolicyParams, nation.LastPolicyTick, nation.NetBeforeLastPolicyMinor,
        nation.TaxesCollectedMinor, nation.WelfarePaidMinor, nation.WagesPaidMinor, nation.TradeValueMinor,
        nation.ProducedUnits, nation.LostUnits, nation.SalesTaxCollectedMinor, nation.ProfitTaxCollectedMinor,
        nation.DoctorVisits, nation.PublicDoctorVisits, nation.DividendsPaidMinor, nation.UnemploymentPaidMinor,
        nation.BusinessesStarted, nation.BusinessesClosed, nation.DepositInsurancePaidMinor,
        nation.BankFailures, nation.DefaultedLoans,
        infrastructure = nation.Infrastructure.Select(project => ProjectState(world, project)).ToArray(),
        nation.MaintenanceFunded,
        construction_materials_used = nation.ConstructionMaterialsUsed.Order(StringComparer.Ordinal).ToArray(),
        nation.BorrowedBuilders, nation.CurrencyIndex, nation.TradeNetWeekMinor, nation.TradeGrossWeekMinor,
        nation.Endowment, nation.ResourceImports, nation.PoweredSessionsLeft, nation.ProductionInputCredits,
        nation.TaxChangedTick, nation.LivingCostAtReview, nation.CrisisUntilTick, nation.CrisisGood,
        nation.Missiles, nation.WarWeeks, nation.NoFitSoldierWeeks, nation.WarWeariness, nation.UnrestSinceTick,
        nation.TaxMarkMinor, nation.WarAccountingInitialized,
        relationships = world.Society!.Relations.Values.Where(relation => relation.First == nation.Id || relation.Second == nation.Id)
            .OrderBy(relation => relation.Id, StringComparer.Ordinal).ToArray(),
        military = MilitaryState(world, nation),
        mission = MissionState(world, nation),
        nation.EmployedResidents, nation.LaborForce, nation.Nonparticipants, nation.JoblessResidents,
        nation.GiniDisposableIncome, nation.GiniNetWealth, nation.EssentialsBasketMinor, nation.MedianRealDisposableIncomeMinor,
        labor_and_distribution = SocietyMetrics.View(nation)
    };

    private static object MilitaryState(World world, Nation nation)
    {
        var soldiers = SocietyWar.Soldiers(world, nation);
        var fit = soldiers.Count(SocietyWar.Fit);
        return new
        {
            soldiers = soldiers.Length, soldiers_fit = fit,
            soldiers_paid = soldiers.Count(soldier => SocietyWar.Paid(world, soldier)),
            strength = fit + SocietyWar.SoldiersPerMissile * nation.Missiles,
            soldiers_to_keep_order = SocietyWar.OrderLine(world, nation),
            weekly_salary_minor = soldiers.Sum(soldier => soldier.SalaryMinor),
            missile_defense_pct = (int)Math.Round(100 * PhaseContracts.Boost(nation, "missile_defense"))
        };
    }

    private static object MissionState(World world, Nation nation)
    {
        var residents = world.InCountry(nation.Id).Where(SocietyLabor.IsResident).ToArray();
        return new
        {
            average_quality_of_life_at_least = SocietyWar.UnrestQol,
            average_quality_of_life = residents.Length == 0 ? (double?)null : residents.Average(resident => resident.Wellbeing),
            unrest_since_tick = nation.UnrestSinceTick >= 0 ? nation.UnrestSinceTick : (int?)null,
            unrest_comes_to_a_head_tick = nation.UnrestSinceTick >= 0 ? nation.UnrestSinceTick + SocietyWar.UnrestDays : (int?)null
        };
    }

    // The engine uses the remaining schedule to measure work; payments are separate from progress.
    private static object ProjectState(World world, Project project)
    {
        var spec = PhaseContracts.Spec(project.Type);
        var progress = project.Complete ? spec.Ticks : Math.Clamp(spec.Ticks - (project.ReadyTick - world.Tick), 0, spec.Ticks);
        return new
        {
            project.Id, project.Type, project.Level, project.Condition, project.CostMinor, project.PaidMinor,
            project.StartedTick, project.ReadyTick, project.Complete,
            progress_days = progress, required_days = spec.Ticks, progress_fraction = (double)progress / spec.Ticks,
            progress_basis = "engine_remaining_schedule_ticks", required_builders = spec.Builders
        };
    }

    // Exactly one row per agent per simulated day. Call at the day's boundary, after late results.
    // Opening balances contain tick-zero state; final checkpoint contains terminal state.
    public static void Day(World world, bool terminal = false)
    {
        if (!world.AnalyticsEnabled || !terminal && (world.Tick == 0 || world.Tick <= world.AuditDailyThroughTick))
            return;
        foreach (var agent in world.Agents.Values)
        {
            var payload = Wire.Node(AgentState(world, agent)).AsObject();
            payload["frame_kind"] = terminal ? "end" : "day";
            Append(world, "agent_day", payload);
        }
        if (world.Society is { } society)
            foreach (var nation in society.Nations.Values)
            {
                var people = world.InCountry(nation.Id);
                var residents = people.Where(SocietyLabor.IsResident).ToArray();
                var payload = Wire.Node(new
                {
                    frame_kind = terminal ? "end" : "day",
                    nation_id = nation.Id, id = nation.Id, government = nation.GovernmentId,
                    escrow_accounts = society.Cargo.Where(cargo => world.Agents[cargo.BuyerId].CountryId == nation.Id)
                        .Select(cargo => new { account_id = cargo.Id, balance_minor = cargo.EscrowMinor }).ToArray(),
                    residents = residents.Length, nation.Unemployed, nation.Approval,
                    nation.EmployedResidents, nation.LaborForce, nation.Nonparticipants, nation.JoblessResidents,
                    nation.GiniDisposableIncome, nation.GiniNetWealth, nation.EssentialsBasketMinor, nation.MedianRealDisposableIncomeMinor,
                    students = residents.Count(SocietyLabor.IsStudent), graduates = residents.Count(agent => agent.Graduated),
                    job_seekers = residents.Count(agent => agent.SeekingUntilTick >= world.Tick),
                    hungry_residents = residents.Count(agent => agent.Hungry), too_sick_residents = residents.Count(SocietyEconomy.TooSick),
                    average_health = residents.Length == 0 ? (double?)null : residents.Average(agent => agent.Health),
                    average_wellbeing = residents.Length == 0 ? (double?)null : residents.Average(agent => agent.Wellbeing),
                    bank_deposits_minor = people.Sum(agent => agent.DepositMinor),
                    planning_view = SocietyLabor.IsPlanned(nation) ? SocietyPlanning.View(world, nation) : null,
                    government_cash_minor = world.Agents[nation.Id + "_government"].CashMinor,
                    gini = nation.GiniCash, unmet_needs = nation.UnmetNeedsLastCycle,
                    wars = society.Relations.Values.Count(relation => relation.AtWar
                        && (relation.First == nation.Id || relation.Second == nation.Id)),
                    stock = SocietyFactory.Goods.ToDictionary(good => good,
                        good => people.Sum(agent => (long)agent.Inventory.GetValueOrDefault(good))),
                    shipments = society.Cargo.Where(cargo => cargo.Status == "in_transit"
                        && world.Agents[cargo.BuyerId].CountryId == nation.Id).Select(cargo => new
                        {
                            cargo.Id, cargo.Good, cargo.Quantity, cargo.BuyerId, cargo.SellerId,
                            cargo.EscrowMinor, cargo.Status,
                            edge = cargo.Plan?.EdgeIds.ElementAtOrDefault(cargo.EdgeIndex), cargo.EdgeRemainingTicks
                        }).ToArray()
                }).AsObject();
                foreach (var field in Wire.Node(NationState(world, nation)).AsObject())
                    payload[field.Key] = field.Value?.DeepClone();
                Append(world, "nation_day", payload);
            }
        if (!terminal)
            world.AuditDailyThroughTick = world.Tick;
    }

    public static void Request(World world, DecisionJob job, DateTimeOffset dispatchedAt)
    {
        if (!world.AnalyticsEnabled)
            return;
        var agent = world.Agents[job.AgentId];
        Append(world, "decision_request", new
        {
            decision_id = job.RequestId, agent_id = job.AgentId, agent.Role, country_id = agent.CountryId,
            observation_tick = job.ObservationTick, dispatched_at = dispatchedAt,
            job, prompt_contract_version = "gearth.numbered_menu.v2", menu_parameter_contract = MenuContract,
            rules_note = job.Observation["note"]?.GetValue<string>(),
            // Worker renders this exact job; raw provider prompt/token counts are not returned to the engine.
            rendered_provider_prompt = (string?)null, token_usage = (object?)null,
            evidence_attribution = "engine supplied; not necessarily model cited"
        });
    }

    public static void Result(World world, DecisionJob job, DecisionResult result, bool accepted, string code, string eventId)
    {
        if (!world.AnalyticsEnabled)
            return;
        var selection = Selection(job, result);
        Append(world, "decision_result", new
        {
            decision_id = job.RequestId, agent_id = job.AgentId, observation_tick = job.ObservationTick,
            applied_tick = world.Tick, result, accepted, code, status = accepted ? "accepted" : "rejected",
            fallback = result.Source == "rule_fallback", selected_option_index = selection.Index,
            selected_params = result.Action?.Params, selection_match_count = selection.Count,
            selection_provenance = "matched returned action to original zero-based menu using fixed parameters and bounded integers",
            action_event_id = eventId
        });
    }

    public static void Canceled(World world, DecisionJob job, DecisionResult? result = null)
    {
        var selection = Selection(job, result);
        Append(world, "decision_result", new
        {
            decision_id = job.RequestId, agent_id = job.AgentId, observation_tick = job.ObservationTick,
            applied_tick = (int?)null, result, accepted = false, code = "HOST_SHUTDOWN",
            status = "canceled", fallback = result?.Source == "rule_fallback", selected_option_index = selection.Index,
            selected_params = result?.Action?.Params, selection_match_count = selection.Count,
            selection_provenance = "matched returned action to original zero-based menu using fixed parameters and bounded integers",
            action_event_id = (string?)null
        });
    }

    private static (int? Index, int Count) Selection(DecisionJob job, DecisionResult? result)
    {
        var matches = result?.Action?.Params is { } parameters
            ? Enumerable.Range(0, job.Candidates.Length)
                .Where(index => SocietyActions.Matches(job.Candidates[index], result.Action.ActionType, parameters)).ToArray()
            : [];
        // The provider does not return its raw choice index. Only infer one when the returned action identifies it uniquely.
        return (matches.Length == 1 ? matches[0] : null, matches.Length);
    }

    public static void Transaction(World world, string from, string to, long amount, string kind)
    {
        if (!world.AnalyticsEnabled || amount == 0)
            return;
        var context = world.CurrentAuditContext;
        Append(world, "transaction", new
        {
            transaction_id = $"{world.Id}:a{world.AuditSequence + 1}", sender_account_id = from,
            recipient_account_id = to, from, to, kind, amount_minor = amount,
            transaction_group_id = context?.GroupId,
            decision_id = context?.DecisionId, event_id = context?.EventId, routine_id = context?.RoutineId,
            good = context?.Good, quantity = context?.Quantity, unit_price_minor = context?.UnitPriceMinor,
            causal_source = context?.EventId is not null ? "engine_event" : context?.RoutineId is not null ? "routine" : "unknown"
        });
    }

    public static IDisposable? Context(World world, AuditContext context)
    {
        if (!world.AnalyticsEnabled)
            return null;
        var previous = world.CurrentAuditContext;
        world.CurrentAuditContext = context;
        return new Restore(() => world.CurrentAuditContext = previous);
    }

    public static IDisposable? Group(World world, string? good = null, int? quantity = null, long? unitPrice = null, string? id = null)
    {
        if (!world.AnalyticsEnabled)
            return null;
        return Context(world, (world.CurrentAuditContext ?? new()) with
        {
            GroupId = id ?? world.CurrentAuditContext?.GroupId ?? $"{world.Id}:g{world.AuditSequence + 1}",
            Good = good ?? world.CurrentAuditContext?.Good,
            Quantity = quantity ?? world.CurrentAuditContext?.Quantity,
            UnitPriceMinor = unitPrice ?? world.CurrentAuditContext?.UnitPriceMinor
        });
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    // Capture only potentially action-controlled entities, not the entire population per decision.
    public static IDisposable? Effects(World world, Agent agent, ActionProposal action, string? decisionId, string eventId)
    {
        if (!world.AnalyticsEnabled)
            return null;
        var ids = new HashSet<string> { agent.Id };
        if (agent.OwnedBusinessId is { } owned)
            ids.Add(owned);
        foreach (var key in new[] { "business_id", "seller_id", "recipient_id", "to_agent_id" })
            if (action.Params[key] is JsonValue value && value.TryGetValue<string>(out var id) && world.Agents.ContainsKey(id))
                ids.Add(id);
        if (action.ActionType == "set_wage" && agent.OwnedBusinessId is { } firm)
            foreach (var worker in SocietyLabor.Staff(world, world.Agents[firm]))
                ids.Add(worker.Id);
        if (action.ActionType == "enroll_school" && SocietySchool.OpenSchool(world, agent.CountryId) is { } school)
        {
            ids.Add(school.Id);
            ids.Add(agent.CountryId + "_government");
            if (agent.DepositMinor > 0)
                ids.Add(SocietyBank.BankOf(world, agent.CountryId).Id);
        }
        if (action.ActionType == "visit_doctor" && world.Society is { } services
            && SocietyEconomy.OpenClinic(world, services.Nations[agent.CountryId]) is { } clinic)
            ids.Add(clinic.Id);
        if (agent.Role is "government" or "planner" && world.Society is not null)
        {
            if (action.ActionType is "recruit_soldiers" or "discharge_soldiers")
                foreach (var resident in world.InCountry(agent.CountryId).Where(SocietyLabor.IsResident))
                    ids.Add(resident.Id);
            if (action.ActionType == "declare_conflict")
                foreach (var soldier in SocietyWar.Soldiers(world, world.Society.Nations[agent.CountryId]))
                    ids.Add(soldier.Id);
            if (action.Params["country_id"]?.GetValue<string>() is { } target
                && world.Agents.ContainsKey(target + "_government"))
                ids.Add(target + "_government");
        }
        var before = Capture();
        return new Restore(() =>
        {
            if (agent.OwnedBusinessId is { } newFirm)
                ids.Add(newFirm);
            var after = Capture();
            foreach (var (key, state) in after)
            {
                before.TryGetValue(key, out var prior);
                if (!JsonNode.DeepEquals(prior, state))
                {
                    var changed = state.AsObject().Where(field => !JsonNode.DeepEquals(prior?[field.Key], field.Value))
                        .Select(field => field.Key).ToArray();
                    var beforeFields = prior is null ? null : new JsonObject(changed.Select(field =>
                        new KeyValuePair<string, JsonNode?>(field, prior[field]?.DeepClone())));
                    var afterFields = new JsonObject(changed.Select(field =>
                        new KeyValuePair<string, JsonNode?>(field, state[field]?.DeepClone())));
                    Append(world, "action_effect", new
                    {
                        decision_id = decisionId, event_id = eventId, entity_id = key[2..],
                        entity_type = key[0] switch { 'a' => "agent", 'n' => "nation", 'r' => "route", _ => "relationship" },
                        before = beforeFields, after = afterFields, link_type = "engine_execution"
                    });
                }
            }
        });

        Dictionary<string, JsonNode> Capture()
        {
            var states = ids.ToDictionary(id => "a:" + id, id =>
            {
                var state = Wire.Node(AgentState(world, world.Agents[id])).AsObject();
                foreach (var field in new[] { "model_decisions", "last_decision_tick", "last_attempt_tick" })
                    state.Remove(field);
                return (JsonNode)state;
            });
            if (agent.Role is "government" or "planner" && world.Society is { } society)
            {
                var nation = society.Nations[agent.CountryId];
                states["n:" + nation.Id] = Wire.Node(NationState(world, nation));
                if (action.Params["edge_id"]?.GetValue<string>() is { } edge)
                    states["r:" + edge] = Wire.Node(world.Routes[edge]);
                if (action.Params["country_id"]?.GetValue<string>() is { } country)
                {
                    if (society.Nations.TryGetValue(country, out var target))
                        states["n:" + target.Id] = Wire.Node(NationState(world, target));
                }
                // Conflict declarations can change third-party scores too; retain only actual changed relations.
                foreach (var relationship in society.Relations.Values)
                    states["d:" + relationship.Id] = Wire.Node(relationship);
            }
            return states;
        }
    }
}
