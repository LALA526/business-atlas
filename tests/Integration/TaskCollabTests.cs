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

    [PostgreSqlFact]
    public async Task AT_T26_Two_people_comment_resolve_feedback_and_revoke_realtime_access()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t26_{Guid.NewGuid():N}";
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
            var first = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "first", PrincipalKind.Human, CancellationToken.None);
            var second = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "second", PrincipalKind.Human, CancellationToken.None);
            var project = await projects.CreateProjectAsync(first, new("Concurrent collaboration", $"collab-{Guid.NewGuid():N}", "t26-project-0001"), "r1", null, CancellationToken.None);
            await AddMembership(administrator.ConnectionString, project.ProjectId, second.PrincipalId, "Contributor");
            var task = await collab.CreateTaskAsync(first, project.ProjectId, new("Review the approval outcome together", null, null, null, null), "t26-task-00001", "r2", null, CancellationToken.None);

            var comments = await Task.WhenAll(
                collab.CreateActivityAsync(first, project.ProjectId, "comment", new(null, null, task.TaskId, null, Json("{\"segmentId\":\"outcome\"}"), "First reviewer comment"), "t26-comment-001", "r3", null, CancellationToken.None),
                collab.CreateActivityAsync(second, project.ProjectId, "comment", new(null, null, task.TaskId, null, Json("{\"segmentId\":\"outcome\"}"), "Second reviewer comment"), "t26-comment-002", "r4", null, CancellationToken.None));
            Assert.Equal(2, comments.Select(comment => comment.Id).Distinct().Count());
            Assert.Equal(first.PrincipalId, comments[0].AuthorId); Assert.Equal(second.PrincipalId, comments[1].AuthorId);
            Assert.Equal(2, (await collab.ListActivitiesAsync(second.PrincipalId, project.ProjectId, "comment", task.TaskId, null, null, null, 30, CancellationToken.None)).Items.Count);

            var resolved = await collab.ResolveActivityAsync(first, project.ProjectId, "comment", comments[0].Id, new("Addressed in the candidate", null, task.TaskId), comments[0].Version, "t26-resolve-001", "r5", null, CancellationToken.None);
            var conflict = await Assert.ThrowsAsync<ProjectIdentityException>(() => collab.ResolveActivityAsync(second, project.ProjectId, "comment", comments[0].Id, new("Concurrent stale resolution", null, null), comments[0].Version, "t26-resolve-002", "r6", null, CancellationToken.None));
            Assert.Equal(412, conflict.StatusCode); Assert.Equal("addressed", resolved.State);

            var feedback = await collab.CreateActivityAsync(second, project.ProjectId, "feedback", new(null, null, task.TaskId, null, Json("{}"), "Please clarify the review outcome"), "t26-feedback-01", "r7", null, CancellationToken.None);
            var routed = await collab.ResolveActivityAsync(first, project.ProjectId, "feedback", feedback.Id, new("Routed to the review task", null, task.TaskId), feedback.Version, "t26-feedback-02", "r8", null, CancellationToken.None);
            Assert.Equal("closed", (await collab.ResolveActivityAsync(second, project.ProjectId, "feedback", feedback.Id, new("The task response is verified", null, null), routed.Version, "t26-feedback-03", "r9", null, CancellationToken.None)).State);
            Assert.True(await collab.CanReadProjectAsync(second.PrincipalId, project.ProjectId, CancellationToken.None));
            await Revoke(administrator.ConnectionString, project.ProjectId, second.PrincipalId);
            Assert.False(await collab.CanReadProjectAsync(second.PrincipalId, project.ProjectId, CancellationToken.None));
            var forbidden = await Assert.ThrowsAsync<ProjectIdentityException>(() => collab.ListActivitiesAsync(second.PrincipalId, project.ProjectId, "comment", task.TaskId, null, null, null, 30, CancellationToken.None));
            Assert.Equal(404, forbidden.StatusCode);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"; await command.ExecuteNonQueryAsync();
        }
    }

    [PostgreSqlFact]
    public async Task AT_T22_Field_reverse_semantics_requirements_candidates_and_blind_spots()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t22_{Guid.NewGuid():N}";
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
            var author = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "impact-author", PrincipalKind.Human, CancellationToken.None);
            var project = await projects.CreateProjectAsync(author, new("Impacts", $"impacts-{Guid.NewGuid():N}", "t22-project-0001"), "t22-r1", null, CancellationToken.None);

            var field = await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "DataField");
            var attribute = await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "BusinessAttribute");
            var rules = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            foreach (var rule in rules) await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "BusinessRule", rule);
            var behavior = await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "Action");
            var implementation = await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "Implementation");
            var requirement = await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "Requirement");
            var acceptance = await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "BusinessCase");
            var contained = await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "Document");
            var dependency = await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "Component");
            var evidence = await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "Source");

            await AddRelation(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "mapsTo", attribute, field);
            foreach (var rule in rules) await AddRelation(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "usesFact", rule, attribute);
            await AddRelation(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "constrains", rules[0], behavior);
            await AddRelation(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "implements", implementation, rules[0]);
            await AddRelation(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "satisfies", behavior, requirement);
            await AddRelation(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "validates", acceptance, requirement);
            await AddRelation(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "contains", contained, rules[0]);
            await AddRelation(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "dependsOn", dependency, rules[0]);
            await AddRelation(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "evidencedBy", rules[0], evidence);

            var task = await collab.CreateTaskAsync(author, project.ProjectId, new("Rename the physical field while preserving business behavior", null, field, null, null), "t22-task-0001", "t22-r2", null, CancellationToken.None);
            var job = await collab.FindImpactsAsync(author, project.ProjectId, task.TaskId,
                new([new(field, null)], "revise", new(ReadMode.Published, ReadPurpose.Change, null, null, null)),
                task.Version, "t22-impacts-01", "t22-r3", null, CancellationToken.None);
            Assert.Equal("succeeded", job.State); Assert.Contains(job.Unknowns, item => item.Field == "impactCoverage");

            var impacts = (await collab.ListImpactsAsync(author.PrincipalId, project.ProjectId, task.TaskId, null, 100, CancellationToken.None)).Items;
            var targets = impacts.Select(item => item.Target!.UnitId).ToHashSet();
            Assert.Equal(8, impacts.Count);
            Assert.Contains(attribute, targets); Assert.Contains(behavior, targets); Assert.Contains(implementation, targets); Assert.Contains(requirement, targets); Assert.Contains(acceptance, targets);
            foreach (var rule in rules) Assert.Contains(rule, targets);
            Assert.DoesNotContain(contained, targets); Assert.DoesNotContain(dependency, targets); Assert.DoesNotContain(evidence, targets);
            Assert.All(impacts, item => { Assert.Equal("potential", item.Disposition); Assert.Equal("candidate", item.InspectionState); Assert.Empty(item.Evidence); });
            var requirementImpact = impacts.Single(item => item.Target!.UnitId == requirement);
            Assert.True(new[] { "reverse", "reverse", "forward", "forward" }.SequenceEqual(requirementImpact.Path.Select(item => item.GetProperty("direction").GetString())));
            Assert.All(requirementImpact.Path, item => Assert.NotEqual(Guid.Empty, item.GetProperty("edge").GetProperty("revisionId").GetGuid()));

            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
            await using var coverageCommand = connection.CreateCommand(); coverageCommand.CommandText = "SELECT target_summary->'impactCoverage' FROM collab.task WHERE project_id=@project AND task_id=@task";
            coverageCommand.Parameters.AddWithValue("project", project.ProjectId); coverageCommand.Parameters.AddWithValue("task", task.TaskId);
            var coverage = JsonDocument.Parse((string)(await coverageCommand.ExecuteScalarAsync())!).RootElement;
            Assert.Equal("bounded_semantic_graph", coverage.GetProperty("coverage").GetString()); Assert.False(coverage.GetProperty("truncated").GetBoolean());
            Assert.True(coverage.GetProperty("blindSpots").GetArrayLength() >= 3);
            Assert.All(coverage.GetProperty("blindSpots").EnumerateArray(), item => Assert.Equal("candidate", item.GetProperty("state").GetString()));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"; await command.ExecuteNonQueryAsync();
        }
    }

    [PostgreSqlFact]
    public async Task AT_T23_Executed_evidence_dispositions_and_coordinated_change_plan()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t23_{Guid.NewGuid():N}";
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
            var author = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "t23-automated-test-actor", PrincipalKind.Human, CancellationToken.None);
            var project = await projects.CreateProjectAsync(author, new("Investigation", $"investigation-{Guid.NewGuid():N}", "t23-project-0001"), "t23-r1", null, CancellationToken.None);
            var field = await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "DataField");
            var attribute = await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "BusinessAttribute");
            var rule = await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "BusinessRule");
            var source = await AddUnit(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "Source");
            await AddRelation(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "mapsTo", attribute, field);
            await AddRelation(administrator.ConnectionString, project.ProjectId, author.PrincipalId, project.CurrentBaselineId, "usesFact", rule, attribute);

            var task = await collab.CreateTaskAsync(author, project.ProjectId, new("Rename a field while preserving matching behavior", null, field, null, null), "t23-task-0001", "t23-r2", null, CancellationToken.None);
            var sourceRevision = await RevisionId(administrator.ConnectionString, project.ProjectId, source);
            var amended = await collab.AmendTaskAsync(author, project.ProjectId, task.TaskId,
                new(null, [new("revise", "Update the physical mapping without changing matching semantics", "Implementation-only field rename", new(source, sourceRevision))], [new(source, sourceRevision)], null),
                task.Version, "t23-amend-001", "t23-r3", null, CancellationToken.None);
            await collab.FindImpactsAsync(author, project.ProjectId, task.TaskId, new([new(field, null)], "revise", new(ReadMode.Published, ReadPurpose.Change, null, null, null)), amended.Version, "t23-impact-001", "t23-r4", null, CancellationToken.None);
            var current = await collab.GetTaskAsync(author.PrincipalId, project.ProjectId, task.TaskId, CancellationToken.None);
            var impacts = (await collab.ListImpactsAsync(author.PrincipalId, project.ProjectId, task.TaskId, null, 100, CancellationToken.None)).Items;
            Assert.Equal(2, impacts.Count);
            foreach (var impact in impacts)
            {
                var denied = await Assert.ThrowsAsync<ProjectIdentityException>(() => collab.DecideImpactAsync(author, project.ProjectId, task.TaskId, impact.ImpactId,
                    new("no_change", "candidate", null, "No search was performed", "medium", null, [], "resolved"), impact.Version, $"t23-deny-{impact.ImpactId:N}", "t23-r5", null, CancellationToken.None));
                Assert.Equal(422, denied.StatusCode);
            }

            var run = await AddCapturedRun(administrator.ConnectionString, project.ProjectId, task.TaskId, author.PrincipalId);
            var evidence = Json($$"""[{"source":{"unitId":"{{source}}","revisionId":"{{sourceRevision}}"},"locator":{"kind":"file","path":"src/Legacy/Handler.cs","repo":"business-atlas","commitSha":"{{new string('a', 40)}}","lineStart":10,"lineEnd":40},"support":"supports","captureKind":"rawCaptured"}]""");
            var investigated = await collab.SubmitInvestigationAsync(author, project.ProjectId, task.TaskId,
                new(null, task.TaskId, project.CurrentBaselineId, "business-atlas", new string('a', 40), ["src"], ["rg entrypoints and legacy route references"], "Inspected known entry, legacy path, arguments and compatibility branch in the declared source scope.", [], "rawCaptured", evidence.EnumerateArray().Select(item => item.Clone()).ToArray(), run),
                current.Version, "t23-investigate-01", "t23-r6", null, CancellationToken.None);
            Assert.NotNull(investigated.InvestigationId);
            impacts = (await collab.ListImpactsAsync(author.PrincipalId, project.ProjectId, task.TaskId, null, 100, CancellationToken.None)).Items;
            var evidenceItems = evidence.EnumerateArray().Select(item => item.Clone()).ToArray();
            foreach (var impact in impacts)
            {
                var decided = await collab.DecideImpactAsync(author, project.ProjectId, task.TaskId, impact.ImpactId,
                    new("no_change", "inspected", investigated.InvestigationId, "Existing entry and legacy route preserve the rule in the inspected scope", "medium", null, evidenceItems, "resolved"),
                    impact.Version, $"t23-decide-{impact.ImpactId:N}", "t23-r7", null, CancellationToken.None);
                Assert.Equal("resolved", decided.CloseState);
            }
            current = await collab.GetTaskAsync(author.PrincipalId, project.ProjectId, task.TaskId, CancellationToken.None);
            Assert.Empty(current.Unknowns);
            var taskContext = await AddExecutableContext(administrator.ConnectionString, project.ProjectId, task.TaskId, project.CurrentBaselineId, author.PrincipalId);
            var planned = await collab.PlanChangeAsync(author, project.ProjectId, task.TaskId, new(taskContext, [investigated.InvestigationId.Value]), current.Version, "t23-plan-0001", "t23-r8", null, CancellationToken.None);
            Assert.Equal("succeeded", planned.State);
            Assert.Equal("ready", (await collab.GetTaskAsync(author.PrincipalId, project.ProjectId, task.TaskId, CancellationToken.None)).State);

            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
            await using var planCommand = connection.CreateCommand(); planCommand.CommandText = "SELECT result->'plan' FROM ops.job WHERE project_id=@project AND job_id=@job";
            planCommand.Parameters.AddWithValue("project", project.ProjectId); planCommand.Parameters.AddWithValue("job", planned.JobId);
            var plan = JsonDocument.Parse((string)(await planCommand.ExecuteScalarAsync())!).RootElement;
            Assert.Single(plan.GetProperty("dependencyGroups").EnumerateArray());
            Assert.Equal("atomic", plan.GetProperty("dependencyGroups")[0].GetProperty("coordination").GetString());
            Assert.Single(plan.GetProperty("preserve").EnumerateArray());
            Assert.Equal(2, plan.GetProperty("revalidations").GetArrayLength());
            Assert.Single(plan.GetProperty("engineeringRecommendations").EnumerateArray());
            Assert.True(plan.GetProperty("investigationCoverage")[0].GetProperty("executed").GetBoolean());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"; await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<Guid> RevisionId(string connection, Guid project, Guid unit)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand();
        command.CommandText = "SELECT revision_id FROM core.revision WHERE project_id=@project AND unit_id=@unit"; command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("unit", unit);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<Guid> AddCapturedRun(string connection, Guid project, Guid task, Guid actor)
    {
        var run = Guid.NewGuid(); await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand();
        command.CommandText = """
            SET session_replication_role=replica;
            INSERT INTO agent.run(project_id,run_id,task_id,executor_id,state,created_by) VALUES(@project,@run,@task,@actor,'succeeded',@actor);
            INSERT INTO agent.run_event(project_id,event_id,run_id,seq,external_event_id,capture_kind,event_type,event_data,occurred_at,created_by)
              VALUES(@project,@event,@run,1,'t23-tool-call','rawCaptured','tool_result','{"command":"rg entrypoints and legacy paths","exitCode":0}',now(),@actor);
            """;
        command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("run", run); command.Parameters.AddWithValue("task", task); command.Parameters.AddWithValue("actor", actor); command.Parameters.AddWithValue("event", Guid.NewGuid());
        await command.ExecuteNonQueryAsync(); return run;
    }

    private static async Task<Guid> AddExecutableContext(string connection, Guid project, Guid task, Guid baseline, Guid actor)
    {
        var context = Guid.NewGuid(); await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO delivery.task_context(project_id,context_id,task_id,baseline_id,policy_epoch,phase,contract_digest,body,created_by) VALUES(@project,@context,@task,@baseline,1,'executable',@digest,'{}',@actor)";
        command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("context", context); command.Parameters.AddWithValue("task", task); command.Parameters.AddWithValue("baseline", baseline); command.Parameters.AddWithValue("digest", new string('b', 64)); command.Parameters.AddWithValue("actor", actor);
        await command.ExecuteNonQueryAsync(); return context;
    }

    private static async Task<Guid> AddUnit(string connection, Guid project, Guid actor, Guid baseline, string kind, Guid? id = null)
    {
        var unit = id ?? Guid.NewGuid(); var revision = Guid.NewGuid();
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand();
        command.CommandText = """
            SET session_replication_role=replica;
            INSERT INTO core.unit(project_id,unit_id,kind,owner_id,created_by) VALUES(@project,@unit,@kind,@actor,@actor);
            INSERT INTO core.revision(project_id,revision_id,unit_id,rev_no,schema_type,schema_version,fact_kind,verification,payload,applicability,content_sha256,sealed,created_by)
              VALUES(@project,@revision,@unit,1,@kind,'1.0','normative','unverified','{"title":"T22 synthetic semantic fixture"}','{}',@hash,true,@actor);
            INSERT INTO gov.baseline_entry(project_id,baseline_id,unit_id,revision_id) VALUES(@project,@baseline,@unit,@revision);
            """;
        command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("unit", unit); command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("kind", kind); command.Parameters.AddWithValue("actor", actor); command.Parameters.AddWithValue("baseline", baseline); command.Parameters.AddWithValue("hash", new string('a', 64));
        await command.ExecuteNonQueryAsync(); return unit;
    }

    private static async Task AddRelation(string connection, Guid project, Guid actor, Guid baseline, string predicate, Guid from, Guid to)
    {
        var relationUnit = await AddUnit(connection, project, actor, baseline, "Relationship");
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand();
        command.CommandText = """
            SET session_replication_role=replica;
            INSERT INTO core.relation_index(project_id,relation_revision_id,predicate,from_unit_id,to_unit_id)
              SELECT @project,revision_id,@predicate,@from,@to FROM gov.baseline_entry WHERE project_id=@project AND baseline_id=@baseline AND unit_id=@unit;
            """;
        command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("unit", relationUnit);
        command.Parameters.AddWithValue("baseline", baseline); command.Parameters.AddWithValue("predicate", predicate); command.Parameters.AddWithValue("from", from); command.Parameters.AddWithValue("to", to);
        await command.ExecuteNonQueryAsync();
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static Task AddReader(string connection, Guid project, Guid principal) => AddMembership(connection, project, principal, "ProjectReader");
    private static async Task AddMembership(string connection, Guid project, Guid principal, string role)
    { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = "INSERT INTO identity.membership(project_id,principal_id,roles) VALUES(@project,@principal,ARRAY[@role])"; command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("principal", principal); command.Parameters.AddWithValue("role", role); await command.ExecuteNonQueryAsync(); }
    private static async Task Revoke(string connection, Guid project, Guid principal)
    { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = "UPDATE identity.membership SET active=false WHERE project_id=@project AND principal_id=@principal"; command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("principal", principal); await command.ExecuteNonQueryAsync(); }
}
