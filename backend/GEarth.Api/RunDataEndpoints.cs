namespace GEarth.Api;

public static class RunDataEndpoints
{
    public static void MapRunData(this WebApplication app)
    {
        app.MapGet("/api/data/runs", (int? limit, string? cursor, RunDataService data, CancellationToken ct) => Respond(() => data.Catalog(limit ?? 50, cursor, ct)));
        app.MapGet("/api/runs/{id}/data/manifest", (string id, RunDataService data, CancellationToken ct) => Respond(() => data.Manifest(id, ct)));
        app.MapGet("/api/runs/{id}/data/checkpoints", (string id, long? after_revision, int? limit, RunDataService data, CancellationToken ct) => Respond(() => data.Checkpoints(id, after_revision ?? 0, limit ?? 25, ct)));
        app.MapGet("/api/runs/{id}/data/records", (string id, long? through_revision, long? through_sequence, long? after,
            int? limit, int? from_tick, int? to_tick, string? kind, RunDataService data, CancellationToken ct) =>
            Respond(() => data.Records(id, through_revision, through_sequence, after ?? 0, limit ?? 100, from_tick, to_tick, kind, ct)));
        app.MapGet("/api/runs/{id}/analytics/datasets", (string id, RunDataService data, CancellationToken ct) => Respond(() => data.Datasets(id, ct)));
        app.MapGet("/api/runs/{id}/analytics/datasets/{datasetId}/rows", (string id, string datasetId, string? cursor,
            int? limit, RunDataService data, CancellationToken ct) => Respond(() => data.Rows(id, datasetId, cursor, limit ?? 100, ct)));
    }
    private static async Task<IResult> Respond(Func<Task<object>> action)
    {
        try { return Results.Ok(await action()); }
        catch (AnalyticsException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException or FormatException or OverflowException)
        { return Results.Json(new { error = "Run-data source is unavailable or inconsistent." }, statusCode: 503); }
    }
}
