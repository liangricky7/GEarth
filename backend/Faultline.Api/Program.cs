using System.Text.Json;
using Faultline.Api;
using Faultline.Core;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

IWorldStore store = Environment.GetEnvironmentVariable("FAULTLINE_STORAGE") == "neon"
    ? new NeonStore(Environment.GetEnvironmentVariable("DATABASE_URL")
        ?? throw new InvalidOperationException("DATABASE_URL missing"))
    : new LocalStore(Path.GetFullPath(".data/runs"));

builder.Services.AddSingleton(store);
builder.Services.AddSingleton<RunNotesService>();
builder.Services.AddSingleton<Coordinator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<Coordinator>());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

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

app.MapGet("/api/runs/{id}/events", async (string id, long? after_seq, int? limit,
    Coordinator coordinator, CancellationToken cancellationToken) =>
{
    var world = await ReadWorld(id, coordinator, cancellationToken);
    if (world is null)
        return Results.NotFound();

    var after = Math.Max(0, after_seq ?? 0);
    var count = Math.Clamp(limit ?? 100, 1, 500);
    var events = world.Events.Count > 0 && after >= world.Events[0].Seq - 1
        ? world.Events.Where(item => item.Seq > after).Take(count).ToArray()
        : await store.ReadEvents(id, after, count, cancellationToken);

    return Results.Ok(events);
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
        agents = matches.Skip(start).Take(count).Select(agent => new
        { agent.Id, agent.Role, agent.CountryId, agent.Occupation, agent.CashMinor, agent.ModelDecisions, agent.LastAttemptAt, agent.LastModelDecisionAt,
          decision_maker = SocietyLabor.DecidesForItself(agent) })
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

app.Run();
