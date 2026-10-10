using System.Net;
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

public sealed class LocalStore(string directory) : IWorldStore
{
    public string Kind => "local";

    public Task Initialize(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        return Task.CompletedTask;
    }

    private sealed record CheckpointHead(long Revision, long Seq);

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
            return;
        if ((existing?.Revision ?? 0) != expectedRevision)
            throw new InvalidOperationException("Revision conflict");

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

public sealed class NeonStore : IWorldStore, IDisposable
{
    private const string SaveQuery = """
        WITH saved AS (
          INSERT INTO faultline.stage1_runs(run_id,revision,state)
          SELECT $1,$2::bigint,$3::jsonb WHERE $4::bigint = 0
            OR EXISTS (SELECT 1 FROM faultline.stage1_runs WHERE run_id=$1)
          ON CONFLICT (run_id) DO UPDATE SET revision=EXCLUDED.revision,state=EXCLUDED.state,updated_at=now()
            WHERE stage1_runs.revision=$4::bigint
          RETURNING run_id
        ), events AS (
          INSERT INTO faultline.stage1_events(run_id,seq,envelope)
          SELECT saved.run_id,(event->>'seq')::bigint,event FROM saved,
            jsonb_array_elements(($3::jsonb)->'events') event
          ON CONFLICT DO NOTHING RETURNING seq
        ) SELECT run_id FROM saved
        """;

    private readonly HttpClient http;
    private readonly string endpoint;
    private readonly string connectionString;

    public string Kind => "neon_https";

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
            CREATE TABLE IF NOT EXISTS faultline.stage1_events (
                run_id text NOT NULL REFERENCES faultline.stage1_runs(run_id),
                seq bigint NOT NULL,
                envelope jsonb NOT NULL,
                PRIMARY KEY(run_id, seq)
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
        string?[] parameters =
        [
            world.Id,
            world.Revision.ToString(),
            serialized ?? JsonSerializer.Serialize(world, Wire.Json),
            expectedRevision.ToString()
        ];

        var rows = await Query(SaveQuery, parameters, cancellationToken);
        if (rows.Count == 1)
            return;

        var existing = await Load(world.Id, cancellationToken);
        if (!LocalStore.SameCheckpoint(existing, world))
            throw new InvalidOperationException("Revision conflict");
    }

    public async Task<World?> Load(string id, CancellationToken cancellationToken)
    {
        var rows = await Query("SELECT state FROM faultline.stage1_runs WHERE run_id=$1", [id], cancellationToken);
        if (rows.Count == 0)
            return null;

        return JsonSerializer.Deserialize<World>(rows[0]![0]!.GetValue<string>(), Wire.Json);
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
