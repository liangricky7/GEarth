using System.Text.Json.Nodes;
using Faultline.Core;

namespace Faultline.Tests;

public sealed class ForkTests
{
    // A fork resumes from a saved week-boundary checkpoint. Reloading must not change the future,
    // so an unchanged fork replays its parent exactly and any difference comes from the fork's change.
    [Fact]
    public void ReloadedCheckpointReplaysItsParentExactly()
    {
        var parent = SocietyFactory.Create("fork_test", false, 400);
        Run(parent, 13);
        var control = Wire.Copy(parent);
        control.Emit("timeline_forked", "rule", [control.Id], new { from_tick = control.Tick });
        var changed = Wire.Copy(parent);
        SocietyFactory.ChangeGovernment(changed, "aster", "dictatorship");

        Run(parent, 28);
        Run(control, 28);
        Run(changed, 28);

        Assert.Equal(State(parent), State(control));
        Assert.NotEqual(State(parent), State(changed));
        Assert.Equal("dictatorship", changed.Society!.Nations["aster"].GovernmentId);
    }

    private static void Run(World world, int untilTick)
    {
        while (world.Tick < untilTick)
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
        }
    }

    // Agents and nations only (events differ by the fork's own event); request IDs are random and event IDs shift by the fork's events.
    private static string State(World world) => System.Text.RegularExpressions.Regex.Replace(
        Wire.Node(world.Agents).ToJsonString() + Wire.Node(world.Society!.Nations).ToJsonString(), "[0-9a-f]{32}|:e[0-9]+", "");
}
