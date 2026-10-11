using System.Text.Json;
using System.Text.Json.Nodes;

namespace GEarth.Core;

public sealed partial class NeonStore : IRunDataStore
{
    // The catalog deliberately uses only scalar columns. Legacy nullable fields stay unknown.
    public async Task<RunDataHead[]> ReadRunCatalog(string? beforeId, int limit, CancellationToken ct)
    {
        var rows = await Query("SELECT run_id,revision,audit_sequence,run_status,analytics_enabled,updated_at FROM gearth.stage1_runs WHERE ($1::text IS NULL OR run_id COLLATE \"C\"<$1 COLLATE \"C\") ORDER BY run_id COLLATE \"C\" DESC LIMIT $2::int", [beforeId, limit.ToString()], ct);
        return rows.Select(r => new RunDataHead(r![0]!.GetValue<string>(), long.Parse(r[1]!.GetValue<string>()),
            long.Parse(r[2]?.GetValue<string>() ?? "0"), r[3]?.GetValue<string>() ?? "unknown",
            r[4] is null ? null : r[4]!.GetValue<string>() is "t" or "true", null, null, null, null, r[5]?.GetValue<string>())).ToArray();
    }
    public async Task<RunDataHead?> ReadRunDataHead(string id, CancellationToken ct)
    {
        RunDataIO.ValidateId(id);
        var rows = await Query("SELECT json_build_object('id',run_id,'revision',revision,'audit_sequence',COALESCE(audit_sequence,(state->>'audit_sequence')::bigint,0),'status',COALESCE(run_status,state->>'status'),'analytics_enabled',(state->>'analytics_enabled')::boolean,'tick',(state->>'tick')::int,'audit_start_tick',(state->>'audit_start_tick')::int,'audit_daily_through_tick',(state->>'audit_daily_through_tick')::int,'audit_opening_balances_captured',(state->>'audit_opening_balances_captured')::boolean,'updated_at',updated_at) FROM gearth.stage1_runs WHERE run_id=$1", [id], ct);
        return rows.Count == 0 ? null : RunDataIO.Head(JsonNode.Parse(rows[0]![0]!.GetValue<string>())!.AsObject());
    }
    public async Task<bool> VerifyAuditPin(string id, long revision, long sequence, CancellationToken ct)
    {
        var rows = await Query("SELECT 1 FROM gearth.stage1_runs r WHERE run_id=$1 AND $2::bigint<=r.revision AND $3::bigint<=COALESCE(r.audit_sequence,(r.state->>'audit_sequence')::bigint,0) AND (($2::bigint=r.revision AND $3::bigint=COALESCE(r.audit_sequence,(r.state->>'audit_sequence')::bigint,0)) OR ((EXISTS(SELECT 1 FROM gearth.run_audit_commits WHERE run_id=$1 AND revision=$2::bigint) OR EXISTS(SELECT 1 FROM gearth.checkpoints WHERE run_id=$1 AND revision=$2::bigint) OR EXISTS(SELECT 1 FROM gearth.renderer_frames WHERE run_id=$1 AND revision=$2::bigint)) AND $3::bigint=COALESCE((SELECT through_sequence FROM gearth.run_audit_commits WHERE run_id=$1 AND revision<=$2::bigint ORDER BY revision DESC LIMIT 1),0)))", [id, revision.ToString(), sequence.ToString()], ct);
        return rows.Count > 0;
    }
    public async Task<RunSaveMarker[]> ReadSaveMarkers(string id, long afterRevision, int limit, CancellationToken ct)
    {
        var rows = await Query("SELECT c.revision,c.tick FROM gearth.checkpoints c JOIN gearth.stage1_runs r ON r.run_id=c.run_id WHERE c.run_id=$1 AND c.revision>$2::bigint AND c.revision<=r.revision ORDER BY c.revision LIMIT $3::int", [id, afterRevision.ToString(), limit.ToString()], ct);
        return rows.Select(r => new RunSaveMarker(long.Parse(r![0]!.GetValue<string>()), int.Parse(r[1]!.GetValue<string>()))).ToArray();
    }
    public async Task<AuditRecord[]> ReadPinnedAudit(string id, long revision, long sequence, long after, int limit, CancellationToken ct)
    {
        var rows = await Query("""
            WITH candidate AS (SELECT a.sequence,a.envelope FROM gearth.run_audit a
            JOIN gearth.run_audit_commits c ON c.run_id=a.run_id AND c.revision=a.revision AND c.batch_id=a.batch_id
            JOIN gearth.stage1_runs r ON r.run_id=a.run_id
            WHERE a.run_id=$1 AND a.sequence>$2::bigint AND a.sequence<=$3::bigint AND a.revision<=$4::bigint
              AND c.revision<=r.revision AND a.sequence<=c.through_sequence
            ORDER BY a.sequence LIMIT $5::int), sized AS (
            SELECT *,sum(octet_length(envelope::text)) OVER(ORDER BY sequence) AS bytes FROM candidate)
            SELECT envelope FROM sized WHERE bytes<=8000000 ORDER BY sequence
            """, [id, after.ToString(), sequence.ToString(), revision.ToString(), limit.ToString()], ct);
        return rows.Select(r => JsonSerializer.Deserialize<AuditRecord>(r![0]!.GetValue<string>(), Wire.Json)!).ToArray();
    }
}
