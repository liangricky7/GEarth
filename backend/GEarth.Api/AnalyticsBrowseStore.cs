using System.IO.Compression;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GEarth.Core;

namespace GEarth.Api;

// Actual, qualified Databricks rows are explicitly published here. Reads never connect to Databricks.
public sealed class AnalyticsBrowseStore(string directory)
{
    public static AnalyticsBrowseStore FromEnvironment() => new(Path.GetFullPath(
        Environment.GetEnvironmentVariable("GEARTH_ANALYTICS_BROWSE_DIRECTORY") ?? ".data/analytics-browse"));
    private readonly ConcurrentDictionary<string, (long Length, DateTime Time, JsonObject Manifest, JsonObject Catalog)> manifests = new();
    private const int MaximumManifestBytes = 16 * 1024 * 1024;
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "bronze.audit", "silver.opening_balances", "silver.agent_daily", "silver.country_daily", "silver.transactions",
        "silver.bank_ledger", "silver.decision_requests", "silver.decision_results", "silver.action_effects", "silver.events",
        "silver.runs", "silver.decision_options", "silver.event_edges", "gold.agent_days", "gold.agent_latest",
        "gold.country_days", "gold.public_service_days", "gold.public_service_transfers", "gold.import_receipts"
    };
    public static string Text(JsonObject obj, string key) => obj[key] is JsonValue value && value.TryGetValue<string>(out var text)
        && !string.IsNullOrWhiteSpace(text) ? text : throw Bad($"Missing or invalid {key}.");
    public static long Integer(JsonObject obj, string key) => obj[key] is JsonValue value && value.TryGetValue<long>(out var number)
        && number >= 0 ? number : throw Bad($"Missing or invalid {key}.");
    private static AnalyticsException Bad(string message) => new(400, message);
    public static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Hash(JsonObject obj, string key)
    {
        var hash = Text(obj, key);
        if (hash.Length != 64 || hash.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))) throw Bad($"Invalid {key}.");
        return hash;
    }
    private static JsonArray Items(JsonObject obj, string key) => obj[key] as JsonArray ?? throw Bad($"Missing {key} array.");
    private static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Bad("Symlinks are not permitted in analytics bundles.");
        }
    }
    private static void ValidateRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 400 || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':')
            || relative.Split('/').Any(s => s is "" or "." or "..") || relative.Any(char.IsControl)) throw Bad("Invalid bundle chunk path.");
    }
    private static string SafePath(string root, string relative)
    {
        ValidateRelative(relative);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw Bad("Bundle path escapes its directory.");
        NoLinks(full); return full;
    }
    private string CacheRoot(string id, string source, string bundle)
    {
        RunDataIO.ValidateId(id);
        var result = Path.Combine(Path.GetFullPath(directory), id, source, bundle); NoLinks(result); return result;
    }
    private static async Task<byte[]> ManifestBytes(string root, CancellationToken ct)
    {
        NoLinks(root); var path = SafePath(root, "browse-manifest.json");
        if (!File.Exists(path)) throw new AnalyticsException(404, "Published dataset bundle is unavailable.");
        if (new FileInfo(path).Length > MaximumManifestBytes) throw Bad("Browse manifest exceeds 16 MiB.");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (bytes.Length > MaximumManifestBytes) throw Bad("Browse manifest exceeds 16 MiB.");
        return bytes;
    }
    private static JsonObject Parse(byte[] bytes)
    {
        try { return JsonNode.Parse(bytes)?.AsObject() ?? throw Bad("Invalid browse manifest."); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw Bad("Invalid browse manifest."); }
    }
    public static JsonObject Catalog(JsonObject manifest, string bundleDigest)
    {
        var catalog = new JsonObject
        {
            ["schema_version"] = "gearth.dataset-catalog.v1", ["run_id"] = manifest["run_id"]!.DeepClone(),
            ["through_revision"] = manifest["through_revision"]!.DeepClone(), ["through_sequence"] = manifest["through_sequence"]!.DeepClone(),
            ["source_manifest_sha256"] = manifest["source_manifest_sha256"]!.DeepClone(), ["bundle_manifest_sha256"] = bundleDigest,
            ["generated_at"] = manifest["generated_at"]!.DeepClone(), ["qualification_status"] = manifest["qualification_status"]!.DeepClone()
        };
        catalog["datasets"] = new JsonArray(Items(manifest, "datasets").Select(node =>
        {
            var entry = node!.DeepClone().AsObject(); entry.Remove("chunks"); return (JsonNode)entry;
        }).ToArray());
        return catalog;
    }
    public static void ValidateCatalog(JsonObject catalog, string id, long revision, long sequence, string source)
    {
        if (Text(catalog, "schema_version") != "gearth.dataset-catalog.v1" || Text(catalog, "run_id") != id
            || Integer(catalog, "through_revision") != revision || Integer(catalog, "through_sequence") != sequence
            || Hash(catalog, "source_manifest_sha256") != source || Text(catalog, "qualification_status") != "pass")
            throw new AnalyticsException(409, "Dataset catalog provenance does not match the report.");
        Hash(catalog, "bundle_manifest_sha256");
        if (!DateTimeOffset.TryParse(Text(catalog, "generated_at"), out _)) throw Bad("Invalid dataset catalog timestamp.");
        ValidateDatasets(Items(catalog, "datasets"), false);
    }
    private static void ValidateDatasets(JsonArray datasets, bool chunks)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal); var paths = new HashSet<string>(StringComparer.Ordinal);
        if (datasets.Count > Allowed.Count) throw Bad("Too many datasets.");
        foreach (var node in datasets)
        {
            var entry = node as JsonObject ?? throw Bad("Invalid dataset entry."); var id = Text(entry, "dataset_id");
            if (!Allowed.Contains(id) || !ids.Add(id)) throw Bad("Unknown or duplicate dataset ID.");
            var parts = id.Split('.'); var table = Text(entry, "table_name");
            if (table.Split('.').Length != 3 || !table.EndsWith($".gearth_{parts[0]}.{parts[1]}", StringComparison.Ordinal)) throw Bad("Dataset table identity mismatch.");
            var columns = Items(entry, "columns"); var fields = Items(entry, "payload_fields");
            if (columns.Count > 2048 || fields.Count > 4096) throw Bad("Dataset schema or payload inventory exceeds its bounded limit.");
            if (entry["availability"]?.GetValue<string>() == "unavailable")
            {
                if (entry["row_count"] is not null || columns.Count != 0 || fields.Count != 0 || Text(entry, "field_coverage_scope") != "unavailable"
                    || Text(entry, "unavailable_reason") != "table_not_present" || chunks && Items(entry, "chunks").Count != 0)
                    throw Bad("Unavailable datasets must not claim measured rows.");
                continue;
            }
            if (entry["availability"] is not null && entry["availability"]!.GetValue<string>() != "available") throw Bad("Unknown dataset availability.");
            var count = Integer(entry, "row_count");
            if (Text(entry, "field_coverage_scope") is not ("all_run_rows" or "all_run_rows_bounded_inventory")) throw Bad("Dataset coverage must refer to all run rows.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in columns)
            {
                var column = c as JsonObject ?? throw Bad("Invalid column.");
                if (!names.Add(Text(column, "name")) || Integer(column, "non_null_count") > count) throw Bad("Invalid column coverage.");
                Text(column, "data_type");
                if (column["nullable"] is not JsonValue nullable || !nullable.TryGetValue<bool>(out _)) throw Bad("Invalid column nullability.");
            }
            if (!names.Contains("run_id")) throw Bad("Dataset lacks run_id column.");
            var fieldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in fields)
            {
                var field = f as JsonObject ?? throw Bad("Invalid payload field.");
                var path = Text(field, "path");
                if (!path.StartsWith('/') || !fieldNames.Add(path)) throw Bad("Invalid or duplicate payload field path.");
                var present = Integer(field, "present_count"); var nil = Integer(field, "null_count"); var nonnull = Integer(field, "non_null_count");
                if (present > count || nil > present || nonnull != present - nil || Integer(field, "zero_or_false_count") > nonnull)
                    throw Bad("Invalid payload field coverage.");
            }
            if (!chunks) continue;
            long rows = 0;
            foreach (var c in Items(entry, "chunks"))
            {
                var chunk = c as JsonObject ?? throw Bad("Invalid chunk."); var path = Text(chunk, "path");
                if (!path.StartsWith(id + "/", StringComparison.Ordinal) || !path.EndsWith(".ndjson.gz", StringComparison.Ordinal) || !paths.Add(path)) throw Bad("Invalid or duplicate chunk path.");
                ValidateRelative(path); Hash(chunk, "sha256");
                var chunkRows = Integer(chunk, "row_count"); var size = Integer(chunk, "uncompressed_bytes");
                if (chunkRows is < 1 or > 2000 || size > (chunkRows == 1 ? RunDataIO.MaximumRowBytes + 1L : 4 * 1024 * 1024L)
                    || Integer(chunk, "compressed_bytes") is < 1 or > 8 * 1024 * 1024) throw Bad("Chunk exceeds row/byte limits.");
                rows = checked(rows + chunkRows);
            }
            if (rows != count) throw Bad("Dataset chunk counts do not match its row count.");
        }
    }
    private static void ValidateManifest(JsonObject manifest, JsonObject catalog, string digest)
    {
        if (Text(manifest, "schema_version") != "gearth.analytics-browse.v1") throw Bad("Unsupported browse manifest schema.");
        ValidateDatasets(Items(manifest, "datasets"), true);
        if (!JsonNode.DeepEquals(Catalog(manifest, digest), catalog)) throw new AnalyticsException(409, "Report catalog does not match the browse manifest.");
    }
    private static async Task ValidateChunk(string root, JsonObject chunk, string id, string source, long revision, long sequence, CancellationToken ct, Coverage? coverage = null)
    {
        var path = SafePath(root, Text(chunk, "path"));
        if (!File.Exists(path) || new FileInfo(path).Length != Integer(chunk, "compressed_bytes")) throw Bad("Missing or incorrect chunk bytes.");
        await using (var file = File.OpenRead(path))
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            if (hash != Hash(chunk, "sha256")) throw Bad("Chunk checksum mismatch.");
        }
        long rows = 0, bytes = 0;
        await using var compressed = File.OpenRead(path); await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        await using var counter = new LimitedReadStream(gzip, Integer(chunk, "uncompressed_bytes"));
        await foreach (var line in RunDataIO.Lines(counter, ct: ct))
        {
            ct.ThrowIfCancellationRequested();
            if (++rows > Integer(chunk, "row_count")) throw Bad("Chunk has excess rows.");
            JsonObject row;
            try { row = JsonNode.Parse(line)?.AsObject() ?? throw Bad("Invalid dataset row."); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw Bad("Invalid dataset row."); }
            if (JsonSerializer.SerializeToUtf8Bytes(row, Wire.Json).Length + 2048 > MaximumResponseBytes) throw Bad("Dataset row exceeds serialized response byte limit.");
            coverage?.Add(row);
            if (row["run_id"]?.GetValue<string>() != id) throw Bad("Dataset row belongs to another run.");
            if (row["source_manifest_sha256"] is not null && row["source_manifest_sha256"]!.GetValue<string>() != source) throw Bad("Dataset row source mismatch.");
            foreach (var field in new[] { "revision", "through_revision" })
                if (row[field] is not null && Integer(row, field) > revision) throw Bad("Dataset row exceeds source revision.");
            foreach (var field in new[] { "sequence", "through_sequence" })
                if (row[field] is not null && Integer(row, field) > sequence) throw Bad("Dataset row exceeds source sequence.");
            bytes = counter.BytesRead;
        }
        bytes = counter.BytesRead;
        if (rows != Integer(chunk, "row_count") || bytes != Integer(chunk, "uncompressed_bytes")) throw Bad("Chunk row or expansion count mismatch.");
    }
    private sealed class Coverage
    {
        private readonly JsonObject entry;
        private readonly Dictionary<string, long> columns;
        private readonly Dictionary<string, long[]> fields;
        private string? payloadSource;
        public Coverage(JsonObject entry)
        {
            this.entry = entry;
            columns = Items(entry, "columns").OfType<JsonObject>().ToDictionary(c => Text(c, "name"), _ => 0L, StringComparer.Ordinal);
            fields = Items(entry, "payload_fields").OfType<JsonObject>().ToDictionary(f => Text(f, "path"), _ => new long[4], StringComparer.Ordinal);
        }
        public void Add(JsonObject row)
        {
            if (row.Count != columns.Count || row.Any(e => !columns.ContainsKey(e.Key))) throw Bad("Dataset row columns disagree with its measured schema.");
            foreach (var item in row) if (item.Value is not null) columns[item.Key]++;
            var source = row.ContainsKey("payload_json") ? "payload_json" : row.ContainsKey("raw_json") ? "raw_json" : null;
            if (source is not null) payloadSource = source;
            JsonNode? payload = source is null ? null : row[source];
            if (payload is JsonValue value && value.TryGetValue<string>(out var text))
            {
                try { payload = JsonNode.Parse(text); } catch (JsonException) { throw Bad("Invalid original payload JSON."); }
            }
            if (source == "raw_json" && payload is JsonObject raw) payload = raw.ContainsKey("payload") ? raw["payload"] : raw;
            void Visit(JsonNode? current, string prefix, int depth)
            {
                if (current is not JsonObject obj) return;
                foreach (var property in obj)
                {
                    var path = prefix + "/" + property.Key.Replace("~", "~0").Replace("/", "~1");
                    if (fields.TryGetValue(path, out var counts))
                    {
                        counts[0]++;
                        if (property.Value is null) counts[1]++; else counts[2]++;
                        if (property.Value is JsonValue scalar && (scalar.TryGetValue<bool>(out var flag) && !flag
                            || scalar.GetValueKind() == JsonValueKind.Number && scalar.TryGetValue<double>(out var number) && number == 0)) counts[3]++;
                    }
                    if (depth < 4) Visit(property.Value, path, depth + 1);
                }
            }
            Visit(payload, "", 1);
        }

        public void Validate()
        {
            if (entry["availability"]?.GetValue<string>() == "unavailable") return;
            foreach (var column in Items(entry, "columns").OfType<JsonObject>())
                if (columns[Text(column, "name")] != Integer(column, "non_null_count")) throw Bad("Measured column coverage disagrees with cached rows.");
            foreach (var field in Items(entry, "payload_fields").OfType<JsonObject>())
            {
                var counts = fields[Text(field, "path")]; var names = new[] { "present_count", "null_count", "non_null_count", "zero_or_false_count" };
                for (var i = 0; i < 4; i++) if (counts[i] != Integer(field, names[i])) throw Bad("Measured payload field coverage disagrees with cached rows.");
            }
            if (entry.ContainsKey("payload_source") && entry["payload_source"]?.GetValue<string>() != payloadSource) throw Bad("Original payload source disagrees with cached rows.");
        }
    }
    public async Task Publish(string sourceDirectory, JsonObject report, CancellationToken ct)
    {
        var catalog = report["dataset_catalog"] as JsonObject ?? throw Bad("Bundle publication requires dataset_catalog.");
        var id = Text(report, "run_id"); var source = Hash(report, "source_manifest_sha256");
        ValidateCatalog(catalog, id, Integer(report, "through_revision"), Integer(report, "through_sequence"), source);
        sourceDirectory = Path.GetFullPath(sourceDirectory); NoLinks(sourceDirectory);
        var bytes = await ManifestBytes(sourceDirectory, ct); var digest = Digest(bytes);
        if (digest != Hash(catalog, "bundle_manifest_sha256")) throw Bad("Browse manifest digest mismatch.");
        var manifest = Parse(bytes); ValidateManifest(manifest, catalog, digest);
        var target = CacheRoot(id, source, digest); var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent); NoLinks(parent);
        var stage = Path.Combine(parent, ".staging-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(stage, "browse-manifest.json"), bytes, ct);
            foreach (var entry in Items(manifest, "datasets").OfType<JsonObject>())
            {
                var coverage = new Coverage(entry);
                foreach (var chunk in Items(entry, "chunks").OfType<JsonObject>())
                {
                    var from = SafePath(sourceDirectory, Text(chunk, "path")); var to = SafePath(stage, Text(chunk, "path"));
                    if (!File.Exists(from) || new FileInfo(from).Length != Integer(chunk, "compressed_bytes")) throw Bad("Source chunk bytes mismatch.");
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    await using (var input = File.OpenRead(from))
                    await using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        await CopyBounded(input, output, Integer(chunk, "compressed_bytes"), ct);
                    await ValidateChunk(stage, chunk, id, source, Integer(report, "through_revision"), Integer(report, "through_sequence"), ct, coverage);
                }
                coverage.Validate();
            }
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(target))
            {
                // Identical replay is safe; never replace content at an already exposed digest.
                var prior = await ManifestBytes(target, ct);
                if (!prior.SequenceEqual(bytes)) throw Bad("Conflicting cache manifest.");
                foreach (var entry in Items(manifest, "datasets").OfType<JsonObject>())
                    foreach (var chunk in Items(entry, "chunks").OfType<JsonObject>())
                        await ValidateChunk(target, chunk, id, source, Integer(report, "through_revision"), Integer(report, "through_sequence"), ct);
            }
            else Directory.Move(stage, target);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    private static async Task CopyBounded(Stream from, Stream to, long maximum, CancellationToken ct)
    {
        var buffer = new byte[65536]; long total = 0;
        while (true)
        {
            var count = await from.ReadAsync(buffer, ct); if (count == 0) break;
            total += count; if (total > maximum) throw Bad("Source chunk grew during publication.");
            await to.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (total != maximum) throw Bad("Source chunk changed during publication.");
    }
    public async Task<JsonObject> ReadManifest(JsonObject report, CancellationToken ct)
    {
        var catalog = report["dataset_catalog"] as JsonObject ?? throw new AnalyticsException(404, "Report is available; row cache is unavailable.");
        var id = Text(report, "run_id"); var source = Hash(report, "source_manifest_sha256"); var digest = Hash(catalog, "bundle_manifest_sha256");
        ValidateCatalog(catalog, id, Integer(report, "through_revision"), Integer(report, "through_sequence"), source);
        var root = CacheRoot(id, source, digest); var path = SafePath(root, "browse-manifest.json");
        var info = new FileInfo(path);
        if (!info.Exists) throw new AnalyticsException(404, "Published dataset bundle is unavailable.");
        if (manifests.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.Time == info.LastWriteTimeUtc)
        {
            if (!JsonNode.DeepEquals(cached.Catalog, catalog)) throw new AnalyticsException(409, "Report catalog does not match the cached publication.");
            return cached.Manifest;
        }
        var bytes = await ManifestBytes(root, ct);
        if (Digest(bytes) != digest) throw new AnalyticsException(409, "Published cache manifest changed.");
        var manifest = Parse(bytes); ValidateManifest(manifest, catalog, digest);
        if (manifests.Count > 256) manifests.Clear();
        manifests[path] = (info.Length, info.LastWriteTimeUtc, manifest, catalog.DeepClone().AsObject());
        return manifest;
    }
    private sealed record Cursor(string RunId, string DatasetId, string Source, string Bundle, int Chunk, int Row);
    private static string Encode(Cursor cursor) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor, Wire.Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Cursor Decode(string cursor)
    {
        try
        {
            if (cursor.Length > 2048) throw Bad("Invalid dataset cursor.");
            var value = cursor.Replace('-', '+').Replace('_', '/'); value += new string('=', (4 - value.Length % 4) % 4);
            return JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(value), Wire.Json) ?? throw Bad("Invalid dataset cursor.");
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { throw Bad("Invalid dataset cursor."); }
    }
    public async Task<object> Rows(JsonObject report, string datasetId, string? cursor, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 200) throw Bad("limit must be between 1 and 200.");
        var manifest = await ReadManifest(report, ct); var id = Text(report, "run_id"); var source = Hash(report, "source_manifest_sha256");
        var bundle = Hash(report["dataset_catalog"]!.AsObject(), "bundle_manifest_sha256");
        var dataset = Items(manifest, "datasets").OfType<JsonObject>().SingleOrDefault(d => Text(d, "dataset_id") == datasetId)
            ?? throw new AnalyticsException(404, "Dataset is not in this publication.");
        if (dataset["availability"]?.GetValue<string>() == "unavailable") throw new AnalyticsException(404, "Dataset was unavailable in the actual Databricks import.");
        var position = cursor is null ? new Cursor(id, datasetId, source, bundle, 0, 0) : Decode(cursor);
        if (position.RunId != id || position.DatasetId != datasetId || position.Source != source || position.Bundle != bundle)
            throw new AnalyticsException(409, "Dataset cursor belongs to a different source or publication.");
        var chunks = Items(dataset, "chunks").OfType<JsonObject>().ToArray();
        if (position.Chunk < 0 || position.Chunk > chunks.Length || position.Row < 0 || position.Chunk == chunks.Length && position.Row != 0
            || position.Chunk < chunks.Length && position.Row >= Integer(chunks[position.Chunk], "row_count")) throw Bad("Invalid dataset cursor position.");
        var rows = new List<JsonObject>(); var bytes = 2048; var chunkIndex = position.Chunk; var rowIndex = position.Row;
        var root = CacheRoot(id, source, bundle);
        while (chunkIndex < chunks.Length)
        {
            var chunk = chunks[chunkIndex]; await ValidateChunk(root, chunk, id, source, Integer(report, "through_revision"), Integer(report, "through_sequence"), ct);
            await using var file = File.OpenRead(SafePath(root, Text(chunk, "path"))); await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            await using var limited = new LimitedReadStream(gzip, Integer(chunk, "uncompressed_bytes")); var index = 0;
            await foreach (var line in RunDataIO.Lines(limited, ct: ct))
            {
                if (index++ < rowIndex) continue;
                var row = JsonNode.Parse(line)!.AsObject(); var size = Encoding.UTF8.GetByteCount(row.ToJsonString(Wire.Json)) + 1;
                if (size + 2048 > MaximumResponseBytes) throw Bad("Dataset row exceeds response byte limit.");
                if (bytes + size > MaximumResponseBytes || rows.Count == limit)
                    return Result(id, datasetId, source, bundle, rows, new Cursor(id, datasetId, source, bundle, chunkIndex, index - 1));
                rows.Add(row); bytes += size; rowIndex = index;
            }
            chunkIndex++; rowIndex = 0;
            if (rows.Count == limit) break;
        }
        return Result(id, datasetId, source, bundle, rows, chunkIndex < chunks.Length ? new Cursor(id, datasetId, source, bundle, chunkIndex, rowIndex) : null);
    }
    private static object Result(string id, string dataset, string source, string bundle, List<JsonObject> rows, Cursor? next) => new
    {
        run_id = id, dataset_id = dataset, source = "databricks_cached", source_manifest_sha256 = source,
        bundle_manifest_sha256 = bundle, rows, next_cursor = next is null ? null : Encode(next), has_more = next is not null
    };
    private sealed class LimitedReadStream(Stream inner, long maximum) : Stream
    {
        public long BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var count = await inner.ReadAsync(buffer, ct); BytesRead += count;
            if (BytesRead > maximum) throw Bad("Gzip expansion exceeds manifest byte count."); return count;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count); BytesRead += read;
            if (BytesRead > maximum) throw Bad("Gzip expansion exceeds manifest byte count."); return read;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
