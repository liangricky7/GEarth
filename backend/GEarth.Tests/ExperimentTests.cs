using System.Text.Json;
using System.Text.Json.Nodes;
using GEarth.Api;
using GEarth.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace GEarth.Tests;

[Collection("Coordinator audit environment")]
public sealed class ExperimentTests
{
    private static ExperimentEdit Edit(string scope, string id, string field, object value, object before)
        => new(scope, id, field, Wire.Node(value), Wire.Node(before));

    [Fact]
    public void HealthShockIsAuditedOnceAndDoesNotCreateMoney()
    {
        var world = SocietyFactory.Create("intervention", false, 200);
        var person = world.Agents.Values.First(SocietyLabor.IsResident);
        var cash = world.Agents.Values.Sum(item => item.CashMinor);
        var edit = Edit("agent", person.Id, "health", 20, person.Health);
        ExperimentInterventions.Apply(world, edit, "experiment_a");
        Assert.Equal(20, person.Health);
        Assert.Equal(cash, world.Agents.Values.Sum(item => item.CashMinor));
        var audit = Assert.Single(world.Events, item => item.EventType == "experiment_intervention");
        Assert.Equal(20, audit.Payload["after"]!.GetValue<int>());
        Assert.Equal("experiment_a", audit.Payload["experiment_id"]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => ExperimentInterventions.Apply(world, edit, "experiment_a"));
        SocietyEconomy.AssertConservation(world);
    }

    [Theory]
    [InlineData("health", -1)]
    [InlineData("health", 101)]
    [InlineData("health", 30.5)]
    [InlineData("productivity", 0.5)]
    [InlineData("productivity", 1.5)]
    [InlineData("cash_minor", 100)]
    public void InvalidEditsDoNotMutateTheCheckpoint(string field, double value)
    {
        var world = SocietyFactory.Create("invalid", false, 200);
        var person = world.Agents.Values.First(SocietyLabor.IsResident);
        var original = Wire.Node(world).ToJsonString();
        Assert.Throws<ArgumentException>(() => ExperimentInterventions.Apply(world,
            Edit("agent", person.Id, field, value, person.Health), "invalid_edit"));
        Assert.Equal(original, Wire.Node(world).ToJsonString());
    }

    [Fact]
    public void StaleExpectedValueAndNonresidentTargetsAreRejected()
    {
        var world = SocietyFactory.Create("stale", false, 200);
        var person = world.Agents.Values.First(SocietyLabor.IsResident);
        var original = Wire.Node(world).ToJsonString();
        Assert.Throws<ArgumentException>(() => ExperimentInterventions.Apply(world,
            Edit("agent", person.Id, "health", 10, person.Health - 1), "stale_edit"));
        Assert.Throws<ArgumentException>(() => ExperimentInterventions.Apply(world,
            Edit("agent", "aster_government", "health", 10, 100), "bad_target"));
        Assert.Equal(original, Wire.Node(world).ToJsonString());
    }

    [Fact]
    public void GovernmentAndTaxUseTheirDeclaredSemantics()
    {
        var world = SocietyFactory.Create("policies", false, 200);
        var nation = world.Society!.Nations["aster"];
        ExperimentInterventions.Apply(world, Edit("country", "aster", "government", "dictatorship", nation.GovernmentId), "government_edit");
        Assert.Equal(1000, nation.TaxBasisPoints);
        ExperimentInterventions.Apply(world, Edit("country", "aster", "income_tax_percent", 22, 10.0), "tax_edit");
        Assert.Equal(2200, nation.TaxBasisPoints);
        Assert.Equal(world.Tick, nation.TaxChangedTick);
        SocietyEconomy.AssertConservation(world);
    }

