namespace Faultline.Api;

public static class AnalyticsEndpoints
{
    public static void MapAnalytics(this WebApplication app)
    {
        app.MapGet("/api/runs/{id}/analytics/export-metadata", (string id, AnalyticsService analytics, CancellationToken ct) =>
            Respond(() => analytics.ExportMetadata(id, ct)));
        app.MapGet("/api/runs/{id}/analytics/audit", (string id, long? through_revision, long? through_sequence,
            long? after, int? limit, AnalyticsService analytics, CancellationToken ct) =>
            Respond(() => analytics.AuditPage(id, through_revision, through_sequence, after ?? 0, limit ?? 500, ct)));
        app.MapGet("/api/runs/{id}/analytics/report", (string id, AnalyticsService analytics, CancellationToken ct) =>
            Respond(async () => await analytics.ReadReport(id, ct)));
    }

    private static async Task<IResult> Respond(Func<Task<object>> action)
    {
        try { return Results.Ok(await action()); }
        catch (AnalyticsException error) { return Results.Json(new { error = error.Message }, statusCode: error.StatusCode); }
    }
}
