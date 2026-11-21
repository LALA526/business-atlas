using System.Text.Json;
using Npgsql;

namespace Governance.Baselines;

/// <summary>
/// Records review work caused by a newly published baseline without rewriting the
/// historical context or conformance decision that became stale.
/// </summary>
internal static class BaselineReviewInvalidation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task RecordAsync(NpgsqlTransaction tx, Guid projectId, Guid baselineId, long baselineNumber,
        Guid changeSetId, DateTimeOffset publishedAt, CancellationToken ct)
    {
        var contexts = await ReadAffectedContextsAsync(tx, projectId, changeSetId, ct);
        foreach (var context in contexts)
        {
            var eventId = Guid.NewGuid();
            await ExecuteAsync(tx, """
                INSERT INTO delivery.context_invalidation(project_id,invalidation_id,context_id,cause,event_id,invalidated_at)
                VALUES(@project,@invalidation,@context,'baseline_revision_changed',@event,@publishedAt);
                INSERT INTO ops.notification(project_id,notification_id,principal_id,kind,opaque_ref)
                VALUES(@project,@notification,@principal,'context_review_required',@event);
                INSERT INTO ops.outbox(project_id,event_id,event_type,aggregate_id,aggregate_revision,schema_version,payload,state)
                VALUES(@project,@event,'ReviewRequired.v1',@baseline,@number,'1.0',@payload::jsonb,'queued');
                """, ct,
                ("project", projectId), ("invalidation", Guid.NewGuid()), ("context", context.ContextId),
                ("event", eventId), ("publishedAt", publishedAt), ("notification", Guid.NewGuid()),
                ("principal", context.OwnerId), ("baseline", baselineId), ("number", baselineNumber),
                ("payload", JsonSerializer.Serialize(new { projectId, baselineId, changeSetId, contextId = context.ContextId, reason = "baseline_revision_changed" }, Json)));
        }

        var checks = await ReadAffectedConformanceChecksAsync(tx, projectId, changeSetId, ct);
        foreach (var check in checks)
        {
            var eventId = Guid.NewGuid();
            await ExecuteAsync(tx, """
                INSERT INTO ops.notification(project_id,notification_id,principal_id,kind,opaque_ref)
                VALUES(@project,@notification,@principal,'conformance_review_required',@event);
                INSERT INTO ops.outbox(project_id,event_id,event_type,aggregate_id,aggregate_revision,schema_version,payload,state)
                VALUES(@project,@event,'ReviewRequired.v1',@baseline,@number,'1.0',@payload::jsonb,'queued');
                """, ct,
                ("project", projectId), ("notification", Guid.NewGuid()), ("principal", check.OwnerId),
                ("event", eventId), ("baseline", baselineId), ("number", baselineNumber),
                ("payload", JsonSerializer.Serialize(new { projectId, baselineId, changeSetId, checkId = check.CheckId, reason = "normative_revision_changed" }, Json)));
        }
    }

    private static async Task<List<(Guid ContextId, Guid OwnerId)>> ReadAffectedContextsAsync(
        NpgsqlTransaction tx, Guid projectId, Guid changeSetId, CancellationToken ct)
    {
        var result = new List<(Guid, Guid)>();
        await using var command = tx.Connection!.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT DISTINCT tc.context_id,tc.created_by
            FROM delivery.task_context tc
            JOIN delivery.context_dependency cd ON cd.project_id=tc.project_id AND cd.context_id=tc.context_id
            JOIN gov.change_item ci ON ci.project_id=cd.project_id AND ci.unit_id=cd.unit_id
            WHERE tc.project_id=@project AND ci.change_set_id=@changeSet AND ci.action IN ('revise','retire');
            """;
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("changeSet", changeSetId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add((reader.GetGuid(0), reader.GetGuid(1)));
        return result;
    }

    private static async Task<List<(Guid CheckId, Guid OwnerId)>> ReadAffectedConformanceChecksAsync(
        NpgsqlTransaction tx, Guid projectId, Guid changeSetId, CancellationToken ct)
    {
        var result = new List<(Guid, Guid)>();
        await using var command = tx.Connection!.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT DISTINCT c.check_id,c.created_by
            FROM delivery.conformance c
            JOIN gov.change_item ci ON ci.project_id=c.project_id AND ci.base_revision_id=c.normative_revision_id
            WHERE c.project_id=@project AND ci.change_set_id=@changeSet AND ci.action IN ('revise','retire');
            """;
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("changeSet", changeSetId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add((reader.GetGuid(0), reader.GetGuid(1)));
        return result;
    }

    private static async Task ExecuteAsync(NpgsqlTransaction tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var command = tx.Connection!.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(ct);
    }
}
