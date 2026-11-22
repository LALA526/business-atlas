using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using ProjectIdentity.Infrastructure;

namespace Atlas.Integration.Tests;

public sealed class ProjectIdentityTests
{
    [PostgreSqlFact]
    public async Task AT_T05_Projects_are_isolated_and_start_with_an_empty_baseline()
    {
        var administrator = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t05_{Guid.NewGuid():N}";
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
            await MigrateAsync(administrator.ConnectionString);
            var runtime = new NpgsqlConnectionStringBuilder(administrator.ConnectionString)
            {
                Options = "-c role=atlas_app"
            }.ConnectionString;
            var store = new PostgresProjectIdentityStore(runtime);
            var issuer = $"https://identity.test/{Guid.NewGuid():N}";
            var owner = await store.ResolvePrincipalAsync(issuer, "owner", PrincipalKind.Human, CancellationToken.None);
            var outsider = await store.ResolvePrincipalAsync(issuer, "outsider", PrincipalKind.Human, CancellationToken.None);

            var firstCommand = new CreateProjectCommand("First", "first", "create-first-0001");
            var first = await store.CreateProjectAsync(owner, firstCommand, "request-1", null, CancellationToken.None);
            var replay = await store.CreateProjectAsync(owner, firstCommand, "request-2", null, CancellationToken.None);
            var second = await store.CreateProjectAsync(owner,
                new CreateProjectCommand("Second", "second", "create-second-0001"), "request-3", null, CancellationToken.None);

            Assert.Equal(first, replay);
            Assert.NotEqual(first.ProjectId, second.ProjectId);
            Assert.Equal(2, (await store.ListProjectsAsync(owner.PrincipalId, null, 30, CancellationToken.None)).Items.Count);
            Assert.Empty((await store.ListProjectsAsync(outsider.PrincipalId, null, 30, CancellationToken.None)).Items);

            var denied = await Assert.ThrowsAsync<ProjectIdentityException>(() =>
                store.GetProjectAsync(outsider.PrincipalId, first.ProjectId, CancellationToken.None));
            Assert.Equal(404, denied.StatusCode);

            var changed = await store.ChangeMembershipAsync(owner, first.ProjectId,
                new ChangeMembershipCommand(outsider.PrincipalId, ["ProjectReader"], 0, true, first.Version, "membership-0001"),
                "request-4", null, CancellationToken.None);
            Assert.Equal(2, changed.PolicyVersion);
            Assert.Equal(first.ProjectId,
                (await store.GetProjectAsync(outsider.PrincipalId, first.ProjectId, CancellationToken.None)).ProjectId);
            Assert.Single((await store.ListProjectsAsync(outsider.PrincipalId, null, 30, CancellationToken.None)).Items);

            var archived = await store.ArchiveProjectAsync(owner, second.ProjectId, second.Version, "archive-second-0001", "request-archive", null, CancellationToken.None);
            var archiveReplay = await store.ArchiveProjectAsync(owner, second.ProjectId, second.Version, "archive-second-0001", "request-replay", null, CancellationToken.None);
            Assert.Equal(archived, archiveReplay);
            Assert.Single((await store.ListProjectsAsync(owner.PrincipalId, null, 30, CancellationToken.None)).Items);
            var archivedRead = await Assert.ThrowsAsync<ProjectIdentityException>(() =>
                store.GetProjectAsync(owner.PrincipalId, second.ProjectId, CancellationToken.None));
            Assert.Equal(404, archivedRead.StatusCode);

            await using var verification = new NpgsqlConnection(administrator.ConnectionString);
            await verification.OpenAsync();
            Assert.Equal(0, await ScalarAsync(verification,
                "SELECT count(*) FROM gov.baseline_entry WHERE project_id = @project", first.ProjectId));
            Assert.Equal(0, await ScalarAsync(verification,
                "SELECT count(*) FROM core.unit WHERE project_id = @project", first.ProjectId));
            Assert.Equal(1, await ScalarAsync(verification,
                "SELECT count(*) FROM identity.project_profile WHERE project_id = @project AND profile_kind = 'balanced' AND policy_version = '1.0'", first.ProjectId));

            await using (var deactivate = verification.CreateCommand())
            {
                deactivate.CommandText = "UPDATE identity.principal SET active = false WHERE principal_id = @principal";
                deactivate.Parameters.AddWithValue("principal", outsider.PrincipalId);
                await deactivate.ExecuteNonQueryAsync();
            }
            var inactive = await Assert.ThrowsAsync<ProjectIdentityException>(() =>
                store.ResolvePrincipalAsync(issuer, "outsider", PrincipalKind.Human, CancellationToken.None));
            Assert.Equal(403, inactive.StatusCode);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)";
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task MigrateAsync(string connectionString)
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BuildingBlocks.Persistence.AtlasDbContext>()
            .UseNpgsql(connectionString).Options;
        await using var context = new BuildingBlocks.Persistence.AtlasDbContext(options);
        await context.Database.MigrateAsync();
    }

    private static async Task<int> ScalarAsync(NpgsqlConnection connection, string sql, Guid projectId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("project", projectId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
