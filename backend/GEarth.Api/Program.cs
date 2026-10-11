using System.Text.Json;
using System.Text.Json.Nodes;
using GEarth.Api;
using GEarth.Core;

IWorldStore CreateStore() => Environment.GetEnvironmentVariable("GEARTH_STORAGE") == "neon"
    ? new NeonStore(Environment.GetEnvironmentVariable("DATABASE_URL")
        ?? throw new InvalidOperationException("DATABASE_URL missing"))
    : new LocalStore(Path.GetFullPath(Environment.GetEnvironmentVariable("GEARTH_RUNS_DIRECTORY") ?? ".data/runs"));

// Operator command exits before starting the observer or any simulation/inference services.
if (args.Contains("--publish-analytics-report"))
{
    try
    {
        var reportIndex = Array.IndexOf(args, "--publish-analytics-report");
        var manifestIndex = Array.IndexOf(args, "--source-manifest");
        if (reportIndex + 1 >= args.Length || manifestIndex < 0 || manifestIndex + 1 >= args.Length)
            throw new ArgumentException("Usage: --publish-analytics-report REPORT.json --source-manifest MANIFEST.json");
        var reportPath = Path.GetFullPath(args[reportIndex + 1]);
        var manifestPath = Path.GetFullPath(args[manifestIndex + 1]);
        if (new FileInfo(reportPath).Length > AnalyticsService.MaximumReportBytes)
            throw new ArgumentException("Analytics report exceeds the 8 MiB limit.");
        if (new FileInfo(manifestPath).Length > AnalyticsService.MaximumManifestBytes)
            throw new ArgumentException("Source manifest exceeds the 16 MiB limit.");
        var report = JsonNode.Parse(await File.ReadAllBytesAsync(reportPath)) as JsonObject
            ?? throw new ArgumentException("Report must be a JSON object.");
        var publicationStore = CreateStore();
        await publicationStore.Initialize(CancellationToken.None);
        var browseIndex = Array.IndexOf(args, "--analytics-browse");
        if (browseIndex >= 0 && browseIndex + 1 >= args.Length) throw new ArgumentException("--analytics-browse requires a bundle directory.");
        await new AnalyticsService(publicationStore, AnalyticsBrowseStore.FromEnvironment()).Publish(report,
            await File.ReadAllBytesAsync(manifestPath), CancellationToken.None,
            browseIndex < 0 ? null : Path.GetFullPath(args[browseIndex + 1]));
        Console.WriteLine($"Published cached analytics report for {report["run_id"]}.");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Analytics publication failed: {(error is AnalyticsException or ArgumentException ? error.Message : error.GetType().Name)}");
        Environment.ExitCode = 1;
    }
    return;
}

var builder = WebApplication.CreateBuilder(args);
var port = int.TryParse(Environment.GetEnvironmentVariable("GEARTH_PORT"), out var configuredPort)
    && configuredPort is >= 1024 and <= 65535 ? configuredPort : 5080;
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddCors(options => options.AddPolicy("RendererReadOnly", policy =>
    policy.AllowAnyOrigin().WithMethods("GET").AllowAnyHeader()));

IWorldStore store = CreateStore();

builder.Services.AddSingleton(store);
builder.Services.AddSingleton<RunNotesService>();
builder.Services.AddSingleton(AnalyticsBrowseStore.FromEnvironment());
builder.Services.AddSingleton<AnalyticsService>();
builder.Services.AddSingleton<RunDataService>();
builder.Services.AddSingleton<Coordinator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<Coordinator>());

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapAnalytics();
app.MapRunData();
app.MapRenderer();

app.MapGet("/api/health", (Coordinator coordinator) => Results.Ok(new
{
    status = coordinator.Error is not null ? "paused" : coordinator.Current is null ? "starting" : "ok",
    coordinator.Error,
    storage = store.Kind,
    run_id = coordinator.Current?.Id,
    tick = coordinator.Current?.Tick,
    run_status = coordinator.Current?.Status,
    updated_at = coordinator.Current?.UpdatedAt
}));

