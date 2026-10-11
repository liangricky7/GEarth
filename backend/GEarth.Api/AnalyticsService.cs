using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Api;

public sealed class AnalyticsException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

// Uses persisted checkpoints exclusively: Coordinator.Current may be ahead of its last commit.
public sealed class AnalyticsService(IWorldStore store, AnalyticsBrowseStore? browse = null)
{
    public const string ReportSchema = "gearth.analytics-report.v1";
    public const int MaximumReportBytes = 8 * 1024 * 1024;
    public const int MaximumManifestBytes = 16 * 1024 * 1024;
    private static readonly string[] PublicServicesMetadataFields =
        ["capture_contract_version", "features", "active_features", "banking_paused", "economy_configuration"];

    private static void ValidateRunId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new AnalyticsException(400, "Invalid run ID.");
    }

    private async Task<World> Committed(string id, CancellationToken ct)
    {
        ValidateRunId(id);
        var world = await store.Load(id, ct) ?? throw new AnalyticsException(404, "Run not found.");
        if (world.Status is not ("completed" or "stopped" or "failed"))
            throw new AnalyticsException(409, "Analytics export requires a committed terminal run.");
        return world;
    }

    private async Task<RunAuditHead> CommittedHead(string id, CancellationToken ct)
    {
        ValidateRunId(id);
        if (store is not IRunAuditStore { SupportsAudit: true } audit)
            throw new AnalyticsException(409, "Audit storage is unavailable for this run.");
        var head = await audit.ReadAuditHead(id, ct) ?? throw new AnalyticsException(404, "Run not found.");
        if (head.RunId != id || head.Revision < 0 || head.AuditSequence < 0)
            throw new AnalyticsException(409, "Committed audit checkpoint is inconsistent.");
        if (head.Status is not ("completed" or "stopped" or "failed"))
            throw new AnalyticsException(409, "Analytics export requires a committed terminal run.");
        return head;
    }

    public async Task<object> ExportMetadata(string id, CancellationToken ct)
    {
        var world = await Committed(id, ct);
        var audit = store as IRunAuditStore;
        var metadataRecord = audit?.SupportsAudit == true && world.AuditSequence > 0
            ? (await audit.ReadAudit(id, 0, 1, ct)).FirstOrDefault() : null;
        var configuration = metadataRecord?.Kind == "run_metadata" ? metadataRecord.Payload : new JsonObject();
        var capturedFromStart = world.AnalyticsEnabled && world.AuditStartTick == 0
            && world.AuditOpeningBalancesCaptured && world.AuditDailyThroughTick == world.Tick;
        var unknown = new List<string> { "token_usage", "model_backend_hardware", "fixture" };
        if (world.Society?.ComputeProviders.Length is not > 0) unknown.Add("model_assignment");
        if (!capturedFromStart) unknown.Add("audit_capture_start");
        foreach (var key in new[] { "initial_population", "government_rotation", "tick_limit", "engine_commit", "worker_capacity", "tick_interval_ms" })
            if (configuration[key] is null) unknown.Add(key);
        foreach (var key in PublicServicesMetadataFields)
            if (configuration[key] is null) unknown.Add(key);
        return new
        {
            run_id = world.Id, status = world.Status, world.Tick,
            through_revision = world.Revision, through_sequence = world.AuditSequence,
            through_event_sequence = world.Seq, world.Seed, world.Mode,
            population = world.Agents.Count, world.InitialCashMinor, world.ScenarioHash,
            initial_population = configuration["initial_population"], configuration,
            capture_contract_version = configuration["capture_contract_version"], features = configuration["features"],
            active_features = configuration["active_features"], banking_paused = configuration["banking_paused"],
            economy_configuration = configuration["economy_configuration"],
            analytics_enabled = world.AnalyticsEnabled, audit_available = audit?.SupportsAudit == true,
            audit_from_tick_zero = capturedFromStart,
            audit_schema_version = world.AuditSchemaVersion, audit_start_tick = world.AuditStartTick,
            audit_daily_through_tick = world.AuditDailyThroughTick,
            audit_opening_balances_captured = world.AuditOpeningBalancesCaptured,
            completeness = world.Status == "completed" && capturedFromStart ? "complete" : "partial",
            metadata_unknown = unknown,
            source = new { rule = world.Mode == "RULE", model = world.Mode != "RULE", fixture = (bool?)null },
            compute_providers = world.Society?.ComputeProviders ?? []
        };
    }

    public async Task<object> AuditPage(string id, long? revision, long? sequence, long after, int limit, CancellationToken ct)
    {
        if (revision is null || sequence is null || revision < 0 || sequence < 0 || after < 0 || limit is < 1 or > 2000)
            throw new AnalyticsException(400, "Supply nonnegative through_revision, through_sequence, after and limit between 1 and 2000.");
        var head = await CommittedHead(id, ct);
        if (head.Revision != revision || head.AuditSequence != sequence)
            throw new AnalyticsException(409, "Export checkpoint changed; fetch export metadata again.");
        if (after > sequence) throw new AnalyticsException(400, "after exceeds the pinned audit sequence.");
        if (store is not IRunAuditStore { SupportsAudit: true } audit)
            throw new AnalyticsException(409, "Audit storage is unavailable for this run.");
        var records = (await audit.ReadAudit(id, after, limit, ct))
            .Where(record => record.Sequence <= sequence).ToArray();
        var cursor = after;
        foreach (var record in records)
        {
            if (record.RunId != id || record.Sequence != cursor + 1 || record.Revision > revision)
                throw new AnalyticsException(409, "Committed audit coverage contains a gap or mismatched checkpoint.");
            cursor = record.Sequence;
        }
        if (records.Length == 0 && after < sequence)
            throw new AnalyticsException(409, "Committed audit records are missing.");
        return new { run_id = id, through_revision = revision.Value, through_sequence = sequence.Value,
            records, next_after = cursor, has_more = cursor < sequence };
    }

    public async Task<JsonObject> ReadReport(string id, CancellationToken ct)
    {
        var head = await CommittedHead(id, ct);
        var report = store is IAnalyticsReportStore cache ? await cache.ReadAnalyticsReport(id, ct) : null;
        if (report is null) throw new AnalyticsException(404, "No imported analytics report is available for this run.");
        // Validation depends only on these committed scalar fields, never the large world body.
        ValidateReport(report, new World { Id = head.RunId, Status = head.Status, Revision = head.Revision,
            AuditSequence = head.AuditSequence, AnalyticsEnabled = head.AnalyticsEnabled });
        // Only the response label changes. The persisted report and source hashes
        // remain unchanged so old evidence is still independently verifiable.
        if (String(report, "schema_version") != ReportSchema)
        {
            report = (JsonObject)report.DeepClone();
            report["schema_version"] = ReportSchema;
        }
        return report;
    }

    public async Task Publish(JsonObject report, byte[] manifestBytes, CancellationToken ct, string? browseDirectory = null)
    {
        if (manifestBytes.Length > MaximumManifestBytes)
            throw new AnalyticsException(400, "Source manifest exceeds the 16 MiB limit.");
        if (JsonSerializer.SerializeToUtf8Bytes(report, Wire.Json).Length > MaximumReportBytes)
            throw new AnalyticsException(400, "Analytics report exceeds the 8 MiB limit.");
        var id = String(report, "run_id");
        var world = await Committed(id, ct);
        ValidateReport(report, world);
        var hash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        if (hash != String(report, "source_manifest_sha256"))
            throw new AnalyticsException(400, "Source manifest digest does not match the report.");
        JsonObject manifest;
        try { manifest = JsonNode.Parse(manifestBytes)?.AsObject() ?? throw new JsonException(); }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException)
        { throw new AnalyticsException(400, "Source manifest must be a JSON object."); }
        if (!ContractVersion.Matches(String(manifest, "schema_version"), "gearth.run-export.v1")
            || !ContractVersion.Matches(String(manifest, "record_schema_version"), "gearth.audit.v1"))
            throw new AnalyticsException(400, "Unsupported export manifest schema.");
        var metadata = Object(manifest, "run");
        if (String(metadata, "run_id") != id || Number(metadata, "through_revision") != world.Revision
            || Number(metadata, "through_sequence") != world.AuditSequence
            || Number(metadata, "through_event_sequence") != world.Seq
            || Number(metadata, "tick") != world.Tick || Number(metadata, "seed") != world.Seed
            || String(metadata, "status") != world.Status || String(metadata, "mode") != world.Mode
            || Number(manifest, "record_count") != world.AuditSequence)
            throw new AnalyticsException(409, "Source manifest does not match the committed checkpoint.");
        if (!Boolean(Object(manifest, "completeness"), "snapshot_complete"))
            throw new AnalyticsException(400, "Publication requires a complete pinned audit export snapshot.");
        if (store is not IRunAuditStore { SupportsAudit: true } || !world.AnalyticsEnabled || world.AuditSequence < 1)
            throw new AnalyticsException(409, "Publication requires committed audit capture.");
        var firstRecord = (await ((IRunAuditStore)store).ReadAudit(id, 0, 1, ct)).FirstOrDefault();
        var captured = firstRecord?.Kind == "run_metadata" ? firstRecord.Payload : new JsonObject();
        if (captured["features"]?["public_services"]?.GetValue<string>() == "v1" || report["public_services"] is not null)
        {
            var reportMetadata = Object(Object(report, "public_services"), "metadata");
            foreach (var field in PublicServicesMetadataFields)
                if (!JsonNode.DeepEquals(captured[field], reportMetadata[field])
                    || !JsonNode.DeepEquals(captured[field], metadata[field]))
                    throw new AnalyticsException(409, $"Public-services {field} does not match the captured run configuration.");
        }
        if (store is not IAnalyticsReportStore cache)
            throw new AnalyticsException(409, "Analytics report storage is unavailable.");
        if (browseDirectory is not null)
        {
            if (browse is null) throw new AnalyticsException(409, "Analytics browse cache is not configured.");
            await browse.Publish(browseDirectory, report, ct);
        }
        else if (report["dataset_catalog"] is not null)
        {
            if (browse is null) throw new AnalyticsException(409, "Analytics browse cache is not configured.");
            await browse.ReadManifest(report, ct);
        }
        else
        {
            // A summary-only refresh of the same source preserves its verified full-row receipt.
            var previous = await cache.ReadAnalyticsReport(id, ct);
            if (previous?["dataset_catalog"] is JsonObject previousCatalog && previous["source_manifest_sha256"]?.GetValue<string>() == hash
                && previous["through_revision"]?.GetValue<long>() == world.Revision && previous["through_sequence"]?.GetValue<long>() == world.AuditSequence)
            {
                if (browse is null) throw new AnalyticsException(409, "Cannot preserve dataset receipt without its configured cache.");
                await browse.ReadManifest(previous, ct);
                report["dataset_catalog"] = previousCatalog.DeepClone();
            }
        }
        if (JsonSerializer.SerializeToUtf8Bytes(report, Wire.Json).Length > MaximumReportBytes)
            throw new AnalyticsException(400, "Analytics report exceeds the 8 MiB limit after preserving its dataset receipt.");
        await cache.SaveAnalyticsReport(id, report, ct);
    }

    public static void ValidateReport(JsonObject report, World world)
    {
        if (!ContractVersion.Matches(String(report, "schema_version"), ReportSchema))
            throw new AnalyticsException(400, "Unsupported analytics report schema.");
        if (String(report, "run_id") != world.Id || Number(report, "through_revision") != world.Revision
            || Number(report, "through_sequence") != world.AuditSequence)
            throw new AnalyticsException(409, "Analytics report does not match the committed checkpoint.");
        var digest = String(report, "source_manifest_sha256");
        if (digest.Length != 64 || digest.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new AnalyticsException(400, "Report requires a lowercase SHA256 source manifest digest.");
        if (String(report, "analysis_version").Length > 100
            || !DateTimeOffset.TryParse(String(report, "generated_at"), out _))
            throw new AnalyticsException(400, "Invalid analysis version or generation timestamp.");
        if (world.Status is not ("completed" or "stopped" or "failed"))
            throw new AnalyticsException(409, "Report requires a terminal checkpoint.");
        var completeness = Object(report, "completeness");
        if (String(completeness, "run_status") != world.Status)
            throw new AnalyticsException(409, "Report completion status does not match the checkpoint.");
        if (!Boolean(completeness, "snapshot_complete"))
            throw new AnalyticsException(400, "Report requires a complete pinned audit snapshot.");
        if (!world.AnalyticsEnabled || world.AuditSequence < 1)
            throw new AnalyticsException(409, "Report requires committed audit capture.");
        if (completeness["run_complete"] is not JsonValue runComplete || !runComplete.TryGetValue<bool>(out var finished)
            || finished != (world.Status == "completed"))
            throw new AnalyticsException(400, "Report must declare stopped and failed runs partial.");
        Array(completeness, "missing_data"); Array(completeness, "metadata_unknown");
        var summary = Object(report, "summary"); Object(report, "government_comparison");
        if (Number(summary, "record_count") != world.AuditSequence)
            throw new AnalyticsException(409, "Report record count does not cover the pinned audit.");
        foreach (var field in new[] { "transaction_count", "transfer_minor", "agent_count", "decision_result_count", "fallback_count" })
            if (Number(summary, field) < 0) throw new AnalyticsException(400, $"Report {field} cannot be negative.");
        var checks = Array(report, "checks"); Array(report, "agents"); Array(report, "countries");
        foreach (var check in checks)
        {
            if (check is not JsonObject item || String(item, "status") is not ("pass" or "fail" or "unknown"))
                throw new AnalyticsException(400, "Report check status must be pass, fail or unknown.");
            String(item, "name"); Object(item, "details"); Array(item, "evidence_refs");
        }
        Array(report, "evidence_refs");
        if (report["public_services"] is not null)
            ValidatePublicServices(Object(report, "public_services"));
        if (report["dataset_catalog"] is JsonObject catalog)
            AnalyticsBrowseStore.ValidateCatalog(catalog, world.Id, world.Revision, world.AuditSequence, digest);
        else if (report.ContainsKey("dataset_catalog") && report["dataset_catalog"] is not null)
            throw new AnalyticsException(400, "dataset_catalog must be an object.");
        ValidateEvidence(report, world);
        ValidateNumbers(report);
    }

    private static void ValidatePublicServices(JsonObject section)
    {
        var metadata = Object(section, "metadata");
        foreach (var field in PublicServicesMetadataFields)
            if (!metadata.ContainsKey(field))
                throw new AnalyticsException(400, $"Public-services metadata requires {field}, or null when unknown.");
        if (metadata["capture_contract_version"] is not null) String(metadata, "capture_contract_version");
        if (metadata["features"] is not null) Object(metadata, "features");
        if (metadata["active_features"] is not null)
            foreach (var feature in Array(metadata, "active_features"))
                if (feature is not JsonValue value || !value.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))
                    throw new AnalyticsException(400, "Active features must be nonempty strings.");
        if (metadata["banking_paused"] is not null) Boolean(metadata, "banking_paused");
        if (metadata["economy_configuration"] is not null) Object(metadata, "economy_configuration");
        var summary = Object(section, "snapshot_summary");
        var total = Nonnegative(summary, "latest_agent_count");
        var residents = Nonnegative(summary, "resident_count");
        if (residents > total) throw new AnalyticsException(400, "Resident count exceeds the agent count.");
        String(summary, "denominator");
        foreach (var field in new[] { "students", "graduates", "job_seekers", "hungry" })
        {
            var metric = Object(summary, field);
            var observed = Nonnegative(metric, "observed_count");
            var known = Nonnegative(metric, "eligible_known_count");
            var missing = Nonnegative(metric, "missing_count");
            if (observed > known || known > residents || missing != residents - known)
                throw new AnalyticsException(400, "Public-services snapshot counts exceed their population.");
        }
        foreach (var transfer in Array(section, "transfers_by_kind"))
        {
            if (transfer is not JsonObject item) throw new AnalyticsException(400, "Transfer summary requires objects.");
            String(item, "kind"); Nonnegative(item, "count"); Nonnegative(item, "amount_minor"); Array(item, "evidence_refs");
        }
        var coverage = Object(section, "coverage");
        var captured = Nonnegative(coverage, "agent_snapshots_with_public_services_fields");
        if (captured > Nonnegative(coverage, "agent_snapshot_count"))
            throw new AnalyticsException(400, "Public-services coverage exceeds the captured snapshots.");
        var count = Nonnegative(coverage, "agent_snapshot_count");
        var expectedStatus = captured == 0 ? "unknown" : captured == count ? "pass" : "partial";
        if (String(coverage, "status") != expectedStatus)
            throw new AnalyticsException(400, "Public-services coverage status does not match its counts.");
    }

    private static long Nonnegative(JsonObject obj, string key)
    {
        var value = Number(obj, key);
        return value >= 0 ? value : throw new AnalyticsException(400, $"Report {key} cannot be negative.");
    }

    private static void ValidateEvidence(JsonNode? node, World world)
    {
        if (node is JsonObject obj)
        {
            foreach (var entry in obj)
            {
                if (entry.Key == "evidence_refs")
                {
                    if (entry.Value is not JsonArray refs) throw new AnalyticsException(400, "Evidence references must be arrays.");
                    foreach (var reference in refs)
                        ValidateEvidenceReference(reference, world);
                }
                else if (entry.Key == "evidence_ref") ValidateEvidenceReference(entry.Value, world);
                ValidateEvidence(entry.Value, world);
            }
        }
        else if (node is JsonArray array) foreach (var entry in array) ValidateEvidence(entry, world);
    }

    private static void ValidateEvidenceReference(JsonNode? reference, World world)
    {
        if (reference is not JsonObject evidence || String(evidence, "run_id") != world.Id
            || Number(evidence, "sequence") is var seq && (seq < 1 || seq > world.AuditSequence))
            throw new AnalyticsException(400, "Report contains evidence outside the committed audit.");
        String(evidence, "kind");
    }

    private static void ValidateNumbers(JsonNode? node)
    {
        if (node is JsonObject obj) { foreach (var entry in obj) ValidateNumbers(entry.Value); }
        else if (node is JsonArray array) { foreach (var entry in array) ValidateNumbers(entry); }
        else if (node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && (!JsonSerializer.SerializeToElement(value).TryGetDouble(out var number) || !double.IsFinite(number)))
            throw new AnalyticsException(400, "Report contains a nonfinite numeric value.");
    }

    private static string String(JsonObject obj, string key) => obj[key] is JsonValue value
        && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
        ? text : throw new AnalyticsException(400, $"Report requires {key}.");
    private static long Number(JsonObject obj, string key) => obj[key] is JsonValue value
        && value.GetValueKind() == JsonValueKind.Number && JsonSerializer.SerializeToElement(value).TryGetInt64(out var number)
        ? number : throw new AnalyticsException(400, $"Report requires integer {key}.");
    private static JsonObject Object(JsonObject obj, string key) => obj[key] as JsonObject
        ?? throw new AnalyticsException(400, $"Report requires object {key}.");
    private static JsonArray Array(JsonObject obj, string key) => obj[key] as JsonArray
        ?? throw new AnalyticsException(400, $"Report requires array {key}.");
    private static bool Boolean(JsonObject obj, string key) => obj[key] is JsonValue value && value.TryGetValue<bool>(out var boolean)
        ? boolean : throw new AnalyticsException(400, $"Report requires boolean {key}.");
}
