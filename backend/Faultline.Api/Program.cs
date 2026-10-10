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

    var events = world.Events
        .Where(item => item.Seq > (after_seq ?? 0))
        .Take(Math.Clamp(limit ?? 100, 1, 500));

    return Results.Ok(events);
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
        decisions = world.Decisions.Values.Where(item => item.Job.AgentId == agentId).TakeLast(20),
        observer_only = true
    });
});

app.Run();