app.MapGet("/api/runs", async (Coordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(new
    {
        current_run_id = coordinator.Current?.Id,
        run_ids = await store.List(cancellationToken)
    }));

async Task<World?> ReadWorld(string id, Coordinator coordinator, CancellationToken cancellationToken)
{
    if (coordinator.Current?.Id == id)
        return coordinator.Current;

    return await store.Load(id, cancellationToken);
}

app.MapGet("/api/runs/{id}/snapshot", async (string id, Coordinator coordinator, CancellationToken cancellationToken) =>
{
    var world = await ReadWorld(id, coordinator, cancellationToken);
    return world is null ? Results.NotFound() : Results.Ok(world.Snapshot());
});

app.MapGet("/api/runs/{id}/events", async (string id, long? after_seq, long? through_seq, int? limit,
    Coordinator coordinator, CancellationToken cancellationToken) =>
{
    var world = await ReadWorld(id, coordinator, cancellationToken);
    if (world is null)
        return Results.NotFound();

    var after = Math.Max(0, after_seq ?? 0);
    var through = Math.Clamp(through_seq ?? world.Seq, 0, world.Seq);
    var count = Math.Clamp(limit ?? 100, 1, 500);
    var events = world.Events.Count > 0 && after >= world.Events[0].Seq - 1
        ? world.Events.Where(item => item.Seq > after).Take(count).ToArray()
        : await store.ReadEvents(id, after, count, cancellationToken);

    return Results.Ok(events.Where(item => item.Seq <= through));
});

app.MapGet("/api/runs/{id}/agents", async (string id, string? country, string? role, string? q, int? offset, int? limit,
    Coordinator coordinator, CancellationToken cancellationToken) =>
{
    var world = await ReadWorld(id, coordinator, cancellationToken);
    if (world is null)
        return Results.NotFound();
    var matches = world.Agents.Values.Where(agent => (string.IsNullOrEmpty(country) || agent.CountryId == country)
        && (string.IsNullOrEmpty(role) || agent.Role == role)
        && (string.IsNullOrEmpty(q) || agent.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
            || agent.Occupation.Contains(q, StringComparison.OrdinalIgnoreCase))).OrderBy(agent => agent.Id).ToArray();
    var start = Math.Max(0, offset ?? 0);
    var count = Math.Clamp(limit ?? 50, 1, 100);
    return Results.Ok(new
    {
        total = matches.Length,
        offset = start,
        limit = count,
        world.Revision,
        world.Seq,
        world.UpdatedAt,
        agents = matches.Skip(start).Take(count).Select(agent => SocietyProjection.AgentSummary(world, agent))
    });
});

app.MapGet("/api/runs/{id}/agents/{agentId}", async (string id, string agentId,
    Coordinator coordinator, CancellationToken cancellationToken) =>
{
    var world = await ReadWorld(id, coordinator, cancellationToken);
    if (world is null || !world.Agents.TryGetValue(agentId, out var agent))
        return Results.NotFound();

    return Results.Ok(new
    {
        agent,
        world.Revision,
        world.Seq,
        world.UpdatedAt,
        model_decision_age_seconds = SocietyProjection.ModelDecisionAgeSeconds(world, agent),
        last_decision = agent.LastDecision,
        decisions = world.Decisions.Values.Where(item => item.Job.AgentId == agentId).TakeLast(20),
        observer_only = true
    });
});

app.MapGet("/api/runs/{id}/notes", async (string id, Coordinator coordinator, RunNotesService notes,
    CancellationToken cancellationToken) =>
{
    var world = await ReadWorld(id, coordinator, cancellationToken);
    return world is null ? Results.NotFound() : Results.Ok(await notes.Record(world, coordinator.Current?.Id == id && coordinator.Error is not null ? "paused" : null, cancellationToken));
});

app.MapGet("/api/runs/{id}/notes.md", async (string id, Coordinator coordinator, RunNotesService notes,
    CancellationToken cancellationToken) =>
{
    var world = await ReadWorld(id, coordinator, cancellationToken);
    if (world is null)
        return Results.NotFound();
    var report = await notes.Record(world, coordinator.Current?.Id == id && coordinator.Error is not null ? "paused" : null, cancellationToken);
    return Results.File(System.Text.Encoding.UTF8.GetBytes(report.Markdown), "text/markdown; charset=utf-8", id + "-observations.md");
});

// Verbose runs only: the gzipped day-by-day history (header, one line per day, end line). Complete once the run ends.
app.MapGet("/api/runs/{id}/history", (string id, HttpContext context) =>
{
    string path;
    try { path = HistoryWriter.PathFor(id); }
    catch (ArgumentException) { return Results.BadRequest(); }
    if (!File.Exists(path))
        return Results.NotFound();
    context.Response.Headers.ContentEncoding = "gzip";
    return Results.Stream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
        "application/x-ndjson", id + "-history.ndjson");
});

app.Run();
