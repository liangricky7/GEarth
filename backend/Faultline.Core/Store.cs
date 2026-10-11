using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Faultline.Core;

public interface IWorldStore
{
    string Kind { get; }
    Task Initialize(CancellationToken cancellationToken);
    Task SaveNotes(RunNotes notes, CancellationToken cancellationToken);
    Task<RunNotes?> LoadNotes(string id, CancellationToken cancellationToken);
    // serialized: the world's JSON when the caller already has it, so a checkpoint is serialized once.
    Task Save(World world, long expectedRevision, CancellationToken cancellationToken, string? serialized = null);
    Task<World?> Load(string id, CancellationToken cancellationToken);
    Task<string[]> List(CancellationToken cancellationToken);
    Task<WorldEvent[]> ReadEvents(string id, long afterSequence, int limit, CancellationToken cancellationToken);
}

public sealed class LocalStore(string directory) : IWorldStore, IRunAuditStore, IAnalyticsReportStore
{
    public string Kind => "local";
    public bool SupportsAudit => true;

    public Task Initialize(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        return Task.CompletedTask;
    }

    private sealed record CheckpointHead(long Revision, long Seq, long AuditSequence);
    private sealed record AuditChunk(string File, long First, long Last);
    private sealed record AuditManifest(string Digest, AuditChunk[] Chunks);

