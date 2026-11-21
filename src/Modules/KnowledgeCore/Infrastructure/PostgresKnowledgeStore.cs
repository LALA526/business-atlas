using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using KnowledgeCore.Application;
using KnowledgeCore.Contracts;
using KnowledgeCore.Relations;
using KnowledgeCore.Flows;
using KnowledgeCore.Vocabulary;
using Npgsql;
using NpgsqlTypes;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;

namespace KnowledgeCore.Infrastructure;

public sealed class PostgresKnowledgeStore(string connectionString, PostgresAccessApplicabilityStore access)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> FactKinds = ["normative", "observed", "documented_intent", "design_decision", "inferred", "assumption"];
    private static readonly HashSet<string> Verifications = ["unverified", "verified", "contradicted", "insufficient", "stale"];

    public async Task<RevisionViewPage> ListUnitsAsync(ResolvedReadContext context, string? kind, string? categoryKey, string? cursor, int limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(tx, context, ct);
        await using var command = tx.CreateCommand();
        command.CommandText = $"WITH selected AS ({SelectedRevisionSql(context.Mode)}) SELECT s.* FROM selected s WHERE (@kind IS NULL OR s.kind=@kind) AND (@category IS NULL OR s.payload->'categoryKeys' ? @category) ORDER BY s.unit_id LIMIT 1001;";
        AddContextParameters(command, context);
        command.Parameters.AddWithValue("kind", NpgsqlDbType.Text, (object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue("category", NpgsqlDbType.Text, (object?)categoryKey ?? DBNull.Value);
        return await ReadPageAsync(command, context, cursor, limit, item => item.UnitId, ct);
    }

    public async Task<RevisionView> GetUnitAsync(ResolvedReadContext context, Guid unitId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(tx, context, ct);
        await using var command = tx.CreateCommand();
        command.CommandText = $"WITH selected AS ({SelectedRevisionSql(context.Mode)}) SELECT s.* FROM selected s WHERE s.unit_id=@unit;";
        AddContextParameters(command, context); command.Parameters.AddWithValue("unit", unitId);
        var revision = await ReadSingleAsync(command, ct) ?? throw ProjectIdentityException.NotFound();
        await AuthorizeAsync(context, revision, ct);
        await access.EnsureCurrentAsync(context, ct);
        return revision;
    }

    public async Task<RevisionViewPage> ListRevisionsAsync(ResolvedReadContext context, Guid unitId, string? cursor, int limit, CancellationToken ct)
    {
        await GetUnitAsync(context, unitId, ct); // Resolve the stable identity and current read permission first.
        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(tx, context, ct);
        await using var command = tx.CreateCommand();
        command.CommandText = SelectColumns + " FROM core.unit u JOIN core.revision r ON r.project_id=u.project_id AND r.unit_id=u.unit_id WHERE u.project_id=@project AND u.unit_id=@unit ORDER BY r.rev_no DESC LIMIT 1001;";
        command.Parameters.AddWithValue("project", context.ProjectId); command.Parameters.AddWithValue("unit", unitId);
        return await ReadPageAsync(command, context, cursor, limit, item => item.RevisionId, ct, false);
    }

    public async Task<RevisionViewPage> ListRelationsAsync(ResolvedReadContext context, Guid rootUnitId, string direction, string? predicate, string? cursor, int limit, CancellationToken ct)
    {
        if (direction is not ("forward" or "reverse" or "both")) throw ProjectIdentityException.Invalid("direction must be forward, reverse, or both.");
        await GetUnitAsync(context, rootUnitId, ct);
        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(tx, context, ct);
        await using var command = tx.CreateCommand();
        var selected = SelectedRevisionSql(context.Mode);
        command.CommandText = $"""
            WITH relation_ids AS (
              SELECT ri.relation_revision_id FROM core.relation_index ri
              WHERE ri.project_id=@project AND ((@direction IN ('forward','both') AND ri.from_unit_id=@root) OR (@direction IN ('reverse','both') AND ri.to_unit_id=@root))
                AND (@predicate IS NULL OR ri.predicate=@predicate)
            ), selected AS ({selected})
            SELECT s.* FROM selected s JOIN relation_ids x ON x.relation_revision_id=s.revision_id ORDER BY s.unit_id LIMIT 1001;
            """;
        AddContextParameters(command, context); command.Parameters.AddWithValue("root", rootUnitId);
        command.Parameters.AddWithValue("direction", direction); command.Parameters.AddWithValue("predicate", NpgsqlDbType.Text, (object?)predicate ?? DBNull.Value);
        var page = await ReadPageAsync(command, context, cursor, limit, item => item.UnitId, ct, visibilityFilter: async (relation, token) =>
        {
            try
            {
                var body = relation.Body;
                if (!body.TryGetProperty("from", out var from) || !body.TryGetProperty("to", out var to)) return false;
                await AuthorizeEndpointAsync(context, from, token);
                await AuthorizeEndpointAsync(context, to, token);
                return true;
            }
            catch (ProjectIdentityException exception) when (exception.StatusCode == 404)
            {
                // A restricted endpoint makes the whole relation invisible. Do not reveal its
                // existence, endpoint names, or a count through a failed/partially filled page.
                return false;
            }
        });
        return page;
    }

    public Task<RevisionMutation> CreateDraftAsync(AtlasPrincipal actor, Guid projectId, Guid changeSetId, RevisionInput input,
        long expectedVersion, string key, string requestId, string? traceId, CancellationToken ct) =>
        WriteDraftAsync(actor, projectId, changeSetId, null, input, expectedVersion, key, requestId, traceId, ct);

    public Task<RevisionMutation> ReviseDraftAsync(AtlasPrincipal actor, Guid projectId, Guid changeSetId, Guid unitId, RevisionInput input,
        long expectedVersion, string key, string requestId, string? traceId, CancellationToken ct) =>
        WriteDraftAsync(actor, projectId, changeSetId, unitId, input, expectedVersion, key, requestId, traceId, ct);

    private async Task<RevisionMutation> WriteDraftAsync(AtlasPrincipal actor, Guid projectId, Guid changeSetId, Guid? requestedUnit, RevisionInput input,
        long expectedVersion, string key, string requestId, string? traceId, CancellationToken ct)
    {
        Validate(input, requestedUnit, key);
        if (actor.Kind == PrincipalKind.Service) throw ProjectIdentityException.Forbidden();
        var operation = requestedUnit.HasValue ? "reviseUnitDraft" : "createUnitDraft";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(JsonSerializer.SerializeToElement(new { changeSetId, requestedUnit, input, expectedVersion }, Json))))).ToLowerInvariant();
        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(tx, projectId, actor.PrincipalId, ct);
        var projectVersion = await RequireProposerAsync(tx, projectId, actor.PrincipalId, ct);
        var replay = await FindIdempotencyAsync(tx, projectId, actor.PrincipalId, operation, key, digest, ct);
        if (replay is not null) { await tx.CommitAsync(ct); return replay; }
        if (projectVersion != expectedVersion) throw ProjectIdentityException.CasMismatch();
        await RequireOpenDraftAsync(tx, projectId, changeSetId, ct);

        var unitId = requestedUnit ?? Guid.NewGuid();
        Guid? parent = null;
        long revNo = 1;
        if (requestedUnit.HasValue)
        {
            var previous = await LatestRevisionAsync(tx, projectId, unitId, ct) ?? throw ProjectIdentityException.NotFound();
            if (previous.Kind != input.Kind) throw ProjectIdentityException.Conflict("A unit kind cannot change across revisions.");
            input = VocabularySemantics.RetainFormerNameAsAlias(input, previous);
            parent = previous.RevisionId; revNo = checked(previous.RevNo + 1);
            await EnsureExternalIdentityStableAsync(tx, projectId, unitId, input.ExternalId, ct);
            if (await ChangeItemExistsAsync(tx, projectId, changeSetId, unitId, ct)) throw ProjectIdentityException.Conflict("This unit already has a proposal in the change set.");
        }
        else
        {
            await InsertUnitAsync(tx, projectId, unitId, input.Kind, actor.PrincipalId, ct);
        }

        var revisionId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var revision = new RevisionView(unitId, revisionId, revNo, input.Title, input.Kind, input.SchemaVersion, input.FactKind,
            input.Verification, input.Applicability.Clone(), input.Body.Clone(), input.Blocks.Clone(), input.Evidence.Clone(), input.Unknowns.Clone(),
            ContentHash(input), createdAt, actor.PrincipalId, Strings(input.CategoryKeys)) { ParentRevisionId = parent };
        await InsertRevisionAsync(tx, projectId, revision, input.SchemaDefinitionRevisionId, ct);
        await ValidateRuleReferencesAsync(tx, projectId, revision, ct);
        await InsertReferencesAsync(tx, projectId, revision, ct);
        await InsertEvidenceAsync(tx, projectId, revision, actor.PrincipalId, ct);
        if (!requestedUnit.HasValue && input.ExternalId is not null) await InsertExternalIdentityAsync(tx, projectId, unitId, input.ExternalId, ct);
        if (RelationSemantics.IsRelation(input.Kind)) await InsertRelationAsync(tx, projectId, revision, ct);
        await using (var write = tx.CreateCommand())
        {
            write.CommandText = """
                INSERT INTO gov.change_item (project_id,item_id,change_set_id,unit_id,base_revision_id,proposed_revision_id,action,dependency_group,reason)
                VALUES (@project,@item,@changeSet,@unit,@parent,@revision,@action,'knowledge','Knowledge draft revision');
                UPDATE identity.project SET version=version+1 WHERE project_id=@project;
                INSERT INTO ops.idempotency (project_id,principal_id,operation_id,idempotency_key,request_sha256,result_ref)
                VALUES (@project,@actor,@operation,@key,@digest,@result::jsonb);
                INSERT INTO ops.audit_event (project_id,actor_id,action,resource_ref,result_code,request_id,trace_id)
                VALUES (@project,@actor,@operation,@resource::jsonb,'OK',@request,@trace);
                INSERT INTO ops.outbox (project_id,event_id,event_type,aggregate_id,aggregate_revision,schema_version,payload,state)
                VALUES (@project,@event,'KnowledgeRevisionCreated.v1',@unit,@revNo,'1.0',@eventPayload::jsonb,'queued');
                """;
            var mutation = new RevisionMutation(revision, checked(projectVersion + 1));
            write.Parameters.AddWithValue("project", projectId); write.Parameters.AddWithValue("item", Guid.NewGuid());
            write.Parameters.AddWithValue("changeSet", changeSetId); write.Parameters.AddWithValue("unit", unitId);
            write.Parameters.AddWithValue("parent", NpgsqlDbType.Uuid, (object?)parent ?? DBNull.Value); write.Parameters.AddWithValue("revision", revisionId);
            write.Parameters.AddWithValue("action", parent.HasValue ? "revise" : "add"); write.Parameters.AddWithValue("actor", actor.PrincipalId);
            write.Parameters.AddWithValue("operation", operation); write.Parameters.AddWithValue("key", key); write.Parameters.AddWithValue("digest", digest);
            write.Parameters.Add(new NpgsqlParameter("result", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(mutation, Json) });
            write.Parameters.Add(new NpgsqlParameter("resource", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { unitId, revisionId, changeSetId }, Json) });
            write.Parameters.AddWithValue("request", requestId); write.Parameters.AddWithValue("trace", NpgsqlDbType.Text, (object?)traceId ?? DBNull.Value);
            write.Parameters.AddWithValue("event", Guid.NewGuid()); write.Parameters.AddWithValue("revNo", revNo);
            write.Parameters.Add(new NpgsqlParameter("eventPayload", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { projectId, unitId, revisionId, changeSetId, revNo }, Json) });
            await write.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return mutation;
        }
    }

    public async Task<RuleResult> EvaluateRuleAsync(ResolvedReadContext context, Guid unitId, JsonElement facts, CancellationToken ct)
    {
        var revision = await GetUnitAsync(context, unitId, ct);
        if (revision.Kind is not ("Rule" or "BusinessRule")) throw ProjectIdentityException.Invalid("The selected unit is not a business Rule.");
        if (revision.FactKind != "normative") throw ProjectIdentityException.Invalid("Only a normative rule can be evaluated; observations are not normative targets.");
        foreach (var input in revision.Body.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array ? inputs.EnumerateArray() : [])
            if (input.TryGetProperty("attribute", out var attribute)) await AuthorizeEndpointAsync(context, attribute, ct);
        var evaluation = RuleEvaluator.Evaluate(revision.Body, facts);
        var visibleEvidence = new List<JsonElement>();
        foreach (var evidence in evaluation.Evidence)
        {
            if (!evidence.TryGetProperty("source", out var source)) continue;
            try { await AuthorizeEndpointAsync(context, source, ct); visibleEvidence.Add(evidence); }
            catch (ProjectIdentityException exception) when (exception.StatusCode == 404) { }
        }
        return new(evaluation.Status, evaluation.Outcomes, evaluation.Conditions, evaluation.Unknowns, context, visibleEvidence);
    }

    private async Task InsertRevisionAsync(NpgsqlTransaction tx, Guid projectId, RevisionView revision, Guid? schemaDefinition, CancellationToken ct)
    {
        var payload = Payload(revision);
        await using var command = tx.CreateCommand();
        command.CommandText = """
            INSERT INTO core.revision (project_id,revision_id,unit_id,rev_no,parent_revision_id,schema_type,schema_version,
              schema_definition_revision_id,fact_kind,verification,payload,applicability,content_sha256,sealed,created_by,created_at)
            VALUES (@project,@revision,@unit,@revNo,@parent,@kind,@schemaVersion,@schemaDefinition,@factKind,@verification,
              @payload::jsonb,@applicability::jsonb,@hash,false,@actor,@createdAt);
            """;
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("revision", revision.RevisionId);
        command.Parameters.AddWithValue("unit", revision.UnitId); command.Parameters.AddWithValue("revNo", revision.RevNo);
        command.Parameters.AddWithValue("parent", NpgsqlDbType.Uuid, (object?)revision.ParentRevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("kind", revision.Kind); command.Parameters.AddWithValue("schemaVersion", revision.SchemaVersion);
        command.Parameters.AddWithValue("schemaDefinition", NpgsqlDbType.Uuid, (object?)schemaDefinition ?? DBNull.Value);
        command.Parameters.AddWithValue("factKind", revision.FactKind); command.Parameters.AddWithValue("verification", revision.Verification);
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload });
        command.Parameters.Add(new NpgsqlParameter("applicability", NpgsqlDbType.Jsonb) { Value = revision.Applicability.GetRawText() });
        command.Parameters.AddWithValue("hash", revision.Sha256); command.Parameters.AddWithValue("actor", revision.CreatedBy); command.Parameters.AddWithValue("createdAt", revision.CreatedAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertReferencesAsync(NpgsqlTransaction tx, Guid projectId, RevisionView revision, CancellationToken ct)
    {
        var references = new Dictionary<string, (Guid Unit, Guid? Revision)>(StringComparer.Ordinal);
        CollectReferences(revision.Body, "$.body", references); CollectReferences(revision.Blocks, "$.blocks", references);
        foreach (var (path, target) in references)
        {
            if (target.Unit == revision.UnitId && target.Revision == revision.RevisionId) continue;
            if (target.Revision.HasValue) await RequireExactRevisionAsync(tx, projectId, target.Unit, target.Revision.Value, ct);
            else await RequireUnitAsync(tx, projectId, target.Unit, ct);
            await using var command = tx.CreateCommand();
            command.CommandText = "INSERT INTO core.revision_ref (project_id,ref_id,source_revision_id,ref_key,ref_kind,target_unit_id,target_revision_id,required) VALUES (@project,@id,@source,@key,'semantic',@unit,@revision,true);";
            command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("source", revision.RevisionId); command.Parameters.AddWithValue("key", path);
            command.Parameters.AddWithValue("unit", target.Unit); command.Parameters.AddWithValue("revision", NpgsqlDbType.Uuid, (object?)target.Revision ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ValidateRuleReferencesAsync(NpgsqlTransaction tx, Guid projectId, RevisionView revision, CancellationToken ct)
    {
        var references = revision.Kind == "Flow" ? FlowSemantics.RuleReferences(revision.Body)
            : revision.Kind == "Transition" ? FlowSemantics.TransitionRuleReferences(revision.Body)
            : [];
        foreach (var reference in references)
        {
            var unitId = reference.GetProperty("unitId").GetGuid();
            var kind = await RequireUnitKindAsync(tx, projectId, unitId, ct);
            if (kind is not ("Rule" or "BusinessRule"))
                throw ProjectIdentityException.Invalid("Flow and transition guard/rule references must target a BusinessRule.");
        }
    }

    private async Task InsertEvidenceAsync(NpgsqlTransaction tx, Guid projectId, RevisionView revision, Guid actor, CancellationToken ct)
    {
        foreach (var evidence in revision.Evidence.EnumerateArray())
        {
            var source = evidence.GetProperty("source");
            var unit = source.GetProperty("unitId").GetGuid(); var sourceRevision = source.GetProperty("revisionId").GetGuid();
            await RequireExactRevisionAsync(tx, projectId, unit, sourceRevision, ct);
            await using var command = tx.CreateCommand();
            command.CommandText = "INSERT INTO core.evidence_link (project_id,evidence_id,fact_revision_id,source_revision_id,locator,support,capture_kind,created_by) VALUES (@project,@id,@fact,@source,@locator::jsonb,@support,@capture,@actor);";
            command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("fact", revision.RevisionId); command.Parameters.AddWithValue("source", sourceRevision);
            command.Parameters.Add(new NpgsqlParameter("locator", NpgsqlDbType.Jsonb) { Value = evidence.GetProperty("locator").GetRawText() });
            command.Parameters.AddWithValue("support", evidence.GetProperty("support").GetString()!); command.Parameters.AddWithValue("capture", evidence.GetProperty("captureKind").GetString()!); command.Parameters.AddWithValue("actor", actor);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task InsertRelationAsync(NpgsqlTransaction tx, Guid projectId, RevisionView revision, CancellationToken ct)
    {
        var body = revision.Body;
        RelationSemantics.ValidateShape(body);
        var predicate = body.GetProperty("predicate").GetString()!;
        var from = body.GetProperty("from"); var to = body.GetProperty("to");
        var fromId = from.GetProperty("unitId").GetGuid(); var toId = to.GetProperty("unitId").GetGuid();
        var fromKind = await RequireUnitKindAsync(tx, projectId, fromId, ct);
        var toKind = await RequireUnitKindAsync(tx, projectId, toId, ct);
        RelationSemantics.ValidateEndpoints(predicate, fromKind, toKind);
        if (predicate == "supersedes" && fromId == toId) throw ProjectIdentityException.Invalid("A supersedes relation cannot point to itself.");
        await using var command = tx.CreateCommand();
        command.CommandText = "INSERT INTO core.relation_index (project_id,relation_revision_id,predicate,from_unit_id,to_unit_id) VALUES (@project,@revision,@predicate,@from,@to);";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("revision", revision.RevisionId);
        command.Parameters.AddWithValue("predicate", predicate); command.Parameters.AddWithValue("from", fromId); command.Parameters.AddWithValue("to", toId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertExternalIdentityAsync(NpgsqlTransaction tx, Guid projectId, Guid unitId, ExternalIdentity identity, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = "INSERT INTO core.external_identity (project_id,mapping_id,source_namespace,external_type,external_id,unit_id) VALUES (@project,@mapping,@namespace,@type,@external,@unit);";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("mapping", Guid.NewGuid());
        command.Parameters.AddWithValue("namespace", identity.Namespace); command.Parameters.AddWithValue("type", identity.ExternalType); command.Parameters.AddWithValue("external", identity.Id); command.Parameters.AddWithValue("unit", unitId);
        try { await command.ExecuteNonQueryAsync(ct); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation) { throw ProjectIdentityException.Conflict("The external identity is already mapped in this project."); }
    }

    private static async Task EnsureExternalIdentityStableAsync(NpgsqlTransaction tx, Guid projectId, Guid unitId, ExternalIdentity? identity, CancellationToken ct)
    {
        if (identity is null) return;
        await using var command = tx.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM core.external_identity WHERE project_id=@project AND unit_id=@unit AND source_namespace=@namespace AND external_type=@type AND external_id=@external);";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("unit", unitId); command.Parameters.AddWithValue("namespace", identity.Namespace); command.Parameters.AddWithValue("type", identity.ExternalType); command.Parameters.AddWithValue("external", identity.Id);
        if (await command.ExecuteScalarAsync(ct) is not true) throw ProjectIdentityException.Conflict("An external identity cannot be changed or added during revision.");
    }

    private async Task<RevisionViewPage> ReadPageAsync(NpgsqlCommand command, ResolvedReadContext context, string? cursor, int limit, Func<RevisionView, Guid> cursorKey, CancellationToken ct, bool exactRevision = true, Func<RevisionView, CancellationToken, Task<bool>>? visibilityFilter = null)
    {
        var after = DecodeCursor(cursor); var candidates = new List<RevisionView>();
        await using (var reader = await command.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) candidates.Add(ReadRevision(reader));
        var authorized = new List<RevisionView>();
        foreach (var item in candidates)
        {
            try
            {
                await AuthorizeAsync(context, item, ct, exactRevision);
                if (visibilityFilter is null || await visibilityFilter(item, ct)) authorized.Add(item);
            }
            catch (ProjectIdentityException exception) when (exception.StatusCode == 404) { }
        }
        var items = authorized.Where(item => !after.HasValue || cursorKey(item).CompareTo(after.Value) > 0).Take(limit + 1).ToList();
        var next = items.Count > limit ? EncodeCursor(cursorKey(items[limit - 1])) : null;
        if (items.Count > limit) items.RemoveAt(limit);
        await access.EnsureCurrentAsync(context, ct);
        return new(items, next);
    }

    private async Task AuthorizeAsync(ResolvedReadContext context, RevisionView revision, CancellationToken ct, bool exactRevision = true)
    {
        await access.AuthorizeReadAsync(context, revision.UnitId, "read", exactRevision ? revision.RevisionId : null, ct);
        if (context.Mode == ReadMode.Historical) await access.AuthorizeReadAsync(context, revision.UnitId, "read_history", revision.RevisionId, ct);
    }

    private async Task AuthorizeEndpointAsync(ResolvedReadContext context, JsonElement reference, CancellationToken ct)
    {
        if (!reference.TryGetProperty("unitId", out var unit) || !Guid.TryParse(unit.GetString(), out var unitId)) throw ProjectIdentityException.Invalid("A reference requires a valid unitId.");
        Guid? revisionId = reference.TryGetProperty("revisionId", out var revision) && Guid.TryParse(revision.GetString(), out var parsed) ? parsed : null;
        await access.AuthorizeReadAsync(context, unitId, "read", revisionId, ct);
    }

    private static string SelectedRevisionSql(ReadMode mode) => mode == ReadMode.Draft ? SelectColumns + """
         FROM core.unit u
         LEFT JOIN gov.change_item ci ON ci.project_id=u.project_id AND ci.unit_id=u.unit_id AND ci.change_set_id=@changeSet
         LEFT JOIN gov.baseline_entry be ON be.project_id=u.project_id AND be.unit_id=u.unit_id AND be.baseline_id=@baseline
         JOIN core.revision r ON r.project_id=u.project_id AND r.unit_id=u.unit_id AND r.revision_id=COALESCE(ci.proposed_revision_id,be.revision_id)
         WHERE u.project_id=@project AND u.archived_at IS NULL AND (ci.unit_id IS NOT NULL OR be.unit_id IS NOT NULL)
        """ : SelectColumns + """
         FROM core.unit u JOIN gov.baseline_entry be ON be.project_id=u.project_id AND be.unit_id=u.unit_id AND be.baseline_id=@baseline
         JOIN core.revision r ON r.project_id=u.project_id AND r.unit_id=u.unit_id AND r.revision_id=be.revision_id
         WHERE u.project_id=@project AND u.archived_at IS NULL
        """;

    private const string SelectColumns = "SELECT r.revision_id,u.unit_id,r.rev_no,u.kind,r.schema_version,r.fact_kind,r.verification,r.payload,r.content_sha256,r.created_at,r.created_by,r.parent_revision_id,r.sealed";

    private static RevisionView ReadRevision(NpgsqlDataReader reader)
    {
        var payload = JsonDocument.Parse(reader.GetString(7)).RootElement;
        return new(reader.GetGuid(1), reader.GetGuid(0), reader.GetInt64(2), Property(payload, "title"), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            Element(payload, "applicability", "{}"), Element(payload, "body", "{}"), Element(payload, "blocks", "[]"), Element(payload, "evidence", "[]"), Element(payload, "unknowns", "[]"),
            reader.GetString(8), new DateTimeOffset(reader.GetDateTime(9), TimeSpan.Zero), reader.GetGuid(10), payload.TryGetProperty("categoryKeys", out var categories) ? Strings(categories) : [])
        { ParentRevisionId = reader.IsDBNull(11) ? null : reader.GetGuid(11), Sealed = reader.GetBoolean(12) };
    }

    private static async Task<RevisionView?> ReadSingleAsync(NpgsqlCommand command, CancellationToken ct)
    { await using var reader = await command.ExecuteReaderAsync(ct); return await reader.ReadAsync(ct) ? ReadRevision(reader) : null; }
    private static async Task<RevisionView?> LatestRevisionAsync(NpgsqlTransaction tx, Guid projectId, Guid unitId, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = SelectColumns + " FROM core.unit u JOIN core.revision r ON r.project_id=u.project_id AND r.unit_id=u.unit_id WHERE u.project_id=@project AND u.unit_id=@unit ORDER BY r.rev_no DESC LIMIT 1;"; command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("unit", unitId); return await ReadSingleAsync(command, ct); }
    private static async Task<long> RequireProposerAsync(NpgsqlTransaction tx, Guid projectId, Guid principalId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand(); command.CommandText = "SELECT p.version,m.roles FROM identity.project p JOIN identity.membership m ON m.project_id=p.project_id AND m.principal_id=@principal AND m.active WHERE p.project_id=@project AND p.archived_at IS NULL FOR UPDATE OF p;";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("principal", principalId);
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        var version = reader.GetInt64(0); var roles = reader.GetFieldValue<string[]>(1); await reader.CloseAsync();
        if (!roles.Any(role => role is "Contributor" or "AgentProposer" or "ProjectAdmin")) throw ProjectIdentityException.Forbidden(); return version;
    }
    private static async Task RequireOpenDraftAsync(NpgsqlTransaction tx, Guid projectId, Guid changeSetId, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT EXISTS (SELECT 1 FROM gov.change_set cs JOIN identity.project p ON p.project_id=cs.project_id AND p.current_baseline_id=cs.base_baseline_id WHERE cs.project_id=@project AND cs.change_set_id=@changeSet AND cs.state IN ('draft','changes_requested'));"; command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("changeSet", changeSetId); if (await command.ExecuteScalarAsync(ct) is not true) throw ProjectIdentityException.Invalid("The change set is not an editable draft based on the current baseline."); }
    private static async Task<bool> ChangeItemExistsAsync(NpgsqlTransaction tx, Guid projectId, Guid changeSetId, Guid unitId, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT EXISTS (SELECT 1 FROM gov.change_item WHERE project_id=@project AND change_set_id=@changeSet AND unit_id=@unit);"; command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("changeSet", changeSetId); command.Parameters.AddWithValue("unit", unitId); return await command.ExecuteScalarAsync(ct) is true; }
    private static async Task InsertUnitAsync(NpgsqlTransaction tx, Guid projectId, Guid unitId, string kind, Guid actor, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "INSERT INTO core.unit (project_id,unit_id,kind,owner_id,visibility,created_by) VALUES (@project,@unit,@kind,@actor,0,@actor);"; command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("unit", unitId); command.Parameters.AddWithValue("kind", kind); command.Parameters.AddWithValue("actor", actor); await command.ExecuteNonQueryAsync(ct); }
    private static async Task RequireUnitAsync(NpgsqlTransaction tx, Guid projectId, Guid unitId, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT EXISTS (SELECT 1 FROM core.unit WHERE project_id=@project AND unit_id=@unit);"; command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("unit", unitId); if (await command.ExecuteScalarAsync(ct) is not true) throw ProjectIdentityException.Invalid("A referenced unit does not exist in this project."); }
    private static async Task<string> RequireUnitKindAsync(NpgsqlTransaction tx, Guid projectId, Guid unitId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = "SELECT kind FROM core.unit WHERE project_id=@project AND unit_id=@unit AND archived_at IS NULL;";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("unit", unitId);
        return await command.ExecuteScalarAsync(ct) as string ?? throw ProjectIdentityException.Invalid("A referenced unit does not exist in this project.");
    }
    private static async Task RequireExactRevisionAsync(NpgsqlTransaction tx, Guid projectId, Guid unitId, Guid revisionId, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT EXISTS (SELECT 1 FROM core.revision WHERE project_id=@project AND unit_id=@unit AND revision_id=@revision);"; command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("unit", unitId); command.Parameters.AddWithValue("revision", revisionId); if (await command.ExecuteScalarAsync(ct) is not true) throw ProjectIdentityException.Invalid("A reference must resolve to an exact revision in this project."); }

    private static async Task<RevisionMutation?> FindIdempotencyAsync(NpgsqlTransaction tx, Guid projectId, Guid principalId, string operation, string key, string digest, CancellationToken ct)
    {
        await using var command = tx.CreateCommand(); command.CommandText = "SELECT request_sha256,result_ref FROM ops.idempotency WHERE project_id=@project AND principal_id=@principal AND operation_id=@operation AND idempotency_key=@key;";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("principal", principalId); command.Parameters.AddWithValue("operation", operation); command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(reader.GetString(0)), Encoding.ASCII.GetBytes(digest))) throw ProjectIdentityException.Conflict("The idempotency key was already used with a different request.");
        return JsonSerializer.Deserialize<RevisionMutation>(reader.GetString(1), Json);
    }

    private static void Validate(RevisionInput input, Guid? requestedUnit, string key)
    {
        if (key.Length is < 8 or > 128) throw ProjectIdentityException.Invalid("Idempotency-Key must contain 8-128 characters.");
        if (!requestedUnit.HasValue && input.UnitId.HasValue) throw ProjectIdentityException.Invalid("unitId is assigned by the server when creating a unit.");
        if (requestedUnit.HasValue && input.UnitId != requestedUnit) throw ProjectIdentityException.Invalid("Revision unitId must match the stable unit identity in the path.");
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 500 || string.IsNullOrWhiteSpace(input.Kind) || input.Kind.Length > 100 || string.IsNullOrWhiteSpace(input.SchemaVersion)) throw ProjectIdentityException.Invalid("title, kind, and schemaVersion are required and bounded.");
        if (!FactKinds.Contains(input.FactKind) || !Verifications.Contains(input.Verification)) throw ProjectIdentityException.Invalid("factKind or verification is invalid.");
        if (input.Applicability.ValueKind != JsonValueKind.Object || input.Body.ValueKind != JsonValueKind.Object || input.Blocks.ValueKind != JsonValueKind.Array || input.Evidence.ValueKind != JsonValueKind.Array || input.Unknowns.ValueKind != JsonValueKind.Array || input.CategoryKeys.ValueKind != JsonValueKind.Array) throw ProjectIdentityException.Invalid("The revision content has an invalid shape.");
        if (input.ExternalId is { } identity && (string.IsNullOrWhiteSpace(identity.Namespace) || string.IsNullOrWhiteSpace(identity.ExternalType) || string.IsNullOrWhiteSpace(identity.Id) || identity.Namespace.Length > 200 || identity.ExternalType.Length > 200 || identity.Id.Length > 1000)) throw ProjectIdentityException.Invalid("externalId fields are required and bounded.");
        VocabularySemantics.Validate(input);
        if (RelationSemantics.IsRelation(input.Kind)) RelationSemantics.ValidateShape(input.Body);
        if (input.Kind == "Flow") FlowSemantics.Validate(input.Body);
        if (input.Kind == "Transition") FlowSemantics.ValidateTransition(input.Body);
        foreach (var evidence in input.Evidence.EnumerateArray()) ValidateEvidence(evidence);
        if (input.FactKind == "observed" && input.Body.TryGetProperty("evidenceRefs", out var observationEvidence) && observationEvidence.ValueKind == JsonValueKind.Array)
            foreach (var evidence in observationEvidence.EnumerateArray()) ValidateEvidence(evidence);
    }
    private static void ValidateEvidence(JsonElement evidence)
    {
        try
        {
            var source = evidence.GetProperty("source"); _ = source.GetProperty("unitId").GetGuid(); _ = source.GetProperty("revisionId").GetGuid(); _ = evidence.GetProperty("locator").GetProperty("kind").GetString();
            if (evidence.GetProperty("support").GetString() is not ("supports" or "refutes" or "contextual") || evidence.GetProperty("captureKind").GetString() is not ("rawCaptured" or "verifiedScript" or "reconstructed" or "proposed" or "humanAttestation")) throw new InvalidOperationException();
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or FormatException) { throw ProjectIdentityException.Invalid("Each evidence item needs an exact source revision, locator, support, and captureKind."); }
    }
    private static void CollectReferences(JsonElement value, string path, Dictionary<string, (Guid Unit, Guid? Revision)> references)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("unitId", out var unit) && Guid.TryParse(unit.GetString(), out var unitId) && value.EnumerateObject().All(item => item.Name is "unitId" or "revisionId"))
                references[path] = (unitId, value.TryGetProperty("revisionId", out var revision) && Guid.TryParse(revision.GetString(), out var revisionId) ? revisionId : null);
            else foreach (var property in value.EnumerateObject()) CollectReferences(property.Value, path + "." + property.Name, references);
        }
        else if (value.ValueKind == JsonValueKind.Array) { var index = 0; foreach (var item in value.EnumerateArray()) CollectReferences(item, path + "[" + index++ + "]", references); }
    }
    private static string ContentHash(RevisionInput input) => CanonicalJson.Sha256(JsonSerializer.SerializeToElement(new { input.Title, input.Kind, input.SchemaVersion, input.FactKind, input.Verification, applicability = input.Applicability, body = input.Body, blocks = input.Blocks, evidence = input.Evidence, unknowns = input.Unknowns, categoryKeys = input.CategoryKeys }, Json));
    private static string Payload(RevisionView revision) => CanonicalJson.Serialize(JsonSerializer.SerializeToElement(new { title = revision.Title, applicability = revision.Applicability, body = revision.Body, blocks = revision.Blocks, evidence = revision.Evidence, unknowns = revision.Unknowns, categoryKeys = revision.CategoryKeys }, Json));
    private static string Property(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : key;
    private static JsonElement Element(JsonElement value, string key, string fallback) => value.TryGetProperty(key, out var item) ? item.Clone() : JsonDocument.Parse(fallback).RootElement.Clone();
    private static IReadOnlyList<string> Strings(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : [];
    private static void AddContextParameters(NpgsqlCommand command, ResolvedReadContext context) { command.Parameters.AddWithValue("project", context.ProjectId); command.Parameters.AddWithValue("baseline", context.BaselineId); command.Parameters.AddWithValue("changeSet", NpgsqlDbType.Uuid, (object?)context.ChangeSetId ?? DBNull.Value); }
    private static async Task SetContextAsync(NpgsqlTransaction tx, ResolvedReadContext context, CancellationToken ct) => await SetContextAsync(tx, context.ProjectId, context.PrincipalId, ct);
    private static async Task SetContextAsync(NpgsqlTransaction tx, Guid projectId, Guid principalId, CancellationToken ct)
    { await using var command = tx.CreateCommand(); command.CommandText = "SELECT set_config('atlas.project_id',@project,true),set_config('atlas.subject_id',@principal,true);"; command.Parameters.AddWithValue("project", projectId.ToString()); command.Parameters.AddWithValue("principal", principalId.ToString()); await command.ExecuteNonQueryAsync(ct); }
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) { if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("ConnectionStrings:Atlas is required for knowledge operations."); var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct); return connection; }
    private static string EncodeCursor(Guid id) => Convert.ToBase64String(id.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Guid? DecodeCursor(string? cursor) { if (string.IsNullOrEmpty(cursor)) return null; try { return new Guid(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/') + new string('=', (4 - cursor.Length % 4) % 4))); } catch (Exception e) when (e is FormatException or ArgumentException) { throw ProjectIdentityException.Invalid("The pagination cursor is invalid."); } }
}
