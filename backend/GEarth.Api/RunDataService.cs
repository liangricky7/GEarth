using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Api;

public sealed class RunDataService(IWorldStore store, AnalyticsService analytics, AnalyticsBrowseStore browse)
{
    private const int ScanLimit = 500;
    private readonly ConcurrentDictionary<string, (long Revision, RunDataHead Head, JsonObject? Metadata)> cached = new();
    private IRunDataStore Data => store as IRunDataStore ?? throw new AnalyticsException(409, "Run-data storage is unavailable.");
    private static void Id(string id) { try { RunDataIO.ValidateId(id); } catch (ArgumentException) { throw new AnalyticsException(400, "Invalid run ID."); } }
    private async Task<(RunDataHead Head, JsonObject? Metadata)> Detail(string id, CancellationToken ct)
    {
        Id(id);
        var scalar = store is IRunAuditStore audit ? await audit.ReadAuditHead(id, ct) : null;
        if (scalar is null) throw new AnalyticsException(404, "Run not found.");
        if (cached.TryGetValue(id, out var prior) && prior.Revision == scalar.Revision) return (prior.Head, prior.Metadata?.DeepClone().AsObject());
        var head = await Data.ReadRunDataHead(id, ct) ?? throw new AnalyticsException(404, "Run not found.");
        if (head.RunId != id) throw new AnalyticsException(409, "Committed run identity mismatch.");
        var first = head.AuditSequence > 0 ? (await Data.ReadPinnedAudit(id, head.Revision, head.AuditSequence, 0, 1, ct)).FirstOrDefault() : null;
        if (first is not null && (first.RunId != id || first.Sequence != 1 || first.Revision > head.Revision))
            throw new AnalyticsException(409, "Committed metadata identity mismatch.");
        var metadata = first?.Kind == "run_metadata" ? first.Payload.DeepClone().AsObject() : null;
        if (cached.Count > 256) cached.Clear();
        cached[id] = (head.Revision, head, metadata);
        return (head, metadata?.DeepClone().AsObject());
    }
    private static string EncodeId(string id) => Convert.ToBase64String(Encoding.UTF8.GetBytes(id)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string DecodeId(string cursor)
    {
        try
        {
            if (cursor.Length > 160) throw new FormatException();
            var value = cursor.Replace('-', '+').Replace('_', '/'); value += new string('=', (4 - value.Length % 4) % 4);
            var id = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value)); Id(id); return id;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { throw new AnalyticsException(400, "Invalid run catalog cursor."); }
    }
    public async Task<object> Catalog(int limit, string? cursor, CancellationToken ct)
    {
        if (limit is < 1 or > 100) throw new AnalyticsException(400, "limit must be between 1 and 100.");
        var rows = await Data.ReadRunCatalog(cursor is null ? null : DecodeId(cursor), limit + 1, ct);
        var hasMore = rows.Length > limit; var selected = rows.Take(limit).ToArray();
        return new { schema_version = "gearth.run-catalog.v1", storage = store.Kind,
            runs = selected.Select(h => new { run_id = h.RunId, revision = h.Revision, status = h.Status,
                analytics_enabled = h.AnalyticsEnabled, audit_sequence = h.AuditSequence, updated_at = h.UpdatedAt }),
            next_cursor = hasMore ? EncodeId(selected[^1].RunId) : null, has_more = hasMore };
    }
    public async Task<object> Manifest(string id, CancellationToken ct)
    {
        var (head, metadata) = await Detail(id, ct);
        var auditStatus = head.AnalyticsEnabled == false && head.AuditSequence == 0 ? "not_captured"
            : head.AuditSequence == 0 ? "unknown" : metadata is null ? "unavailable"
            : head.AnalyticsEnabled == true && head.AuditStartTick == 0 && head.AuditOpeningBalancesCaptured == true
                && head.AuditDailyThroughTick == head.Tick ? "available" : "partial";
        var datasetStatus = head.Status is not ("completed" or "stopped" or "failed") ? "awaiting_finished_run"
            : auditStatus == "not_captured" ? "not_captured" : "not_published";
        JsonObject? catalog = null; string? failure = null; var verifiedReport = false;
        if (datasetStatus == "not_published")
        {
            try
            {
                var report = await analytics.ReadReport(id, ct); verifiedReport = true;
                if (report["dataset_catalog"] is null) datasetStatus = "report_only";
                else { await browse.ReadManifest(report, ct); catalog = report["dataset_catalog"]!.DeepClone().AsObject(); catalog.Remove("datasets"); datasetStatus = "available"; }
            }
            catch (AnalyticsException ex) when (ex.StatusCode is 404 or 409 or 400)
            {
                datasetStatus = ex.StatusCode == 404 ? (verifiedReport ? "report_only" : "not_published") : ex.StatusCode == 409 ? "stale" : "unavailable";
                failure = ex.Message;
            }
        }
        return new { schema_version = "gearth.run-data.v1", run_id = id, storage = store.Kind,
            through_revision = head.Revision, through_sequence = head.AuditSequence, tick = head.Tick, status = head.Status,
            analytics_enabled = head.AnalyticsEnabled, audit_start_tick = head.AuditStartTick,
            audit_daily_through_tick = head.AuditDailyThroughTick, audit_opening_balances_captured = head.AuditOpeningBalancesCaptured,
            capture_metadata = metadata, capture_metadata_reason = metadata is null ? "Original committed run_metadata is unavailable." : null,
            timeline_status = Timeline(head, metadata), audit_status = auditStatus, databricks_status = datasetStatus,
            dataset_catalog = catalog, databricks_reason = failure };
    }
    private static string Timeline(RunDataHead head, JsonObject? metadata)
    {
        if (metadata?["timeline_status"]?.GetValue<string>() is "normal" or "fork_unsupported" or "unknown") return metadata["timeline_status"]!.GetValue<string>();
        // Legacy captures do not carry explicit lineage; an inherited prefix alone is not proof of a fork.
        return "unknown";
    }
    public async Task<object> Checkpoints(string id, long afterRevision, int limit, CancellationToken ct)
    {
        Id(id); if (afterRevision < 0 || limit is < 1 or > 100) throw new AnalyticsException(400, "Invalid checkpoint cursor or limit.");
        var head = await Data.ReadRunDataHead(id, ct) ?? throw new AnalyticsException(404, "Run not found.");
        var markers = await Data.ReadSaveMarkers(id, afterRevision, limit + 1, ct);
        var frames = store is IRendererStore renderer ? await renderer.ReadRendererFrames(id, afterRevision, Math.Min(limit + 1, 100), ct) : [];
        var entries = markers.Select(m => new JsonObject { ["revision"] = m.Revision, ["tick"] = m.Tick, ["kind"] = "save_marker", ["replay_available"] = false })
            .Concat(frames.Select(f => new JsonObject { ["revision"] = f.Revision, ["tick"] = f.Tick, ["week"] = f.Week,
                ["kind"] = f.FrameKind, ["replay_available"] = true, ["frame_url"] = $"/api/runs/{id}/renderer/frames/{f.Revision}" }))
            .Where(e => e["revision"]!.GetValue<long>() <= head.Revision).GroupBy(e => e["revision"]!.GetValue<long>())
            .Select(g => g.Last()).OrderBy(e => e["revision"]!.GetValue<long>()).ToArray();
        var page = entries.Take(limit).ToArray();
        // Renderer API's own 100-head bound means the final page may need one extra request to prove exhaustion.
        var more = entries.Length > limit || frames.Length == 100;
        return new { run_id = id, checkpoints = page, next_after_revision = page.LastOrDefault()?["revision"]?.GetValue<long>() ?? afterRevision, has_more = more };
    }
    public async Task<object> Records(string id, long? revision, long? sequence, long after, int limit, int? fromTick, int? toTick, string? kind, CancellationToken ct)
    {
        Id(id);
        if (revision is null || sequence is null || revision < 0 || sequence < 0 || after < 0 || after > sequence || limit is < 1 or > 200
            || fromTick < 0 || toTick < 0 || fromTick > toTick || kind is not null && (kind.Length > 100 || kind.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')))
            throw new AnalyticsException(400, "Invalid audit pin, filters, cursor or limit.");
        var head = await Data.ReadRunDataHead(id, ct) ?? throw new AnalyticsException(404, "Run not found.");
        if (head.AnalyticsEnabled == false && head.AuditSequence > 0) throw new AnalyticsException(409, "Audit identity after capture was disabled cannot be qualified for browsing.");
        if (!await Data.VerifyAuditPin(id, revision.Value, sequence.Value, ct)) throw new AnalyticsException(409, "Audit pin is not a committed boundary for this run.");
        var raw = await Data.ReadPinnedAudit(id, revision.Value, sequence.Value, after, ScanLimit, ct);
        var matches = new List<AuditRecord>(); var cursor = after; var scanned = 0; var bytes = 2048;
        foreach (var record in raw)
        {
            if (record.RunId != id || record.Sequence != cursor + 1 || record.Sequence > sequence || record.Revision > revision)
                throw new AnalyticsException(409, "Committed audit history has a gap or source mismatch.");
            var matching = (fromTick is null || record.Tick >= fromTick) && (toTick is null || record.Tick <= toTick) && (kind is null || record.Kind == kind);
            if (matching)
            {
                var size = JsonSerializer.SerializeToUtf8Bytes(record, Wire.Json).Length;
                if (bytes + size > 8 * 1024 * 1024) { if (matches.Count == 0) throw new AnalyticsException(409, "Audit row exceeds response byte limit."); break; }
                matches.Add(record); bytes += size;
            }
            cursor = record.Sequence; scanned++;
            if (matches.Count == limit) break;
        }
        if (scanned == 0 && after < sequence) throw new AnalyticsException(409, "Committed audit history is missing or exceeds the page byte limit.");
        return new { run_id = id, source = "committed_audit", through_revision = revision.Value, through_sequence = sequence.Value,
            records = matches, next_after = cursor, has_more = cursor < sequence, scanned_count = scanned, scan_limit = ScanLimit,
            scope = new { from_tick = fromTick, to_tick = toTick, kind } };
    }
    public async Task<object> Datasets(string id, CancellationToken ct)
    {
        Id(id); var verifiedReport = false;
        try
        {
            var report = await analytics.ReadReport(id, ct); verifiedReport = true;
            if (report["dataset_catalog"] is null) return new { run_id = id, status = "report_only", dataset_catalog = (JsonObject?)null, bundle_manifest_sha256 = (string?)null };
            await browse.ReadManifest(report, ct); return new { run_id = id, status = "available", dataset_catalog = report["dataset_catalog"], bundle_manifest_sha256 = report["dataset_catalog"]?["bundle_manifest_sha256"] };
        }
        catch (AnalyticsException ex) when (ex.StatusCode == 404)
        {
            if (await Data.ReadRunDataHead(id, ct) is null) throw;
            return new { run_id = id, status = verifiedReport ? "report_only" : "not_published", dataset_catalog = (JsonObject?)null, bundle_manifest_sha256 = (string?)null };
        }
    }
    public async Task<object> Rows(string id, string datasetId, string? cursor, int limit, CancellationToken ct)
    {
        Id(id); return await browse.Rows(await analytics.ReadReport(id, ct), datasetId, cursor, limit, ct);
    }
}