    [Fact]
    public async Task RealCoordinatorRunsPairedTimelinesAndPreservesTheParent()
    {
        var root = Path.Combine(Path.GetTempPath(), "experiment_pair_" + Guid.NewGuid().ToString("N"));
        var parent = SocietyFactory.Create("pair_parent", false, 200);
        for (var day = 0; day < 6; day++) SocietyEngine.Advance(parent);
        parent.Revision = 1;
        var person = parent.Agents.Values.First(SocietyLabor.IsResident);
        var parentStore = new LocalStore(Path.Combine(root, "parent"));
        var settings = new Dictionary<string, string?>
        {
            ["GEARTH_WORLD"] = "true", ["GEARTH_LIVE"] = "false", ["GEARTH_TICKS"] = "14",
            ["GEARTH_INTERVAL_MS"] = "0", ["GEARTH_ANALYTICS"] = "false", ["GEARTH_VERBOSE"] = "false",
            ["GEARTH_COMPUTE_PROVIDERS"] = null, ["GEARTH_SAVED_RUN_ID"] = null,
            ["GEARTH_RESUME_RUN_ID"] = parent.Id, ["GEARTH_FORK_GOVERNMENT"] = null,
            ["GEARTH_EXPERIMENT_SCOPE"] = "agent", ["GEARTH_EXPERIMENT_TARGET"] = person.Id,
            ["GEARTH_EXPERIMENT_ID"] = "paired_test", ["GEARTH_CONTROL_DIRECTORY"] = Path.Combine(root, "pause"),
            ["GEARTH_EXPERIMENT_EDIT"] = null, ["GEARTH_EXPERIMENT_METRICS"] = null,
            ["GEARTH_TIMELINE"] = null, ["GEARTH_NOTES_DIRECTORY"] = null
        };
        var previous = settings.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            await parentStore.Initialize(CancellationToken.None);
            await parentStore.Save(parent, 0, CancellationToken.None);
            var original = await File.ReadAllTextAsync(Path.Combine(root, "parent", parent.Id + ".json"));
            var baseline = await Run("baseline", null);
            var control = await Run("control", null);
            var changed = await Run("changed", Edit("agent", person.Id, "health", 10, person.Health));
            Assert.Equal(State(baseline), State(control));
            Assert.NotEqual(State(control), State(changed));
            Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(root, "parent", parent.Id + ".json")));
            foreach (var name in new[] { "control", "changed" })
            {
                var rows = (await File.ReadAllLinesAsync(Path.Combine(root, name, "metrics.ndjson")))
                    .Select(line => JsonNode.Parse(line)!).GroupBy(row => row["tick"]!.GetValue<int>()).Select(group => group.Last()).ToArray();
                Assert.Equal(Enumerable.Range(6, 9), rows.Select(row => row["tick"]!.GetValue<int>()));
                Assert.Equal("completed", rows[^1]["status"]!.GetValue<string>());
            }

            async Task<World> Run(string name, ExperimentEdit? edit)
            {
                var directory = Path.Combine(root, name);
                Directory.CreateDirectory(directory);
                var store = new LocalStore(Path.Combine(directory, "runs"));
                await store.Initialize(CancellationToken.None);
                await store.Save(Wire.Copy(parent), 0, CancellationToken.None);
                foreach (var (key, value) in settings) Environment.SetEnvironmentVariable(key, value);
                Environment.SetEnvironmentVariable("GEARTH_EXPERIMENT_METRICS", Path.Combine(directory, "metrics.ndjson"));
                Environment.SetEnvironmentVariable("GEARTH_NOTES_DIRECTORY", Path.Combine(directory, "notes"));
                Environment.SetEnvironmentVariable("GEARTH_TIMELINE", name);
                if (edit is not null)
                {
                    var path = Path.Combine(directory, "edit.json");
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(edit, Wire.Json));
                    Environment.SetEnvironmentVariable("GEARTH_EXPERIMENT_EDIT", path);
                }
                using var coordinator = new Coordinator(store, new RunNotesService(store, NullLogger<RunNotesService>.Instance), NullLogger<Coordinator>.Instance);
                await coordinator.StartAsync(CancellationToken.None);
                await coordinator.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Null(coordinator.Error);
                var final = (await store.Load(parent.Id, CancellationToken.None))!;
                Assert.Equal("completed", final.Status);
                SocietyEconomy.AssertConservation(final);
                return final;
            }
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string State(World world) => System.Text.RegularExpressions.Regex.Replace(
        Wire.Node(world.Agents).ToJsonString() + Wire.Node(world.Society!.Nations).ToJsonString(),
        "[0-9a-f]{32}|:e[0-9]+|\\d{4}-\\d{2}-\\d{2}T[0-9:.+Z-]+", "");

    [Fact]
    public void EmptyCountryRecordsMissingResidentMetricsWithoutStoppingTheEngine()
    {
        var world = SocietyFactory.Create("empty_country", false, 200);
        foreach (var person in world.InCountry("aster").Where(SocietyLabor.IsResident)) person.CountryId = "beryl";
        world.Reindex();
        var directory = Path.Combine(Path.GetTempPath(), "empty_metrics_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "metrics.ndjson");
        try
        {
            using (var writer = new ExperimentMetricWriter(path, "country", "aster", "empty", world)) writer.Day(world);
            var row = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Null(row["health"]);
            Assert.Null(row["cash_minor"]);
            Assert.Null(row["unemployment_rate"]);
            Assert.NotNull(row["treasury_minor"]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
