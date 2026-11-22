using System.Text.Json;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using ContractDelivery.Contracts;
using ContractDelivery.Infrastructure;
using ImportEvidence.Blob;
using KnowledgeCore.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using ProjectIdentity.Infrastructure;
using PlatformOps;
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
            var proposal = ValidProposal("T24-PROPOSAL", project.ProjectId, task.TaskId, "24");
            await delivery.IngestProposalAsync(author, project.ProjectId, proposal, "t24-proposal-01", "t24-r8", null, CancellationToken.None);
            var proposalSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(proposal.GetRawText()))).ToLowerInvariant();
            var commit = new string('c', 40); var build = new string('d', 64);
            var manifest = JsonSerializer.SerializeToElement(new { schemaVersion = "1.3.0", deliveryId = "T24-DELIVERY", proposalId = "T24-PROPOSAL", proposalSha256 = proposalSha,
                projectId = project.ProjectId, taskId = task.TaskId, repo = new { url = "https://example.invalid/business-atlas.git", pullRequestId = "24" }, finalCommitSha = commit,
                mergeStrategy = "squash", buildId = "CI-24", buildDigest = build, verifiedAt = DateTimeOffset.UtcNow,
                testEvidence = new[] { new { testId = "AT-T24", result = "passed", artifactUri = "private://ci/24/tests", artifactSha256 = new string('f', 64) } },
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

    [PostgreSqlFact]
    public async Task AT_T25_Reverse_observation_invalidates_and_real_evidence_gates_human_closure()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t25_{Guid.NewGuid():N}"; var blobRoot = Path.Combine(Path.GetTempPath(), $"atlas-t25-{Guid.NewGuid():N}");
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
            var author = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "t25-automated-author", PrincipalKind.Human, CancellationToken.None);
            var verifier = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "t25-automated-verifier", PrincipalKind.Human, CancellationToken.None);
            var acceptor = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "t25-automated-independent-acceptor", PrincipalKind.Human, CancellationToken.None);
            var ci = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "t25-ci-service", PrincipalKind.Service, CancellationToken.None);
            var project = await projects.CreateProjectAsync(author, new("Conformance closure", $"closure-{Guid.NewGuid():N}", "t25-project-0001"), "t25-r1", null, CancellationToken.None);
            await AddMembership(administrator.ConnectionString, project.ProjectId, verifier.PrincipalId, "Verifier");
            await AddMembership(administrator.ConnectionString, project.ProjectId, acceptor.PrincipalId, "Verifier");
            await AddMembership(administrator.ConnectionString, project.ProjectId, ci.PrincipalId, "CIService");
            var unit = Guid.NewGuid(); var revision = Guid.NewGuid();
            await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, unit, revision);
            var task = await collab.CreateTaskAsync(author, project.ProjectId, new("Preserve the documented outcome during implementation", null, unit, null, null), "t25-task-0001", "t25-r2", null, CancellationToken.None);
            var amended = await collab.AmendTaskAsync(author, project.ProjectId, task.TaskId,
                new(null, [new("revalidate", "Recheck the exact implementation observation", "Implementation-only compatibility check", new(unit, revision))], [new(unit, revision)], null),
                task.Version, "t25-amend-001", "t25-r3", null, CancellationToken.None);
            await SetImplementationOnly(administrator.ConnectionString, project.ProjectId, task.TaskId);
            var readRequest = new ReadContextRequest(ReadMode.Published, ReadPurpose.Implement, project.CurrentBaselineId, null, null);
            var built = await delivery.BuildContextAsync(author, project.ProjectId, task.TaskId, new(readRequest, "executable"), amended.Version, "t25-context-01", "t25-r4", null, CancellationToken.None);
            var blocked = await delivery.CheckClosureAsync(verifier.PrincipalId, project.ProjectId, task.TaskId, built.ContextId, CancellationToken.None);
            Assert.Equal("blocked", blocked.State);
            Assert.Contains(blocked.Checks, item => item.Key == "testCheck" && item.Status != "pass");
            Assert.Contains(blocked.Checks, item => item.Key == "conformanceCheck" && item.Status != "pass");

            var evidence = new[] { new ContextEvidence(new(unit, revision), JsonSerializer.SerializeToElement(new { kind = "test", uri = "private://ci/25/conformance" }), "supports", "rawCaptured") };
            var checkRequest = new ConformanceCheckRequest(new(unit, revision), new(unit, revision), readRequest, new string('d', 64), "production");
            var checkedConformance = await delivery.CheckConformanceAsync(verifier.PrincipalId, project.ProjectId, checkRequest, CancellationToken.None);
            var conformanceInput = checkedConformance with { Evidence = evidence, EvidenceHash = TestEvidenceHash(evidence) };
            await delivery.RecordConformanceAsync(verifier, project.ProjectId, conformanceInput, "t25-conformance-pass", "t25-r5", null, CancellationToken.None);
            var expected = JsonSerializer.SerializeToElement(new { outcome = "preserved", permission = "unchanged" });
            var contradictory = await Assert.ThrowsAsync<ProjectIdentityException>(() => delivery.RecordAcceptanceAsync(verifier, project.ProjectId, task.TaskId,
                new(built.ContextId, revision.ToString(), expected, JsonSerializer.SerializeToElement(new { outcome = "changed" }), "pass", checkRequest.BuildDigest, "production", evidence),
                amended.Version + 1, "t25-acceptance-bad", "t25-r6", null, CancellationToken.None));
            Assert.Equal(422, contradictory.StatusCode);
            await delivery.RecordAcceptanceAsync(verifier, project.ProjectId, task.TaskId,
                new(built.ContextId, revision.ToString(), expected, expected, "pass", checkRequest.BuildDigest, "production", evidence),
                amended.Version + 1, "t25-acceptance-pass", "t25-r7", null, CancellationToken.None);

            var proposal = ValidProposal("T25-PROPOSAL", project.ProjectId, task.TaskId, "25");
            await delivery.IngestProposalAsync(author, project.ProjectId, proposal, "t25-proposal-01", "t25-r8", null, CancellationToken.None);
            var proposalSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(proposal.GetRawText()))).ToLowerInvariant();
            var commit = new string('c', 40);
            var manifest = JsonSerializer.SerializeToElement(new { schemaVersion = "1.3.0", deliveryId = "T25-DELIVERY", proposalId = "T25-PROPOSAL", proposalSha256 = proposalSha,
                projectId = project.ProjectId, taskId = task.TaskId, repo = new { url = "https://example.invalid/business-atlas.git", pullRequestId = "25" }, finalCommitSha = commit,
                mergeStrategy = "squash", buildId = "CI-25", buildDigest = checkRequest.BuildDigest, verifiedAt = DateTimeOffset.UtcNow,
                testEvidence = new[] { new { testId = "AT-T25", result = "passed", artifactUri = "private://ci/25/tests", artifactSha256 = new string('f', 64) } },
                anchorResolutions = new[] { new { path = "src/Modules/ContractDelivery", locator = "contract", resolvedBlobSha = new string('e', 40), verification = "matched" } }, status = "verified" });
            var delivered = await delivery.IngestManifestAsync(ci, project.ProjectId, manifest, "t25-manifest-01", "t25-r9", null, CancellationToken.None);
            await PublishKnowledge(administrator.ConnectionString, project.ProjectId, project.CurrentBaselineId, delivered.DeliveryId, author.PrincipalId);
            await delivery.RecordDeploymentAsync(ci, project.ProjectId, new(null, "production", "https://example.invalid/business-atlas.git", commit, checkRequest.BuildDigest,
                "20261122000000", new string('f', 64), DateTimeOffset.UtcNow, null, evidence), "t25-deploy-0001", "t25-r10", null, CancellationToken.None);
            var ready = await delivery.CheckClosureAsync(verifier.PrincipalId, project.ProjectId, task.TaskId, built.ContextId, CancellationToken.None);
            Assert.Equal("ready_for_acceptance", ready.State);
            Assert.All(ready.Checks, item => Assert.True(item.Status is "pass" or "not_applicable", $"{item.Key}: {item.Reason}"));
            var current = await collab.GetTaskAsync(author.PrincipalId, project.ProjectId, task.TaskId, CancellationToken.None);
            var closed = await delivery.RequestClosureAsync(acceptor, project.ProjectId, task.TaskId, new(built.ContextId, "Automated test actor verified the complete synthetic evidence chain."),
                current.Version, "t25-close-0001", "t25-r11", null, CancellationToken.None);
            Assert.Equal("complete", closed.State);

            var observed = Guid.NewGuid(); await AddObservedRevision(administrator.ConnectionString, project.ProjectId, unit, revision, observed, verifier.PrincipalId);
            var historicalRead = new ReadContextRequest(ReadMode.Historical, ReadPurpose.Implement, project.CurrentBaselineId, null, null);
            var reverse = await delivery.CheckConformanceAsync(verifier.PrincipalId, project.ProjectId, checkRequest with { ObservedRevision = new(unit, observed), ReadContext = historicalRead }, CancellationToken.None);
            Assert.Equal("divergent", reverse.Verdict);
            await delivery.RecordConformanceAsync(verifier, project.ProjectId, reverse with { Evidence = evidence, EvidenceHash = TestEvidenceHash(evidence) }, "t25-conformance-reverse", "t25-r12", null, CancellationToken.None);
            Assert.Equal("stale", (await delivery.GetContextAsync(author.PrincipalId, project.ProjectId, built.ContextId, CancellationToken.None)).Status);
            var invalidated = await delivery.CheckClosureAsync(verifier.PrincipalId, project.ProjectId, task.TaskId, built.ContextId, CancellationToken.None);
            Assert.Equal("blocked", invalidated.State);
            Assert.Contains(invalidated.Checks, item => item.Key == "authorization" && item.Status == "fail");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"; await command.ExecuteNonQueryAsync();
            if (Directory.Exists(blobRoot)) Directory.Delete(blobRoot, true);
        }
    }

    [PostgreSqlFact]
    public async Task AT_T32_Deployment_environments_rollback_and_reviewed_error_preflight_are_reconciled()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t32_{Guid.NewGuid():N}"; var blobRoot = Path.Combine(Path.GetTempPath(), $"atlas-t32-{Guid.NewGuid():N}");
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
            var author = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "t32-automated-author", PrincipalKind.Human, CancellationToken.None);
            var ci = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "t32-ci-service", PrincipalKind.Service, CancellationToken.None);
            var verifier = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "t32-automated-verifier", PrincipalKind.Human, CancellationToken.None);
            var project = await projects.CreateProjectAsync(author, new("Deployment reconciliation", $"reconcile-{Guid.NewGuid():N}", "t32-project-0001"), "t32-r1", null, CancellationToken.None);
            await AddMembership(administrator.ConnectionString, project.ProjectId, ci.PrincipalId, "CIService");
            await AddMembership(administrator.ConnectionString, project.ProjectId, verifier.PrincipalId, "Verifier");
            var unit = Guid.NewGuid(); var revision = Guid.NewGuid();
            await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, unit, revision);
            var task = await collab.CreateTaskAsync(author, project.ProjectId, new("Exercise deployment reconciliation and reviewed preflight", null, unit, null, null), "t32-task-0001", "t32-r2", null, CancellationToken.None);
            var amended = await collab.AmendTaskAsync(author, project.ProjectId, task.TaskId,
                new(null, [new("revalidate", "Reconcile actual deployment evidence", "Implementation-only operations check", new(unit, revision))], [new(unit, revision)], null),
                task.Version, "t32-amend-001", "t32-r3", null, CancellationToken.None);
            await SetImplementationOnly(administrator.ConnectionString, project.ProjectId, task.TaskId);
            var readRequest = new ReadContextRequest(ReadMode.Published, ReadPurpose.Implement, project.CurrentBaselineId, null, null);

            await using (var context = new BuildingBlocks.Persistence.AtlasDbContext(options))
            {
                var patterns = new PostgresErrorPatternStore(context);
                var candidate = await patterns.ProposeAsync(project.ProjectId, author.PrincipalId, new ErrorPattern(Guid.Empty, "t32.rollback", 1,
                    JsonSerializer.SerializeToElement(new { kind = "implementation_only" }), "stale deployment accepted", "environment was not scoped", "latest deployment mismatch",
                    "reconcile the latest deployment per environment", "AT-T32", JsonSerializer.SerializeToElement(Array.Empty<object>()), "candidate"),
                    "t32-pattern-1", "t32-pattern-request-1", null, CancellationToken.None);
                var candidateContext = await delivery.BuildContextAsync(author, project.ProjectId, task.TaskId, new(readRequest, "executable"), amended.Version,
                    "t32-context-candidate", "t32-r4", null, CancellationToken.None);
                Assert.DoesNotContain(candidateContext.KnowledgeRequirements, item => item.Contains("t32.rollback", StringComparison.Ordinal));
                var reviewed = await patterns.ReviewAsync(project.ProjectId, verifier.PrincipalId, true, candidate.PatternId, 1, "reviewed",
                    "Automated test actor reviewed the synthetic preflight candidate.", "t32-pattern-review", "t32-pattern-request-2", null, CancellationToken.None);
                Assert.Equal("reviewed", reviewed.State);
            }
            var currentTask = await collab.GetTaskAsync(author.PrincipalId, project.ProjectId, task.TaskId, CancellationToken.None);
            var contextWithPreflight = await delivery.BuildContextAsync(author, project.ProjectId, task.TaskId, new(readRequest, "executable"), currentTask.Version,
                "t32-context-reviewed", "t32-r5", null, CancellationToken.None);
            Assert.Contains(contextWithPreflight.KnowledgeRequirements, item => item.Contains("t32.rollback@2", StringComparison.Ordinal) && item.Contains("AT-T32", StringComparison.Ordinal));

            var previousProposal = ValidProposal("T32-PREVIOUS", project.ProjectId, task.TaskId, "321");
            await delivery.IngestProposalAsync(author, project.ProjectId, previousProposal, "t32-proposal-old", "t32-r6", null, CancellationToken.None);
            var currentProposal = ValidProposal("T32-CURRENT", project.ProjectId, task.TaskId, "322");
            await delivery.IngestProposalAsync(author, project.ProjectId, currentProposal, "t32-proposal-new", "t32-r7", null, CancellationToken.None);
            var previousCommit = new string('b', 40); var previousBuild = new string('e', 64);
            var currentCommit = new string('c', 40); var currentBuild = new string('d', 64);
            var previous = await delivery.IngestManifestAsync(ci, project.ProjectId, Manifest("T32-PREVIOUS-DELIVERY", "T32-PREVIOUS", previousProposal, project.ProjectId, task.TaskId, "321", previousCommit, previousBuild),
                "t32-manifest-old", "t32-r8", null, CancellationToken.None);
            var current = await delivery.IngestManifestAsync(ci, project.ProjectId, Manifest("T32-CURRENT-DELIVERY", "T32-CURRENT", currentProposal, project.ProjectId, task.TaskId, "322", currentCommit, currentBuild),
                "t32-manifest-new", "t32-r9", null, CancellationToken.None);
            var evidence = new[] { new ContextEvidence(new(unit, revision), JsonSerializer.SerializeToElement(new { kind = "deployment", uri = "private://ci/32/deploy" }), "supports", "rawCaptured") };
            var now = DateTimeOffset.UtcNow;
            await delivery.RecordDeploymentAsync(ci, project.ProjectId, new(null, "production", "https://example.invalid/business-atlas.git", previousCommit, previousBuild,
                "20261122000000", new string('f', 64), now.AddMinutes(-10), null, evidence), "t32-deploy-old", "t32-r10", null, CancellationToken.None);
            await delivery.RecordDeploymentAsync(ci, project.ProjectId, new(null, "production", "https://example.invalid/business-atlas.git", currentCommit, currentBuild,
                "20261122000001", new string('f', 64), now.AddMinutes(-5), null, evidence), "t32-deploy-new", "t32-r11", null, CancellationToken.None);
            await delivery.RecordDeploymentAsync(ci, project.ProjectId, new(null, "staging", "https://example.invalid/business-atlas.git", currentCommit, currentBuild,
                "20261122000001", new string('f', 64), now.AddMinutes(-5), null, evidence), "t32-deploy-stage", "t32-r12", null, CancellationToken.None);
            Assert.Equal("aligned", (await delivery.ReconcileAsync(author, project.ProjectId, new(task.TaskId, "production"), "t32-reconcile-prod", "t32-r13", null, CancellationToken.None)).Status);

            await delivery.RecordDeploymentAsync(ci, project.ProjectId, new(null, "production", "https://example.invalid/business-atlas.git", previousCommit, previousBuild,
                "20261122000000", new string('f', 64), now, null, evidence), "t32-deploy-rollback", "t32-r14", null, CancellationToken.None);
            var rolledBack = await delivery.ReconcileAsync(author, project.ProjectId, new(task.TaskId, "production"), "t32-reconcile-back", "t32-r15", null, CancellationToken.None);
            Assert.Equal(current.DeliveryId, rolledBack.DeliveryId); Assert.Equal("rolled_back", rolledBack.Status);
            Assert.Equal("stale", (await delivery.GetContextAsync(author.PrincipalId, project.ProjectId, contextWithPreflight.ContextId, CancellationToken.None)).Status);
            var all = await delivery.ListDeliveriesAsync(author.PrincipalId, project.ProjectId, null, 30, CancellationToken.None);
            Assert.Equal("divergent", all.Items.Single(item => item.DeliveryId == current.DeliveryId).Status);
            Assert.Equal("divergent", all.Items.Single(item => item.DeliveryId == previous.DeliveryId).Status);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"; await command.ExecuteNonQueryAsync();
            if (Directory.Exists(blobRoot)) Directory.Delete(blobRoot, true);
        }
    }

    private static JsonElement Manifest(string deliveryId, string proposalId, JsonElement proposal, Guid project, Guid task, string pullRequest, string commit, string build) =>
        JsonSerializer.SerializeToElement(new { schemaVersion = "1.3.0", deliveryId, proposalId,
            proposalSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(proposal.GetRawText()))).ToLowerInvariant(),
            projectId = project, taskId = task, repo = new { url = "https://example.invalid/business-atlas.git", pullRequestId = pullRequest }, finalCommitSha = commit,
            mergeStrategy = "squash", buildId = $"CI-{pullRequest}", buildDigest = build, verifiedAt = DateTimeOffset.UtcNow,
            testEvidence = new[] { new { testId = "AT-T32", result = "passed", artifactUri = $"private://ci/{pullRequest}/tests", artifactSha256 = new string('f', 64) } },
            anchorResolutions = new[] { new { path = "src/Modules/ContractDelivery", locator = "contract", resolvedBlobSha = new string('e', 40), verification = "matched" } }, status = "verified" });

    private static JsonElement ValidProposal(string proposalId, Guid project, Guid task, string pullRequest) =>
        JsonSerializer.SerializeToElement(new
        {
            schemaVersion = "1.3.0", proposalId, projectId = project, taskId = task, requirementIds = new[] { "PD-32" }, changeKind = "feature",
            repo = new { url = "https://example.invalid/business-atlas.git", branch = $"task/{proposalId[..3]}", pullRequestId = pullRequest, baseSha = new string('a', 40) },
            changedFiles = new[] { new { path = "src/Modules/ContractDelivery", changeType = "modified", contentSha256 = new string('b', 64) } },
            entityChanges = Array.Empty<object>(), assertionChanges = Array.Empty<object>(), apiChanges = Array.Empty<object>(), dataChanges = Array.Empty<object>(),
            testEvidence = new[] { new { testId = "AT-T31", result = "passed", artifactUri = "private://ci/proposal/tests" } },
            sourceAnchors = new[] { new { path = "src/Modules/ContractDelivery", locator = "contract", contentSha256 = new string('e', 64) } },
            knowledgeStatus = "proposed", unknowns = Array.Empty<string>(), errorsObserved = Array.Empty<object>()
        });

    private static string TestEvidenceHash(IEnumerable<ContextEvidence> evidence) =>
        CanonicalJson.Sha256(JsonSerializer.SerializeToElement(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static async Task PublishKnowledge(string connection, Guid project, Guid baseBaseline, Guid delivery, Guid actor)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = """
            SET session_replication_role=replica;
            INSERT INTO gov.change_set(project_id,change_set_id,base_baseline_id,title,state,review_requirements,summary,created_by,version)
              VALUES(@project,@change,@base,'T25 knowledge','published','{}','{}',@actor,1);
            INSERT INTO gov.baseline(project_id,baseline_id,baseline_no,parent_baseline_id,change_set_id,registry_version,digest,sealed,created_by)
              SELECT @project,@published,baseline_no+1,@base,@change,registry_version,@digest,true,@actor FROM gov.baseline WHERE project_id=@project AND baseline_id=@base;
            UPDATE delivery.delivery_record SET change_set_id=@change WHERE project_id=@project AND delivery_id=@delivery;
            """;
        command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("change", Guid.NewGuid()); command.Parameters.AddWithValue("published", Guid.NewGuid());
        command.Parameters.AddWithValue("base", baseBaseline); command.Parameters.AddWithValue("delivery", delivery); command.Parameters.AddWithValue("actor", actor); command.Parameters.AddWithValue("digest", new string('b', 64));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AddObservedRevision(string connection, Guid project, Guid unit, Guid parent, Guid revision, Guid actor)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = """
            SET session_replication_role=replica;
            INSERT INTO core.revision(project_id,revision_id,unit_id,rev_no,parent_revision_id,schema_type,schema_version,fact_kind,verification,payload,applicability,content_sha256,sealed,created_by)
              VALUES(@project,@revision,@unit,2,@parent,'Source','1.0','observed','verified','{"title":"Reverse observation invalidates the old conclusion"}','{}',@hash,true,@actor);
            INSERT INTO access.grant(project_id,grant_id,unit_id,principal_id,action,field_paths,effect,created_by)
              VALUES(@project,gen_random_uuid(),@unit,@actor,'read_history',ARRAY['*'],'allow',@actor);
            """;
        command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("unit", unit); command.Parameters.AddWithValue("parent", parent);
        command.Parameters.AddWithValue("revision", revision); command.Parameters.AddWithValue("actor", actor); command.Parameters.AddWithValue("hash", new string('c', 64)); await command.ExecuteNonQueryAsync();
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
