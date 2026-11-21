using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessApplicability.Contracts;
using Npgsql;
using NpgsqlTypes;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;

namespace AccessApplicability.Infrastructure;

public sealed class PostgresAccessApplicabilityStore(string connectionString)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "read", "read_history", "read_source", "export", "create_task", "propose", "review", "publish", "administer"
    };

    public async Task<ResolvedReadContext> CreateReadContextAsync(
        Guid principalId, Guid projectId, ReadContextRequest request, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(transaction, projectId, principalId, ct);
        await using var command = transaction.CreateCommand();
        command.CommandText = """
            SELECT p.current_baseline_id, p.policy_epoch, m.roles, m.clearance
            FROM identity.project p
            JOIN identity.membership m ON m.project_id=p.project_id AND m.principal_id=@principal AND m.active
            WHERE p.project_id=@project AND p.archived_at IS NULL;
            """;
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("principal", principalId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        var currentBaseline = reader.GetGuid(0);
        var epoch = reader.GetInt64(1);
        var roles = reader.GetFieldValue<string[]>(2);
        var clearance = reader.GetInt32(3);
        await reader.CloseAsync();

        if (!Enum.IsDefined(request.Mode) || !Enum.IsDefined(request.Purpose))
            throw ProjectIdentityException.Invalid("The read mode or purpose is invalid.");
        var baselineId = request.BaselineId ?? currentBaseline;
        DateTimeOffset? historicalAsOf = null;
        if (request.Mode == ReadMode.Published)
        {
            if (baselineId != currentBaseline) throw ProjectIdentityException.Invalid("Published reads must use the current baseline; use historical mode for an older baseline.");
        }
        else if (request.Mode == ReadMode.Historical)
        {
            if (request.BaselineId is null) throw ProjectIdentityException.Invalid("Historical reads require an explicit baselineId.");
            if (!await HasGrantAsync(transaction, projectId, principalId, "read_history", null, ct)) throw ProjectIdentityException.Forbidden();
            historicalAsOf = await RequireSealedBaselineAsync(transaction, projectId, baselineId, ct);
        }
        else
        {
            if (request.ChangeSetId is null) throw ProjectIdentityException.Invalid("Draft reads require an explicit changeSetId.");
            if (!roles.Any(role => role is "Contributor" or "AgentProposer" or "ProjectAdmin")) throw ProjectIdentityException.Forbidden();
            await RequireDraftAsync(transaction, projectId, request.ChangeSetId.Value, baselineId, ct);
        }

        var context = request.Context ?? new ApplicableContext(null, null, null, null, null, null);
        var asOf = context.AsOf ?? historicalAsOf ?? DateTimeOffset.UtcNow;
        context = context with { AsOf = asOf };
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            projectId, principalId, baselineId, mode = request.Mode, request.ChangeSetId, epoch, context, request.Purpose
        }, Json))).ToLowerInvariant();
        return new ResolvedReadContext(projectId, principalId, baselineId, request.Mode, request.ChangeSetId,
            epoch, roles, clearance, hash, asOf, request.Purpose, context);
    }

    public async Task EnsureCurrentAsync(ResolvedReadContext context, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(transaction, context.ProjectId, context.PrincipalId, ct);
        await using var command = transaction.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
              SELECT 1 FROM identity.project p
              JOIN identity.membership m ON m.project_id=p.project_id AND m.principal_id=@principal AND m.active
              WHERE p.project_id=@project AND p.archived_at IS NULL AND p.policy_epoch=@epoch);
            """;
        command.Parameters.AddWithValue("project", context.ProjectId);
        command.Parameters.AddWithValue("principal", context.PrincipalId);
        command.Parameters.AddWithValue("epoch", context.PolicyEpoch);
        if (await command.ExecuteScalarAsync(ct) is not true)
            throw new ProjectIdentityException("POLICY_CONTEXT_STALE", "The authorization context has been revoked or superseded; reauthorize before reading.", 403);
    }

    public async Task<ResourceAccess> AuthorizeReadAsync(ResolvedReadContext context, Guid unitId, string action, Guid? revisionId, CancellationToken ct)
    {
        if (!Actions.Contains(action)) throw ProjectIdentityException.Invalid("The requested resource action is unknown.");
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(transaction, context.ProjectId, context.PrincipalId, ct);
        await using var epochCommand = transaction.CreateCommand();
        epochCommand.CommandText = "SELECT policy_epoch FROM identity.project WHERE project_id=@project AND archived_at IS NULL;";
        epochCommand.Parameters.AddWithValue("project", context.ProjectId);
        if (await epochCommand.ExecuteScalarAsync(ct) is not long epoch || epoch != context.PolicyEpoch)
            throw new ProjectIdentityException("POLICY_CONTEXT_STALE", "The authorization context is stale; reauthorize before reading.", 403);
        await using var membership = transaction.CreateCommand();
        membership.CommandText = "SELECT clearance FROM identity.membership WHERE project_id=@project AND principal_id=@principal AND active;";
        membership.Parameters.AddWithValue("project", context.ProjectId);
        membership.Parameters.AddWithValue("principal", context.PrincipalId);
        if (await membership.ExecuteScalarAsync(ct) is not int currentClearance || currentClearance != context.Clearance)
            throw new ProjectIdentityException("POLICY_CONTEXT_STALE", "Membership clearance has changed; reauthorize before reading.", 403);

        var revisionSql = context.Mode == ReadMode.Draft ? """
            SELECT u.visibility, r.revision_id, r.applicability
            FROM core.unit u
            JOIN gov.change_item ci ON ci.project_id=u.project_id AND ci.unit_id=u.unit_id AND ci.change_set_id=@changeSet
            JOIN core.revision r ON r.project_id=u.project_id AND r.unit_id=u.unit_id AND r.revision_id=ci.proposed_revision_id
            WHERE u.project_id=@project AND u.unit_id=@unit AND u.archived_at IS NULL
              AND (@revision IS NULL OR r.revision_id=@revision)
            UNION ALL
            SELECT u.visibility, r.revision_id, r.applicability
            FROM core.unit u JOIN gov.baseline_entry be ON be.project_id=u.project_id AND be.unit_id=u.unit_id AND be.baseline_id=@baseline
            JOIN core.revision r ON r.project_id=u.project_id AND r.unit_id=u.unit_id AND r.revision_id=be.revision_id
            WHERE u.project_id=@project AND u.unit_id=@unit AND u.archived_at IS NULL
              AND (@revision IS NULL OR be.revision_id=@revision)
              AND NOT EXISTS (SELECT 1 FROM gov.change_item ci WHERE ci.project_id=@project AND ci.unit_id=@unit AND ci.change_set_id=@changeSet)
            LIMIT 1;
            """ : """
            SELECT u.visibility, r.revision_id, r.applicability
            FROM core.unit u
            JOIN gov.baseline_entry be ON be.project_id=u.project_id AND be.unit_id=u.unit_id AND be.baseline_id=@baseline
            JOIN core.revision r ON r.project_id=u.project_id AND r.unit_id=u.unit_id AND r.revision_id=be.revision_id
            WHERE u.project_id=@project AND u.unit_id=@unit AND u.archived_at IS NULL
              AND (@revision IS NULL OR be.revision_id=@revision);
            """;
        await using var unit = transaction.CreateCommand();
        unit.CommandText = revisionSql;
        unit.Parameters.AddWithValue("project", context.ProjectId);
        unit.Parameters.AddWithValue("unit", unitId);
        unit.Parameters.AddWithValue("baseline", context.BaselineId);
        unit.Parameters.AddWithValue("changeSet", NpgsqlDbType.Uuid, (object?)context.ChangeSetId ?? DBNull.Value);
        unit.Parameters.AddWithValue("revision", NpgsqlDbType.Uuid, (object?)revisionId ?? DBNull.Value);
        await using var revisionReader = await unit.ExecuteReaderAsync(ct);
        if (!await revisionReader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        var visibility = revisionReader.GetInt32(0);
        var resolvedRevisionId = revisionReader.GetGuid(1);
        var applicability = JsonDocument.Parse(revisionReader.GetString(2)).RootElement.Clone();
        await revisionReader.CloseAsync();
        if (context.Mode == ReadMode.Historical && !await HasGrantAsync(transaction, context.ProjectId, context.PrincipalId, "read_history", unitId, ct))
            throw ProjectIdentityException.NotFound();

        await using var grants = transaction.CreateCommand();
        grants.CommandText = """
            SELECT effect, field_paths FROM access.grant
            WHERE project_id=@project AND unit_id=@unit AND principal_id=@principal AND action=@action;
            """;
        grants.Parameters.AddWithValue("project", context.ProjectId);
        grants.Parameters.AddWithValue("unit", unitId);
        grants.Parameters.AddWithValue("principal", context.PrincipalId);
        grants.Parameters.AddWithValue("action", action);
        var allowed = new List<string>();
        var denied = new List<string>();
        var hasAllow = false;
        var hasWholeDeny = false;
        await using var grantReader = await grants.ExecuteReaderAsync(ct);
        while (await grantReader.ReadAsync(ct))
        {
            var fieldPaths = grantReader.GetFieldValue<string[]>(1);
            if (grantReader.GetString(0) == "deny")
            {
                hasWholeDeny |= fieldPaths.Length == 0 || fieldPaths.Contains("*", StringComparer.Ordinal);
                denied.AddRange(fieldPaths);
            }
            else { hasAllow = true; allowed.AddRange(fieldPaths); }
        }
        await grantReader.CloseAsync();
        if (hasWholeDeny || !hasAllow && context.Clearance < visibility)
            throw ProjectIdentityException.NotFound();
        return new ResourceAccess(unitId, resolvedRevisionId, applicability, allowed.Distinct(StringComparer.Ordinal).ToArray(), denied.Distinct(StringComparer.Ordinal).ToArray());
    }

    public async Task<GrantMutation> SetGrantAsync(AtlasPrincipal actor, Guid projectId, ResourceGrant input,
        long expectedVersion, string idempotencyKey, string requestId, string? traceId, CancellationToken ct)
    {
        ValidateGrant(input, idempotencyKey);
        if (actor.Kind != PrincipalKind.Human) throw ProjectIdentityException.Forbidden();
        var digest = Digest(new { input.GrantId, input.UnitId, input.PrincipalId, input.Action, input.FieldPaths, input.Effect, input.Version, expectedVersion });
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(transaction, projectId, actor.PrincipalId, ct);
        var project = await RequireAdminAsync(transaction, projectId, actor.PrincipalId, ct);
        var securityApproved = project.Roles.Contains("SecurityOwner", StringComparer.Ordinal) ||
            input.Action == "read_source" && project.Roles.Contains("DataOwner", StringComparer.Ordinal);
        if (input.Action is "read_source" or "review" or "publish" or "administer" && !securityApproved)
            throw ProjectIdentityException.Forbidden();
        var replay = await FindIdempotencyAsync(transaction, projectId, actor.PrincipalId, idempotencyKey, digest, ct);
        if (replay is not null) { await transaction.CommitAsync(ct); return replay; }
        if (project.Version != expectedVersion) throw ProjectIdentityException.CasMismatch();
        if (!await UnitExistsAsync(transaction, projectId, input.UnitId, ct) || !await PrincipalExistsAsync(transaction, input.PrincipalId, ct))
            throw ProjectIdentityException.NotFound();

        ResourceGrant? existing = null;
        await using (var find = transaction.CreateCommand())
        {
            find.CommandText = input.GrantId.HasValue
                ? "SELECT grant_id, unit_id, principal_id, action, field_paths, effect, version FROM access.grant WHERE project_id=@project AND grant_id=@id FOR UPDATE;"
                : "SELECT grant_id, unit_id, principal_id, action, field_paths, effect, version FROM access.grant WHERE project_id=@project AND unit_id=@unit AND principal_id=@principal AND action=@action AND effect=@effect FOR UPDATE;";
            find.Parameters.AddWithValue("project", projectId);
            find.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, (object?)input.GrantId ?? DBNull.Value);
            find.Parameters.AddWithValue("unit", input.UnitId);
            find.Parameters.AddWithValue("principal", input.PrincipalId);
            find.Parameters.AddWithValue("action", input.Action);
            find.Parameters.AddWithValue("effect", input.Effect);
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) existing = ReadGrant(reader);
        }
        if (input.GrantId.HasValue && existing is null) throw ProjectIdentityException.NotFound();
        if (existing is not null && (existing.UnitId != input.UnitId || existing.PrincipalId != input.PrincipalId ||
            existing.Action != input.Action || existing.Effect != input.Effect))
            throw ProjectIdentityException.Invalid("A grant's resource, principal, action and effect are immutable.");
        if (existing is not null && input.Version.HasValue && existing.Version != input.Version) throw ProjectIdentityException.CasMismatch();

        var grantId = existing?.GrantId ?? Guid.NewGuid();
        var version = (existing?.Version ?? 0) + 1;
        await using (var write = transaction.CreateCommand())
        {
            write.CommandText = existing is null ? """
                INSERT INTO access.grant (project_id,grant_id,unit_id,principal_id,action,field_paths,effect,created_by,version)
                VALUES (@project,@id,@unit,@principal,@action,@fields,@effect,@actor,1);
                """ : """
                UPDATE access.grant SET field_paths=@fields, version=version+1
                WHERE project_id=@project AND grant_id=@id AND version=@expectedGrant;
                """;
            write.Parameters.AddWithValue("project", projectId);
            write.Parameters.AddWithValue("id", grantId);
            write.Parameters.AddWithValue("unit", input.UnitId);
            write.Parameters.AddWithValue("principal", input.PrincipalId);
            write.Parameters.AddWithValue("action", input.Action);
            write.Parameters.Add(new NpgsqlParameter("fields", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = input.FieldPaths.ToArray() });
            write.Parameters.AddWithValue("effect", input.Effect);
            write.Parameters.AddWithValue("actor", actor.PrincipalId);
            write.Parameters.AddWithValue("expectedGrant", existing?.Version ?? 0);
            await write.ExecuteNonQueryAsync(ct);
        }
        var grant = input with { GrantId = grantId, Version = version };
        var policyVersion = checked(project.Version + 1);
        await using (var update = transaction.CreateCommand())
        {
            update.CommandText = """
                UPDATE identity.project SET version=version+1, policy_epoch=policy_epoch+1 WHERE project_id=@project;
                INSERT INTO ops.idempotency (project_id,principal_id,operation_id,idempotency_key,request_sha256,result_ref)
                VALUES (@project,@actor,'setResourceGrant',@key,@digest,@result::jsonb);
                INSERT INTO ops.audit_event (project_id,actor_id,action,resource_ref,result_code,request_id,trace_id)
                VALUES (@project,@actor,'setResourceGrant',@resource::jsonb,'OK',@request,@trace);
                INSERT INTO ops.outbox (project_id,event_id,event_type,aggregate_id,aggregate_revision,schema_version,payload)
                VALUES (@project,@event,'AccessPolicyChanged.v1',@unit,@policyVersion,'1.2',@eventPayload::jsonb);
                """;
            update.Parameters.AddWithValue("project", projectId);
            update.Parameters.AddWithValue("actor", actor.PrincipalId);
            update.Parameters.AddWithValue("key", idempotencyKey);
            update.Parameters.AddWithValue("digest", digest);
            update.Parameters.Add(new NpgsqlParameter("result", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new GrantMutation(grant, policyVersion), Json) });
            update.Parameters.Add(new NpgsqlParameter("resource", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(grant, Json) });
            update.Parameters.AddWithValue("request", requestId);
            update.Parameters.AddWithValue("trace", NpgsqlDbType.Text, (object?)traceId ?? DBNull.Value);
            update.Parameters.AddWithValue("event", Guid.NewGuid());
            update.Parameters.AddWithValue("unit", input.UnitId);
            update.Parameters.AddWithValue("policyVersion", policyVersion);
            update.Parameters.Add(new NpgsqlParameter("eventPayload", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { projectId, unitId = input.UnitId, principalId = input.PrincipalId, policyEpoch = project.Epoch + 1 }, Json) });
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new GrantMutation(grant, policyVersion);
    }

    public async Task<ResourceGrantPage> ListGrantsAsync(Guid principalId, Guid projectId, string? cursor, int limit, CancellationToken ct)
    {
        var after = DecodeCursor(cursor);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(transaction, projectId, principalId, ct);
        _ = await RequireAdminAsync(transaction, projectId, principalId, ct);
        await using var command = transaction.CreateCommand();
        command.CommandText = """
            SELECT grant_id,unit_id,principal_id,action,field_paths,effect,version FROM access.grant
            WHERE project_id=@project AND (@after IS NULL OR grant_id>@after) ORDER BY grant_id LIMIT @limit;
            """;
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("after", NpgsqlDbType.Uuid, (object?)after ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit + 1);
        var items = new List<ResourceGrant>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(ReadGrant(reader));
        string? next = items.Count > limit ? EncodeCursor(items[limit - 1].GrantId!.Value) : null;
        if (items.Count > limit) items.RemoveAt(limit);
        return new ResourceGrantPage(items, next);
    }

    private static async Task<(long Version, long Epoch, string[] Roles)> RequireAdminAsync(NpgsqlTransaction tx, Guid projectId, Guid principalId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = """
            SELECT p.version,p.policy_epoch,m.roles FROM identity.project p
            JOIN identity.membership m ON m.project_id=p.project_id AND m.principal_id=@principal AND m.active
            WHERE p.project_id=@project AND p.archived_at IS NULL FOR UPDATE OF p;
            """;
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("principal", principalId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        var result = (reader.GetInt64(0), reader.GetInt64(1), reader.GetFieldValue<string[]>(2));
        if (!result.Item3.Contains("ProjectAdmin", StringComparer.Ordinal)) throw ProjectIdentityException.Forbidden();
        return result;
    }

    private static async Task<bool> HasGrantAsync(NpgsqlTransaction tx, Guid projectId, Guid principalId, string action, Guid? unitId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = unitId.HasValue ? """
            SELECT NOT EXISTS (SELECT 1 FROM access.grant WHERE project_id=@project AND unit_id=@unit AND principal_id=@principal AND action=@action AND effect='deny' AND (cardinality(field_paths)=0 OR field_paths @> ARRAY['*']))
               AND EXISTS (SELECT 1 FROM access.grant WHERE project_id=@project AND unit_id=@unit AND principal_id=@principal AND action=@action AND effect='allow');
            """ : """
            SELECT EXISTS (
              SELECT 1 FROM access.grant allowed
              WHERE allowed.project_id=@project AND allowed.principal_id=@principal AND allowed.action=@action AND allowed.effect='allow'
                AND NOT EXISTS (SELECT 1 FROM access.grant denied WHERE denied.project_id=allowed.project_id AND denied.unit_id=allowed.unit_id
                  AND denied.principal_id=allowed.principal_id AND denied.action=allowed.action AND denied.effect='deny'
                  AND (cardinality(denied.field_paths)=0 OR denied.field_paths @> ARRAY['*'])));
            """;
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("unit", NpgsqlDbType.Uuid, (object?)unitId ?? DBNull.Value);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<DateTimeOffset> RequireSealedBaselineAsync(NpgsqlTransaction tx, Guid projectId, Guid baselineId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = "SELECT created_at FROM gov.baseline WHERE project_id=@project AND baseline_id=@baseline AND sealed;";
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("baseline", baselineId);
        var value = await command.ExecuteScalarAsync(ct);
        return value switch
        {
            DateTimeOffset timestamp => timestamp,
            DateTime timestamp => new DateTimeOffset(timestamp, TimeSpan.Zero),
            _ => throw ProjectIdentityException.NotFound()
        };
    }

    private static async Task RequireDraftAsync(NpgsqlTransaction tx, Guid projectId, Guid changeSetId, Guid baselineId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM gov.change_set WHERE project_id=@project AND change_set_id=@changeSet AND base_baseline_id=@baseline AND state NOT IN ('published','rejected','abandoned'));";
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("changeSet", changeSetId);
        command.Parameters.AddWithValue("baseline", baselineId);
        if (await command.ExecuteScalarAsync(ct) is not true) throw ProjectIdentityException.NotFound();
    }

    private static async Task<bool> UnitExistsAsync(NpgsqlTransaction tx, Guid projectId, Guid unitId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM core.unit WHERE project_id=@project AND unit_id=@unit AND archived_at IS NULL);";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("unit", unitId);
        return await command.ExecuteScalarAsync(ct) is true;
    }
    private static async Task<bool> PrincipalExistsAsync(NpgsqlTransaction tx, Guid principalId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM identity.principal WHERE principal_id=@principal AND active);";
        command.Parameters.AddWithValue("principal", principalId); return await command.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<GrantMutation?> FindIdempotencyAsync(NpgsqlTransaction tx, Guid projectId, Guid principalId, string key, string digest, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = "SELECT request_sha256,result_ref FROM ops.idempotency WHERE project_id=@project AND principal_id=@principal AND operation_id='setResourceGrant' AND idempotency_key=@key;";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("principal", principalId); command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(reader.GetString(0)), Encoding.ASCII.GetBytes(digest)))
            throw ProjectIdentityException.Conflict("The idempotency key was already used with a different request.");
        return JsonSerializer.Deserialize<GrantMutation>(reader.GetString(1), Json);
    }

    private static void ValidateGrant(ResourceGrant input, string key)
    {
        if (key.Length is < 8 or > 128) throw ProjectIdentityException.Invalid("Idempotency-Key must contain 8-128 characters.");
        if (string.IsNullOrWhiteSpace(input.Action) || string.IsNullOrWhiteSpace(input.Effect) || input.FieldPaths is null ||
            !Actions.Contains(input.Action) || input.Effect is not ("allow" or "deny") || input.UnitId == Guid.Empty || input.PrincipalId == Guid.Empty)
            throw ProjectIdentityException.Invalid("The resource grant action, effect or target is invalid.");
        if (input.FieldPaths.Count > 100 || input.FieldPaths.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 512 || path != "*" && (!path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal))))
            throw ProjectIdentityException.Invalid("Grant field paths must be JSON pointers or '*'.");
        if (input.FieldPaths.Distinct(StringComparer.Ordinal).Count() != input.FieldPaths.Count)
            throw ProjectIdentityException.Invalid("Grant field paths must be unique.");
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("ConnectionStrings:Atlas is required for access authorization.");
        var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct); return connection;
    }
    private static async Task SetContextAsync(NpgsqlTransaction tx, Guid projectId, Guid principalId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = "SELECT set_config('atlas.project_id',@project,true),set_config('atlas.subject_id',@principal,true);";
        command.Parameters.AddWithValue("project", projectId.ToString()); command.Parameters.AddWithValue("principal", principalId.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }
    private static ResourceGrant ReadGrant(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetFieldValue<string[]>(4), reader.GetString(5), reader.GetInt64(6));
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json))).ToLowerInvariant();
    private static string EncodeCursor(Guid id) => Convert.ToBase64String(id.ToByteArray()).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static Guid? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        try { return new Guid(Convert.FromBase64String(cursor.Replace('-','+').Replace('_','/') + new string('=', (4-cursor.Length%4)%4))); }
        catch (Exception e) when (e is FormatException or ArgumentException) { throw ProjectIdentityException.Invalid("The pagination cursor is invalid."); }
    }
}
