using System.Text.Json;
using AccessApplicability.Application;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using KnowledgeCore.Contracts;
using KnowledgeCore.Infrastructure;
using KnowledgeCore.Relations;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using ProjectIdentity.Infrastructure;

namespace Atlas.Integration.Tests;

public sealed class KnowledgeCoreTests
{
    [PostgreSqlFact]
    public async Task AT_T08_Stable_identity_revision_references_evidence_and_rule_path()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t08_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(administrator.ConnectionString))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE {database}"; await command.ExecuteNonQueryAsync();
        }
        administrator.Database = database;
        try
        {
            await MigrateAsync(administrator.ConnectionString);
            var runtime = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Options = "-c role=atlas_app" }.ConnectionString;
            var projects = new PostgresProjectIdentityStore(runtime);
            var access = new PostgresAccessApplicabilityStore(runtime);
            var policy = new PolicyService(access);
            var store = new PostgresKnowledgeStore(runtime, access);
            var owner = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "owner", PrincipalKind.Human, CancellationToken.None);
            var first = await projects.CreateProjectAsync(owner, new("First", "first", "t08-first-0001"), "r1", null, CancellationToken.None);
            var second = await projects.CreateProjectAsync(owner, new("Second", "second", "t08-second-0001"), "r2", null, CancellationToken.None);
            var firstChange = await ChangeSetAsync(administrator.ConnectionString, first);
            var secondChange = await ChangeSetAsync(administrator.ConnectionString, second);

            var sourceInput = Input("Shared title", "Source", "source", null);
            var source = await store.CreateDraftAsync(owner, first.ProjectId, firstChange, sourceInput, first.Version, "t08-source-0001", "r3", null, CancellationToken.None);
            var sameName = await store.CreateDraftAsync(owner, second.ProjectId, secondChange, sourceInput, second.Version, "t08-source-0002", "r4", null, CancellationToken.None);
            Assert.NotEqual(source.Revision.UnitId, sameName.Revision.UnitId);

            var ruleInput = Input("Eligibility", "Rule", "rule", source.Revision, source.Revision.UnitId);
            var rule = await store.CreateDraftAsync(owner, first.ProjectId, firstChange, ruleInput, source.ProjectVersion, "t08-rule-0001", "r5", null, CancellationToken.None);
            var read = await policy.CreateReadContextAsync(owner.PrincipalId, first.ProjectId, new(ReadMode.Draft, ReadPurpose.Verify, null, firstChange, null), CancellationToken.None);
            var loaded = await store.GetUnitAsync(read, rule.Revision.UnitId, CancellationToken.None);
            Assert.Equal(source.Revision.UnitId, loaded.Body.GetProperty("subject").GetProperty("unitId").GetGuid());
            var units = await store.ListUnitsAsync(read, null, null, null, 30, CancellationToken.None);
            Assert.Equal(2, units.Items.Count);

            var facts = JsonSerializer.SerializeToElement(new Dictionary<string, string> { [source.Revision.UnitId.ToString()] = "yes" });
            var evaluated = await store.EvaluateRuleAsync(read, rule.Revision.UnitId, facts, CancellationToken.None);
            Assert.Equal("matched", evaluated.Status);
            Assert.Single(evaluated.Evidence);

            var nextChange = await ChangeSetAsync(administrator.ConnectionString, first);
            var revisionInput = ruleInput with { UnitId = rule.Revision.UnitId, Title = "Eligibility v2" };
            var revised = await store.ReviseDraftAsync(owner, first.ProjectId, nextChange, rule.Revision.UnitId, revisionInput, rule.ProjectVersion, "t08-revise-0001", "r6", null, CancellationToken.None);
            Assert.Equal(rule.Revision.UnitId, revised.Revision.UnitId);
            Assert.NotEqual(rule.Revision.RevisionId, revised.Revision.RevisionId);
            Assert.Equal(2, revised.Revision.RevNo);
            var nextRead = await policy.CreateReadContextAsync(owner.PrincipalId, first.ProjectId, new(ReadMode.Draft, ReadPurpose.Change, null, nextChange, null), CancellationToken.None);
            Assert.Equal(2, (await store.ListRevisionsAsync(nextRead, rule.Revision.UnitId, null, 30, CancellationToken.None)).Items.Count);

            await using var verification = new NpgsqlConnection(administrator.ConnectionString);
            await verification.OpenAsync();
            Assert.Equal(2, await ScalarAsync(verification, "SELECT count(*) FROM core.evidence_link WHERE project_id=@project", first.ProjectId));
            Assert.True(await ScalarAsync(verification, "SELECT count(*) FROM core.revision_ref WHERE project_id=@project", first.ProjectId) >= 1);
            await using var immutable = verification.CreateCommand();
            immutable.CommandText = "UPDATE core.revision_ref SET required=false WHERE project_id=@project";
            immutable.Parameters.AddWithValue("project", first.ProjectId);
            await Assert.ThrowsAsync<PostgresException>(() => immutable.ExecuteNonQueryAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"; await command.ExecuteNonQueryAsync();
        }
    }

    [PostgreSqlFact]
    public async Task AT_T09_Field_rename_preserves_business_attribute_and_reverse_rule_lookup_without_leaks()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t09_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(administrator.ConnectionString))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE {database}"; await command.ExecuteNonQueryAsync();
        }
        administrator.Database = database;
        try
        {
            await MigrateAsync(administrator.ConnectionString);
            var runtime = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Options = "-c role=atlas_app" }.ConnectionString;
            var projects = new PostgresProjectIdentityStore(runtime);
            var access = new PostgresAccessApplicabilityStore(runtime);
            var policy = new PolicyService(access);
            var store = new PostgresKnowledgeStore(runtime, access);
            var owner = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "owner", PrincipalKind.Human, CancellationToken.None);
            var project = await projects.CreateProjectAsync(owner, new("Semantic trace", "semantic-trace", "t09-project-0001"), "r1", null, CancellationToken.None);
            var change = await ChangeSetAsync(administrator.ConnectionString, project);
            var version = project.Version;

            var source = await Add(Input("Semantic source", "Source", "source", null));
            var attribute = await Add(VocabularyInput("Parameter amount", "BusinessAttribute", source.Revision));
            var field = await Add(DataFieldInput("legacy_amount", source.Revision));
            var firstRule = await Add(CanonicalRuleInput("First amount rule", attribute.Revision, source.Revision));
            var secondRule = await Add(CanonicalRuleInput("Second amount rule", attribute.Revision, source.Revision));
            var mapping = await Add(RelationInput("Amount field mapping", "mapsTo", attribute.Revision, field.Revision, source.Revision));
            _ = await Add(RelationInput("First rule input", "usesFact", firstRule.Revision, attribute.Revision, source.Revision));
            var secondUse = await Add(RelationInput("Second rule input", "usesFact", secondRule.Revision, attribute.Revision, source.Revision));

            var read = await policy.CreateReadContextAsync(owner.PrincipalId, project.ProjectId, new(ReadMode.Draft, ReadPurpose.Change, null, change, null), CancellationToken.None);
            var fieldMappings = await store.ListRelationsAsync(read, field.Revision.UnitId, "reverse", "mapsTo", null, 30, CancellationToken.None);
            Assert.Single(fieldMappings.Items);
            Assert.Equal(mapping.Revision.UnitId, fieldMappings.Items[0].UnitId);
            var rules = await store.ListRelationsAsync(read, attribute.Revision.UnitId, "reverse", "usesFact", null, 30, CancellationToken.None);
            Assert.Equal(2, rules.Items.Count);
            Assert.Contains(rules.Items, relation => relation.Body.GetProperty("from").GetProperty("unitId").GetGuid() == firstRule.Revision.UnitId);
            Assert.Contains(rules.Items, relation => relation.Body.GetProperty("from").GetProperty("unitId").GetGuid() == secondRule.Revision.UnitId);
            var facts = JsonSerializer.SerializeToElement(new Dictionary<string, string> { [attribute.Revision.UnitId.ToString()] = "yes" });
            Assert.Equal("matched", (await store.EvaluateRuleAsync(read, firstRule.Revision.UnitId, facts, CancellationToken.None)).Status);

            var reader = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "restricted-reader", PrincipalKind.Human, CancellationToken.None);
            await using (var db = new NpgsqlConnection(administrator.ConnectionString))
            {
                await db.OpenAsync();
                await using var membership = db.CreateCommand();
                membership.CommandText = "INSERT INTO identity.membership(project_id,principal_id,roles) VALUES(@project,@reader,ARRAY['Contributor']); INSERT INTO access.grant(project_id,grant_id,unit_id,principal_id,action,field_paths,effect,created_by) VALUES(@project,@grant,@unit,@reader,'read',ARRAY['*'],'deny',@owner);";
                membership.Parameters.AddWithValue("project", project.ProjectId); membership.Parameters.AddWithValue("reader", reader.PrincipalId);
                membership.Parameters.AddWithValue("grant", Guid.NewGuid()); membership.Parameters.AddWithValue("unit", secondRule.Revision.UnitId); membership.Parameters.AddWithValue("owner", owner.PrincipalId);
                await membership.ExecuteNonQueryAsync();
            }
            var restrictedRead = await policy.CreateReadContextAsync(reader.PrincipalId, project.ProjectId, new(ReadMode.Draft, ReadPurpose.Change, null, change, null), CancellationToken.None);
            var visibleRules = await store.ListRelationsAsync(restrictedRead, attribute.Revision.UnitId, "reverse", "usesFact", null, 30, CancellationToken.None);
            var visible = Assert.Single(visibleRules.Items);
            Assert.Equal(firstRule.Revision.UnitId, visible.Body.GetProperty("from").GetProperty("unitId").GetGuid());
            Assert.DoesNotContain(secondRule.Revision.UnitId.ToString(), JsonSerializer.Serialize(visibleRules));

            var nextChange = await ChangeSetAsync(administrator.ConnectionString, project);
            var renamedField = DataFieldInput("renamed_amount_v2", source.Revision) with { UnitId = field.Revision.UnitId };
            var renamed = await store.ReviseDraftAsync(owner, project.ProjectId, nextChange, field.Revision.UnitId, renamedField, secondUse.ProjectVersion, "t09-field-rename-0001", "r9", null, CancellationToken.None);
            Assert.Equal(field.Revision.UnitId, renamed.Revision.UnitId);
            Assert.Equal("Technical field rename only", renamed.Revision.Body.GetProperty("businessMeaning").GetString());
            var nextRead = await policy.CreateReadContextAsync(owner.PrincipalId, project.ProjectId, new(ReadMode.Draft, ReadPurpose.Change, null, nextChange, null), CancellationToken.None);
            Assert.Equal("renamed_amount_v2", (await store.GetUnitAsync(nextRead, field.Revision.UnitId, CancellationToken.None)).Title);
            Assert.Single((await store.ListRelationsAsync(read, field.Revision.UnitId, "reverse", "mapsTo", null, 30, CancellationToken.None)).Items);
            Assert.Equal(2, (await store.ListRelationsAsync(read, attribute.Revision.UnitId, "reverse", "usesFact", null, 30, CancellationToken.None)).Items.Count);

            async Task<RevisionMutation> Add(RevisionInput input)
            {
                var result = await store.CreateDraftAsync(owner, project.ProjectId, change, input, version, $"t09-{Guid.NewGuid():N}", Guid.NewGuid().ToString(), null, CancellationToken.None);
                version = result.ProjectVersion; return result;
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"; await command.ExecuteNonQueryAsync();
        }
    }

    private static RevisionInput VocabularyInput(string title, string kind, RevisionView source) => new(null, title, kind, "1.0", null, "normative", "unverified", Json("{}"),
        JsonSerializer.SerializeToElement(new { definition = "Independent business meaning survives physical field changes", contextKey = "atlas-test", aliases = new[] { "Amount" }, businessSemantics = "Missing and null are business decisions, not SQL defaults" }),
        Json("[]"), JsonSerializer.SerializeToElement(new[] { Evidence(source) }), Json("[]"), Json("[\"business\"]"));

    private static RevisionInput DataFieldInput(string title, RevisionView source) => new(null, title, "DataField", "1.0", null, "normative", "unverified", Json("{}"),
        JsonSerializer.SerializeToElement(new { businessMeaning = "Technical field rename only", valueType = "decimal", missingNullSemantics = "Defined independently by the business attribute", origin = "AT-T09 synthetic schema", constraints = Array.Empty<string>(), mappingRefs = Array.Empty<object>() }),
        Json("[]"), JsonSerializer.SerializeToElement(new[] { Evidence(source) }), Json("[]"), Json("[\"system\"]"));

    private static RevisionInput CanonicalRuleInput(string title, RevisionView attribute, RevisionView source) => new(null, title, "BusinessRule", "1.0", null, "normative", "unverified", Json("{}"),
        JsonSerializer.SerializeToElement(new { purpose = "AT-T09 deterministic rule", evaluationMode = "structured", inputs = new[] { new { attribute = new { unitId = attribute.UnitId }, valueType = "string" } }, priorityPolicy = "unique",
            rows = new[] { new { rowId = "yes", when = new { op = "eq", comparison = "ordinal", left = new { kind = "field", attributeId = attribute.UnitId }, right = new { kind = "literal", valueType = "string", value = "yes" } }, outcome = new { kind = "perform", description = "Return only" }, evidenceRefs = new[] { Evidence(source) } } },
            exceptionExplanation = "none", caseRefs = Array.Empty<object>() }),
        Json("[]"), JsonSerializer.SerializeToElement(new[] { Evidence(source) }), Json("[]"), Json("[\"business\"]"));

    private static RevisionInput RelationInput(string title, string predicate, RevisionView from, RevisionView to, RevisionView source) => new(null, title, "Relationship", "1.0", null, "normative", "unverified", Json("{}"),
        JsonSerializer.SerializeToElement(new { predicate, from = new { unitId = from.UnitId }, to = new { unitId = to.UnitId }, meaning = RelationSemantics.Meaning(predicate) }),
        Json("[]"), JsonSerializer.SerializeToElement(new[] { Evidence(source) }), Json("[]"), Json("[\"knowledge\"]"));

    private static RevisionInput Input(string title, string kind, string type, RevisionView? source, Guid? subject = null)
    {
        var body = kind == "Rule" ? JsonSerializer.SerializeToElement(new
        {
            purpose = "Deterministic test rule", evaluationMode = "structured", inputs = Array.Empty<object>(), priorityPolicy = "unique",
            rows = new[] { new { rowId = "eligible", situation = "yes", comparisonExplanation = "equals yes", when = new { op = "eq", comparison = "ordinal", left = new { kind = "field", attributeId = subject!.Value }, right = new { kind = "literal", valueType = "string", value = "yes" } }, outcome = new { kind = "perform", description = "Return only" }, evidenceRefs = new[] { Evidence(source!) } } },
            exceptionExplanation = "none", caseRefs = Array.Empty<object>(), subject = new { unitId = subject!.Value }
        }) : JsonSerializer.SerializeToElement(new { sourceKind = "human", locator = new { kind = "human", description = "Automated test source" }, provenance = "AT-T08" });
        var evidence = source is null ? Json("[]") : JsonSerializer.SerializeToElement(new[] { Evidence(source) });
        return new(null, title, kind, "1.0", null, "normative", "unverified", Json("{}"), body, Json("[]"), evidence, Json("[]"), Json($"[\"{type}\"]"), new("atlas-test", type, "shared-external-id"));
    }

    private static object Evidence(RevisionView source) => new { source = new { unitId = source.UnitId, revisionId = source.RevisionId }, locator = new { kind = "human", description = "Automated test evidence" }, support = "supports", captureKind = "humanAttestation" };
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static async Task<Guid> ChangeSetAsync(string connection, ProjectSummary project)
    {
        var id = Guid.NewGuid(); await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO gov.change_set (project_id,change_set_id,base_baseline_id,title,state,created_by) VALUES (@project,@id,@baseline,'AT-T08','draft',(SELECT owner_id FROM identity.project WHERE project_id=@project));";
        command.Parameters.AddWithValue("project", project.ProjectId); command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("baseline", project.CurrentBaselineId); await command.ExecuteNonQueryAsync(); return id;
    }
    private static async Task MigrateAsync(string connection) { var options = new DbContextOptionsBuilder<BuildingBlocks.Persistence.AtlasDbContext>().UseNpgsql(connection).Options; await using var context = new BuildingBlocks.Persistence.AtlasDbContext(options); await context.Database.MigrateAsync(); }
    private static async Task<int> ScalarAsync(NpgsqlConnection connection, string sql, Guid project) { await using var command = connection.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("project", project); return Convert.ToInt32(await command.ExecuteScalarAsync()); }
}
