using System.Globalization;
using System.Text;

namespace Faultline.Core;

public sealed record RunObservation(string Category, string Text, string[] EvidenceIds);
public sealed record BehaviorCount(string Role, string Action, string Source, int Count, int Agents);
public sealed record CountryOutcome(string Id, string Government, double NeedsMet, double Approval,
    double GiniCash, long TreasuryMinor, long TradeValueMinor, int UnmetNeeds);
public sealed record DecisionExample(string AgentId, string Action, string Reason, string EventId, int Tick);
public sealed record RunNotes(string RunId, string Status, string Mode, int Seed, int Tick, long ThroughSequence,
    DateTimeOffset GeneratedAt, bool HistoryComplete, int Population, int ModelCoverage,
    long AcceptedModelDecisions, int Fallbacks, int Rejections, RunObservation[] Observations,
    BehaviorCount[] Behaviors, CountryOutcome[] Countries, DecisionExample[] Examples, string[] Limits,
    string Markdown);

public static class RunAnalysis
{
    public static RunNotes Create(World world, IEnumerable<WorldEvent> history, string? status = null)
    {
        var events = history.Where(item => item.RunId == world.Id && item.Seq <= world.Seq)
            .DistinctBy(item => item.Seq).OrderBy(item => item.Seq).ToArray();
        var complete = events.Length == world.Seq && events.Select((item, index) => item.Seq == index + 1).All(value => value);
        var accepted = events.Where(item => item.EventType == "action_accepted").ToArray();
        var model = accepted.Where(item => item.Source == "model").ToArray();
        var fallback = events.Where(item => item.Source == "rule_fallback"
            && item.EventType is "action_accepted" or "action_rejected").ToArray();
        var rejected = events.Where(item => item.EventType == "action_rejected").ToArray();
        var observations = new List<RunObservation>();
        var behaviors = accepted.Where(item => item.Source is "model" or "rule")
            .GroupBy(item => (Role: Role(world, item), Action: Action(item), item.Source))
            .Select(group => new BehaviorCount(group.Key.Role, group.Key.Action, group.Key.Source,
                group.Count(), group.Select(item => item.EntityIds[0]).Distinct().Count()))
            .OrderByDescending(item => item.Count).ToArray();
        var coverage = world.Society is null ? model.Select(item => item.EntityIds[0]).Distinct().Count()
            : world.Agents.Values.Count(agent => agent.ModelDecisions > 0);
        var modelCount = world.Society?.ModelDecisionCount ?? model.Length;
        observations.Add(new("coverage", $"{coverage}/{world.Agents.Count} agents had accepted model turns; {modelCount} model decisions were accepted. Routine economic activity followed engine rules between turns.", Evidence(model)));

        foreach (var backend in model.GroupBy(item => (
            Name: item.Payload["backend"]?.GetValue<string>() ?? "unrecorded",
            Model: item.Payload["model"]?.GetValue<string>() ?? "unknown")))
            observations.Add(new("compute", $"{backend.Key.Name} running {backend.Key.Model} supplied {backend.Count()} accepted model decisions in the retrieved history.", Evidence(backend)));

        foreach (var provider in world.Society?.ComputeProviders ?? [])
            observations.Add(new("configuration", $"{provider.Name} was configured to serve {provider.DecisionGroup} decisions with {provider.Model} and {provider.Capacity} request slots. Configured slots are not a measured throughput result.", []));

        foreach (var role in behaviors.Select(item => item.Role).Distinct())
        {
            var leader = behaviors.First(item => item.Role == role);
            var evidence = accepted.Where(item => Role(world, item) == role && Action(item) == leader.Action && item.Source == leader.Source);
            observations.Add(new("behavior", $"For {role} agents, the most frequent accepted {leader.Source} action was {leader.Action}: {leader.Count} selections by {leader.Agents} distinct agents. This counts observed choices, not inferred motives.", Evidence(evidence)));
        }
        var work = accepted.Where(item => Action(item) == "work").ToArray();
        if (work.Length > 0)
        {
            var effort = work.GroupBy(item => item.Payload["action"]!["params"]!["effort"]!.GetValue<int>())
                .OrderByDescending(group => group.Count()).Select(group => $"effort {group.Key}: {group.Count()}");
            observations.Add(new("work", $"Work choices were {string.Join(", ", effort)}. Effort 2 accepts employer-offered overtime (a quarter more hours at 1.5x pay) and costs free time and some health; it has no effect when no overtime is offered.", Evidence(work)));
        }
        var imports = events.Where(item => item.EventType == "shipment_dispatched").ToArray();
        var deliveries = events.Where(item => item.EventType == "shipment_delivered").ToArray();
        var failed = events.Where(item => item.EventType == "shipment_failed").ToArray();
        var rerouted = events.Where(item => item.EventType == "shipment_rerouted").ToArray();
        if (imports.Length > 0)
        {
            var goods = imports.GroupBy(item => item.Payload["good"]!.GetValue<string>())
                .Select(group => $"{group.Key}: {group.Sum(item => item.Payload["quantity"]!.GetValue<int>())} units");
            observations.Add(new("trade", $"{imports.Length} cross-border shipments were dispatched ({string.Join(", ", goods)}); {deliveries.Length} arrived, {failed.Length} failed, and {rerouted.Length} reroute events occurred. Settlement used internal escrow.", Evidence(imports.Concat(deliveries).Concat(failed))));
        }
        else
            observations.Add(new("trade", "No physical cross-border shipment dispatches were recorded in this history.", []));
        var migrations = events.Where(item => item.EventType == "agent_migrated").ToArray();
        if (migrations.Length > 0)
        {
            var route = migrations.GroupBy(item => $"{item.Payload["from"]!.GetValue<string>()} → {item.Payload["to"]!.GetValue<string>()}")
                .OrderByDescending(group => group.Count()).First();
            observations.Add(new("migration", $"{migrations.Length} relocations were recorded. The most frequent direction was {route.Key} ({route.Count()} moves). This is immediate simulated relocation, not passenger travel.", Evidence(route)));
        }
        AddEventCount(observations, events, "policy", "policy_enacted", "Government policies were enacted");
        AddEventCount(observations, events, "policy", "policy_blocked", "Policy proposals were blocked by authority or current preconditions");
        AddEventCount(observations, events, "election", "election_result", "Synthetic approval-based elections occurred");
        foreach (var crisis in events.Where(item => item.EventType == "resource_crisis"))
            observations.Add(new("crisis", $"A scripted {crisis.Payload["good"]!.GetValue<string>()} production shock affected {crisis.EntityIds[0]} at tick {crisis.Tick}, configured through tick {crisis.Payload["until_tick"]}. This was a scenario event.", [crisis.EventId]));
        var offers = events.Count(item => item.EventType == "diplomatic_offer");
        var responses = events.Where(item => item.EventType == "diplomatic_response").ToArray();
        var conflicts = events.Where(item => item.EventType == "conflict_started").ToArray();
        observations.Add(new("diplomacy", $"{offers} diplomatic offers, {responses.Length} responses and {conflicts.Length} conflict declarations were recorded. An offered agreement is not a completed agreement.", Evidence(responses.Concat(conflicts))));
        var requested = events.Count(item => item.EventType == "loan_requested");
        var funded = events.Where(item => item.EventType == "loan_funded").ToArray();
        var settled = events.Where(item => item.EventType == "loan_settled").ToArray();
        if (requested > 0 || funded.Length > 0)
        {
            var defaults = settled.Count(item => item.Payload["status"]!.GetValue<string>() == "defaulted");
            var losses = settled.Sum(item => item.Payload["loss_minor"]!.GetValue<long>());
            observations.Add(new("credit", $"{requested} loans were requested, {funded.Length} funded and {settled.Length} settled; {defaults} settled in default with {Money(losses)} WLD unpaid. Unmatured loans are not counted as defaults.", Evidence(funded.Concat(settled))));
        }
        if (world.Society is { } banking)
        {
            var deposits = world.Agents.Values.Sum(agent => agent.DepositMinor);
            var withdrawals = events.Where(item => item.EventType == "savings_withdrawn").ToArray();
            var failures = events.Where(item => item.EventType == "bank_failed").ToArray();
            var insured = banking.Nations.Values.Sum(nation => nation.DepositInsurancePaidMinor);
            observations.Add(new("banking", $"Deposits stood at {Money(deposits)} WLD at the last tick. residents chose to take their savings out of the bank {withdrawals.Length} times; deposit insurance paid {Money(insured)} WLD; {failures.Length} banks failed and were taken over by the state.", Evidence(withdrawals.Concat(failures))));
        }
        var payments = events.Where(item => item.EventType == "payment_sent").ToArray();
        var purchases = events.Where(item => item.EventType == "goods_purchased").ToArray();
        if (payments.Length > 0 || purchases.Length > 0)
        {
            var flows = payments.GroupBy(item => $"{item.Payload["from_role"]} to {item.Payload["to_role"]}")
                .OrderByDescending(group => group.Count()).Take(4)
                .Select(group => $"{group.Key}: {group.Count()}");
            observations.Add(new("payments", $"{payments.Length} chosen payments moved {Money(payments.Sum(item => item.Payload["amount_minor"]!.GetValue<long>()))} WLD ({string.Join(", ", flows)}); {purchases.Length} chosen household purchases spent {Money(purchases.Sum(item => item.Payload["total_minor"]!.GetValue<long>()))} WLD. Routine wages, taxes and restocking are not included.", Evidence(payments.Concat(purchases))));
        }
        var started = events.Where(item => item.EventType == "business_started").ToArray();
        var closed = events.Where(item => item.EventType == "business_closed").ToArray();
        if (world.Society is { } labor)
        {
            var residents = world.Agents.Values.Count(agent => agent.Role is "citizen" or "entrepreneur");
            var unemployed = labor.Nations.Values.Sum(nation => nation.Unemployed);
            observations.Add(new("labor", $"{started.Length} businesses were started ({started.Count(item => item.CauseIds.Length > 0)} by agent decision, the rest by the empty-sector rule) and {closed.Length} closed; {unemployed} of {residents} residents ({(residents == 0 ? 0 : unemployed / (double)residents):P0}) were unemployed at the last weekly review. Wages follow employer offers; entrepreneurs are paid from profit and receive business cash above 8 weeks of payroll as dividends.", Evidence(started.Concat(closed))));
        }
        if (world.MedicineRoutePlan is { } plan)
            observations.Add(new("routing", $"The final medicine route plan required {plan.TravelTicks} travel ticks and {Money(plan.CostMinor)} WLD. A route plan is not a completed delivery.", Evidence(accepted.Where(item => Action(item) == "choose_route"))));
        if (fallback.Length > 0 || rejected.Length > 0)
        {
            var codes = rejected.GroupBy(item => item.Payload["code"]!.GetValue<string>())
                .Select(group => $"{group.Key}: {group.Count()}");
            observations.Add(new("reliability", $"{fallback.Length} inference fallback results and {rejected.Length} rejected decisions were recorded{(rejected.Length > 0 ? $" ({string.Join(", ", codes)})" : "")}. They do not count toward accepted model coverage.", Evidence(rejected.Concat(fallback))));
        }
        var countries = world.Society?.Nations.Values.Select(nation => new CountryOutcome(nation.Id,
            Governments.Get(nation.GovernmentId).Name, nation.History.LastOrDefault()?.NeedsMet ?? 1,
            nation.Approval, nation.GiniCash, world.Agents[nation.Id + "_government"].CashMinor,
            nation.TradeValueMinor, nation.UnmetNeeds)).ToArray() ?? [];
        foreach (var country in countries.OrderBy(item => item.NeedsMet).Take(3))
            observations.Add(new("outcome", $"{country.Id} ({country.Government}) met {country.NeedsMet:P0} of needs in its latest economic cycle; cumulative unmet needs were {country.UnmetNeeds}, support {country.Approval:P0}, and cash Gini {country.GiniCash:F3}.", Evidence(events.Where(item => item.EventType == "economy_cycle" && item.EntityIds.Contains(country.Id)).TakeLast(1))));
        var cash = world.Agents.Values.Sum(agent => agent.CashMinor) + (world.Society?.Cargo.Sum(cargo => cargo.EscrowMinor) ?? 0);
        observations.Add(new("accounting", $"Cash plus goods escrow at this checkpoint was {Money(cash)} WLD against {Money(world.InitialCashMinor)} WLD initially; {(cash == world.InitialCashMinor ? "the totals reconcile" : "the totals do not reconcile")}. This is a checkpoint comparison, not a causal economic conclusion.", []));
        var initialized = events.FirstOrDefault(item => item.EventType == "world_initialized");
        if (initialized?.Payload["agent_weights"]?.GetValue<string>() == AgentEndowments.Version)
        {
            var residents = world.Agents.Values.Where(SocietyLabor.IsResident).ToArray();
            observations.Add(new("inequality", $"Seeded {AgentEndowments.Version} profiles varied starting wealth, productivity, bargaining power and personal priorities. Current resident cash ranged from {Money(residents.Min(agent => agent.CashMinor))} to {Money(residents.Max(agent => agent.CashMinor))} WLD. Starting cash was redistributed within a fixed country budget; productivity weights scaled goods output, and salary agreements remained fixed. Bargaining weights were retained as profile metadata. Equal voting and scheduler access were preserved.", [initialized.EventId]));
            foreach (var group in residents.GroupBy(agent => agent.Weights.Wealth < 0.75 ? "lower initial wealth"
                : agent.Weights.Wealth > 1.5 ? "higher initial wealth" : "middle initial wealth"))
                observations.Add(new("inequality", $"The {group.Key} cohort contained {group.Count()} residents: current mean cash {Money((long)group.Average(agent => agent.CashMinor))} WLD, wellbeing {group.Average(agent => agent.Wellbeing):F1}/100, health {group.Average(agent => agent.Health):F1}/100. These descriptive cohorts retain initial profiles after migration and do not establish causal effects.", [initialized.EventId]));
        }
        var examples = model.Where(item => Action(item) != "hold").GroupBy(item => Role(world, item)).Take(5)
            .Select(group => group.First()).Select(item => new DecisionExample(item.EntityIds[0], Action(item),
                item.Payload["action"]!["reason"]!.GetValue<string>(), item.EventId, item.Tick)).ToArray();
        string[] limits = [
            "These are descriptive observations from one configured fictional run, not causal conclusions about real governments.",
            "Government profiles share an economic engine; geography, resources, personality, timing and migration also affect outcomes.",
            "Model explanations are attributed statements, not proof of private motives or factual accuracy.",
            "Fallbacks and rule choices are separated from accepted model behavior. Wall duration may include host or network interruptions.",
            "When multiple models supply decisions, model assignment can affect behavior and is an additional comparison confound.",
            complete ? "Complete ordered event history was used through the stated sequence." : "Event history is incomplete; behavior counts describe only the retrieved events."
        ];
        var notes = new RunNotes(world.Id, status ?? world.Status, world.Mode, world.Seed, world.Tick, world.Seq,
            DateTimeOffset.UtcNow, complete, world.Agents.Count, coverage, modelCount, fallback.Length,
            rejected.Length, observations.ToArray(), behaviors, countries, examples, limits, "");
        return notes with { Markdown = Render(notes) };
    }

