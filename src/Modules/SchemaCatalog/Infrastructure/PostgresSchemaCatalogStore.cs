using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using Npgsql;
using NpgsqlTypes;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using SchemaCatalog.Application;
using SchemaCatalog.Contracts;

namespace SchemaCatalog.Infrastructure;

public sealed class PostgresSchemaCatalogStore(string connectionString, PostgresAccessApplicabilityStore access)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RegistryRevisionPage> ListTypesAsync(ResolvedReadContext context, string? cursor, int limit, CancellationToken ct)
    {
        var items = BuiltInRegistry.Types.Select(BuiltInRegistry.TypeRevision).ToList();
        items.AddRange(await ReadRegistryAsync(context, ["TypeDefinition", "ContentProfile"], ct));
        return Page(items, cursor, limit);
    }

    public async Task<RegistryRevisionPage> ListPredicatesAsync(ResolvedReadContext context, string? cursor, int limit, CancellationToken ct)
    {
        var items = BuiltInRegistry.Predicates.Select(BuiltInRegistry.PredicateRevision).ToList();
        items.AddRange(await ReadRegistryAsync(context, ["PredicateDefinition"], ct));
        return Page(items, cursor, limit);
    }

    public async Task<IReadOnlyList<CatalogNode>> GetCatalogAsync(ResolvedReadContext context, CancellationToken ct)
    {
        var types = BuiltInRegistry.Types.Select(item => new CatalogType(item.Kind, item.Dimension)).ToDictionary(item => item.Key, StringComparer.Ordinal);
        foreach (var revision in await ReadRegistryAsync(context, ["TypeDefinition", "ContentProfile"], ct))
        {
            if (revision.Kind == "TypeDefinition" && TryTypeDefinition(revision.Body, out var definition)) types[definition!.Key] = new(definition.Key, definition.Category);
        }
        var counts = await CountCatalogItemsAsync(context, ct);
        var nodes = new List<CatalogNode>();
        foreach (var dimension in BuiltInRegistry.Dimensions)
        {
            var children = new List<CatalogNode>();
            foreach (var type in types.Values.Where(item => item.Dimension == dimension.Key).OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                counts.TryGetValue(type.Key, out var count);
                count ??= new CatalogCount(0, 0);
                var restricted = Math.Max(0, count.Total - count.Visible);
                var description = count.Total == 0 ? "No items in this category." :
                    $"{count.Visible} visible item{(count.Visible == 1 ? "" : "s")}" +
                    (restricted > 0 ? $"; {restricted} restricted item{(restricted == 1 ? "" : "s")} not included." : ".");
                children.Add(new CatalogNode("type:" + type.Key, type.Key, [], description));
            }
            nodes.Add(new CatalogNode(dimension.Key, dimension.Label, children, $"Registry {BuiltInRegistry.Version}"));
        }
        return nodes;
    }

    public Task<RegistryMutation> ProposeTypeAsync(AtlasPrincipal actor, Guid projectId, RegistryProposal proposal,
        long expectedVersion, string idempotencyKey, string requestId, string? traceId, CancellationToken ct) =>
        ProposeAsync(actor, projectId, proposal, expectedVersion, idempotencyKey, requestId, traceId, true, ct);

    public Task<RegistryMutation> ProposePredicateAsync(AtlasPrincipal actor, Guid projectId, RegistryProposal proposal,
        long expectedVersion, string idempotencyKey, string requestId, string? traceId, CancellationToken ct) =>
        ProposeAsync(actor, projectId, proposal, expectedVersion, idempotencyKey, requestId, traceId, false, ct);

    private async Task<RegistryMutation> ProposeAsync(AtlasPrincipal actor, Guid projectId, RegistryProposal proposal,
        long expectedVersion, string idempotencyKey, string requestId, string? traceId, bool isType, CancellationToken ct)
    {
        if (idempotencyKey.Length is < 8 or > 128) throw ProjectIdentityException.Invalid("Idempotency-Key must contain 8-128 characters.");
        if (proposal.ChangeSetId == Guid.Empty || proposal.Revision is null) throw ProjectIdentityException.Invalid("changeSetId and revision are required.");
        var input = proposal.Revision;
        var operation = isType ? "proposeType" : "proposePredicate";
        var type = isType ? RegistryValidator.ValidateType(input) : null;
        var predicate = isType ? null : RegistryValidator.ValidatePredicate(input);
        if (actor.Kind == PrincipalKind.Service) throw ProjectIdentityException.Forbidden();
        var digest = Digest(new { proposal.ChangeSetId, revision = input, expectedVersion, operation });

        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(transaction, projectId, actor.PrincipalId, ct);
        var project = await RequireProposerAsync(transaction, projectId, actor.PrincipalId, ct);
        var replay = await FindIdempotencyAsync(transaction, projectId, actor.PrincipalId, operation, idempotencyKey, digest, ct);
        if (replay is not null) { await transaction.CommitAsync(ct); return replay; }
        if (project != expectedVersion) throw ProjectIdentityException.CasMismatch();
        await RequireDraftAsync(transaction, projectId, proposal.ChangeSetId, ct);

        Guid unitId;
        Guid? parentRevision = null;
        long revNo;
        if (input.UnitId.HasValue)
        {
            unitId = input.UnitId.Value;
            var previous = await LatestRevisionAsync(transaction, projectId, unitId, input.Kind!, ct) ?? throw ProjectIdentityException.NotFound();
            parentRevision = previous.RevisionId;
            revNo = previous.RevNo + 1;
            if (type is not null)
            {
                var old = ParseType(previous.Body);
                if (old.Key != type.Key) throw ProjectIdentityException.Conflict("typeKey is immutable for a type definition; create a new unit for a new typeKey.");
                RegistryValidator.EnsureTypeCompatible(type, old);
            }
            else
            {
                var old = ParsePredicate(previous.Body);
                if (old.Key != predicate!.Key) throw ProjectIdentityException.Conflict("predicate is immutable for a predicate definition; create a new unit for a new key.");
                var existing = BuiltInRegistry.Predicates.Select(item => new PredicateDefinition(item.Key, item.FromKinds, item.ToKinds, item.Meaning, item.ImpactDirections)).ToList();
                existing.AddRange(await LatestPredicatesAsync(transaction, projectId, ct));
                RegistryValidator.EnsurePredicateUnique(predicate!, existing.Where(item => item.Key != old.Key));
            }
            if (await ChangeItemExistsAsync(transaction, projectId, proposal.ChangeSetId, unitId, ct))
                throw ProjectIdentityException.Conflict("This registry unit already has a proposal in the change set.");
        }
        else
        {
            unitId = Guid.NewGuid(); revNo = 1;
            if (type is not null)
            {
                if (BuiltInRegistry.Types.Any(item => item.Kind == type.Key))
                    throw ProjectIdentityException.Conflict($"Type '{type.Key}' is a built-in type and cannot be replaced by a project extension.");
                var existing = await FindTypeByKeyAsync(transaction, projectId, type.Key, input.Kind!, ct);
                if (existing is not null) throw ProjectIdentityException.Conflict($"Type '{type.Key}' already exists at version {existing.Version}; provide its unitId and a greater compatible version.");
                if (type.Extends is not null && !BuiltInRegistry.Types.Any(item => item.Kind == type.Extends))
                    throw ProjectIdentityException.Invalid($"extends references unknown built-in type '{type.Extends}'.");
            }
            else
            {
                var existing = BuiltInRegistry.Predicates.Select(item => new PredicateDefinition(item.Key, item.FromKinds, item.ToKinds, item.Meaning, item.ImpactDirections)).ToList();
                existing.AddRange(await LatestPredicatesAsync(transaction, projectId, ct));
                RegistryValidator.EnsurePredicateUnique(predicate!, existing);
                await EnsureKindsExistAsync(transaction, projectId, predicate!.FromKinds.Concat(predicate.ToKinds), ct);
            }
            await InsertUnitAsync(transaction, projectId, unitId, input.Kind!, actor.PrincipalId, ct);
        }

        var revisionId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new { title = input.Title, applicability = input.Applicability, body = input.Body, blocks = input.Blocks,
            evidence = input.Evidence, unknowns = input.Unknowns, categoryKeys = input.CategoryKeys }, Json);
        var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        RegistryMutation? mutation = null;
        await using (var write = transaction.CreateCommand())
        {
            write.CommandText = """
                INSERT INTO core.revision (project_id,revision_id,unit_id,rev_no,parent_revision_id,schema_type,schema_version,
                  schema_definition_revision_id,fact_kind,verification,payload,applicability,content_sha256,sealed,created_by)
                VALUES (@project,@revision,@unit,@revNo,@parent,@kind,@schemaVersion,NULL,'normative','unverified',
                  @payload::jsonb,@applicability::jsonb,@hash,false,@actor);
                INSERT INTO gov.change_item (project_id,item_id,change_set_id,unit_id,base_revision_id,proposed_revision_id,action,dependency_group,reason)
                VALUES (@project,@item,@changeSet,@unit,@parent,@revision,@action,'registry','Versioned project schema proposal');
                UPDATE identity.project SET version=version+1 WHERE project_id=@project;
                INSERT INTO ops.idempotency (project_id,principal_id,operation_id,idempotency_key,request_sha256,result_ref)
                VALUES (@project,@actor,@operation,@key,@digest,@result::jsonb);
                INSERT INTO ops.audit_event (project_id,actor_id,action,resource_ref,result_code,request_id,trace_id)
                VALUES (@project,@actor,@operation,@resource::jsonb,'OK',@request,@trace);
                INSERT INTO ops.outbox (project_id,event_id,event_type,aggregate_id,aggregate_revision,schema_version,payload,state)
                VALUES (@project,@event,'RegistryProposalCreated.v1',@unit,@revNo,'1.0',@eventPayload::jsonb,'queued');
                """;
            write.Parameters.AddWithValue("project", projectId); write.Parameters.AddWithValue("revision", revisionId);
            write.Parameters.AddWithValue("unit", unitId); write.Parameters.AddWithValue("revNo", revNo);
            write.Parameters.AddWithValue("parent", NpgsqlDbType.Uuid, (object?)parentRevision ?? DBNull.Value);
            write.Parameters.AddWithValue("kind", input.Kind!); write.Parameters.AddWithValue("schemaVersion", input.SchemaVersion!);
            write.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload });
            write.Parameters.Add(new NpgsqlParameter("applicability", NpgsqlDbType.Jsonb) { Value = input.Applicability.GetRawText() });
            write.Parameters.AddWithValue("hash", contentHash); write.Parameters.AddWithValue("actor", actor.PrincipalId);
            write.Parameters.AddWithValue("item", Guid.NewGuid()); write.Parameters.AddWithValue("changeSet", proposal.ChangeSetId);
            write.Parameters.AddWithValue("action", parentRevision.HasValue ? "revise" : "add");
            write.Parameters.AddWithValue("operation", operation); write.Parameters.AddWithValue("key", idempotencyKey);
            write.Parameters.AddWithValue("digest", digest);
            var revision = new RegistryRevision(unitId, revisionId, revNo, input.Title!, input.Kind!, input.SchemaVersion!,
                "normative", "unverified", input.Applicability.Clone(), input.Body.Clone(), input.Blocks.Clone(), input.Unknowns.Clone(),
                contentHash, DateTimeOffset.UtcNow, actor.PrincipalId, ReadStrings(input.CategoryKeys));
            mutation = new RegistryMutation(revision, checked(project + 1));
            write.Parameters.Add(new NpgsqlParameter("result", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(mutation, Json) });
            write.Parameters.Add(new NpgsqlParameter("resource", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { unitId, revisionId, proposal.ChangeSetId }, Json) });
            write.Parameters.AddWithValue("request", requestId); write.Parameters.AddWithValue("trace", NpgsqlDbType.Text, (object?)traceId ?? DBNull.Value);
            write.Parameters.AddWithValue("event", Guid.NewGuid());
            write.Parameters.Add(new NpgsqlParameter("eventPayload", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { projectId, unitId, revisionId, proposal.ChangeSetId, revNo }, Json) });
            await write.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return mutation ?? throw new InvalidOperationException("The registry proposal did not produce a revision.");
    }

    private async Task<List<RegistryRevision>> ReadRegistryAsync(ResolvedReadContext context, string[] kinds, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(transaction, context.ProjectId, context.PrincipalId, ct);
        await using var command = transaction.CreateCommand();
        command.CommandText = context.Mode == ReadMode.Draft ? """
            SELECT r.revision_id,u.unit_id,r.rev_no,u.kind,r.schema_version,r.fact_kind,r.verification,r.payload,r.content_sha256,r.created_at,r.created_by
            FROM core.unit u JOIN gov.change_item ci ON ci.project_id=u.project_id AND ci.unit_id=u.unit_id AND ci.change_set_id=@changeSet
            JOIN core.revision r ON r.project_id=u.project_id AND r.unit_id=u.unit_id AND r.revision_id=ci.proposed_revision_id
            WHERE u.project_id=@project AND u.kind=ANY(@kinds) AND u.archived_at IS NULL
            UNION ALL
            SELECT r.revision_id,u.unit_id,r.rev_no,u.kind,r.schema_version,r.fact_kind,r.verification,r.payload,r.content_sha256,r.created_at,r.created_by
            FROM core.unit u JOIN gov.baseline_entry be ON be.project_id=u.project_id AND be.unit_id=u.unit_id AND be.baseline_id=@baseline
            JOIN core.revision r ON r.project_id=u.project_id AND r.unit_id=u.unit_id AND r.revision_id=be.revision_id
            WHERE u.project_id=@project AND u.kind=ANY(@kinds) AND u.archived_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM gov.change_item ci WHERE ci.project_id=u.project_id AND ci.unit_id=u.unit_id AND ci.change_set_id=@changeSet);
            """ : """
            SELECT r.revision_id,u.unit_id,r.rev_no,u.kind,r.schema_version,r.fact_kind,r.verification,r.payload,r.content_sha256,r.created_at,r.created_by
            FROM core.unit u JOIN gov.baseline_entry be ON be.project_id=u.project_id AND be.unit_id=u.unit_id AND be.baseline_id=@baseline
            JOIN core.revision r ON r.project_id=u.project_id AND r.unit_id=u.unit_id AND r.revision_id=be.revision_id
            WHERE u.project_id=@project AND u.kind=ANY(@kinds) AND u.archived_at IS NULL;
            """;
        command.Parameters.AddWithValue("project", context.ProjectId); command.Parameters.AddWithValue("baseline", context.BaselineId);
        command.Parameters.AddWithValue("changeSet", NpgsqlDbType.Uuid, (object?)context.ChangeSetId ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = kinds });
        var items = new List<RegistryRevision>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(ReadRevision(reader));
        await reader.CloseAsync();
        var authorized = new List<RegistryRevision>();
        foreach (var item in items)
        {
            try
            {
                await access.AuthorizeReadAsync(context, item.UnitId, "read", item.RevisionId, ct);
                if (context.Mode == ReadMode.Historical)
                    await access.AuthorizeReadAsync(context, item.UnitId, "read_history", item.RevisionId, ct);
                authorized.Add(item);
            }
            catch (ProjectIdentityException exception) when (exception.StatusCode == 404)
            {
                // A registry unit that is not visible in this exact read context is absent, not an empty definition.
            }
        }
        items = authorized;
        await access.EnsureCurrentAsync(context, ct);
        return items;
    }

    private async Task<Dictionary<string, CatalogCount>> CountCatalogItemsAsync(ResolvedReadContext context, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await SetContextAsync(transaction, context.ProjectId, context.PrincipalId, ct);
        await using var command = transaction.CreateCommand();
        // Registry and category names are public technical structure. Only authorized item counts are returned;
        // a restricted count distinguishes hidden content from an actually empty category without exposing titles.
        command.CommandText = context.Mode == ReadMode.Draft ? """
            WITH selected AS (
              SELECT u.unit_id,u.kind,u.visibility FROM core.unit u
              JOIN gov.change_item ci ON ci.project_id=u.project_id AND ci.unit_id=u.unit_id AND ci.change_set_id=@changeSet
              WHERE u.project_id=@project AND u.archived_at IS NULL
              UNION ALL
              SELECT u.unit_id,u.kind,u.visibility FROM core.unit u
              JOIN gov.baseline_entry be ON be.project_id=u.project_id AND be.unit_id=u.unit_id AND be.baseline_id=@baseline
              WHERE u.project_id=@project AND u.archived_at IS NULL
                AND NOT EXISTS (SELECT 1 FROM gov.change_item ci WHERE ci.project_id=u.project_id AND ci.unit_id=u.unit_id AND ci.change_set_id=@changeSet)
            ), visible AS (
              SELECT s.* FROM selected s WHERE NOT EXISTS (
                SELECT 1 FROM access.grant g WHERE g.project_id=@project AND g.unit_id=s.unit_id AND g.principal_id=@principal AND g.action='read' AND g.effect='deny' AND (cardinality(g.field_paths)=0 OR g.field_paths @> ARRAY['*']))
                AND (s.visibility<=@clearance OR EXISTS (SELECT 1 FROM access.grant g WHERE g.project_id=@project AND g.unit_id=s.unit_id AND g.principal_id=@principal AND g.action='read' AND g.effect='allow'))
                AND (@historical=false OR (EXISTS (SELECT 1 FROM access.grant g WHERE g.project_id=@project AND g.unit_id=s.unit_id AND g.principal_id=@principal AND g.action='read_history' AND g.effect='allow')
                  AND NOT EXISTS (SELECT 1 FROM access.grant g WHERE g.project_id=@project AND g.unit_id=s.unit_id AND g.principal_id=@principal AND g.action='read_history' AND g.effect='deny' AND (cardinality(g.field_paths)=0 OR g.field_paths @> ARRAY['*']))))
            ) SELECT s.kind,count(*),count(v.unit_id) FROM selected s LEFT JOIN visible v ON v.unit_id=s.unit_id GROUP BY s.kind;
            """ : """
            WITH selected AS (
              SELECT u.unit_id,u.kind,u.visibility FROM core.unit u
              JOIN gov.baseline_entry be ON be.project_id=u.project_id AND be.unit_id=u.unit_id AND be.baseline_id=@baseline
              WHERE u.project_id=@project AND u.archived_at IS NULL
            ), visible AS (
              SELECT s.* FROM selected s WHERE NOT EXISTS (
                SELECT 1 FROM access.grant g WHERE g.project_id=@project AND g.unit_id=s.unit_id AND g.principal_id=@principal AND g.action='read' AND g.effect='deny' AND (cardinality(g.field_paths)=0 OR g.field_paths @> ARRAY['*']))
                AND (s.visibility<=@clearance OR EXISTS (SELECT 1 FROM access.grant g WHERE g.project_id=@project AND g.unit_id=s.unit_id AND g.principal_id=@principal AND g.action='read' AND g.effect='allow'))
                AND (@historical=false OR (EXISTS (SELECT 1 FROM access.grant g WHERE g.project_id=@project AND g.unit_id=s.unit_id AND g.principal_id=@principal AND g.action='read_history' AND g.effect='allow')
                  AND NOT EXISTS (SELECT 1 FROM access.grant g WHERE g.project_id=@project AND g.unit_id=s.unit_id AND g.principal_id=@principal AND g.action='read_history' AND g.effect='deny' AND (cardinality(g.field_paths)=0 OR g.field_paths @> ARRAY['*']))))
            ) SELECT s.kind,count(*),count(v.unit_id) FROM selected s LEFT JOIN visible v ON v.unit_id=s.unit_id GROUP BY s.kind;
            """;
        command.Parameters.AddWithValue("project", context.ProjectId); command.Parameters.AddWithValue("baseline", context.BaselineId);
        command.Parameters.AddWithValue("changeSet", NpgsqlDbType.Uuid, (object?)context.ChangeSetId ?? DBNull.Value);
        command.Parameters.AddWithValue("principal", context.PrincipalId); command.Parameters.AddWithValue("clearance", context.Clearance);
        command.Parameters.AddWithValue("historical", context.Mode == ReadMode.Historical);
        var result = new Dictionary<string, CatalogCount>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result[reader.GetString(0)] = new(reader.GetInt64(1), reader.GetInt64(2));
        return result;
    }

    private static RegistryRevisionPage Page(IEnumerable<RegistryRevision> source, string? cursor, int limit)
    {
        var after = DecodeCursor(cursor);
        var items = source.OrderBy(item => item.UnitId).Where(item => !after.HasValue || item.UnitId.CompareTo(after.Value) > 0).Take(limit + 1).ToList();
        var next = items.Count > limit ? EncodeCursor(items[limit - 1].UnitId) : null;
        if (items.Count > limit) items.RemoveAt(limit);
        return new RegistryRevisionPage(items, next);
    }

    private static RegistryRevision ReadRevision(NpgsqlDataReader reader)
    {
        var payload = JsonDocument.Parse(reader.GetString(7)).RootElement;
        var created = reader.GetDateTime(9);
        return new RegistryRevision(reader.GetGuid(1), reader.GetGuid(0), reader.GetInt64(2), Property(payload, "title"),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            PropertyElement(payload, "applicability", "{}"), PropertyElement(payload, "body", "{}"), PropertyElement(payload, "blocks", "[]"),
            PropertyElement(payload, "unknowns", "[]"), reader.GetString(8), new DateTimeOffset(created, TimeSpan.Zero), reader.GetGuid(10),
            payload.TryGetProperty("categoryKeys", out var categories) ? ReadStrings(categories) : []);
    }

    private static string Property(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()! : name;
    private static JsonElement PropertyElement(JsonElement value, string name, string fallback) => value.TryGetProperty(name, out var property) ? property.Clone() : JsonDocument.Parse(fallback).RootElement.Clone();
    private static IReadOnlyList<string> ReadStrings(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : [];

    private static TypeDefinition ParseType(JsonElement body)
    { if (!TryTypeDefinition(body, out var definition)) throw ProjectIdentityException.Conflict("The previous type revision is malformed and cannot be extended safely."); return definition; }
    private static bool TryTypeDefinition(JsonElement body, out TypeDefinition definition)
    {
        definition = default!;
        try
        {
            if (!body.TryGetProperty("typeKey", out var key) || !body.TryGetProperty("version", out var version) ||
                !body.TryGetProperty("profile", out var profile) || !body.TryGetProperty("schema", out var schema) || !body.TryGetProperty("category", out var category)) return false;
            definition = new(key.GetString()!, version.GetString()!, profile.GetString()!, category.GetString()!, schema.Clone(), body.TryGetProperty("extends", out var parent) ? parent.GetString() : null);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }
    private static PredicateDefinition ParsePredicate(JsonElement body) => new(body.GetProperty("predicate").GetString()!, ReadStrings(body.GetProperty("fromKinds")),
        ReadStrings(body.GetProperty("toKinds")), body.GetProperty("meaning").GetString()!, ReadStrings(body.GetProperty("impactDirections")));

    private static async Task<RegistryRevision?> LatestRevisionAsync(NpgsqlTransaction tx, Guid projectId, Guid unitId, string kind, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = """
            SELECT r.revision_id,u.unit_id,r.rev_no,u.kind,r.schema_version,r.fact_kind,r.verification,r.payload,r.content_sha256,r.created_at,r.created_by
            FROM core.unit u JOIN core.revision r ON r.project_id=u.project_id AND r.unit_id=u.unit_id
            WHERE u.project_id=@project AND u.unit_id=@unit AND u.kind=@kind ORDER BY r.rev_no DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("unit", unitId); command.Parameters.AddWithValue("kind", kind);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRevision(reader) : null;
    }

    private static async Task<TypeDefinition?> FindTypeByKeyAsync(NpgsqlTransaction tx, Guid projectId, string key, string kind, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = """
            SELECT r.payload FROM core.unit u JOIN core.revision r ON r.project_id=u.project_id AND r.unit_id=u.unit_id
            WHERE u.project_id=@project AND u.kind=@kind AND r.payload->'body'->>'typeKey'=@key ORDER BY r.rev_no DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("kind", kind); command.Parameters.AddWithValue("key", key);
        if (await command.ExecuteScalarAsync(ct) is not string payload) return null;
        return ParseType(JsonDocument.Parse(payload).RootElement.GetProperty("body"));
    }

    private static async Task<List<PredicateDefinition>> LatestPredicatesAsync(NpgsqlTransaction tx, Guid projectId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT ON (u.unit_id) r.payload FROM core.unit u JOIN core.revision r ON r.project_id=u.project_id AND r.unit_id=u.unit_id
            WHERE u.project_id=@project AND u.kind='PredicateDefinition' ORDER BY u.unit_id,r.rev_no DESC;
            """;
        command.Parameters.AddWithValue("project", projectId);
        var result = new List<PredicateDefinition>(); await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ParsePredicate(JsonDocument.Parse(reader.GetString(0)).RootElement.GetProperty("body")));
        return result;
    }

    private static async Task EnsureKindsExistAsync(NpgsqlTransaction tx, Guid projectId, IEnumerable<string> kinds, CancellationToken ct)
    {
        foreach (var kind in kinds.Where(kind => kind != "*").Distinct(StringComparer.Ordinal))
            if (!BuiltInRegistry.Types.Any(item => item.Kind == kind) && await FindTypeByKeyAsync(tx, projectId, kind, "TypeDefinition", ct) is null)
                throw ProjectIdentityException.Invalid($"Predicate endpoint '{kind}' is not a registered built-in or project type.");
    }

    private static async Task<long> RequireProposerAsync(NpgsqlTransaction tx, Guid projectId, Guid principalId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = """
            SELECT p.version,m.roles FROM identity.project p JOIN identity.membership m ON m.project_id=p.project_id AND m.principal_id=@principal AND m.active
            WHERE p.project_id=@project AND p.archived_at IS NULL FOR UPDATE OF p;
            """;
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("principal", principalId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        var version = reader.GetInt64(0); var roles = reader.GetFieldValue<string[]>(1); await reader.CloseAsync();
        if (!roles.Any(role => role is "Contributor" or "AgentProposer" or "ProjectAdmin")) throw ProjectIdentityException.Forbidden();
        return version;
    }

    private static async Task RequireDraftAsync(NpgsqlTransaction tx, Guid projectId, Guid changeSetId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM gov.change_set cs JOIN identity.project p ON p.project_id=cs.project_id AND p.current_baseline_id=cs.base_baseline_id
            WHERE cs.project_id=@project AND cs.change_set_id=@changeSet AND cs.state NOT IN ('published','rejected','abandoned'));
            """;
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("changeSet", changeSetId);
        if (await command.ExecuteScalarAsync(ct) is not true) throw ProjectIdentityException.Invalid("The change set is not an open draft based on the current baseline.");
    }

    private static async Task<bool> ChangeItemExistsAsync(NpgsqlTransaction tx, Guid projectId, Guid changeSetId, Guid unitId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand(); command.CommandText = "SELECT EXISTS (SELECT 1 FROM gov.change_item WHERE project_id=@project AND change_set_id=@changeSet AND unit_id=@unit);";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("changeSet", changeSetId); command.Parameters.AddWithValue("unit", unitId);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private static async Task InsertUnitAsync(NpgsqlTransaction tx, Guid projectId, Guid unitId, string kind, Guid actor, CancellationToken ct)
    {
        await using var command = tx.CreateCommand(); command.CommandText = "INSERT INTO core.unit (project_id,unit_id,kind,owner_id,visibility,created_by,version) VALUES (@project,@unit,@kind,@actor,0,@actor,1);";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("unit", unitId); command.Parameters.AddWithValue("kind", kind); command.Parameters.AddWithValue("actor", actor);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<RegistryMutation?> FindIdempotencyAsync(NpgsqlTransaction tx, Guid projectId, Guid principalId, string operation, string key, string digest, CancellationToken ct)
    {
        await using var command = tx.CreateCommand(); command.CommandText = "SELECT request_sha256,result_ref FROM ops.idempotency WHERE project_id=@project AND principal_id=@principal AND operation_id=@operation AND idempotency_key=@key;";
        command.Parameters.AddWithValue("project", projectId); command.Parameters.AddWithValue("principal", principalId); command.Parameters.AddWithValue("operation", operation); command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(reader.GetString(0)), Encoding.ASCII.GetBytes(digest))) throw ProjectIdentityException.Conflict("The idempotency key was already used with a different request.");
        return JsonSerializer.Deserialize<RegistryMutation>(reader.GetString(1), Json);
    }

    private static async Task SetContextAsync(NpgsqlTransaction tx, Guid projectId, Guid principalId, CancellationToken ct)
    {
        await using var command = tx.CreateCommand(); command.CommandText = "SELECT set_config('atlas.project_id',@project,true),set_config('atlas.subject_id',@principal,true);";
        command.Parameters.AddWithValue("project", projectId.ToString()); command.Parameters.AddWithValue("principal", principalId.ToString()); await command.ExecuteNonQueryAsync(ct);
    }
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    { if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("ConnectionStrings:Atlas is required for schema catalog operations."); var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct); return connection; }
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json))).ToLowerInvariant();
    private static string EncodeCursor(Guid id) => Convert.ToBase64String(id.ToByteArray()).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static Guid? DecodeCursor(string? cursor)
    { if (string.IsNullOrEmpty(cursor)) return null; try { return new Guid(Convert.FromBase64String(cursor.Replace('-','+').Replace('_','/') + new string('=', (4-cursor.Length%4)%4))); } catch (Exception e) when (e is FormatException or ArgumentException) { throw ProjectIdentityException.Invalid("The pagination cursor is invalid."); } }

    private sealed record CatalogType(string Key, string Dimension);
    private sealed record CatalogCount(long Total, long Visible);
}

internal static class NpgsqlTransactionExtensions
{
    public static NpgsqlCommand CreateCommand(this NpgsqlTransaction transaction)
    {
        var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        return command;
    }
}
