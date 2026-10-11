using System.Text.Json;
using System.Text.Json.Nodes;

namespace GEarth.Core;

public sealed record ExperimentEdit(string Scope, string TargetId, string Field,
    JsonNode Value, JsonNode ExpectedValue);

public static class ExperimentInterventions
{
    public static void Apply(World world, ExperimentEdit edit, string experimentId)
    {
        if (world.Events.Any(item => item.EventType == "experiment_intervention"
            && (string?)item.Payload["experiment_id"] == experimentId))
            throw new ArgumentException("Intervention already applied");
        JsonNode before;
        Action apply;
        if (edit.Scope == "agent" && world.Agents.TryGetValue(edit.TargetId, out var agent)
            && SocietyLabor.IsResident(agent))
        {
            switch (edit.Field)
            {
                case "health":
                    var health = Number(edit.Value, 0, 100);
                    if (health != Math.Truncate(health)) throw new ArgumentException("Health must be an integer");
                    before = JsonValue.Create(agent.Health)!;
                    apply = () => { agent.Health = (int)health; agent.Version++; };
                    break;
                case "productivity":
                    var productivity = Number(edit.Value, 0.6, 1.4);
                    before = JsonValue.Create(agent.Weights.Productivity)!;
                    apply = () => { agent.Weights.Productivity = productivity; agent.Version++; };
                    break;
                default: throw new ArgumentException("Unsupported agent field");
            }
        }
        else if (edit.Scope == "country" && world.Society?.Nations.GetValueOrDefault(edit.TargetId) is { } nation)
        {
            switch (edit.Field)
            {
                case "government":
                    var government = edit.Value.GetValue<string>();
                    if (!Governments.Profiles.Any(item => item.Id == government))
                        throw new ArgumentException("Unsupported government");
                    before = JsonValue.Create(nation.GovernmentId)!;
                    apply = () => SocietyFactory.ChangeGovernment(world, edit.TargetId, government);
                    break;
                case "income_tax_percent":
                    var tax = Number(edit.Value, 0, SocietyActions.MaxTaxBasisPoints / 100);
                    if (tax != Math.Truncate(tax)) throw new ArgumentException("Tax must be a whole percent");
                    before = JsonValue.Create(nation.TaxBasisPoints / 100.0)!;
                    apply = () =>
                    {
                        nation.TaxBasisPoints = (int)tax * 100;
                        nation.TaxChangedTick = world.Tick;
                        nation.LastPolicyTick = world.Tick;
                        nation.LastPolicyAction = "experiment_income_tax";
                        nation.LastPolicyParams = new JsonObject { ["income_tax_percent"] = tax };
                    };
                    break;
                default: throw new ArgumentException("Unsupported country field");
            }
        }
        else throw new ArgumentException("Unknown or ineligible experiment target");
        // Compare numbers by value, so 50 and 50.0 have the same expected value.
        var matches = before.ToJsonString() == edit.ExpectedValue.ToJsonString();
        if (double.TryParse(before.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture, out var oldNumber)
            && double.TryParse(edit.ExpectedValue.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture, out var expectedNumber))
            matches = oldNumber == expectedNumber;
        if (!matches) throw new ArgumentException("Checkpoint value changed; preview the edit again");
        apply();
        world.Emit("experiment_intervention", "rule", [edit.TargetId], new
        {
            version = 1, experiment_id = experimentId, edit.Scope, target_id = edit.TargetId,
            edit.Field, before, after = edit.Value, effective_tick = world.Tick
        });
        SocietyEconomy.AssertConservation(world);
    }

    private static double Number(JsonNode node, double minimum, double maximum)
    {
        var value = JsonSerializer.Deserialize<double>(node.ToJsonString());
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentException("Experiment value outside supported range");
        return value;
    }
}

// A small independent stream: forks cannot append to the parent's audit-v1 dataset.
public sealed class ExperimentMetricWriter : IDisposable
{
    private readonly StreamWriter writer;
    private readonly string countryId;
    private readonly string? agentId;
    private readonly string timelineId;

    public ExperimentMetricWriter(string path, string scope, string targetId, string timeline, World world)
    {
        agentId = scope == "agent" ? targetId : null;
        countryId = agentId is null ? targetId : world.Agents[targetId].CountryId;
        if (world.Society?.Nations.ContainsKey(countryId) != true)
            throw new ArgumentException("Unknown metric target");
        timelineId = timeline;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
    }

    public void Day(World world)
    {
        var nation = world.Society!.Nations[countryId];
        var residents = world.InCountry(countryId).Where(SocietyLabor.IsResident).ToArray();
        var people = agentId is null ? residents : new[] { world.Agents[agentId] };
        var seeking = residents.Count(SocietyMetrics.SeekingWork);
        var employed = residents.Count(SocietyMetrics.Employed);
        var row = new
        {
            schema_version = "gearth.experiment-metrics.v1", timeline_id = timelineId,
            world.Tick, world.Status, cash_minor = people.Length == 0 ? (double?)null : people.Average(item => (double)item.CashMinor),
            net_wealth_minor = people.Length == 0 ? (double?)null : people.Average(item => (double)SocietyMetrics.NetWealth(world, item)),
            health = people.Length == 0 ? (double?)null : people.Average(item => (double)item.Health),
            wellbeing = people.Length == 0 ? (double?)null : people.Average(item => (double)item.Wellbeing),
            employment_rate = people.Length == 0 ? (double?)null : people.Count(SocietyMetrics.Employed) / (double)people.Length,
            unemployment_rate = seeking + employed == 0 ? (double?)null : seeking / (double)(seeking + employed),
            approval = nation.Approval, treasury_minor = world.Agents[countryId + "_government"].CashMinor,
            liquid_assets_gini = residents.Length == 0 ? (double?)null : SocietyEconomy.Gini(residents.Select(SocietyBank.Savings).ToArray())
        };
        writer.WriteLine(JsonSerializer.Serialize(row, Wire.Json));
        writer.Flush();
    }

    public void Dispose() => writer.Dispose();
}
