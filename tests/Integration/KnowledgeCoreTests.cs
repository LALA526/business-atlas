using System.Text.Json;
using AccessApplicability.Application;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using KnowledgeCore.Contracts;
using KnowledgeCore.Infrastructure;
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