    public async Task Save(World world, long expectedRevision, CancellationToken cancellationToken, string? serialized = null)
    {
        var path = PathFor(world.Id);
        using var writeLock = new FileStream(path + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);

        // Only the saved revision and sequence are needed; the full checkpoint is read only for a same-revision retry.
        var existing = File.Exists(path)
            ? JsonSerializer.Deserialize<CheckpointHead>(await File.ReadAllTextAsync(path, cancellationToken), Wire.Json)
            : null;
        if (existing?.Revision == world.Revision && SameCheckpoint(await Load(world.Id, cancellationToken), world))
        {
            if (world.PendingAudit.Count > 0)
            {
                var manifestPath = Path.Combine(path + ".audit", $"{world.Revision:D12}.json");
                var manifest = JsonSerializer.Deserialize<AuditManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), Wire.Json)!;
                if (manifest.Digest != AuditPersistence.Digest(world))
                    throw new InvalidOperationException("Conflicting audit retry");
            }
            world.PendingAudit.Clear();
            return;
        }
        if ((existing?.Revision ?? 0) != expectedRevision || world.Revision != expectedRevision + 1)
            throw new InvalidOperationException("Revision conflict");

        AuditPersistence.Validate(world, existing?.AuditSequence ?? 0);
        if (world.PendingAudit.Count > 0)
        {
            var auditDirectory = path + ".audit";
            Directory.CreateDirectory(auditDirectory);
            var digest = AuditPersistence.Digest(world);
            var chunks = new List<AuditChunk>();
            foreach (var batch in AuditPersistence.Batches(world))
            {
                var name = $"{world.Revision:D12}-{digest}-{batch[0].Sequence:D16}-{batch[^1].Sequence:D16}.ndjson.gz";
                var chunkPath = Path.Combine(auditDirectory, name);
                await using (var stream = new FileStream(chunkPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                await using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
                await using (var writer = new StreamWriter(gzip))
                    foreach (var row in batch)
                        await writer.WriteLineAsync(JsonSerializer.Serialize(row, Wire.Json).AsMemory(), cancellationToken);
                File.Move(chunkPath + ".tmp", chunkPath, true);
                chunks.Add(new AuditChunk(name, batch[0].Sequence, batch[^1].Sequence));
            }
            var manifestPath = Path.Combine(auditDirectory, $"{world.Revision:D12}.json");
            await File.WriteAllTextAsync(manifestPath + ".tmp", JsonSerializer.Serialize(new AuditManifest(digest, chunks.ToArray()), Wire.Json), cancellationToken);
            File.Move(manifestPath + ".tmp", manifestPath, true);
        }

        var eventDirectory = path + ".events";
        Directory.CreateDirectory(eventDirectory);
        var newEvents = world.Events.Where(item => item.Seq > (existing?.Seq ?? 0)).ToArray();
        if (newEvents.Length > 0)
        {
            var chunk = Path.Combine(eventDirectory, $"{world.Revision:D12}.json");
            await File.WriteAllTextAsync(chunk + ".tmp", JsonSerializer.Serialize(newEvents, Wire.Json), cancellationToken);
            File.Move(chunk + ".tmp", chunk, true);
        }

        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, serialized ?? JsonSerializer.Serialize(world, Wire.Json), cancellationToken);
        File.Move(temporaryPath, path, true);
        world.PendingAudit.Clear();
    }

    public async Task<World?> Load(string id, CancellationToken cancellationToken)
    {
        var path = PathFor(id);
        if (!File.Exists(path))
            return null;

        var serialized = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<World>(serialized, Wire.Json);
    }

    public Task<string[]> List(CancellationToken cancellationToken)
    {
        var runIds = Directory.GetFiles(directory, "*.json")
            .OrderDescending()
            .Take(100)
            .Select(Path.GetFileNameWithoutExtension)
            .Cast<string>()
            .ToArray();

        return Task.FromResult(runIds);
    }

    public async Task<WorldEvent[]> ReadEvents(string id, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        var world = await Load(id, cancellationToken);
        if (world is null)
            return [];
        var events = new List<WorldEvent>(world.Events);
        var eventDirectory = PathFor(id) + ".events";
        if (Directory.Exists(eventDirectory))
        {
            foreach (var path in Directory.GetFiles(eventDirectory, "*.json").Order())
            {
                if (!long.TryParse(Path.GetFileNameWithoutExtension(path), out var revision) || revision > world.Revision)
                    continue;
                var content = await File.ReadAllTextAsync(path, cancellationToken);
                events.AddRange(JsonSerializer.Deserialize<WorldEvent[]>(content, Wire.Json) ?? []);
            }
        }
        return events.Where(item => item.Seq > afterSequence && item.Seq <= world.Seq)
            .DistinctBy(item => item.Seq).OrderBy(item => item.Seq).Take(limit).ToArray();
    }

    public async Task SaveNotes(RunNotes notes, CancellationToken cancellationToken)
    {
        var existing = await LoadNotes(notes.RunId, cancellationToken);
        if (existing is not null && existing.ThroughSequence > notes.ThroughSequence)
            return;
        var path = PathFor(notes.RunId) + ".notes";
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(notes, Wire.Json), cancellationToken);
        File.Move(path + ".tmp", path, true);
    }

    public async Task<RunNotes?> LoadNotes(string id, CancellationToken cancellationToken)
    {
        var path = PathFor(id) + ".notes";
        return File.Exists(path) ? JsonSerializer.Deserialize<RunNotes>(await File.ReadAllTextAsync(path, cancellationToken), Wire.Json) : null;
    }

    public async Task<RunAuditHead?> ReadAuditHead(string runId, CancellationToken cancellationToken)
    {
        var path = PathFor(runId);
        if (!File.Exists(path))
            return null;
        // World writes its scalar metadata first. Read only this prefix, never the population per page.
        await using var stream = File.OpenRead(path);
        var bytes = new byte[64 * 1024];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken);
            count += read;
            if (AuditPersistence.ReadHead(bytes.AsSpan(0, count), count == stream.Length) is { } head)
                return head;
            if (read == 0)
                break;
        }
        throw new InvalidOperationException("Checkpoint metadata prefix is invalid");
    }

    public async Task<AuditRecord[]> ReadAudit(string runId, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 10000 || afterSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var path = PathFor(runId);
        if (!File.Exists(path))
            return [];
        var head = (await ReadAuditHead(runId, cancellationToken))!;
        var auditDirectory = path + ".audit";
        if (!Directory.Exists(auditDirectory))
            return [];
        var result = new List<AuditRecord>();
        var bytes = 0;
        foreach (var manifestPath in Directory.GetFiles(auditDirectory, "*.json").Order())
        {
            if (!long.TryParse(Path.GetFileNameWithoutExtension(manifestPath), out var revision) || revision > head.Revision)
                continue;
            var manifest = JsonSerializer.Deserialize<AuditManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), Wire.Json)!;
            foreach (var chunk in manifest.Chunks.Where(chunk => chunk.Last > afterSequence && chunk.First <= head.AuditSequence))
            {
                using var reader = new StreamReader(new GZipStream(File.OpenRead(Path.Combine(auditDirectory, chunk.File)), CompressionMode.Decompress));
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    var row = JsonSerializer.Deserialize<AuditRecord>(line, Wire.Json)!;
                    if (row.Sequence <= afterSequence || row.Sequence > head.AuditSequence)
                        continue;
                    bytes += Encoding.UTF8.GetByteCount(line);
                    if (bytes > 8_000_000 && result.Count > 0)
                        return result.ToArray();
                    result.Add(row);
                    if (result.Count == limit)
                        return result.ToArray();
                }
            }
        }
        return result.ToArray();
    }

    public async Task<JsonObject?> ReadAnalyticsReport(string runId, CancellationToken cancellationToken)
    {
        var path = PathFor(runId) + ".analytics-report";
        return File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))!.AsObject() : null;
    }

    public async Task SaveAnalyticsReport(string runId, JsonObject report, CancellationToken cancellationToken)
    {
        var path = PathFor(runId) + ".analytics-report";
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, report.ToJsonString(Wire.Json), cancellationToken);
        File.Move(temporary, path, true);
    }

    private string PathFor(string id)
    {
        if (id.Length > 100 || id.Any(character => !char.IsAsciiLetterOrDigit(character)
            && character is not ('-' or '_')))
            throw new ArgumentException("Invalid run ID");

        return Path.Combine(directory, id + ".json");
    }

    internal static bool SameCheckpoint(World? existing, World world)
    {
        return existing?.Revision == world.Revision
            && JsonNode.DeepEquals(Wire.Node(existing), Wire.Node(world));
    }
}

