using System.IO.Compression;
using System.Text.Json;

namespace GEarth.Core;

public sealed partial class LocalStore
{
    private async Task StageRendererFrame(World world, string state, CancellationToken ct)
    {
        var path = Path.Combine(PathFor(world.Id) + ".renderer", $"{world.Revision:D12}");
        if (RendererReplay.FrameKind(world) is null)
        {
            // A failed candidate at this uncommitted revision may lose to a non-frame checkpoint.
            if (Directory.Exists(Path.GetDirectoryName(path)))
            {
                File.Delete(path + ".meta.json");
                File.Delete(path + ".json.gz");
            }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var digest = AuditPersistence.CheckpointDigest(state);
        if (File.Exists(path + ".meta.json"))
        {
            var existing = JsonSerializer.Deserialize<RendererFrameHead>(await File.ReadAllTextAsync(path + ".meta.json", ct), Wire.Json)!;
            if (existing.CheckpointDigest == digest && File.Exists(path + ".json.gz")) return;
            // The checkpoint CAS and write lock already established this revision is uncommitted.
            // Replace a losing staged candidate; committed revisions never reach this path.
        }
        await using (var stream = File.Create(path + ".tmp"))
        await using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
        await using (var writer = new StreamWriter(gzip))
            await writer.WriteAsync(state.AsMemory(), ct);
        File.Move(path + ".tmp", path + ".json.gz", true);
        await File.WriteAllTextAsync(path + ".meta.tmp", JsonSerializer.Serialize(RendererReplay.Head(world, digest, 1), Wire.Json), ct);
        File.Move(path + ".meta.tmp", path + ".meta.json", true);
    }

    public async Task<RendererFrameHead[]> ReadRendererFrames(string runId, long afterRevision, int limit, CancellationToken ct)
    {
        var saved = await ReadAuditHead(runId, ct);
        var path = PathFor(runId) + ".renderer";
        if (saved is null || !Directory.Exists(path)) return [];
        var heads = new List<RendererFrameHead>();
        foreach (var file in Directory.GetFiles(path, "*.meta.json").Order())
        {
            var revision = long.Parse(Path.GetFileName(file).Split('.')[0]);
            if (revision <= afterRevision || revision > saved.Revision) continue;
            heads.Add(JsonSerializer.Deserialize<RendererFrameHead>(await File.ReadAllTextAsync(file, ct), Wire.Json)!);
            if (heads.Count >= Math.Clamp(limit, 1, 100)) break;
        }
        return heads.ToArray();
    }

    public async Task<World?> LoadRendererFrame(string runId, long revision, CancellationToken ct)
    {
        var head = (await ReadRendererFrames(runId, revision - 1, 1, ct)).FirstOrDefault();
        if (head?.Revision != revision) return null;
        await using var file = File.OpenRead(Path.Combine(PathFor(runId) + ".renderer", $"{revision:D12}.json.gz"));
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var state = await reader.ReadToEndAsync(ct);
        if (AuditPersistence.CheckpointDigest(state) != head.CheckpointDigest)
            throw new InvalidOperationException("Renderer checkpoint digest verification failed");
        return JsonSerializer.Deserialize<World>(state, Wire.Json);
    }
}

public sealed partial class NeonStore
{
    public async Task<RendererFrameHead[]> ReadRendererFrames(string runId, long afterRevision, int limit, CancellationToken ct)
    {
        var rows = await Query("""
            SELECT f.revision,f.tick,f.week,f.frame_kind,f.through_event_sequence,f.checkpoint_digest,f.part_count
            FROM gearth.renderer_frames f JOIN gearth.stage1_runs r ON r.run_id=f.run_id
            WHERE f.run_id=$1 AND f.revision>$2::bigint AND f.revision<=r.revision
            ORDER BY f.revision LIMIT $3::int
            """, [runId, afterRevision.ToString(), Math.Clamp(limit, 1, 100).ToString()], ct);
        return rows.Select(row => new RendererFrameHead(runId, long.Parse(row![0]!.GetValue<string>()),
            int.Parse(row[1]!.GetValue<string>()), int.Parse(row[2]!.GetValue<string>()), row[3]!.GetValue<string>(),
            long.Parse(row[4]!.GetValue<string>()), row[5]!.GetValue<string>(), int.Parse(row[6]!.GetValue<string>()))).ToArray();
    }

    public async Task<World?> LoadRendererFrame(string runId, long revision, CancellationToken ct)
    {
        var head = (await ReadRendererFrames(runId, revision - 1, 1, ct)).FirstOrDefault();
        return head?.Revision == revision ? await LoadCheckpoint(runId, head.CheckpointDigest, head.PartCount, ct) : null;
    }
}
