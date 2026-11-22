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

public sealed class IndependentInvariantTests
{
    [PostgreSqlFact]
    public async Task AT_T41_Real_PostgreSQL_isolates_projects_drafts_and_sealed_history()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t41_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(administrator.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {database}";
            await command.ExecuteNonQueryAsync();
        }
        administrator.Database = database;
        try
        {
            var options = new DbContextOptionsBuilder<BuildingBlocks.Persistence.AtlasDbContext>().UseNpgsql(administrator.ConnectionString).Options;
            await using (var context = new BuildingBlocks.Persistence.AtlasDbContext(options)) await context.Database.MigrateAsync();
            var runtime = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Options = "-c role=atlas_app" }.ConnectionString;
            var projects = new PostgresProjectIdentityStore(runtime);
            var access = new PostgresAccessApplicabilityStore(runtime);
            var policy = new PolicyService(access);
            var knowledge = new PostgresKnowledgeStore(runtime, access);
            var actor = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "t41-automated-test-actor", PrincipalKind.Human, CancellationToken.None);
            var first = await projects.CreateProjectAsync(actor, new("Same name", $"t41-first-{Guid.NewGuid():N}", "t41-first-0001"), "r1", null, CancellationToken.None);
            var second = await projects.CreateProjectAsync(actor, new("Same name", $"t41-second-{Guid.NewGuid():N}", "t41-second-0001"), "r2", null, CancellationToken.None);
            var changeSet = Guid.NewGuid();
            await using (var db = new NpgsqlConnection(administrator.ConnectionString))
            {
                await db.OpenAsync();
                await using var command = db.CreateCommand();
                command.CommandText = "INSERT INTO gov.change_set(project_id,change_set_id,base_baseline_id,title,state,created_by) VALUES(@project,@change,@baseline,'AT-T41 independent draft','draft',@actor)";
                command.Parameters.AddWithValue("project", first.ProjectId); command.Parameters.AddWithValue("change", changeSet);
                command.Parameters.AddWithValue("baseline", first.CurrentBaselineId); command.Parameters.AddWithValue("actor", actor.PrincipalId);
                await command.ExecuteNonQueryAsync();
            }
            var input = new RevisionInput(null, "Shared business title", "Source", "1.0", null, "observed", "unverified", Json("{}"),
                Json("""{"sourceKind":"human","locator":{"kind":"human","description":"AT-T41 synthetic source"},"provenance":"Independent acceptance fixture"}"""),
                Json("[]"), Json("[]"), Json("[]"), Json("[\"source\"]"));
            var draft = await knowledge.CreateDraftAsync(actor, first.ProjectId, changeSet, input, first.Version, "t41-draft-0001", "r3", null, CancellationToken.None);
            var firstPublished = await policy.CreateReadContextAsync(actor.PrincipalId, first.ProjectId, new(ReadMode.Published, ReadPurpose.Understand, null, null, null), CancellationToken.None);
            var secondPublished = await policy.CreateReadContextAsync(actor.PrincipalId, second.ProjectId, new(ReadMode.Published, ReadPurpose.Understand, null, null, null), CancellationToken.None);
            Assert.Empty((await knowledge.ListUnitsAsync(firstPublished, null, null, null, 30, CancellationToken.None)).Items);
            var crossProject = await Assert.ThrowsAsync<ProjectIdentityException>(() => knowledge.GetUnitAsync(secondPublished, draft.Revision.UnitId, CancellationToken.None));
            Assert.Equal(404, crossProject.StatusCode);
            var direct = await Assert.ThrowsAsync<ProjectIdentityException>(() => knowledge.GetUnitAsync(firstPublished, draft.Revision.UnitId, CancellationToken.None));
            Assert.Equal(404, direct.StatusCode);

            await using var verification = new NpgsqlConnection(administrator.ConnectionString);
            await verification.OpenAsync();
            await using var mutate = verification.CreateCommand();
            mutate.CommandText = "UPDATE core.revision SET payload='{}' WHERE project_id=@project AND revision_id=@revision";
            mutate.Parameters.AddWithValue("project", first.ProjectId); mutate.Parameters.AddWithValue("revision", draft.Revision.RevisionId);
            await Assert.ThrowsAsync<PostgresException>(() => mutate.ExecuteNonQueryAsync());
            await using var count = verification.CreateCommand();
            count.CommandText = "SELECT count(*) FROM gov.baseline_entry WHERE project_id=@project";
            count.Parameters.AddWithValue("project", first.ProjectId);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"; await command.ExecuteNonQueryAsync();
        }
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}
