using System.Data.Common;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Commands;
using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace PlatformOps;

public sealed record AuditEntry(
    Guid AuditId,
    string Action,
    JsonElement ResourceRef,
    string ResultCode,
    DateTimeOffset OccurredAt,
    string RequestId,
    Guid? ActorId);

public sealed record AuditEntryPage(IReadOnlyList<AuditEntry> Items, string? NextCursor);

/// <summary>Implements listAudit with fresh membership, role and scope checks plus RLS.</summary>
public sealed class AuditQueryService(AtlasDbContext database)
{
    public async Task<AuditEntryPage> ListAsync(
        Guid projectId,
        Guid principalId,
        IReadOnlyCollection<string> grantedScopes,
        string? cursor,
        int limit = 30,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty || principalId == Guid.Empty)
        {
            throw new AtlasCommandException("UNAUTHENTICATED", "Authentication is required.", 401);
        }

        if (!grantedScopes.Contains("atlas.admin", StringComparer.Ordinal))
        {
            throw new AtlasCommandException("FORBIDDEN", "The current principal cannot read audit events.", 403);
        }

        if (limit is < 1 or > 100)
        {
            throw new AtlasCommandException("VALIDATION_FAILED", "The page limit must be between 1 and 100.", 400);
        }

        var after = DecodeCursor(projectId, cursor);
        await using var transaction = await AtlasTransaction.BeginAsync(database, projectId, principalId, cancellationToken);
        await using (var authorization = CreateCommand(transaction,
            """
            SELECT EXISTS(
              SELECT 1 FROM identity.membership
              WHERE project_id = @project AND principal_id = @principal AND active
                AND roles && @roles
            )
            """))
        {
            AddParameter(authorization, "project", projectId);
            AddParameter(authorization, "principal", principalId);
            AddParameter(authorization, "roles", new[] { "SecurityOwner", "ProjectAdmin" });
            var authorized = await authorization.ExecuteScalarAsync(cancellationToken);
            if (authorized is not true)
            {
                throw new AtlasCommandException("FORBIDDEN", "The current principal cannot read audit events.", 403);
            }
        }

        var sql =
            """
            SELECT audit_id, actor_id, action, resource_ref, result_code, request_id, occurred_at
            FROM ops.audit_event
            WHERE project_id = @project
            """;
        if (after is not null)
        {
            sql += " AND (occurred_at, audit_id) < (@afterTime, @afterId)";
        }

        sql += " ORDER BY occurred_at DESC, audit_id DESC LIMIT @limit";
        await using var command = CreateCommand(transaction, sql);
        AddParameter(command, "project", projectId);
        if (after is not null)
        {
            AddParameter(command, "afterTime", after.OccurredAt, NpgsqlDbType.TimestampTz);
            AddParameter(command, "afterId", after.AuditId, NpgsqlDbType.Uuid);
        }

        AddParameter(command, "limit", limit + 1, NpgsqlDbType.Integer);
        var entries = new List<AuditEntry>(limit + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                entries.Add(new AuditEntry(
                    reader.GetGuid(0),
                    reader.GetString(2),
                    JsonDocument.Parse(reader.GetString(3)).RootElement.Clone(),
                    reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(6),
                    reader.GetString(5),
                    reader.IsDBNull(1) ? null : reader.GetGuid(1)));
            }
        }

        string? nextCursor = null;
        if (entries.Count > limit)
        {
            entries.RemoveAt(limit);
            var last = entries[^1];
            nextCursor = EncodeCursor(projectId, last.AuditId, last.OccurredAt);
        }

        return new AuditEntryPage(entries, nextCursor);
    }

    private static DbCommand CreateCommand(AtlasTransaction transaction, string sql)
    {
        var dbTransaction = transaction.ConnectionTransaction;
        var command = dbTransaction.Connection!.CreateCommand();
        command.Transaction = dbTransaction;
        command.CommandText = sql;
        return command;
    }

    private static string EncodeCursor(Guid projectId, Guid auditId, DateTimeOffset occurredAt)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Cursor(projectId, auditId, occurredAt));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static Cursor? DecodeCursor(Guid projectId, string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
            var value = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(base64))
                ?? throw new JsonException("Empty cursor.");
            return value.ProjectId == projectId && value.AuditId != Guid.Empty
                ? value
                : throw new JsonException("Cursor project mismatch.");
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw new AtlasCommandException("VALIDATION_FAILED", "The audit cursor is invalid.", 400);
        }
    }

    private static void AddParameter(DbCommand command, string name, object? value, NpgsqlDbType? type = null)
    {
        var parameter = new NpgsqlParameter { ParameterName = name, Value = value ?? DBNull.Value };
        if (type.HasValue)
        {
            parameter.NpgsqlDbType = type.Value;
        }

        command.Parameters.Add(parameter);
    }

    private sealed record Cursor(Guid ProjectId, Guid AuditId, DateTimeOffset OccurredAt);
}
