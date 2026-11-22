using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessApplicability.Application;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using KnowledgeCore.Contracts;
using KnowledgeCore.Infrastructure;
using Npgsql;
using NpgsqlTypes;
using ProjectIdentity.Application;
using ReadSearchExplain.Contracts;

namespace ReadSearchExplain.Search;

/// <summary>Baseline-bound PostgreSQL search. Candidate generation never supplies response text.</summary>
public sealed class PostgresReadSearchService(
    string connectionString,
    PostgresKnowledgeStore knowledge,
    PostgresAccessApplicabilityStore access,
    string cursorKey)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly byte[] signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(cursorKey));

    public async Task<SearchHitPage> SearchAsync(ResolvedReadContext context, string query, string? kind, string? cursor, int limit, CancellationToken ct)
    {
        var normalized = Normalize(query);
        if (normalized.Length == 0 || normalized.Length > 500 || limit is < 1 or > 100) throw ProjectIdentityException.Invalid("A bounded non-empty search query is required.");
        if (kind is not null && (string.IsNullOrWhiteSpace(kind) || kind.Length > 100)) throw ProjectIdentityException.Invalid("The kind filter is invalid.");
        var offset = DecodeCursor(cursor, context, normalized, kind);
        var candidates = await CandidatesAsync(context, normalized, kind, ct);
        var authorizations = await access.AuthorizeReadBatchAsync(context,
            candidates.Select(item => (item.UnitId, item.RevisionId)).ToArray(), "read", ct);
        var hits = new List<SearchHit>();
        var skipped = 0;
        foreach (var candidate in candidates)
        {
            if (skipped++ < offset) continue;
            if (hits.Count == limit) break;
            try
            {
                if (!authorizations.TryGetValue(candidate.UnitId, out var authorization)) continue;
                var applicability = ApplicabilityEvaluator.Evaluate(candidate.Applicability, context.Context);
                if (applicability.Status != ApplicabilityStatus.Applicable) continue;
                var revision = ProjectAuthorized(candidate.Revision, authorization);
                // Candidate selection may inspect fields which are later removed by
                // field-level policy. Never return a hit whose only match was hidden.
                if (!MatchesVisibleContent(revision, normalized)) continue;
                hits.Add(new(revision.UnitId, revision.RevisionId, revision.Title, revision.Kind, Snippet(revision, normalized)));
            }
            catch (ProjectIdentityException error) when (error.StatusCode is 403 or 404) { if (error.Code == "POLICY_CONTEXT_STALE") throw; }
        }
        await access.EnsureCurrentAsync(context, ct);
        var consumed = Math.Min(skipped, candidates.Count);
        var next = hits.Count == limit && consumed < candidates.Count ? EncodeCursor(consumed, context, normalized, kind) : null;
        return new(hits, next);
    }

    public async Task<VocabularyResult> ResolveVocabularyAsync(ResolvedReadContext context, string term, string? contextKey, CancellationToken ct)
    {
        var normalized = Normalize(term);
        if (normalized.Length == 0 || normalized.Length > 500) throw ProjectIdentityException.Invalid("A bounded non-empty term is required.");
        var candidates = await CandidatesAsync(context, normalized, null, ct);
        var exact = new List<RevisionView>();
        var similar = new List<RevisionView>();
        foreach (var candidate in candidates)
        {
            try
            {
                await access.AuthorizeReadAsync(context, candidate.UnitId, "read", candidate.RevisionId, ct);
                if (ApplicabilityEvaluator.Evaluate(candidate.Applicability, context.Context).Status != ApplicabilityStatus.Applicable) continue;
                var revision = await ProjectAsync(context, await knowledge.GetUnitAsync(context, candidate.UnitId, ct), ct);
                var candidateContext = String(revision.Body, "contextKey");
                if (contextKey is not null && !string.Equals(candidateContext, contextKey, StringComparison.Ordinal)) continue;
                var aliases = Aliases(revision.Body).Append(revision.Title).Select(Normalize).ToArray();
                if (aliases.Contains(normalized, StringComparer.Ordinal)) exact.Add(revision); else similar.Add(revision);
            }
            catch (ProjectIdentityException error) when (error.StatusCode is 403 or 404) { if (error.Code == "POLICY_CONTEXT_STALE") throw; }
        }
        var matches = exact.Count > 0 ? exact : similar;
        await access.EnsureCurrentAsync(context, ct);
        if (matches.Count == 1) return new("resolved", matches, []);
        if (matches.Count > 1) return new("ambiguous", matches, [new("vocabulary.ambiguous", "The term has multiple authorized meanings; select a context or candidate.", "warning", "conflict", "Supply contextKey or refine the term.")]);
        return new("unknown", [], [new("vocabulary.unknown", "No authorized vocabulary definition matched the term.", "info", "missing")]);
    }

    public async Task<RevisionView> ProjectAsync(ResolvedReadContext context, RevisionView revision, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(revision, Json);
        var projected = await new PolicyService(access).ProjectFieldsAsync(context, revision.UnitId, payload, ct);
        return JsonSerializer.Deserialize<RevisionView>(projected, Json) ?? throw ProjectIdentityException.NotFound();
    }

    private static RevisionView ProjectAuthorized(RevisionView revision, ResourceAccess authorization)
    {
        var payload = JsonSerializer.SerializeToElement(revision, Json);
        var projected = PolicyService.ProjectAuthorizedFields(authorization, payload);
        return JsonSerializer.Deserialize<RevisionView>(projected, Json) ?? throw ProjectIdentityException.NotFound();
    }

    private async Task<List<Candidate>> CandidatesAsync(ResolvedReadContext context, string query, string? kind, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var set = tx.CreateCommand();
        set.CommandText = "SELECT set_config('atlas.project_id',@project,true),set_config('atlas.subject_id',@principal,true);";
        set.Parameters.AddWithValue("project", context.ProjectId.ToString()); set.Parameters.AddWithValue("principal", context.PrincipalId.ToString());
        await set.ExecuteNonQueryAsync(ct);
        await using var command = tx.CreateCommand();
        var selected = context.Mode == ReadMode.Draft ? """
            FROM core.unit u
            LEFT JOIN gov.change_item ci ON ci.project_id=u.project_id AND ci.unit_id=u.unit_id AND ci.change_set_id=@changeSet
            LEFT JOIN gov.baseline_entry be ON be.project_id=u.project_id AND be.unit_id=u.unit_id AND be.baseline_id=@baseline
            JOIN core.revision r ON r.project_id=u.project_id AND r.unit_id=u.unit_id AND r.revision_id=COALESCE(ci.proposed_revision_id,be.revision_id)
            WHERE u.project_id=@project AND u.archived_at IS NULL AND (ci.unit_id IS NOT NULL OR be.unit_id IS NOT NULL)
            """ : """
            FROM core.unit u JOIN gov.baseline_entry be ON be.project_id=u.project_id AND be.unit_id=u.unit_id AND be.baseline_id=@baseline
            JOIN core.revision r ON r.project_id=u.project_id AND r.unit_id=u.unit_id AND r.revision_id=be.revision_id
            WHERE u.project_id=@project AND u.archived_at IS NULL
            """;
        command.CommandText = $"""
            SELECT u.unit_id,r.revision_id,r.rev_no,u.kind,r.schema_version,r.fact_kind,r.verification,r.payload,
              r.content_sha256,r.created_at,r.created_by,r.parent_revision_id,r.sealed,r.applicability,
              CASE WHEN lower(r.payload->>'title')=@query THEN 0
                   WHEN EXISTS(SELECT 1 FROM jsonb_array_elements_text(COALESCE(r.payload->'body'->'aliases','[]'::jsonb)) a WHERE lower(a)=@query) THEN 1
                   WHEN strpos(lower(r.payload->>'title'),@query)>0 THEN 2
                   ELSE 3 END AS rank
            {selected}
              AND (@kind IS NULL OR u.kind=@kind)
              AND (lower(r.payload->>'title') LIKE '%'||@query||'%'
                OR lower(COALESCE(r.payload->'body'->>'aliases','')) LIKE '%'||@query||'%'
                OR lower(r.payload->>'body'::text) LIKE '%'||@query||'%'
                OR (r.payload->>'title') % @query)
            ORDER BY rank,u.unit_id LIMIT 1000;
            """;
        command.Parameters.AddWithValue("project", context.ProjectId); command.Parameters.AddWithValue("baseline", context.BaselineId);
        command.Parameters.AddWithValue("changeSet", NpgsqlDbType.Uuid, (object?)context.ChangeSetId ?? DBNull.Value);
        command.Parameters.AddWithValue("query", NpgsqlDbType.Text, query); command.Parameters.AddWithValue("kind", NpgsqlDbType.Text, (object?)kind ?? DBNull.Value);
        var result = new List<Candidate>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var payload = JsonDocument.Parse(reader.GetString(7)).RootElement.Clone();
            var revision = new RevisionView(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2),
                payload.GetProperty("title").GetString() ?? string.Empty, reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                payload.TryGetProperty("applicability", out var applicability) ? applicability.Clone() : Empty("{}"),
                payload.TryGetProperty("body", out var body) ? body.Clone() : Empty("{}"),
                payload.TryGetProperty("blocks", out var blocks) ? blocks.Clone() : Empty("[]"),
                payload.TryGetProperty("evidence", out var evidence) ? evidence.Clone() : Empty("[]"),
                payload.TryGetProperty("unknowns", out var unknowns) ? unknowns.Clone() : Empty("[]"),
                reader.GetString(8), new DateTimeOffset(reader.GetDateTime(9), TimeSpan.Zero), reader.GetGuid(10),
                payload.TryGetProperty("categoryKeys", out var categories) ? categories.EnumerateArray().Select(item => item.GetString()!).ToArray() : [])
            { ParentRevisionId = reader.IsDBNull(11) ? null : reader.GetGuid(11), Sealed = reader.GetBoolean(12) };
            result.Add(new(revision.UnitId, revision.RevisionId, revision.Kind, JsonDocument.Parse(reader.GetString(13)).RootElement.Clone(), reader.GetInt32(14), revision));
        }
        return result;
    }

    private string EncodeCursor(int offset, ResolvedReadContext context, string query, string? kind)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { offset, context.ProjectId, context.PrincipalId, context.PolicyEpoch, context.BaselineId, context.ChangeSetId, context.ContextHash, query, kind, expiry = DateTimeOffset.UtcNow.AddMinutes(15) }, Json);
        var body = Convert.ToBase64String(payload); var signature = Convert.ToBase64String(HMACSHA256.HashData(signingKey, payload));
        return Base64Url(body + "." + signature);
    }

    private int DecodeCursor(string? cursor, ResolvedReadContext context, string query, string? kind)
    {
        if (string.IsNullOrEmpty(cursor)) return 0;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(Base64UrlDecode(cursor))); var parts = decoded.Split('.');
            var payload = Convert.FromBase64String(parts[0]); var signature = Convert.FromBase64String(parts[1]);
            if (parts.Length != 2 || !CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(signingKey, payload))) throw new FormatException();
            using var json = JsonDocument.Parse(payload); var root = json.RootElement;
            if (root.GetProperty("projectId").GetGuid() != context.ProjectId || root.GetProperty("principalId").GetGuid() != context.PrincipalId ||
                root.GetProperty("policyEpoch").GetInt64() != context.PolicyEpoch || root.GetProperty("baselineId").GetGuid() != context.BaselineId ||
                root.GetProperty("contextHash").GetString() != context.ContextHash || root.GetProperty("query").GetString() != query || root.GetProperty("kind").GetString() != kind ||
                root.GetProperty("expiry").GetDateTimeOffset() < DateTimeOffset.UtcNow) throw new FormatException();
            return root.GetProperty("offset").GetInt32();
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        { throw ProjectIdentityException.Invalid("The search cursor is expired, invalid, or belongs to another authorization context."); }
    }

    private static string Base64Url(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Base64UrlDecode(string value) => value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4);
    private static string Base64Url(string value, bool decode) => decode ? Base64UrlDecode(value) : Base64Url(value);
    private static string Normalize(string value) => (value ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
    private static string? String(JsonElement body, string key) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static IEnumerable<string> Aliases(JsonElement body) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty("aliases", out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!) : [];
    private static bool MatchesVisibleContent(RevisionView revision, string query)
    {
        static bool Contains(string? value, string query) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        if (Contains(revision.Title, query) || Aliases(revision.Body).Any(alias => Contains(alias, query)) || Contains(revision.Body.GetRawText(), query)) return true;
        // Preserve PostgreSQL trigram title matches without treating a match in a
        // denied JSON property as a visible hit.
        var left = Trigrams(revision.Title.ToLowerInvariant()); var right = Trigrams(query);
        return right.Count > 0 && left.Intersect(right).Count() * 2d / (left.Count + right.Count) >= 0.3;
    }
    private static HashSet<string> Trigrams(string value)
    {
        var padded = "  " + value + " ";
        return Enumerable.Range(0, Math.Max(0, padded.Length - 2)).Select(index => padded.Substring(index, 3)).ToHashSet(StringComparer.Ordinal);
    }
    private static string Snippet(RevisionView revision, string query)
    {
        var text = revision.Body.ValueKind == JsonValueKind.Object && revision.Body.TryGetProperty("definition", out var definition) && definition.ValueKind == JsonValueKind.String ? definition.GetString()! : revision.Title;
        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); var start = Math.Max(0, index < 0 ? 0 : index - 60);
        return text.Length <= 240 ? text : text.Substring(start, Math.Min(240, text.Length - start));
    }
    private static JsonElement Empty(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private sealed record Candidate(Guid UnitId, Guid RevisionId, string Kind, JsonElement Applicability, int Rank, RevisionView Revision);
}
