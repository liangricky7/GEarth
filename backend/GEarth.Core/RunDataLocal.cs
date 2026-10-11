using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace GEarth.Core;

public sealed partial class LocalStore : IRunDataStore
{
    public async Task<RunDataHead?> ReadRunDataHead(string id, CancellationToken ct)
    {
        RunDataIO.ValidateId(id); var path = PathFor(id);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[65536]; var count = 0;
        while (count < bytes.Length)
        {
            count += await stream.ReadAsync(bytes.AsMemory(count), ct);
            if (RunDataIO.Prefix(bytes.AsSpan(0, count), count == stream.Length) is { } head)
            {
                if (head.RunId != id) throw new InvalidDataException("Stored run identity mismatch");
                return head;
            }
            if (count == stream.Length) break;
        }
        throw new InvalidDataException("Stored metadata prefix is invalid");
    }
    public async Task<RunDataHead[]> ReadRunCatalog(string? beforeId, int limit, CancellationToken ct)
    {
        var result = new List<RunDataHead>();
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Run storage is unavailable");
        // Sorting names does not read any population or checkpoint body.
        foreach (var id in Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension)
            .Where(id => beforeId is null || string.CompareOrdinal(id, beforeId) < 0).OrderDescending(StringComparer.Ordinal).Take(limit))
        {
            ct.ThrowIfCancellationRequested();
            if (await ReadRunDataHead(id!, ct) is { } head) result.Add(head);
        }
        return result.ToArray();
    }
    public async Task<bool> VerifyAuditPin(string id, long revision, long sequence, CancellationToken ct)
    {
        var head = await ReadRunDataHead(id, ct);
        if (head is null || revision > head.Revision || sequence > head.AuditSequence) return false;
        if (revision == head.Revision) return sequence == head.AuditSequence;
        var path = PathFor(id) + ".audit"; long through = 0; var revisionExists = false;
        if (Directory.Exists(path))
            foreach (var file in Directory.EnumerateFiles(path, "*.json").Order(StringComparer.Ordinal))
            {
                if (!long.TryParse(Path.GetFileNameWithoutExtension(file), out var rev) || rev > revision) continue;
                var manifest = JsonSerializer.Deserialize<AuditManifest>(await File.ReadAllTextAsync(file, ct), Wire.Json)!;
                through = Math.Max(through, manifest.Chunks.LastOrDefault()?.Last ?? 0);
                if (rev == revision) revisionExists = true;
            }
        if (!revisionExists) revisionExists = (await ReadRendererFrames(id, revision - 1, 1, ct)).FirstOrDefault()?.Revision == revision;
        return revisionExists && through == sequence;
    }
    public async Task<RunSaveMarker[]> ReadSaveMarkers(string id, long afterRevision, int limit, CancellationToken ct)
    {
        var head = await ReadRunDataHead(id, ct); var path = PathFor(id) + ".audit";
        if (head is null || !Directory.Exists(path)) return [];
        return Directory.EnumerateFiles(path, "*.json").Select(file => long.TryParse(Path.GetFileNameWithoutExtension(file), out var rev) ? rev : -1)
            .Where(rev => rev > afterRevision && rev <= head.Revision).Order().Take(limit)
            .Select(rev => new RunSaveMarker(rev, rev == head.Revision ? head.Tick : null)).ToArray();
    }
    public async Task<AuditRecord[]> ReadPinnedAudit(string id, long revision, long sequence, long after, int limit, CancellationToken ct)
    {
        var path = PathFor(id) + ".audit"; if (!Directory.Exists(path)) return [];
        var result = new List<AuditRecord>(); var bytes = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*.json").Order(StringComparer.Ordinal))
        {
            if (!long.TryParse(Path.GetFileNameWithoutExtension(file), out var rev) || rev > revision) continue;
            var manifest = JsonSerializer.Deserialize<AuditManifest>(await File.ReadAllTextAsync(file, ct), Wire.Json)!;
            foreach (var chunk in manifest.Chunks.Where(c => c.Last > after && c.First <= sequence))
            {
                // Saved manifests are internal, nevertheless never permit a path outside the audit directory.
                if (chunk.File != Path.GetFileName(chunk.File) || chunk.File.Contains("..")) throw new InvalidDataException("Invalid audit chunk path");
                await using var stream = File.OpenRead(Path.Combine(path, chunk.File));
                await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                await foreach (var line in RunDataIO.Lines(gzip, ct: ct))
                {
                    var row = JsonSerializer.Deserialize<AuditRecord>(line, Wire.Json) ?? throw new InvalidDataException("Invalid audit row");
                    if (row.Sequence <= after || row.Sequence > sequence || row.Revision > revision) continue;
                    var size = Encoding.UTF8.GetByteCount(line);
                    if (bytes + size > RunDataIO.MaximumRowBytes) return result.ToArray();
                    bytes += size; result.Add(row); if (result.Count == limit) return result.ToArray();
                }
            }
        }
        return result.ToArray();
    }
}
