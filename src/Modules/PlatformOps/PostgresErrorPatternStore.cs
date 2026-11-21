using System.Data.Common;
using System.Text.Json;
using BuildingBlocks.Commands;
using BuildingBlocks.Persistence;

namespace PlatformOps;

public sealed record ErrorPattern(
    Guid PatternId, string PatternKey, int PatternRevision, JsonElement Applicability, string Symptom,
    string RootCause, string Detector, string Mitigation, string RegressionTestId, JsonElement Evidence, string State);
public sealed record ErrorPatternPage(IReadOnlyList<ErrorPattern> Items, string? NextCursor);

public sealed class PostgresErrorPatternStore(AtlasDbContext database)
{
    public async Task<ErrorPatternPage> ListAsync(Guid projectId, Guid principalId, string? cursor, int limit, string? kind, string? path, CancellationToken ct)
    {
        if (limit is < 1 or > 100) throw Invalid("Page limit must be between 1 and 100.");
        await using var transaction = await Begin(projectId, principalId, "listErrorPatterns", $"read-{Guid.NewGuid():N}", ct);
        await RequireMember(transaction, null, ct);
        var after = string.IsNullOrWhiteSpace(cursor) ? null : Decode(cursor);
        await using var command = transaction.CreateCommand($"""
            WITH latest AS (SELECT *,row_number() OVER(PARTITION BY pattern_key ORDER BY pattern_revision DESC) AS rank FROM ops.error_pattern WHERE project_id=@project)
            SELECT * FROM latest WHERE rank=1 AND (@after::text IS NULL OR pattern_key>@after::text)
              AND (@kind::text IS NULL OR applicability->>'kind'=@kind::text) AND (@path::text IS NULL OR applicability->>'path'=@path::text)
            ORDER BY pattern_key LIMIT @limit
            """);
        Add(command, "project", projectId); Add(command, "after", after); Add(command, "kind", kind); Add(command, "path", path); Add(command, "limit", limit + 1);
        var items = await ReadMany(command, ct);
        await transaction.RollbackAsync(ct);
        string? next = null;
        if (items.Count > limit) { items.RemoveAt(limit); next = Encode(items[^1].PatternKey); }
        return new ErrorPatternPage(items, next);
    }

    public async Task<ErrorPattern> ProposeAsync(Guid projectId, Guid principalId, ErrorPattern input, string key, string requestId, string? traceId, CancellationToken ct)
    {
        Validate(input);
        if (input.State != "candidate" || input.PatternRevision != 1) throw Invalid("New error patterns must be candidate revision 1.");
        await using var transaction = await Begin(projectId, principalId, "proposeErrorPattern", key, ct, requestId, traceId);
        await RequireMember(transaction, ["Contributor", "AgentProposer", "ProjectAdmin", "TechOwner"], ct);
        var digest = RequestDigest.Compute(input);
        var existing = await transaction.FindIdempotencyAsync(ct);
        if (existing is not null)
        {
            if (digest != existing.RequestSha256) throw AtlasCommandException.Conflict();
            return existing.Result.Data.Deserialize<ErrorPattern>()!;
        }
        var pattern = input with { PatternId = Guid.NewGuid(), State = "candidate", PatternRevision = 1 };
        await Insert(transaction, projectId, principalId, pattern, ct);
        await transaction.SaveIdempotencyAsync(digest, new StoredCommandResult(JsonSerializer.SerializeToElement(pattern), 201), ct);
        await transaction.AppendAuditAsync(new AuditEntryInput("error_pattern.proposed", JsonSerializer.SerializeToElement(new { patternId = pattern.PatternId }), "OK", JsonSerializer.SerializeToElement(new { pattern.PatternKey })), ct);
        await transaction.CommitAsync(ct);
        return pattern;
    }

