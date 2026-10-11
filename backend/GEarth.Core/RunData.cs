using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GEarth.Core;

// Scalar committed metadata. Null is retained when an older checkpoint did not record a field.
public sealed record RunDataHead(string RunId, long Revision, long AuditSequence, string Status,
    bool? AnalyticsEnabled, int? Tick, int? AuditStartTick, int? AuditDailyThroughTick,
    bool? AuditOpeningBalancesCaptured, string? UpdatedAt);
public sealed record RunSaveMarker(long Revision, int? Tick);
public interface IRunDataStore
{
    Task<RunDataHead[]> ReadRunCatalog(string? beforeId, int limit, CancellationToken ct);
    Task<RunDataHead?> ReadRunDataHead(string id, CancellationToken ct);
    Task<bool> VerifyAuditPin(string id, long revision, long sequence, CancellationToken ct);
    Task<RunSaveMarker[]> ReadSaveMarkers(string id, long afterRevision, int limit, CancellationToken ct);
    Task<AuditRecord[]> ReadPinnedAudit(string id, long revision, long sequence, long after, int limit, CancellationToken ct);
}

public static class RunDataIO
{
    public const int MaximumRowBytes = 8_000_000;
    public static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Invalid run ID");
    }
    public static async IAsyncEnumerable<string> Lines(Stream stream, int maximumBytes = MaximumRowBytes,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 8192, leaveOpen: true);
        var chars = new char[8192]; var line = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(chars.AsMemory(), ct);
            if (read == 0) break;
            for (var i = 0; i < read; i++)
            {
                if (chars[i] == '\n')
                {
                    var text = line.ToString().TrimEnd('\r');
                    if (Encoding.UTF8.GetByteCount(text) > maximumBytes) throw new InvalidDataException("Audit row exceeds byte limit");
                    yield return text; line.Clear();
                }
                else
                {
                    line.Append(chars[i]);
                    // UTF-16 characters cannot be more numerous than UTF-8 bytes.
                    if (line.Length > maximumBytes) throw new InvalidDataException("Audit row exceeds byte limit");
                }
            }
        }
        if (line.Length > 0)
        {
            var text = line.ToString();
            if (Encoding.UTF8.GetByteCount(text) > maximumBytes) throw new InvalidDataException("Audit row exceeds byte limit");
            yield return text;
        }
    }
    internal static RunDataHead? Prefix(ReadOnlySpan<byte> bytes, bool final)
    {
        var reader = new Utf8JsonReader(bytes, final, default); var values = new JsonObject(); string? key = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                key = reader.GetString();
                if (key is "countries" or "agents" or "locations") return Head(values);
            }
            else if (reader.CurrentDepth == 1 && key is not null)
            {
                values[key] = reader.TokenType switch
                {
                    JsonTokenType.String => JsonValue.Create(reader.GetString()),
                    JsonTokenType.Number => JsonValue.Create(reader.GetInt64()),
                    JsonTokenType.True => JsonValue.Create(true), JsonTokenType.False => JsonValue.Create(false),
                    _ => null
                };
                key = null;
            }
        }
        return final ? Head(values) : null;
    }
    internal static RunDataHead Head(JsonObject values) => new(values["id"]!.GetValue<string>(),
        values["revision"]!.GetValue<long>(), values["audit_sequence"]?.GetValue<long>() ?? 0,
        values["status"]!.GetValue<string>(), values["analytics_enabled"]?.GetValue<bool>(),
        values["tick"] is null ? null : checked((int)values["tick"]!.GetValue<long>()), values["audit_start_tick"] is null ? null : checked((int)values["audit_start_tick"]!.GetValue<long>()),
        values["audit_daily_through_tick"] is null ? null : checked((int)values["audit_daily_through_tick"]!.GetValue<long>()), values["audit_opening_balances_captured"]?.GetValue<bool>(),
        values["updated_at"]?.GetValue<string>());
}
