using GEarth.Core;

namespace GEarth.Api;

public static class RendererEndpoints
{
    public static void MapRenderer(this WebApplication app)
    {
        var group = app.MapGroup("/api/runs/{id}/renderer").RequireCors("RendererReadOnly");
        group.MapGet("/manifest", async (string id, IWorldStore store, CancellationToken ct) =>
        {
            var world = await store.Load(id, ct);
            if (world is null) return Results.NotFound();
            if (store is not IRendererStore replay || world.Society is null) return Results.StatusCode(409);
            var first = (await replay.ReadRendererFrames(id, 0, 1, ct)).FirstOrDefault();
            return Results.Ok(new
            {
                schema_version = RendererReplay.Contract, run_id = id, world.Status, world.Seed,
                checkpoint_tick = world.Tick, checkpoint_revision = world.Revision,
                world.RendererCaptureEnabled, step_days = SocietyLabor.DaysPerWeek,
                first_available_tick = first?.Tick, captured_from_opening = first?.Tick == 0,
                frames_url = $"/api/runs/{Uri.EscapeDataString(id)}/renderer/frames",
                events_url = $"/api/runs/{Uri.EscapeDataString(id)}/renderer/events",
                current_url = $"/api/runs/{Uri.EscapeDataString(id)}/renderer/current",
                frame_url_template = $"/api/runs/{Uri.EscapeDataString(id)}/renderer/frames/{{revision}}"
            });
        });
        group.MapGet("/frames", async (string id, long? after_revision, int? limit, IWorldStore store, CancellationToken ct) =>
        {
            if (store is not IRendererStore replay) return Results.StatusCode(409);
            if (await store.Load(id, ct) is null) return Results.NotFound();
            var count = Math.Clamp(limit ?? 20, 1, 50);
            var heads = await replay.ReadRendererFrames(id, Math.Max(0, after_revision ?? 0), count + 1, ct);
            var page = heads.Take(count).ToArray();
            return Results.Ok(new
            {
                schema_version = RendererReplay.Contract, run_id = id, frames = page,
                next_after_revision = page.LastOrDefault()?.Revision ?? Math.Max(0, after_revision ?? 0),
                has_more = heads.Length > count
            });
        });
        group.MapGet("/frames/{revision:long}", async (string id, long revision, IWorldStore store, CancellationToken ct) =>
        {
            if (revision <= 0) return Results.BadRequest();
            if (store is not IRendererStore replay) return Results.StatusCode(409);
            var world = await replay.LoadRendererFrame(id, revision, ct);
            return world is null ? Results.NotFound() : Results.Ok(RendererReplay.Frame(world));
        });
        group.MapGet("/current", async (string id, IWorldStore store, CancellationToken ct) =>
        {
            var world = await store.Load(id, ct);
            return world?.Society is null ? Results.NotFound() : Results.Ok(RendererReplay.Frame(world));
        });
        group.MapGet("/events", async (string id, long? after_seq, long? through_seq, int? limit,
            IWorldStore store, CancellationToken ct) =>
        {
            var world = await store.Load(id, ct);
            if (world is null) return Results.NotFound();
            var through = Math.Clamp(through_seq ?? world.Seq, 0, world.Seq);
            var after = Math.Max(0, after_seq ?? 0);
            var events = (await store.ReadEvents(id, after, Math.Clamp(limit ?? 500, 1, 500), ct))
                .TakeWhile(item => item.Seq <= through).ToArray();
            return Results.Ok(new
            {
                schema_version = RendererReplay.Contract, run_id = id, through_event_sequence = through,
                events, next_after_seq = events.LastOrDefault()?.Seq ?? after,
                has_more = events.Length > 0 && events[^1].Seq < through
            });
        });
    }
}