    public async Task<ErrorPattern> ReviewAsync(Guid projectId, Guid principalId, bool human, Guid patternId, int expectedRevision, string decision, string reason, string key, string requestId, string? traceId, CancellationToken ct)
    {
        if (!human) throw new AtlasCommandException("FORBIDDEN", "Error pattern review requires a human principal.", 403);
        if (decision is not ("reviewed" or "retired") || string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            throw Invalid("A reviewed or retired decision and a reason of at most 1000 characters are required.");
        await using var transaction = await Begin(projectId, principalId, "reviewErrorPattern", key, ct, requestId, traceId);
        await RequireMember(transaction, ["Verifier", "QA", "TechOwner", "SecurityOwner"], ct);
        var digest = RequestDigest.Compute(new { patternId, expectedRevision, decision, reason });
        var existing = await transaction.FindIdempotencyAsync(ct);
        if (existing is not null)
        {
            if (digest != existing.RequestSha256) throw AtlasCommandException.Conflict();
            return existing.Result.Data.Deserialize<ErrorPattern>()!;
        }
        var candidate = await Find(transaction, patternId, ct) ?? throw new AtlasCommandException("NOT_FOUND", "The error pattern was not found.", 404);
        if (candidate.PatternRevision != expectedRevision) throw AtlasCommandException.CasMismatch();
        if (candidate.State != "candidate") throw new AtlasCommandException("INVALID_STATE", "Only candidate error patterns can be reviewed.", 409);
        var reviewed = candidate with { PatternId = Guid.NewGuid(), PatternRevision = checked(candidate.PatternRevision + 1), State = decision };
        await Insert(transaction, projectId, principalId, reviewed, ct);
        await transaction.SaveIdempotencyAsync(digest, new StoredCommandResult(JsonSerializer.SerializeToElement(reviewed), 200), ct);
        await transaction.AppendAuditAsync(new AuditEntryInput("error_pattern.reviewed", JsonSerializer.SerializeToElement(new { patternId = reviewed.PatternId }), "OK", JsonSerializer.SerializeToElement(new { reviewed.PatternKey, reviewed.PatternRevision, decision, reason })), ct);
        await transaction.CommitAsync(ct);
        return reviewed;
    }

    private static void Validate(ErrorPattern value)
    {
        if (string.IsNullOrWhiteSpace(value.PatternKey) || string.IsNullOrWhiteSpace(value.Symptom) || string.IsNullOrWhiteSpace(value.RootCause) ||
            string.IsNullOrWhiteSpace(value.Detector) || string.IsNullOrWhiteSpace(value.Mitigation) || string.IsNullOrWhiteSpace(value.RegressionTestId) ||
            value.Applicability.ValueKind != JsonValueKind.Object || value.Evidence.ValueKind != JsonValueKind.Array)
            throw Invalid("All error pattern fields and object/array applicability/evidence are required.");
    }

    private static async Task Insert(PostgresCommandTransaction transaction, Guid project, Guid actor, ErrorPattern value, CancellationToken ct)
    {
        await using var command = transaction.CreateCommand("""
            INSERT INTO ops.error_pattern(project_id,pattern_id,pattern_key,pattern_revision,applicability,symptom,root_cause,detector,mitigation,regression_test_id,evidence_refs,state,created_by)
            VALUES(@project,@id,@key,@revision,@applicability::jsonb,@symptom,@cause,@detector,@mitigation,@test,@evidence::jsonb,@state,@actor)
            """);
        Add(command, "project", project); Add(command, "id", value.PatternId); Add(command, "key", value.PatternKey); Add(command, "revision", value.PatternRevision);
        Add(command, "applicability", value.Applicability.GetRawText()); Add(command, "symptom", value.Symptom); Add(command, "cause", value.RootCause);
        Add(command, "detector", value.Detector); Add(command, "mitigation", value.Mitigation); Add(command, "test", value.RegressionTestId);
        Add(command, "evidence", value.Evidence.GetRawText()); Add(command, "state", value.State); Add(command, "actor", actor);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<ErrorPattern?> Find(PostgresCommandTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var command = transaction.CreateCommand("SELECT * FROM ops.error_pattern WHERE project_id=@project AND pattern_id=@id");
        Add(command, "project", transaction.CommandContext.ProjectId); Add(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    private static async Task<List<ErrorPattern>> ReadMany(DbCommand command, CancellationToken ct)
    {
        var items = new List<ErrorPattern>(); await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(Read(reader)); return items;
    }

    private static ErrorPattern Read(DbDataReader r) => new(r.GetGuid(r.GetOrdinal("pattern_id")), r.GetString(r.GetOrdinal("pattern_key")), r.GetInt32(r.GetOrdinal("pattern_revision")),
        JsonDocument.Parse(r.GetString(r.GetOrdinal("applicability"))).RootElement.Clone(), r.GetString(r.GetOrdinal("symptom")), r.GetString(r.GetOrdinal("root_cause")),
        r.GetString(r.GetOrdinal("detector")), r.GetString(r.GetOrdinal("mitigation")), r.GetString(r.GetOrdinal("regression_test_id")),
        JsonDocument.Parse(r.GetString(r.GetOrdinal("evidence_refs"))).RootElement.Clone(), r.GetString(r.GetOrdinal("state")));

    private static async Task RequireMember(PostgresCommandTransaction transaction, string[]? roles, CancellationToken ct)
    {
        await using var command = transaction.CreateCommand("SELECT EXISTS(SELECT 1 FROM identity.membership WHERE project_id=@project AND principal_id=@principal AND active AND (@roles::text[] IS NULL OR roles && @roles::text[]))");
        Add(command, "project", transaction.CommandContext.ProjectId); Add(command, "principal", transaction.CommandContext.PrincipalId); Add(command, "roles", roles);
        if (await command.ExecuteScalarAsync(ct) is not true) throw new AtlasCommandException("FORBIDDEN", "The current principal cannot perform this error-pattern operation.", 403);
    }

    private static string Encode(string key) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(key)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Decode(string cursor)
    {
        try { var value = cursor.Replace('-', '+').Replace('_', '/'); return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value.PadRight(value.Length + (4 - value.Length % 4) % 4, '='))); }
        catch (FormatException) { throw Invalid("The error-pattern cursor is invalid."); }
    }
    private static AtlasCommandException Invalid(string message) => new("VALIDATION_FAILED", message, 400);
    private Task<PostgresCommandTransaction> Begin(Guid project, Guid principal, string operation, string key, CancellationToken ct, string? request = null, string? trace = null) =>
        PostgresCommandTransaction.BeginAsync(database, new CommandContext(project, principal, operation, key, request ?? Guid.NewGuid().ToString("N"), trace), ct);
    private static void Add(DbCommand command, string name, object? value) => PostgresCommandTransaction.AddParameter(command, name, value);
}
