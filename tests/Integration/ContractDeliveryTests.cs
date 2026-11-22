using System.Text.Json;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using ContractDelivery.Contracts;
using ContractDelivery.Infrastructure;
using ImportEvidence.Blob;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using ProjectIdentity.Infrastructure;
using TaskCollab.Contracts;
using TaskCollab.Infrastructure;

namespace Atlas.Integration.Tests;

public sealed class ContractDeliveryTests
{
    [PostgreSqlFact]
    public async Task AT_T24_Context_is_immutable_reauthorized_and_stale_on_policy_or_revision_change()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t24_{Guid.NewGuid():N}"; var blobRoot = Path.Combine(Path.GetTempPath(), $"atlas-t24-{Guid.NewGuid():N}");
        await using (var connection = new NpgsqlConnection(administrator.ConnectionString))
        { await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE {database}"; await command.ExecuteNonQueryAsync(); }
        administrator.Database = database;
        try
        {
            var options = new DbContextOptionsBuilder<BuildingBlocks.Persistence.AtlasDbContext>().UseNpgsql(administrator.ConnectionString).Options;
            await using (var context = new BuildingBlocks.Persistence.AtlasDbContext(options)) await context.Database.MigrateAsync();
            var runtime = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Options = "-c role=atlas_app" }.ConnectionString;
            var projects = new PostgresProjectIdentityStore(runtime); var access = new PostgresAccessApplicabilityStore(runtime);
            var collab = new PostgresTaskCollabStore(runtime, access); var delivery = new PostgresContractDeliveryStore(runtime, access, new PrivateBlobStorage(blobRoot));
            var author = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "t24-automated-test-actor", PrincipalKind.Human, CancellationToken.None);
            var reader = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "t24-reader", PrincipalKind.Human, CancellationToken.None);
            var project = await projects.CreateProjectAsync(author, new("Context invalidation", $"context-{Guid.NewGuid():N}", "t24-project-0001"), "t24-r1", null, CancellationToken.None);
            await AddMembership(administrator.ConnectionString, project.ProjectId, reader.PrincipalId, "ProjectReader");
            var unit = Guid.NewGuid(); var revision = Guid.NewGuid();
            await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, unit, revision);
            var task = await collab.CreateTaskAsync(author, project.ProjectId, new("Refactor the documented implementation while preserving its outcome", null, unit, null, null), "t24-task-0001", "t24-r2", null, CancellationToken.None);
            var amended = await collab.AmendTaskAsync(author, project.ProjectId, task.TaskId,
                new(null, [new("revalidate", "Revalidate the exact normative source after refactoring", "Implementation-only compatibility check", new(unit, revision))], [new(unit, revision)], null),
                task.Version, "t24-amend-001", "t24-r3", null, CancellationToken.None);
            await SetImplementationOnly(administrator.ConnectionString, project.ProjectId, task.TaskId);
            var readRequest = new ReadContextRequest(ReadMode.Published, ReadPurpose.Implement, project.CurrentBaselineId, null, null);
            var built = await delivery.BuildContextAsync(author, project.ProjectId, task.TaskId, new(readRequest, "executable"), amended.Version, "t24-context-01", "t24-r4", null, CancellationToken.None);
            Assert.Equal("valid", built.Status); Assert.Equal(64, built.Digest.Length); Assert.Single(built.RequiredRefs);

            var before = await ReadCount(administrator.ConnectionString, project.ProjectId, "delivery.task_context") + await ReadCount(administrator.ConnectionString, project.ProjectId, "ops.audit_event");
            var read = await delivery.GetContextAsync(reader.PrincipalId, project.ProjectId, built.ContextId, CancellationToken.None);
            var after = await ReadCount(administrator.ConnectionString, project.ProjectId, "delivery.task_context") + await ReadCount(administrator.ConnectionString, project.ProjectId, "ops.audit_event");
            Assert.Equal(built.Digest, read.Digest); Assert.Equal(before, after);

            await access.SetGrantAsync(author, project.ProjectId, new(null, unit, reader.PrincipalId, "read", ["*"], "allow", null), project.Version, "t24-grant-001", "t24-r5", null, CancellationToken.None);
            var policyStale = await delivery.GetContextAsync(author.PrincipalId, project.ProjectId, built.ContextId, CancellationToken.None);
            Assert.Equal("stale", policyStale.Status);
            var implementationBlocked = await Assert.ThrowsAsync<ProjectIdentityException>(() => collab.TransitionTaskAsync(author, project.ProjectId, task.TaskId,
                new("begin_implementation", "A stale context must not execute", built.ContextId), amended.Version + 1, "t24-transition-1", "t24-r6", null, CancellationToken.None));
            Assert.Equal(409, implementationBlocked.StatusCode);

            var currentTask = await collab.GetTaskAsync(author.PrincipalId, project.ProjectId, task.TaskId, CancellationToken.None);
            var rebuilt = await delivery.BuildContextAsync(author, project.ProjectId, task.TaskId, new(readRequest, "executable"), currentTask.Version, "t24-context-02", "t24-r7", null, CancellationToken.None);
            var newRevision = Guid.NewGuid(); await ReplaceRevision(administrator.ConnectionString, project.ProjectId, project.CurrentBaselineId, unit, revision, newRevision, author.PrincipalId);
            var revisionStale = await delivery.GetContextAsync(author.PrincipalId, project.ProjectId, rebuilt.ContextId, CancellationToken.None);
            Assert.Equal("stale", revisionStale.Status);
            var immutable = await ReadCount(administrator.ConnectionString, project.ProjectId, "delivery.context_invalidation");
            Assert.Equal(0, immutable);

            var ci = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "t24-ci-service", PrincipalKind.Service, CancellationToken.None);
            await AddMembership(administrator.ConnectionString, project.ProjectId, ci.PrincipalId, "CIService");
            var proposal = JsonSerializer.SerializeToElement(new { schemaVersion = "1.3.0", proposalId = "T24-PROPOSAL", projectId = project.ProjectId, taskId = task.TaskId,
                repo = new { url = "https://example.invalid/business-atlas.git", branch = "task/T24", pullRequestId = "24" } });
            await delivery.IngestProposalAsync(author, project.ProjectId, proposal, "t24-proposal-01", "t24-r8", null, CancellationToken.None);
            var proposalSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(proposal.GetRawText()))).ToLowerInvariant();
            var commit = new string('c', 40); var build = new string('d', 64);
            var manifest = JsonSerializer.SerializeToElement(new { schemaVersion = "1.3.0", deliveryId = "T24-DELIVERY", proposalId = "T24-PROPOSAL", proposalSha256 = proposalSha,
                projectId = project.ProjectId, taskId = task.TaskId, repo = new { url = "https://example.invalid/business-atlas.git", pullRequestId = "24" }, finalCommitSha = commit,
                mergeStrategy = "squash", buildId = "CI-24", buildDigest = build, verifiedAt = DateTimeOffset.UtcNow,
                testEvidence = new[] { new { testId = "AT-T24", result = "passed", artifactUri = "private://ci/24/tests" } },
                anchorResolutions = new[] { new { path = "src/Modules/ContractDelivery", locator = "contract", resolvedBlobSha = new string('e', 40), verification = "matched" } }, status = "verified" });
            var delivered = await delivery.IngestManifestAsync(ci, project.ProjectId, manifest, "t24-manifest-01", "t24-r9", null, CancellationToken.None);
            Assert.Equal("evidence_missing", delivered.Status);
            var evidence = new[] { new ContextEvidence(new(unit, revision), JsonSerializer.SerializeToElement(new { kind = "test", uri = "private://ci/24/deploy" }), "supports", "rawCaptured") };
            var deployed = await delivery.RecordDeploymentAsync(ci, project.ProjectId, new(null, "production", "https://example.invalid/business-atlas.git", commit, build, "20261122000000", new string('f', 64), DateTimeOffset.UtcNow, null, evidence),
                "t24-deploy-0001", "t24-r10", null, CancellationToken.None);
            Assert.Equal("aligned", deployed.Status);
            Assert.Equal("aligned", (await delivery.ListDeliveriesAsync(author.PrincipalId, project.ProjectId, null, 30, CancellationToken.None)).Items.Single().Status);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"; await command.ExecuteNonQueryAsync();
            if (Directory.Exists(blobRoot)) Directory.Delete(blobRoot, true);
        }
    }

    private static async Task AddUnit(string connection, Guid project, Guid actor, Guid baseline, Guid unit, Guid revision)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = """
            SET session_replication_role=replica;
            INSERT INTO core.unit(project_id,unit_id,kind,owner_id,created_by) VALUES(@project,@unit,'Source',@actor,@actor);
            INSERT INTO core.revision(project_id,revision_id,unit_id,rev_no,schema_type,schema_version,fact_kind,verification,payload,applicability,content_sha256,sealed,created_by)
              VALUES(@project,@revision,@unit,1,'Source','1.0','normative','verified','{"title":"T24 synthetic context fixture"}','{}',@hash,true,@actor);
            INSERT INTO gov.baseline_entry(project_id,baseline_id,unit_id,revision_id) VALUES(@project,@baseline,@unit,@revision);
            """;
        command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("unit", unit); command.Parameters.AddWithValue("revision", revision); command.Parameters.AddWithValue("baseline", baseline); command.Parameters.AddWithValue("actor", actor); command.Parameters.AddWithValue("hash", new string('a', 64)); await command.ExecuteNonQueryAsync();
    }
    private static async Task ReplaceRevision(string connection, Guid project, Guid baseline, Guid unit, Guid oldRevision, Guid revision, Guid actor)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = """
            SET session_replication_role=replica;
            INSERT INTO core.revision(project_id,revision_id,unit_id,rev_no,parent_revision_id,schema_type,schema_version,fact_kind,verification,payload,applicability,content_sha256,sealed,created_by)
              VALUES(@project,@revision,@unit,2,@old,'Source','1.0','normative','verified','{"title":"Changed T24 fixture"}','{}',@hash,true,@actor);
            INSERT INTO gov.baseline(project_id,baseline_id,baseline_no,parent_baseline_id,registry_version,digest,sealed,created_by)
              SELECT @project,gen_random_uuid(),baseline_no+1,@baseline,registry_version,@hash,true,@actor FROM gov.baseline WHERE project_id=@project AND baseline_id=@baseline;
            INSERT INTO gov.baseline_entry(project_id,baseline_id,unit_id,revision_id)
              SELECT @project,b.baseline_id,@unit,@revision FROM gov.baseline b WHERE b.project_id=@project AND b.parent_baseline_id=@baseline;
            UPDATE identity.project SET current_baseline_id=(SELECT baseline_id FROM gov.baseline WHERE project_id=@project AND parent_baseline_id=@baseline) WHERE project_id=@project;
            INSERT INTO access.grant(project_id,grant_id,unit_id,principal_id,action,field_paths,effect,created_by)
              VALUES(@project,gen_random_uuid(),@unit,@actor,'read_history',ARRAY['*'],'allow',@actor);
            """;
        command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("unit", unit); command.Parameters.AddWithValue("revision", revision); command.Parameters.AddWithValue("old", oldRevision); command.Parameters.AddWithValue("baseline", baseline); command.Parameters.AddWithValue("actor", actor); command.Parameters.AddWithValue("hash", new string('b', 64)); await command.ExecuteNonQueryAsync();
    }
    private static async Task SetImplementationOnly(string connection, Guid project, Guid task)
    { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = "UPDATE collab.task SET classification='implementation_only' WHERE project_id=@project AND task_id=@task"; command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("task", task); await command.ExecuteNonQueryAsync(); }
    private static async Task<long> ReadCount(string connection, Guid project, string table)
    { if (table is not ("delivery.task_context" or "ops.audit_event" or "delivery.context_invalidation")) throw new ArgumentException(); await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = $"SELECT count(*) FROM {table} WHERE project_id=@project"; command.Parameters.AddWithValue("project", project); return (long)(await command.ExecuteScalarAsync())!; }
    private static async Task AddMembership(string connection, Guid project, Guid principal, string role)
    { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = "INSERT INTO identity.membership(project_id,principal_id,roles) VALUES(@project,@principal,ARRAY[@role])"; command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("principal", principal); command.Parameters.AddWithValue("role", role); await command.ExecuteNonQueryAsync(); }
}
