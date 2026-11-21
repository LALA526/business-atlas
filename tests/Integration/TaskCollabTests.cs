using System.Text.Json;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using ProjectIdentity.Infrastructure;
using TaskCollab.Contracts;
using TaskCollab.Infrastructure;

namespace Atlas.Integration.Tests;

public sealed class TaskCollabTests
{
    [PostgreSqlFact]
    public async Task AT_T21_Intent_task_versions_reader_permissions_and_feedback_closure()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t21_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(administrator.ConnectionString))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE {database}"; await command.ExecuteNonQueryAsync();
        }
        administrator.Database = database;
        try
        {
            var options = new DbContextOptionsBuilder<BuildingBlocks.Persistence.AtlasDbContext>().UseNpgsql(administrator.ConnectionString).Options;
            await using (var context = new BuildingBlocks.Persistence.AtlasDbContext(options)) await context.Database.MigrateAsync();
            var runtime = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Options = "-c role=atlas_app" }.ConnectionString;
            var projects = new PostgresProjectIdentityStore(runtime);
            var collab = new PostgresTaskCollabStore(runtime, new PostgresAccessApplicabilityStore(runtime));
            var author = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "author", PrincipalKind.Human, CancellationToken.None);
            var reader = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "reader", PrincipalKind.Human, CancellationToken.None);
            var project = await projects.CreateProjectAsync(author, new("Collaboration", $"collab-{Guid.NewGuid():N}", "t21-project-0001"), "r1", null, CancellationToken.None);
            await AddReader(administrator.ConnectionString, project.ProjectId, reader.PrincipalId);

            await Assert.ThrowsAsync<ProjectIdentityException>(() => collab.CreateTaskAsync(author, project.ProjectId, new("  ", null, null, null, null), "t21-empty-0001", "r2", null, CancellationToken.None));
            var task = await collab.CreateTaskAsync(author, project.ProjectId, new("Improve the current approval journey", null, null, null, null), "t21-task-0001", "r3", null, CancellationToken.None);
            Assert.Equal("captured", task.State); Assert.Equal("unknown", task.Classification); Assert.Single(task.Unknowns); Assert.Empty(task.TargetChanges);
            var replay = await collab.CreateTaskAsync(author, project.ProjectId, new("Improve the current approval journey", null, null, null, null), "t21-task-0001", "r3", null, CancellationToken.None);
            Assert.Equal(task.TaskId, replay.TaskId);
            var forbidden = await Assert.ThrowsAsync<ProjectIdentityException>(() => collab.CreateTaskAsync(reader, project.ProjectId, new("Reader cannot create", null, null, null, null), "t21-reader-001", "r4", null, CancellationToken.None));
            Assert.Equal(403, forbidden.StatusCode);

            var analyzed = await collab.AnalyzeIntentAsync(author, project.ProjectId, task.TaskId,
                new(new(ReadMode.Published, ReadPurpose.Change, null, null, null), []), task.Version, "t21-analyze-001", "r5", null, CancellationToken.None);
            Assert.Equal("succeeded", analyzed.State); Assert.Single(analyzed.Unknowns);
            var afterAnalysis = await collab.GetTaskAsync(author.PrincipalId, project.ProjectId, task.TaskId, CancellationToken.None);
            Assert.NotEqual("ready", afterAnalysis.State);
            var amended = await collab.AmendTaskAsync(author, project.ProjectId, task.TaskId,
                new("Clarify approval outcome", [new("add", "Preserve explicit human approval while improving its presentation", "The request needs a distinct target outcome")], [], null),
                afterAnalysis.Version, "t21-amend-0001", "r6", null, CancellationToken.None);
            Assert.Equal(1, amended.Version - afterAnalysis.Version); Assert.Single(amended.TargetChanges); Assert.Empty(amended.Unknowns);
            var stale = await Assert.ThrowsAsync<ProjectIdentityException>(() => collab.AmendTaskAsync(author, project.ProjectId, task.TaskId, new("Stale", null, null, null), task.Version, "t21-stale-0001", "r7", null, CancellationToken.None));
            Assert.Equal(412, stale.StatusCode);

            var feedback = await collab.CreateActivityAsync(author, project.ProjectId, "feedback", new(null, null, task.TaskId, null, Json("{}"), "The approval outcome needs clarification"), "t21-feedback-01", "r8", null, CancellationToken.None);
            Assert.Equal("open", feedback.State); Assert.Equal(task.TaskId, feedback.Target.TaskId);
            var resolved = await collab.ResolveActivityAsync(author, project.ProjectId, "feedback", feedback.Id, new("Route to the tracked task", null, task.TaskId), feedback.Version, "t21-resolve-001", "r9", null, CancellationToken.None);
            Assert.Equal("addressed", resolved.State); Assert.Equal(task.TaskId, resolved.Target.TaskId);
            var closedFeedback = await collab.ResolveActivityAsync(author, project.ProjectId, "feedback", feedback.Id, new("Verified in the linked task and closed", null, null), resolved.Version, "t21-feedback-close", "r8b", null, CancellationToken.None);
            Assert.Equal("closed", closedFeedback.State);
            Assert.Single((await collab.ListActivitiesAsync(author.PrincipalId, project.ProjectId, "feedback", task.TaskId, null, null, null, 30, CancellationToken.None)).Items);
            var comment = await collab.CreateActivityAsync(author, project.ProjectId, "comment", new(null, null, task.TaskId, null, Json("{}"), "Human review comment on the candidate"), "t21-comment-001", "r10", null, CancellationToken.None);
            Assert.Equal("open", comment.State);
            var issue = await collab.CreateActivityAsync(author, project.ProjectId, "issue", new(null, null, task.TaskId, null, Json("{}"), "Unresolved approval presentation issue"), "t21-issue-0001", "r11", null, CancellationToken.None);
            Assert.Equal("closed", (await collab.ResolveActivityAsync(author, project.ProjectId, "issue", issue.Id, new("Documented disposition without changing knowledge", null, null), issue.Version, "t21-issue-close", "r12", null, CancellationToken.None)).State);
            Assert.NotEmpty((await collab.ListActivityAsync(author.PrincipalId, project.ProjectId, null, 100, CancellationToken.None)).Items);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"; await command.ExecuteNonQueryAsync();
        }
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static async Task AddReader(string connection, Guid project, Guid principal)
    { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = "INSERT INTO identity.membership(project_id,principal_id,roles) VALUES(@project,@principal,ARRAY['ProjectReader'])"; command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("principal", principal); await command.ExecuteNonQueryAsync(); }
}
