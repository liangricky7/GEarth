using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Faultline.Core;

public interface IWorldStore
{
    string Kind { get; }
    Task Initialize(CancellationToken cancellationToken);
    Task Save(World world, long expectedRevision, CancellationToken cancellationToken);
    Task<World?> Load(string id, CancellationToken cancellationToken);
    Task<string[]> List(CancellationToken cancellationToken);
}

public sealed class LocalStore(string directory) : IWorldStore
{
    public string Kind => "local";

    public Task Initialize(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        return Task.CompletedTask;
    }

    public async Task Save(World world, long expectedRevision, CancellationToken cancellationToken)
    {
        var path = PathFor(world.Id);
        using var writeLock = new FileStream(path + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);

        var existing = await Load(world.Id, cancellationToken);
        if (SameCheckpoint(existing, world))
            return;
        if ((existing?.Revision ?? 0) != expectedRevision)
            throw new InvalidOperationException("Revision conflict");

        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(world, Wire.Json), cancellationToken);
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
    }

    public async Task Save(World world, long expectedRevision, CancellationToken cancellationToken)
    {
        string?[] parameters =
        [
            world.Id,
            world.Revision.ToString(),
            JsonSerializer.Serialize(world, Wire.Json),
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