public sealed class NeonStore : IWorldStore, IRunAuditStore, IAnalyticsReportStore, IDisposable
{
    private const string SaveQuery = """
        WITH input AS (
          SELECT COALESCE($3::text,(SELECT string_agg(content,'' ORDER BY part)
            FROM faultline.checkpoint_parts WHERE run_id=$1 AND digest=$7)) AS serialized
        ), ready AS (
          SELECT serialized::jsonb AS state FROM input WHERE $7::text IS NULL OR
            ((SELECT count(*) FROM faultline.checkpoint_parts WHERE run_id=$1 AND digest=$7)=$8::int
              AND encode(sha256(convert_to(serialized,'UTF8')),'hex')=$7)
        ), saved AS (
          INSERT INTO faultline.stage1_runs(run_id,revision,state,checkpoint_digest,checkpoint_part_count)
          SELECT $1,$2::bigint,state,$7,$8::int FROM ready WHERE $2::bigint=$4::bigint+1 AND ($4::bigint = 0
            OR EXISTS (SELECT 1 FROM faultline.stage1_runs WHERE run_id=$1))
            AND ($5::text IS NULL OR (SELECT count(*) FROM faultline.run_audit
              WHERE run_id=$1 AND revision=$2::bigint AND batch_id=$5)=$6::bigint)
          ON CONFLICT (run_id) DO UPDATE SET revision=EXCLUDED.revision,state=EXCLUDED.state,updated_at=now(),
            checkpoint_digest=EXCLUDED.checkpoint_digest,checkpoint_part_count=EXCLUDED.checkpoint_part_count
            WHERE stage1_runs.revision=$4::bigint
          RETURNING run_id,state
        ), events AS (
          INSERT INTO faultline.stage1_events(run_id,seq,envelope)
          SELECT saved.run_id,(event->>'seq')::bigint,event FROM saved,
            jsonb_array_elements(saved.state->'events') event
          ON CONFLICT DO NOTHING RETURNING seq
        ), audit_committed AS (
          INSERT INTO faultline.run_audit_commits(run_id,revision,batch_id,through_sequence)
          SELECT run_id,$2::bigint,$5,(state->>'audit_sequence')::bigint FROM saved WHERE $5::text IS NOT NULL
          ON CONFLICT (run_id,revision) DO NOTHING RETURNING revision
        ) SELECT run_id FROM saved
        """;

    private readonly HttpClient http;
    private readonly string endpoint;
    private readonly string connectionString;

    public string Kind => "neon_https";
    public bool SupportsAudit => true;

