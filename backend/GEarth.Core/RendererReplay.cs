using System.Text.Json.Nodes;

namespace GEarth.Core;

public sealed record RendererFrameHead(string RunId, long Revision, int Tick, int Week,
    string FrameKind, long ThroughEventSequence, string CheckpointDigest, int PartCount);

public interface IRendererStore
{
    Task<RendererFrameHead[]> ReadRendererFrames(string runId, long afterRevision, int limit, CancellationToken ct);
    Task<World?> LoadRendererFrame(string runId, long revision, CancellationToken ct);
}

public static class RendererReplay
{
    public const string Contract = "gearth.renderer.v1";

    public static string? FrameKind(World world) => !world.RendererCaptureEnabled || world.Society is null ? null
        : world.Status is "completed" or "stopped" or "failed" ? "terminal"
        : world.Tick == 0 ? "opening"
        : world.Tick % SocietyLabor.DaysPerWeek == 0 ? "week" : null;

    public static RendererFrameHead Head(World world, string digest, int parts) => new(world.Id,
        world.Revision, world.Tick, (world.Tick + 6) / SocietyLabor.DaysPerWeek,
        FrameKind(world) ?? throw new InvalidOperationException("Not a renderer boundary"), world.Seq, digest, parts);

    public static JsonObject Frame(World world)
    {
        var snapshot = Wire.Node(world.Snapshot()).AsObject();
        snapshot["agents"] = Wire.Node(world.Agents.Values.Select(agent => RunAudit.AgentState(world, agent)));
        snapshot["banks"] = Wire.Node(world.Agents.Values.Where(SocietyBank.IsBank)
            .Select(bank => new { bank_id = bank.Id, state = SocietyBank.View(world, bank, true) }));
        return new JsonObject
        {
            ["schema_version"] = Contract,
            ["run_id"] = world.Id,
            ["revision"] = world.Revision,
            ["tick"] = world.Tick,
            ["week"] = (world.Tick + 6) / SocietyLabor.DaysPerWeek,
            ["frame_kind"] = FrameKind(world) ?? "checkpoint",
            ["complete_week"] = world.Tick > 0 && world.Tick % SocietyLabor.DaysPerWeek == 0,
            ["through_event_sequence"] = world.Seq,
            ["units"] = Wire.Node(new { days_per_tick = 1, days_per_week = SocietyLabor.DaysPerWeek,
                minor_units_per_wld = 100, hungry_output_factor = SocietyEconomy.HungerOutputFactor }),
            ["snapshot"] = snapshot
        };
    }
}
