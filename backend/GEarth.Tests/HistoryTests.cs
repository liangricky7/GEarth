using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Tests;

public sealed class HistoryTests
{
    // Five weeks with rule decisions: every agent's daily cash change must equal the money flows
    // recorded for it, so the renderer's giver/recipient lines account for every minor unit.
    [Fact]
    public void DailyFlowsReconcileWithEveryAgentsCash()
    {
        var world = SocietyFactory.Create("history_test", false, 1100);
        var path = Path.Combine(Path.GetTempPath(), $"gearth_history_{Guid.NewGuid():N}.ndjson.gz");
        try
        {
            using (var history = new HistoryWriter(path, world))
            {
                history.Day(world);
                for (var day = 1; day <= 35; day++)
                {
                    SocietyEngine.Advance(world);
                    if (world.Tick == 1 || world.Tick % SocietyLabor.DaysPerWeek == 0)
                        foreach (var agent in SocietyScheduler.WeeklyCohort(world).Where(SocietyLabor.DecidesForItself))
                        {
                            var job = SocietyEngine.Observe(world, agent.Id, DateTimeOffset.UnixEpoch);
                            var pick = job.Candidates[System.Security.Cryptography.SHA256.HashData(
                                System.Text.Encoding.UTF8.GetBytes($"{agent.Id}:{world.Tick}"))[0] % job.Candidates.Length];
                            SocietyEngine.Apply(world, job, new DecisionResult(job.RequestId,
                                new ActionProposal(pick.ActionType, SocietyActions.Resolve(pick), "test", []),
                                "rule", "none", 0), DateTimeOffset.UnixEpoch);
                        }
                    history.Day(world);
                }
                history.End(world);
            }

            using var reader = new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress));
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonNode.Parse(line)!).ToArray();
            Assert.Equal("header", lines[0]["type"]!.GetValue<string>());
            Assert.Equal("end", lines[^1]["type"]!.GetValue<string>());
            var days = lines.Where(line => line["type"]!.GetValue<string>() == "day").ToArray();
            Assert.Equal(36, days.Length);

            Dictionary<string, long> Cash(JsonNode line) => line["agents"]!.AsArray()
                .ToDictionary(row => row![0]!.GetValue<string>(), row => row![6]!.GetValue<long>());
            var kinds = new HashSet<string>();
            for (var index = 1; index < days.Length; index++)
            {
                var before = Cash(days[index - 1]);
                var net = new Dictionary<string, long>();
                foreach (var flow in days[index]["flows"]!.AsArray())
                {
                    var amount = flow![3]!.GetValue<long>();
                    net[flow[0]!.GetValue<string>()] = net.GetValueOrDefault(flow[0]!.GetValue<string>()) - amount;
                    net[flow[1]!.GetValue<string>()] = net.GetValueOrDefault(flow[1]!.GetValue<string>()) + amount;
                    kinds.Add(flow[2]!.GetValue<string>());
                }
                foreach (var (id, cash) in Cash(days[index]))
                    Assert.True(cash - before.GetValueOrDefault(id) == net.GetValueOrDefault(id),
                        $"day {days[index]["tick"]}: {id} cash moved {cash - before.GetValueOrDefault(id)}, flows say {net.GetValueOrDefault(id)}");
            }
            Assert.Contains("wage", kinds);
            Assert.Contains("purchase", kinds);
            Assert.Contains("sales_tax", kinds);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