    private static void AddEventCount(List<RunObservation> notes, WorldEvent[] events, string category, string kind, string label)
    {
        var selected = events.Where(item => item.EventType == kind).ToArray();
        if (selected.Length > 0)
            notes.Add(new(category, $"{label}: {selected.Length}.", Evidence(selected)));
    }

    private static string Role(World world, WorldEvent item) => world.Agents.GetValueOrDefault(item.EntityIds[0])?.Role ?? "unknown";
    private static string Action(WorldEvent item) => item.Payload["action"]?["action_type"]?.GetValue<string>() ?? "unknown";
    private static string[] Evidence(IEnumerable<WorldEvent> events) => events.TakeLast(3).Select(item => item.EventId).Distinct().ToArray();
    private static string Money(long minor) => (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);
    private static string Escape(string text) => text.Replace("\n", " ").Replace("\r", " ")
        .Replace("\\", "\\\\").Replace("`", "\\`").Replace("[", "\\[").Replace("]", "\\]").Replace("|", "\\|")
        .Replace("<", "&lt;").Replace(">", "&gt;").Replace("*", "\\*").Replace("_", "\\_");

    private static string Render(RunNotes notes)
    {
        var text = new StringBuilder();
        var local = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(notes.GeneratedAt, "America/New_York");
        text.AppendLine($"# Run observations — {notes.RunId}\n");
        text.AppendLine($"Status: **{notes.Status}** · Mode: **{notes.Mode}** · Seed: {notes.Seed} · Tick: {notes.Tick} · Through event: {notes.ThroughSequence}\n");
        text.AppendLine($"Generated {local:yyyy-MM-dd HH:mm zzz} (America/New_York).\n");
        text.AppendLine($"{notes.Population} agents; {notes.ModelCoverage} with accepted model turns; {notes.AcceptedModelDecisions} accepted model decisions; {notes.Fallbacks} fallback results; {notes.Rejections} rejections.\n");
        text.AppendLine("## Key observations\n");
        foreach (var note in notes.Observations)
        {
            text.AppendLine($"- {Escape(note.Text)}");
            if (note.EvidenceIds.Length > 0)
                text.AppendLine($"  Evidence: {string.Join(", ", note.EvidenceIds.Select(id => $"`{id}`"))}.");
        }
        text.AppendLine("\n## Observed choices\n");
        text.AppendLine("| Role | Action | Source | Accepted choices | Distinct agents |\n|---|---|---|---:|---:|");
        foreach (var behavior in notes.Behaviors)
            text.AppendLine($"| {behavior.Role} | {behavior.Action} | {behavior.Source} | {behavior.Count} | {behavior.Agents} |");
        if (notes.Countries.Length > 0)
        {
            text.AppendLine("\n## Country outcomes at the final observed checkpoint\n");
            text.AppendLine("Needs met is for the latest economic cycle; unmet needs and delivered trade value are cumulative.\n");
            text.AppendLine("| Country | Government | Needs met | Support | Cash Gini | Treasury WLD | Trade WLD | Unmet needs |\n|---|---|---:|---:|---:|---:|---:|---:|");
            foreach (var country in notes.Countries)
                text.AppendLine(FormattableString.Invariant($"| {country.Id} | {country.Government} | {country.NeedsMet:P0} | {country.Approval:P0} | {country.GiniCash:F3} | {Money(country.TreasuryMinor)} | {Money(country.TradeValueMinor)} | {country.UnmetNeeds} |"));
        }
        if (notes.Examples.Length > 0)
        {
            text.AppendLine("\n## Examples of stated model reasoning\n");
            foreach (var example in notes.Examples)
                text.AppendLine($"- T{example.Tick} · {example.AgentId} · {example.Action}: “{Escape(example.Reason)}” (`{example.EventId}`).");
        }
        text.AppendLine("\n## Interpretation limits\n");
        foreach (var limit in notes.Limits)
            text.AppendLine($"- {limit}");
        return text.ToString();
    }
}
