using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;

namespace ProjectIdentity.Infrastructure;

public sealed partial class PostgresProjectIdentityStore(string connectionString)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string EmptyBaselineDigest = Convert.ToHexString(SHA256.HashData("{}"u8.ToArray())).ToLowerInvariant();

    public async Task<AtlasPrincipal> ResolvePrincipalAsync(
        string issuer, string subject, PrincipalKind kind, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject) || issuer.Length > 2048 || subject.Length > 512)
            throw new ProjectIdentityException("INVALID_SUBJECT", "The token issuer and subject are required.", 401);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT out_principal_id, out_issuer, out_subject, out_kind, out_active
            FROM identity.resolve_principal(@issuer, @subject, @kind);
            """;
        command.Parameters.AddWithValue("issuer", issuer);
        command.Parameters.AddWithValue("subject", subject);
        command.Parameters.AddWithValue("kind", KindName(kind));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new ProjectIdentityException("INVALID_SUBJECT", "The subject could not be resolved.", 401);
        var principal = ReadPrincipal(reader);
        if (!principal.Active) throw ProjectIdentityException.Forbidden();
        return principal;
    }

    public async Task<ProjectPage> ListProjectsAsync(
        Guid principalId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var after = DecodeCursor(cursor);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetContextAsync(transaction, null, principalId, cancellationToken);
        await using var command = transaction.CreateCommand();
        command.CommandText = """
            SELECT p.project_id, p.slug, p.display_name, p.version, p.policy_epoch, p.current_baseline_id
            FROM identity.project p
            JOIN identity.membership m ON m.project_id = p.project_id
            WHERE m.principal_id = @principal AND m.active AND p.archived_at IS NULL
              AND (@after IS NULL OR p.project_id > @after)
            ORDER BY p.project_id LIMIT @limit;
            """;
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.AddWithValue("after", NpgsqlDbType.Uuid, (object?)after ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit + 1);
        return await ReadProjectPageAsync(command, limit, cancellationToken);
    }

    public async Task<ProjectSummary> CreateProjectAsync(
        AtlasPrincipal principal, CreateProjectCommand input, string requestId, string? traceId,
        CancellationToken cancellationToken)
    {
        ValidateProject(input.DisplayName, input.Slug, input.IdempotencyKey);
        if (principal.Kind != PrincipalKind.Human) throw ProjectIdentityException.Forbidden();
        var digest = Digest(new { input.DisplayName, input.Slug });
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetContextAsync(transaction, null, principal.PrincipalId, cancellationToken);
        await using (var lockCommand = transaction.CreateCommand())
        {
            lockCommand.CommandText = "SELECT pg_advisory_xact_lock(hashtext(@key));";
            lockCommand.Parameters.AddWithValue("key", $"createProject:{principal.PrincipalId}:{input.IdempotencyKey}");
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var replay = await FindProjectIdempotencyAsync(transaction, principal.PrincipalId, input.IdempotencyKey, digest, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }

        var projectId = Guid.NewGuid();
        var baselineId = Guid.NewGuid();
        await SetContextAsync(transaction, projectId, principal.PrincipalId, cancellationToken);
        try
        {
            await ExecuteAsync(transaction, """
                INSERT INTO identity.project (project_id, slug, display_name, owner_id)
                VALUES (@project, @slug, @name, @principal);
                INSERT INTO identity.membership (project_id, principal_id, roles)
                VALUES (@project, @principal, ARRAY['ProjectAdmin']);
                INSERT INTO gov.baseline
                  (project_id, baseline_id, baseline_no, registry_version, digest, sealed, created_by)
                VALUES (@project, @baseline, 0, 'atlas.empty-v1', @digest, true, @principal);
                UPDATE identity.project SET current_baseline_id = @baseline WHERE project_id = @project;
                INSERT INTO identity.project_profile (project_id, profile_kind, policy_version)
                VALUES (@project, 'balanced', '1.0');
                """, cancellationToken,
                ("project", projectId), ("slug", input.Slug), ("name", input.DisplayName),
                ("principal", principal.PrincipalId), ("baseline", baselineId), ("digest", EmptyBaselineDigest));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw ProjectIdentityException.Conflict("The project slug is already in use.");
        }

        var result = new ProjectSummary(projectId, input.Slug, input.DisplayName, 1, 1, baselineId);
        await SaveIdempotencyAsync(transaction, projectId, principal.PrincipalId, "createProject", input.IdempotencyKey, digest, result, cancellationToken);
        await AuditAsync(transaction, projectId, principal.PrincipalId, "createProject", result, requestId, traceId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<ProjectSummary> GetProjectAsync(Guid principalId, Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetContextAsync(transaction, projectId, principalId, cancellationToken);
        await using var command = transaction.CreateCommand();
        command.CommandText = """
            SELECT p.project_id, p.slug, p.display_name, p.version, p.policy_epoch, p.current_baseline_id
            FROM identity.project p
            JOIN identity.membership m ON m.project_id = p.project_id AND m.principal_id = @principal AND m.active
            WHERE p.project_id = @project AND p.archived_at IS NULL;
            """;
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.AddWithValue("project", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw ProjectIdentityException.NotFound();
        return ReadProject(reader);
    }

    public async Task<AuthorizedReadContext> CreateReadContextAsync(Guid principalId, Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetContextAsync(transaction, projectId, principalId, cancellationToken);
        await using var command = transaction.CreateCommand();
        command.CommandText = """
            SELECT p.project_id, p.current_baseline_id, p.policy_epoch, m.roles, m.clearance
            FROM identity.project p
            JOIN identity.membership m ON m.project_id = p.project_id AND m.principal_id = @principal AND m.active
            WHERE p.project_id = @project AND p.archived_at IS NULL;
            """;
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.AddWithValue("project", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw ProjectIdentityException.NotFound();
        return new AuthorizedReadContext(reader.GetGuid(0), principalId, reader.GetGuid(1), reader.GetInt64(2),
            reader.GetFieldValue<string[]>(3), reader.GetInt32(4));
    }

    public async Task<ProjectSummary> UpdateProjectAsync(
        AtlasPrincipal principal, Guid projectId, UpdateProjectCommand input, string requestId, string? traceId,
        CancellationToken cancellationToken)
    {
        ValidateName(input.DisplayName);
        ValidateKey(input.IdempotencyKey);
        var digest = Digest(new { input.DisplayName, input.ExpectedVersion });
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetContextAsync(transaction, projectId, principal.PrincipalId, cancellationToken);
        var project = await RequireAdminForUpdateAsync(transaction, principal.PrincipalId, projectId, cancellationToken);
        var replay = await FindIdempotencyAsync<ProjectSummary>(transaction, projectId, principal.PrincipalId, "updateProjectShell", input.IdempotencyKey, digest, cancellationToken);
        if (replay is not null) { await transaction.CommitAsync(cancellationToken); return replay; }
        if (project.Version != input.ExpectedVersion) throw ProjectIdentityException.CasMismatch();
        var nextVersion = checked(project.Version + 1);
        await ExecuteAsync(transaction, """
            UPDATE identity.project SET display_name = @name, version = version + 1
            WHERE project_id = @project AND version = @expected;
            """, cancellationToken, ("name", input.DisplayName), ("project", projectId), ("expected", input.ExpectedVersion));
        var result = project with { DisplayName = input.DisplayName, Version = nextVersion };
        await SaveIdempotencyAsync(transaction, projectId, principal.PrincipalId, "updateProjectShell", input.IdempotencyKey, digest, result, cancellationToken);
        await AuditAsync(transaction, projectId, principal.PrincipalId, "updateProjectShell", result, requestId, traceId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<MembershipPage> ListMembersAsync(
        Guid principalId, Guid projectId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var after = DecodeCursor(cursor);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetContextAsync(transaction, projectId, principalId, cancellationToken);
        await RequireAdminForUpdateAsync(transaction, principalId, projectId, cancellationToken);
        await using var command = transaction.CreateCommand();
        command.CommandText = """
            SELECT principal_id, roles, clearance, active, version FROM identity.membership
            WHERE project_id = @project AND (@after IS NULL OR principal_id > @after)
            ORDER BY principal_id LIMIT @limit;
            """;
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("after", NpgsqlDbType.Uuid, (object?)after ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<Membership>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadMembership(reader));
        string? next = items.Count > limit ? EncodeCursor(items[limit - 1].PrincipalId) : null;
        if (items.Count > limit) items.RemoveAt(limit);
        return new MembershipPage(items, next);
    }

    public async Task<(Membership Membership, long PolicyVersion)> ChangeMembershipAsync(
        AtlasPrincipal actor, Guid projectId, ChangeMembershipCommand input, string requestId, string? traceId,
        CancellationToken cancellationToken)
    {
        ValidateMembership(input);
        var digest = Digest(new { input.PrincipalId, input.Roles, input.Clearance, input.Active, input.ExpectedVersion });
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetContextAsync(transaction, projectId, actor.PrincipalId, cancellationToken);
        var project = await RequireAdminForUpdateAsync(transaction, actor.PrincipalId, projectId, cancellationToken);
        var replay = await FindIdempotencyAsync<Membership>(transaction, projectId, actor.PrincipalId, "changeMembership", input.IdempotencyKey, digest, cancellationToken);
        if (replay is not null) { await transaction.CommitAsync(cancellationToken); return (replay, project.Version); }
        if (project.Version != input.ExpectedVersion) throw ProjectIdentityException.CasMismatch();
        if (actor.Kind != PrincipalKind.Human) throw ProjectIdentityException.Forbidden();
        if (await PrincipalIsInactiveAsync(transaction, input.PrincipalId, cancellationToken)) throw ProjectIdentityException.NotFound();
        var existing = await GetMembershipForUpdateAsync(transaction, projectId, input.PrincipalId, cancellationToken);
        if (existing is not null && IsAdmin(existing) && (!input.Active || !input.Roles.Contains("ProjectAdmin", StringComparer.Ordinal)))
            await EnsureAnotherAdminAsync(transaction, projectId, input.PrincipalId, cancellationToken);
        var roles = input.Roles.Distinct(StringComparer.Ordinal).Order().ToArray();
        await ExecuteAsync(transaction, """
            INSERT INTO identity.membership (project_id, principal_id, roles, clearance, active)
            VALUES (@project, @principal, @roles, @clearance, @active)
            ON CONFLICT (project_id, principal_id) DO UPDATE SET
              roles = EXCLUDED.roles, clearance = EXCLUDED.clearance, active = EXCLUDED.active,
              version = identity.membership.version + 1;
            UPDATE identity.project SET version = version + 1, policy_epoch = policy_epoch + 1
            WHERE project_id = @project;
            """, cancellationToken, ("project", projectId), ("principal", input.PrincipalId),
            ("roles", roles), ("clearance", input.Clearance), ("active", input.Active));
        var result = new Membership(input.PrincipalId, roles, input.Clearance, input.Active, (existing?.Version ?? 0) + 1);
        await SaveIdempotencyAsync(transaction, projectId, actor.PrincipalId, "changeMembership", input.IdempotencyKey, digest, result, cancellationToken);
        await AuditAsync(transaction, projectId, actor.PrincipalId, "changeMembership", result, requestId, traceId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (result, checked(project.Version + 1));
    }

    public async Task<long> RemoveMembershipAsync(
        AtlasPrincipal actor, Guid projectId, Guid targetPrincipalId, long expectedVersion, string key,
        string requestId, string? traceId, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        var digest = Digest(new { targetPrincipalId, expectedVersion });
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetContextAsync(transaction, projectId, actor.PrincipalId, cancellationToken);
        var project = await RequireAdminForUpdateAsync(transaction, actor.PrincipalId, projectId, cancellationToken);
        var replay = await FindIdempotencyAsync<Dictionary<string, long>>(transaction, projectId, actor.PrincipalId, "removeMembership", key, digest, cancellationToken);
        if (replay is not null) { await transaction.CommitAsync(cancellationToken); return replay["version"]; }
        if (project.Version != expectedVersion) throw ProjectIdentityException.CasMismatch();
        if (actor.Kind != PrincipalKind.Human) throw ProjectIdentityException.Forbidden();
        var existing = await GetMembershipForUpdateAsync(transaction, projectId, targetPrincipalId, cancellationToken)
            ?? throw ProjectIdentityException.NotFound();
        if (IsAdmin(existing)) await EnsureAnotherAdminAsync(transaction, projectId, targetPrincipalId, cancellationToken);
        await ExecuteAsync(transaction, """
            DELETE FROM identity.membership WHERE project_id = @project AND principal_id = @principal;
            UPDATE identity.project SET version = version + 1, policy_epoch = policy_epoch + 1 WHERE project_id = @project;
            """, cancellationToken, ("project", projectId), ("principal", targetPrincipalId));
        var nextVersion = checked(project.Version + 1);
        await SaveIdempotencyAsync(transaction, projectId, actor.PrincipalId, "removeMembership", key, digest,
            new Dictionary<string, long> { ["version"] = nextVersion }, cancellationToken);
        await AuditAsync(transaction, projectId, actor.PrincipalId, "removeMembership", new { principalId = targetPrincipalId, version = nextVersion }, requestId, traceId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return nextVersion;
    }

    private static async Task SetContextAsync(NpgsqlTransaction transaction, Guid? projectId, Guid principalId, CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand();
        command.CommandText = "SELECT set_config('atlas.project_id', @project, true), set_config('atlas.subject_id', @subject, true);";
        command.Parameters.AddWithValue("project", NpgsqlDbType.Text, projectId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("subject", principalId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:Atlas is required for project identity.");
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<ProjectSummary> RequireAdminForUpdateAsync(
        NpgsqlTransaction transaction, Guid principalId, Guid projectId, CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand();
        command.CommandText = """
            SELECT p.project_id, p.slug, p.display_name, p.version, p.policy_epoch, p.current_baseline_id, m.roles
            FROM identity.project p
            JOIN identity.membership m ON m.project_id = p.project_id AND m.principal_id = @principal AND m.active
            WHERE p.project_id = @project AND p.archived_at IS NULL FOR UPDATE OF p;
            """;
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.AddWithValue("project", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw ProjectIdentityException.NotFound();
        var project = ReadProject(reader);
        if (!reader.GetFieldValue<string[]>(6).Contains("ProjectAdmin", StringComparer.Ordinal)) throw ProjectIdentityException.Forbidden();
        return project;
    }

    private static async Task<Membership?> GetMembershipForUpdateAsync(NpgsqlTransaction transaction, Guid projectId, Guid principalId, CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand();
        command.CommandText = "SELECT principal_id, roles, clearance, active, version FROM identity.membership WHERE project_id = @project AND principal_id = @principal FOR UPDATE;";
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("principal", principalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMembership(reader) : null;
    }

    private static async Task<bool> PrincipalIsInactiveAsync(NpgsqlTransaction transaction, Guid principalId, CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand();
        command.CommandText = "SELECT active FROM identity.principal WHERE principal_id = @principal;";
        command.Parameters.AddWithValue("principal", principalId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not true;
    }

    private static async Task EnsureAnotherAdminAsync(NpgsqlTransaction transaction, Guid projectId, Guid principalId, CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM identity.membership WHERE project_id = @project AND principal_id <> @principal AND active AND roles @> ARRAY['ProjectAdmin']);";
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("principal", principalId);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw ProjectIdentityException.Conflict("A project must retain at least one active ProjectAdmin.");
    }

    private static async Task<ProjectSummary?> FindProjectIdempotencyAsync(NpgsqlTransaction transaction, Guid principalId, string key, string digest, CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand();
        command.CommandText = """
            SELECT i.request_sha256, p.project_id, p.slug, p.display_name, p.version, p.policy_epoch, p.current_baseline_id
            FROM ops.idempotency i JOIN identity.project p ON p.project_id = i.project_id
            WHERE i.principal_id = @principal AND i.operation_id = 'createProject' AND i.idempotency_key = @key;
            """;
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(reader.GetString(0)), Encoding.ASCII.GetBytes(digest)))
            throw ProjectIdentityException.Conflict("The idempotency key was already used with a different request.");
        return ReadProject(reader, 1);
    }

    private static async Task<T?> FindIdempotencyAsync<T>(NpgsqlTransaction transaction, Guid projectId, Guid principalId, string operation, string key, string digest, CancellationToken cancellationToken) where T : class
    {
        await using var command = transaction.CreateCommand();
        command.CommandText = "SELECT request_sha256, result_ref FROM ops.idempotency WHERE project_id = @project AND principal_id = @principal AND operation_id = @operation AND idempotency_key = @key;";
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(reader.GetString(0)), Encoding.ASCII.GetBytes(digest)))
            throw ProjectIdentityException.Conflict("The idempotency key was already used with a different request.");
        return JsonSerializer.Deserialize<T>(reader.GetString(1), Json);
    }

    private static async Task SaveIdempotencyAsync<T>(NpgsqlTransaction transaction, Guid projectId, Guid principalId, string operation, string key, string digest, T result, CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand();
        command.CommandText = "INSERT INTO ops.idempotency (project_id, principal_id, operation_id, idempotency_key, request_sha256, result_ref) VALUES (@project, @principal, @operation, @key, @digest, @result::jsonb);";
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("digest", digest);
        command.Parameters.Add(new NpgsqlParameter("result", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(result, Json) });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AuditAsync<T>(NpgsqlTransaction transaction, Guid projectId, Guid principalId, string action, T resource, string requestId, string? traceId, CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand();
        command.CommandText = "INSERT INTO ops.audit_event (project_id, actor_id, action, resource_ref, result_code, request_id, trace_id) VALUES (@project, @principal, @action, @resource::jsonb, 'OK', @request, @trace);";
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.Add(new NpgsqlParameter("resource", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(resource, Json) });
        command.Parameters.AddWithValue("request", requestId);
        command.Parameters.AddWithValue("trace", NpgsqlDbType.Text, (object?)traceId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = transaction.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ProjectPage> ReadProjectPageAsync(NpgsqlCommand command, int limit, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ProjectSummary>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadProject(reader));
        string? next = items.Count > limit ? EncodeCursor(items[limit - 1].ProjectId) : null;
        if (items.Count > limit) items.RemoveAt(limit);
        return new ProjectPage(items, next);
    }

    private static AtlasPrincipal ReadPrincipal(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3) switch { "human" => PrincipalKind.Human, "agent" => PrincipalKind.Agent, _ => PrincipalKind.Service }, reader.GetBoolean(4));
    private static ProjectSummary ReadProject(NpgsqlDataReader reader, int offset = 0) => new(reader.GetGuid(offset), reader.GetString(offset + 1), reader.GetString(offset + 2), reader.GetInt64(offset + 3), reader.GetInt64(offset + 4), reader.GetGuid(offset + 5));
    private static Membership ReadMembership(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetFieldValue<string[]>(1), reader.GetInt32(2), reader.GetBoolean(3), reader.GetInt64(4));
    private static bool IsAdmin(Membership membership) => membership.Active && membership.Roles.Contains("ProjectAdmin", StringComparer.Ordinal);
    private static string KindName(PrincipalKind kind) => kind switch { PrincipalKind.Human => "human", PrincipalKind.Agent => "agent", _ => "service" };
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json))).ToLowerInvariant();
    private static string EncodeCursor(Guid id) => Convert.ToBase64String(id.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Guid? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        try { return new Guid(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/') + new string('=', (4 - cursor.Length % 4) % 4))); }
        catch (Exception exception) when (exception is FormatException or ArgumentException) { throw ProjectIdentityException.Invalid("The pagination cursor is invalid."); }
    }
    private static void ValidateProject(string name, string slug, string key) { ValidateName(name); ValidateKey(key); if (!SlugRegex().IsMatch(slug)) throw ProjectIdentityException.Invalid("Slug must be 1-80 lowercase letters, digits, or hyphens."); }
    private static void ValidateName(string name) { if (string.IsNullOrWhiteSpace(name) || name.Length > 200) throw ProjectIdentityException.Invalid("Display name must contain 1-200 characters."); }
    private static void ValidateKey(string key) { if (key.Length is < 8 or > 128) throw ProjectIdentityException.Invalid("Idempotency-Key must contain 8-128 characters."); }
    private static void ValidateMembership(ChangeMembershipCommand input)
    {
        ValidateKey(input.IdempotencyKey);
        if (input.Clearance < 0 || input.Roles.Count == 0 || input.Roles.Any(string.IsNullOrWhiteSpace) || input.Roles.Any(role => role.Length > 100)) throw ProjectIdentityException.Invalid("Membership roles and clearance are invalid.");
        string[] allowed = ["ProjectReader", "Contributor", "AgentProposer", "BusinessOwner", "TechOwner", "Verifier", "QA", "KnowledgePublisher", "SecurityOwner", "DataOwner", "ProjectAdmin", "CIService", "WorkerService"];
        if (input.Roles.Any(role => !allowed.Contains(role, StringComparer.Ordinal))) throw ProjectIdentityException.Invalid("An unknown membership role was supplied.");
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,78}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();
}