    public NeonStore(string databaseUrl, HttpMessageHandler? handler = null)
    {
        var uri = new Uri(databaseUrl);
        if (uri.Scheme is not ("postgres" or "postgresql")
            || !uri.Host.EndsWith(".neon.tech", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("DATABASE_URL must be a Neon PostgreSQL connection string");

        connectionString = databaseUrl;
        endpoint = "https://api." + uri.Host[(uri.Host.IndexOf('.') + 1)..] + "/sql";
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task Initialize(CancellationToken cancellationToken)
    {
        await Query("CREATE SCHEMA IF NOT EXISTS faultline", [], cancellationToken);
        await Query("""
            CREATE TABLE IF NOT EXISTS faultline.stage1_runs (
                run_id text PRIMARY KEY,
                revision bigint NOT NULL,
                state jsonb NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now()
            )
            """, [], cancellationToken);
        await Query("""
            ALTER TABLE faultline.stage1_runs ADD COLUMN IF NOT EXISTS checkpoint_digest text,
              ADD COLUMN IF NOT EXISTS checkpoint_part_count integer
            """, [], cancellationToken);
        await Query("""
            CREATE TABLE IF NOT EXISTS faultline.checkpoint_parts (
              run_id text NOT NULL, digest text NOT NULL, part integer NOT NULL, content text NOT NULL,
              PRIMARY KEY(run_id,digest,part)
            )
            """, [], cancellationToken);
        await Query("""
            CREATE TABLE IF NOT EXISTS faultline.stage1_events (
                run_id text NOT NULL REFERENCES faultline.stage1_runs(run_id),
                seq bigint NOT NULL,
                envelope jsonb NOT NULL,
                PRIMARY KEY(run_id, seq)
            )
            """, [], cancellationToken);
        await Query("""
            CREATE TABLE IF NOT EXISTS faultline.run_audit (
                run_id text NOT NULL,
                revision bigint NOT NULL,
                batch_id text NOT NULL,
                sequence bigint NOT NULL,
                envelope jsonb NOT NULL,
                PRIMARY KEY(run_id,revision,batch_id,sequence)
            )
            """, [], cancellationToken);
        await Query("""
            CREATE TABLE IF NOT EXISTS faultline.run_audit_commits (
                run_id text NOT NULL REFERENCES faultline.stage1_runs(run_id),
                revision bigint NOT NULL,
                batch_id text NOT NULL,
                through_sequence bigint NOT NULL,
                PRIMARY KEY(run_id,revision)
            )
            """, [], cancellationToken);
        await Query("""
            CREATE INDEX IF NOT EXISTS run_audit_sequence_idx ON faultline.run_audit(run_id,sequence)
            """, [], cancellationToken);
        await Query("""
            CREATE TABLE IF NOT EXISTS faultline.analytics_reports (
                run_id text PRIMARY KEY REFERENCES faultline.stage1_runs(run_id),
                report jsonb NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now()
            )
            """, [], cancellationToken);
        await Query("""
            CREATE TABLE IF NOT EXISTS faultline.run_notes (
                run_id text PRIMARY KEY REFERENCES faultline.stage1_runs(run_id),
                through_seq bigint NOT NULL,
                notes jsonb NOT NULL
            )
            """, [], cancellationToken);
    }

    public async Task SaveNotes(RunNotes notes, CancellationToken cancellationToken)
    {
        await Query("""
            INSERT INTO faultline.run_notes(run_id,through_seq,notes)
            VALUES($1,$2::bigint,$3::jsonb)
            ON CONFLICT(run_id) DO UPDATE SET through_seq=EXCLUDED.through_seq,notes=EXCLUDED.notes
              WHERE run_notes.through_seq<=EXCLUDED.through_seq
            """, [notes.RunId, notes.ThroughSequence.ToString(), JsonSerializer.Serialize(notes, Wire.Json)], cancellationToken);
    }

    public async Task<RunNotes?> LoadNotes(string id, CancellationToken cancellationToken)
    {
        var rows = await Query("SELECT notes FROM faultline.run_notes WHERE run_id=$1", [id], cancellationToken);
        return rows.Count == 0 ? null : JsonSerializer.Deserialize<RunNotes>(rows[0]![0]!.GetValue<string>(), Wire.Json);
    }

    public async Task Save(World world, long expectedRevision, CancellationToken cancellationToken, string? serialized = null)
    {
        var state = serialized ?? JsonSerializer.Serialize(world, Wire.Json);
        if (!world.AnalyticsEnabled && Encoding.UTF8.GetByteCount(state) > 16 * 1024 * 1024)
            throw new InvalidOperationException("Legacy Neon checkpoint exceeds the application's 16 MiB request safety limit; enable analytics for bounded checkpoint transport");
        var heads = world.AnalyticsEnabled
            ? await Query("SELECT revision,state->>'audit_sequence' FROM faultline.stage1_runs WHERE run_id=$1", [world.Id], cancellationToken)
            : new JsonArray();
        var previousRevision = heads.Count == 0 ? 0 : long.Parse(heads[0]![0]!.GetValue<string>());
        var previousAuditSequence = heads.Count == 0 ? 0 : long.Parse(heads[0]![1]?.GetValue<string>() ?? "0");
        if (world.AnalyticsEnabled && previousRevision == world.Revision
            && LocalStore.SameCheckpoint(await Load(world.Id, cancellationToken), world))
        {
            if (world.PendingAudit.Count > 0)
            {
                var committed = await Query("SELECT batch_id FROM faultline.run_audit_commits WHERE run_id=$1 AND revision=$2::bigint",
                    [world.Id, world.Revision.ToString()], cancellationToken);
                if (committed.Count != 1 || committed[0]![0]!.GetValue<string>() != AuditPersistence.Digest(world))
                    throw new InvalidOperationException("Conflicting audit retry");
            }
            world.PendingAudit.Clear();
            return;
        }
        if (world.AnalyticsEnabled)
        {
            if (previousRevision != expectedRevision || world.Revision != expectedRevision + 1)
                throw new InvalidOperationException("Revision conflict");
            AuditPersistence.Validate(world, previousAuditSequence);
        }
        var digest = world.PendingAudit.Count == 0 ? null : AuditPersistence.Digest(world);
        foreach (var batch in AuditPersistence.Batches(world))
            await Query("""
                INSERT INTO faultline.run_audit(run_id,revision,batch_id,sequence,envelope)
                SELECT $1,$2::bigint,$3,(item->>'sequence')::bigint,item
                FROM jsonb_array_elements($4::jsonb) item
                ON CONFLICT (run_id,revision,batch_id,sequence) DO NOTHING
                """, [world.Id, world.Revision.ToString(), digest, JsonSerializer.Serialize(batch, Wire.Json)], cancellationToken);
        string? checkpointDigest = null;
        var partCount = 0;
        if (world.AnalyticsEnabled)
        {
            checkpointDigest = AuditPersistence.CheckpointDigest(state);
            foreach (var part in AuditPersistence.CheckpointParts(state))
            {
                await Query("""
                    INSERT INTO faultline.checkpoint_parts(run_id,digest,part,content) VALUES($1,$2,$3::int,$4)
                    ON CONFLICT(run_id,digest,part) DO NOTHING
                    """, [world.Id, checkpointDigest, partCount.ToString(), part], cancellationToken);
                partCount++;
            }
        }
        string?[] parameters =
        [world.Id, world.Revision.ToString(), checkpointDigest is null ? state : null,
            expectedRevision.ToString(), digest, world.PendingAudit.Count.ToString(), checkpointDigest, partCount.ToString()];

        var rows = await Query(SaveQuery, parameters, cancellationToken);
        if (rows.Count == 1)
        {
            world.PendingAudit.Clear();
            return;
        }

        var existing = await Load(world.Id, cancellationToken);
        if (!LocalStore.SameCheckpoint(existing, world))
            throw new InvalidOperationException("Revision conflict");
        if (digest is not null)
        {
            var committed = await Query("SELECT batch_id FROM faultline.run_audit_commits WHERE run_id=$1 AND revision=$2::bigint",
                [world.Id, world.Revision.ToString()], cancellationToken);
            if (committed.Count != 1 || committed[0]![0]!.GetValue<string>() != digest)
                throw new InvalidOperationException("Conflicting audit retry");
        }
        world.PendingAudit.Clear();
    }

    public async Task<World?> Load(string id, CancellationToken cancellationToken)
    {
        var header = await Query("SELECT checkpoint_digest,checkpoint_part_count FROM faultline.stage1_runs WHERE run_id=$1", [id], cancellationToken);
        if (header.Count == 0)
            return null;
        if (header[0]![0] is null)
        {
            var rows = await Query("SELECT state FROM faultline.stage1_runs WHERE run_id=$1", [id], cancellationToken);
            return rows.Count == 0 ? null : JsonSerializer.Deserialize<World>(rows[0]![0]!.GetValue<string>(), Wire.Json);
        }
        var digest = header[0]![0]!.GetValue<string>();
        var count = int.Parse(header[0]![1]!.GetValue<string>());
        var serialized = new StringBuilder();
        for (var next = 0; next < count;)
        {
            var parts = await Query("SELECT part,content FROM faultline.checkpoint_parts WHERE run_id=$1 AND digest=$2 AND part>=$3::int ORDER BY part LIMIT 16",
                [id, digest, next.ToString()], cancellationToken);
            if (parts.Count == 0)
                throw new InvalidOperationException("Committed checkpoint parts are missing");
            foreach (var row in parts)
            {
                if (int.Parse(row![0]!.GetValue<string>()) != next || next >= count)
                    throw new InvalidOperationException("Committed checkpoint part sequence is invalid");
                serialized.Append(row[1]!.GetValue<string>());
                next++;
            }
        }
        var state = serialized.ToString();
        if (AuditPersistence.CheckpointDigest(state) != digest)
            throw new InvalidOperationException("Committed checkpoint digest verification failed");
        return JsonSerializer.Deserialize<World>(state, Wire.Json);
    }

    public async Task<string[]> List(CancellationToken cancellationToken)
    {
        var rows = await Query("SELECT run_id FROM faultline.stage1_runs ORDER BY updated_at DESC LIMIT 100",
            [], cancellationToken);

        return rows.Select(row => row![0]!.GetValue<string>()).ToArray();
    }

    public async Task<WorldEvent[]> ReadEvents(string id, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        var rows = await Query("SELECT envelope FROM faultline.stage1_events WHERE run_id=$1 AND seq>$2::bigint ORDER BY seq LIMIT $3::int",
            [id, afterSequence.ToString(), limit.ToString()], cancellationToken);
        return rows.Select(row => JsonSerializer.Deserialize<WorldEvent>(row![0]!.GetValue<string>(), Wire.Json)!).ToArray();
    }

    public async Task<RunAuditHead?> ReadAuditHead(string runId, CancellationToken cancellationToken)
    {
        var rows = await Query("SELECT revision,state->>'audit_sequence',state->>'status',state->>'analytics_enabled' FROM faultline.stage1_runs WHERE run_id=$1",
            [runId], cancellationToken);
        return rows.Count == 0 ? null : new RunAuditHead(runId, long.Parse(rows[0]![0]!.GetValue<string>()),
            long.Parse(rows[0]![1]?.GetValue<string>() ?? "0"), rows[0]![2]!.GetValue<string>(),
            rows[0]![3]?.GetValue<string>() == "true");
    }

    public async Task<AuditRecord[]> ReadAudit(string runId, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 10000 || afterSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var rows = await Query("""
            WITH candidate AS (
              SELECT a.sequence,a.envelope FROM faultline.run_audit a
              JOIN faultline.run_audit_commits c ON c.run_id=a.run_id AND c.revision=a.revision AND c.batch_id=a.batch_id
              JOIN faultline.stage1_runs r ON r.run_id=a.run_id
              WHERE a.run_id=$1 AND a.sequence>$2::bigint AND a.revision<=r.revision
                AND a.sequence<=(r.state->>'audit_sequence')::bigint
              ORDER BY a.sequence LIMIT $3::int
            ), sized AS (
              SELECT *,sum(octet_length(envelope::text)) OVER (ORDER BY sequence) AS bytes,
                row_number() OVER (ORDER BY sequence) AS position FROM candidate
            ) SELECT envelope FROM sized WHERE bytes<=8000000 OR position=1 ORDER BY sequence
            """, [runId, afterSequence.ToString(), limit.ToString()], cancellationToken);
        return rows.Select(row => JsonSerializer.Deserialize<AuditRecord>(row![0]!.GetValue<string>(), Wire.Json)!).ToArray();
    }

    public async Task<JsonObject?> ReadAnalyticsReport(string runId, CancellationToken cancellationToken)
    {
        var rows = await Query("SELECT report FROM faultline.analytics_reports WHERE run_id=$1", [runId], cancellationToken);
        return rows.Count == 0 ? null : JsonNode.Parse(rows[0]![0]!.GetValue<string>())!.AsObject();
    }

    public async Task SaveAnalyticsReport(string runId, JsonObject report, CancellationToken cancellationToken)
    {
        await Query("""
            INSERT INTO faultline.analytics_reports(run_id,report) VALUES($1,$2::jsonb)
            ON CONFLICT(run_id) DO UPDATE SET report=EXCLUDED.report,updated_at=now()
            """, [runId, report.ToJsonString(Wire.Json)], cancellationToken);
    }

    private async Task<JsonArray> Query(string sql, string?[] parameters, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return await SendQuery(sql, parameters, cancellationToken);
            }
            catch (Exception exception) when (IsTransient(exception) && !cancellationToken.IsCancellationRequested)
            {
                if (attempt == 3)
                    throw new InvalidOperationException("Neon transport failed after three attempts");

                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException("Neon query failed");
    }

    private async Task<JsonArray> SendQuery(string sql, string?[] parameters, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("Neon-Connection-String", connectionString);
        request.Headers.Add("Neon-Raw-Text-Output", "true");
        request.Headers.Add("Neon-Array-Mode", "true");
        request.Content = JsonContent.Create(new { query = sql, @params = parameters });

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            throw new HttpRequestException("Transient Neon response");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Neon query failed (HTTP {(int)response.StatusCode}); details suppressed");

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return body!["rows"]!.AsArray();
    }

    private static bool IsTransient(Exception exception)
    {
        return exception is HttpRequestException or TaskCanceledException;
    }

    public void Dispose()
    {
        http.Dispose();
    }
}

internal static class AuditPersistence
{
    public static RunAuditHead? ReadHead(ReadOnlySpan<byte> bytes, bool final)
    {
        var reader = new Utf8JsonReader(bytes, final, default);
        string? id = null, status = null, property = null;
        long revision = 0, sequence = 0;
        bool enabled = false, hasRevision = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                property = reader.GetString();
                if (property == "scheduler_cursor" && id is not null && status is not null && hasRevision)
                    return new(id, revision, sequence, status, enabled); // also handles legacy checkpoints
            }
            else if (reader.CurrentDepth == 1)
            {
                switch (property)
                {
                    case "id": id = reader.GetString(); break;
                    case "status": status = reader.GetString(); break;
                    case "revision": revision = reader.GetInt64(); hasRevision = true; break;
                    case "audit_sequence": sequence = reader.GetInt64(); break;
                    case "analytics_enabled": enabled = reader.GetBoolean(); break;
                }
                property = null;
            }
        }
        return null;
    }

    // A UTF-16 slice of 128K characters occupies at most 384KiB of valid UTF-8.
    // Do not split surrogate pairs; the reconstructed string is byte-identical to the original JSON.
    public static IEnumerable<string> CheckpointParts(string serialized)
    {
        for (var offset = 0; offset < serialized.Length;)
        {
            var length = Math.Min(128 * 1024, serialized.Length - offset);
            if (offset + length < serialized.Length && char.IsHighSurrogate(serialized[offset + length - 1]))
                length--;
            yield return serialized.Substring(offset, length);
            offset += length;
        }
    }

    public static string CheckpointDigest(string serialized)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in CheckpointParts(serialized))
            hash.AppendData(Encoding.UTF8.GetBytes(part));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static void Validate(World world, long previousSequence)
    {
        if (world.AuditSequence - previousSequence != world.PendingAudit.Count
            || world.PendingAudit.Where((row, index) => row.RunId != world.Id
                || row.Sequence != previousSequence + index + 1).Any())
            throw new InvalidOperationException("Audit buffer does not cover checkpoint cursor; refusing to lose records");
    }

    public static string Digest(World world)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var row in world.PendingAudit)
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row with { Revision = world.Revision }, Wire.Json));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // Both record and byte limits keep individual Neon requests bounded, even with full prompt contexts.
    public static IEnumerable<AuditRecord[]> Batches(World world)
    {
        var rows = new List<AuditRecord>();
        var bytes = 0;
        foreach (var pending in world.PendingAudit)
        {
            var row = pending with { Revision = world.Revision };
            var size = JsonSerializer.SerializeToUtf8Bytes(row, Wire.Json).Length;
            if (size > 2_000_000)
                throw new InvalidOperationException("One audit record exceeds the 2 MB capture limit; simulation paused without committing this revision");
            if (rows.Count > 0 && (rows.Count >= 1000 || bytes + size > 2_000_000))
            {
                yield return rows.ToArray();
                rows.Clear();
                bytes = 0;
            }
            rows.Add(row);
            bytes += size;
        }
        if (rows.Count > 0)
            yield return rows.ToArray();
    }
}
