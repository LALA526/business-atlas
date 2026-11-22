using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AccessApplicability.Infrastructure;
using AgentMcp.Contracts;
using AgentMcp.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using ProjectIdentity.Infrastructure;
using TaskCollab.Infrastructure;

namespace Atlas.Integration.Tests;

public sealed class AgentMcpTests
{
    [PostgreSqlFact]
    public async Task AT_T29_Real_repository_evidence_failed_recovery_stale_fence_and_no_auto_approval()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t29_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(administrator.ConnectionString))
        { await connection.OpenAsync(); await Execute(connection, $"CREATE DATABASE {database}"); }
        administrator.Database = database;
        try
        {
            var options = new DbContextOptionsBuilder<BuildingBlocks.Persistence.AtlasDbContext>().UseNpgsql(administrator.ConnectionString).Options;
            await using (var context = new BuildingBlocks.Persistence.AtlasDbContext(options)) await context.Database.MigrateAsync();
            var runtime = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Options = "-c role=atlas_app" }.ConnectionString;
            var projects = new PostgresProjectIdentityStore(runtime);
            var collab = new PostgresTaskCollabStore(runtime, new PostgresAccessApplicabilityStore(runtime));
            var agents = new PostgresAgentMcpStore(runtime, new PostgresAccessApplicabilityStore(runtime), TimeSpan.FromMinutes(2));
            var human = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "t29-human-test-actor", PrincipalKind.Human, CancellationToken.None);
            var runner = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "t29-controlled-runner-test-actor", PrincipalKind.Service, CancellationToken.None);
            var model = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "t29-model-test-actor", PrincipalKind.Agent, CancellationToken.None);
            var project = await projects.CreateProjectAsync(human, new("Agent protocol", $"agent-{Guid.NewGuid():N}", "t29-project-0001"), "t29-r1", null, CancellationToken.None);
            await Membership(administrator.ConnectionString, project.ProjectId, runner.PrincipalId);
            await Membership(administrator.ConnectionString, project.ProjectId, model.PrincipalId);
            var task = await collab.CreateTaskAsync(human, project.ProjectId, new("Investigate the actual M11 API route without changing approved knowledge", null, null, null, null), "t29-task-00001", "t29-r2", null, CancellationToken.None);
            var taskContext = await ExecutableContext(administrator.ConnectionString, project.ProjectId, task.TaskId, project.CurrentBaselineId, human.PrincipalId);
            var repository = RepositoryRoot(); var commit = Git(repository, "rev-parse HEAD").Trim();
            var scope = JsonSerializer.SerializeToElement(new { repo = "business-atlas", commitSha = commit, allowedDirectories = new[] { "apps/Atlas.Api" }, tools = new[] { "read_file", "search", "git" } });
            var budget = JsonSerializer.SerializeToElement(new { maxAttempts = 3 });
            var work = await agents.CreateWorkAsync(human, project.ProjectId, new(task.TaskId, taskContext, "investigate", scope, budget, DateTimeOffset.UtcNow.AddHours(1)), "t29-create-0001", "t29-r3", null, CancellationToken.None);
            Assert.Equal("queued", work.State);

            var modelRun = await agents.StartRunAsync(model, project.ProjectId, new(task.TaskId, taskContext, budget), "t29-model-run-1", "t29-r4", null, CancellationToken.None);
            var forged = new RunEventView("model-claims-raw", 0, "tool_result", "rawCaptured", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { tool = "read_file", exitCode = 0 }), new string('a', 64));
            Assert.Equal(403, (await Assert.ThrowsAsync<ProjectIdentityException>(() => agents.AppendEventAsync(model, project.ProjectId, modelRun.RunId, forged, "t29-forged-raw1", "t29-r5", null, CancellationToken.None))).StatusCode);

            var firstClaim = await agents.ClaimAsync(runner, project.ProjectId, work.WorkId, new(["read_file", "search", "git"]), work.Version, "t29-claim-0001", "t29-r6", null, CancellationToken.None);
            Assert.Equal(1, firstClaim.Fence); Assert.Equal("leased", firstClaim.State);
            await ExpireLease(administrator.ConnectionString, project.ProjectId, work.WorkId);
            Assert.Equal(409, (await Assert.ThrowsAsync<ProjectIdentityException>(() => agents.HeartbeatAsync(runner, project.ProjectId, work.WorkId, new(firstClaim.Fence, Json("{}")), "t29-expired-hb1", "t29-r7", null, CancellationToken.None))).StatusCode);
            var current = (await agents.ListWorkAsync(runner.PrincipalId, project.ProjectId, null, 30, CancellationToken.None)).Items.Single();
            var secondClaim = await agents.ClaimAsync(runner, project.ProjectId, work.WorkId, new(["read_file", "search", "git"]), current.Version, "t29-claim-0002", "t29-r8", null, CancellationToken.None);
            Assert.True(secondClaim.Fence > firstClaim.Fence);

            var firstRun = await agents.StartRunAsync(runner, project.ProjectId, new(task.TaskId, taskContext, budget), "t29-first-run-1", "t29-r9", null, CancellationToken.None);
            var failed = await agents.FinishAsync(runner, project.ProjectId, work.WorkId, new(secondClaim.Fence, "failed", firstRun.RunId, JsonSerializer.SerializeToElement(new { reason = "controlled runner interrupted before capture" })), "t29-failed-0001", "t29-r10", null, CancellationToken.None);
            Assert.Equal("queued", failed.State);
            current = (await agents.ListWorkAsync(runner.PrincipalId, project.ProjectId, null, 30, CancellationToken.None)).Items.Single();
            var recovered = await agents.ClaimAsync(runner, project.ProjectId, work.WorkId, new(["read_file", "search", "git"]), current.Version, "t29-claim-0003", "t29-r11", null, CancellationToken.None);
            Assert.True(recovered.Fence > secondClaim.Fence);
            var finalRun = await agents.StartRunAsync(runner, project.ProjectId, new(task.TaskId, taskContext, budget), "t29-final-run-1", "t29-r12", null, CancellationToken.None);
            Assert.Equal(409, (await Assert.ThrowsAsync<ProjectIdentityException>(() => agents.FinishAsync(runner, project.ProjectId, work.WorkId, new(secondClaim.Fence, "failed", finalRun.RunId, JsonSerializer.SerializeToElement(new { stale = true })), "t29-old-fence-01", "t29-r13", null, CancellationToken.None))).StatusCode);

            // This is the external controlled test runner's actual inspection of the authorized repository.
            var inspectedPath = Path.Combine(repository, "apps/Atlas.Api/AgentMcp/AgentMcpEndpoints.cs");
            var bytes = await File.ReadAllBytesAsync(inspectedPath); var text = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.Contains("MapAgentMcpEndpoints", text, StringComparison.Ordinal); Assert.Contains("RequireAuthorization(\"AtlasAgent\")", text, StringComparison.Ordinal);
            var artifact = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var captured = new RunEventView("actual-repo-read", 0, "tool_result", "rawCaptured", DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(new { tool = "read_file", exitCode = 0, repo = "business-atlas", commitSha = commit, locator = new { path = "apps/Atlas.Api/AgentMcp/AgentMcpEndpoints.cs", symbols = new[] { "MapAgentMcpEndpoints", "AtlasAgent" } } }), artifact);
            await agents.AppendEventAsync(runner, project.ProjectId, finalRun.RunId, captured, "t29-raw-event-1", "t29-r14", null, CancellationToken.None);
            var heartbeat = await agents.HeartbeatAsync(runner, project.ProjectId, work.WorkId, new(recovered.Fence, JsonSerializer.SerializeToElement(new { step = "repository inspected", eventId = "actual-repo-read" })), "t29-heartbeat1", "t29-r15", null, CancellationToken.None);
            Assert.Equal("running", heartbeat.State);
            var deniedApproval = await Assert.ThrowsAsync<ProjectIdentityException>(() => agents.FinishAsync(runner, project.ProjectId, work.WorkId, new(recovered.Fence, "succeeded", finalRun.RunId, JsonSerializer.SerializeToElement(new { approved = true })), "t29-auto-approve1", "t29-r16", null, CancellationToken.None));
            Assert.Equal(403, deniedApproval.StatusCode);
            var succeeded = await agents.FinishAsync(runner, project.ProjectId, work.WorkId, new(recovered.Fence, "succeeded", finalRun.RunId,
                JsonSerializer.SerializeToElement(new { kind = "investigation_evidence", evidenceEventIds = new[] { "actual-repo-read" }, approval = "unchanged; human review required" })), "t29-finish-0001", "t29-r17", null, CancellationToken.None);
            Assert.Equal("succeeded", succeeded.State);
            var duplicate = await agents.AppendEventAsync(runner, project.ProjectId, finalRun.RunId, captured, "t29-raw-replay1", "t29-r18", null, CancellationToken.None);
            Assert.NotEqual(Guid.Empty, duplicate.Id);
            var events = await agents.ListEventsAsync(human.PrincipalId, project.ProjectId, finalRun.RunId, null, 30, CancellationToken.None);
            Assert.Single(events.Items); Assert.Equal("rawCaptured", events.Items[0].CaptureKind); Assert.Equal(artifact, events.Items[0].ArtifactSha256);
            Assert.Contains((await agents.ListRunsAsync(human.PrincipalId, project.ProjectId, task.TaskId, null, 30, CancellationToken.None)).Items, run => run.RunId == finalRun.RunId && run.State == "succeeded");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync(); await Execute(connection, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BusinessAtlas.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
    private static string Git(string workingDirectory, string arguments)
    {
        var process = Process.Start(new ProcessStartInfo("git", arguments) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEnd(); process.WaitForExit(); if (process.ExitCode != 0) throw new InvalidOperationException(process.StandardError.ReadToEnd()); return output;
    }
    private static async Task<Guid> ExecutableContext(string connection, Guid project, Guid task, Guid baseline, Guid actor)
    {
        var context = Guid.NewGuid(); await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await Execute(db, "INSERT INTO delivery.task_context(project_id,context_id,task_id,baseline_id,policy_epoch,phase,contract_digest,body,created_by) VALUES(@p,@c,@t,@b,1,'executable',@d,'{}',@a)",
            new NpgsqlParameter("p", project), new NpgsqlParameter("c", context), new NpgsqlParameter("t", task), new NpgsqlParameter("b", baseline), new NpgsqlParameter("d", new string('b', 64)), new NpgsqlParameter("a", actor)); return context;
    }
    private static async Task ExpireLease(string connection, Guid project, Guid work)
    { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await Execute(db, "UPDATE agent.work_request SET lease_until=now()-interval '1 second' WHERE project_id=@p AND work_id=@w", new NpgsqlParameter("p", project), new NpgsqlParameter("w", work)); }
    private static async Task Membership(string connection, Guid project, Guid principal)
    { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await Execute(db, "INSERT INTO identity.membership(project_id,principal_id,roles) VALUES(@p,@u,ARRAY['AgentProposer'])", new NpgsqlParameter("p", project), new NpgsqlParameter("u", principal)); }
    private static async Task Execute(NpgsqlConnection db, string sql, params NpgsqlParameter[] parameters)
    { await using var command = new NpgsqlCommand(sql, db); command.Parameters.AddRange(parameters); await command.ExecuteNonQueryAsync(); }
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
