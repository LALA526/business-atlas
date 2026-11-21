using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessApplicability.Application;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using Governance.Contracts;
using Governance.Infrastructure;
using KnowledgeCore.Contracts;
using KnowledgeCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using ProjectIdentity.Infrastructure;

namespace Atlas.Integration.Tests;

public sealed class GovernanceTests
{
    [PostgreSqlFact]
    public async Task AT_T13_Draft_submission_review_publish_and_reopen_preserve_governance_boundaries()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t13_{Guid.NewGuid():N}";
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
            var knowledge = new PostgresKnowledgeStore(runtime, access);
            var governance = new PostgresGovernanceStore(runtime, knowledge, access);
            var author = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "author", PrincipalKind.Human, CancellationToken.None);
            var reviewer = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "reviewer", PrincipalKind.Human, CancellationToken.None);
            var project = await projects.CreateProjectAsync(author, new("Governance", $"governance-{Guid.NewGuid():N}", "t13-project-0001"), "r1", null, CancellationToken.None);
            await AddMembershipAsync(administrator.ConnectionString, project.ProjectId, reviewer.PrincipalId, ["BusinessOwner"]);
            await AddRolesAsync(administrator.ConnectionString, project.ProjectId, author.PrincipalId, ["KnowledgePublisher"]);

            var input = Input("Candidate rule");
            var proposal = await governance.SubmitProposalAsync(author, project.ProjectId, new(project.CurrentBaselineId, "AT-T13 candidate", null, [input], []), "t13-proposal-0001", "r2", null, CancellationToken.None);
            Assert.Equal("draft", proposal.Data.State);
            Assert.Single(proposal.Data.Items);
            var publishedRead = await policy.CreateReadContextAsync(author.PrincipalId, project.ProjectId, new(ReadMode.Published, ReadPurpose.Understand, null, null, null), CancellationToken.None);
            Assert.Empty((await knowledge.ListUnitsAsync(publishedRead, null, null, null, 30, CancellationToken.None)).Items);
            var draftRead = await policy.CreateReadContextAsync(author.PrincipalId, project.ProjectId, new(ReadMode.Draft, ReadPurpose.Change, null, proposal.Data.ChangeSetId, null), CancellationToken.None);
            Assert.Single((await knowledge.ListUnitsAsync(draftRead, null, null, null, 30, CancellationToken.None)).Items);

            var submitted = await governance.SubmitChangeSetAsync(author, project.ProjectId, proposal.Data.ChangeSetId, new([Json("{\"type\":\"text\",\"text\":\"Candidate for human review\"}")], []), proposal.ProjectVersion, "t13-submit-0001", "r3", null, CancellationToken.None);
            Assert.Equal("submitted", submitted.Data.State);
            Assert.Equal(64, submitted.Data.SubmitDigest!.Length);
            var approved = await governance.ReviewChangeSetAsync(reviewer, project.ProjectId, proposal.Data.ChangeSetId,
                new(submitted.Data.SubmitDigest, submitted.Data.SubmittedSeq!.Value, [submitted.Data.Approvals.Single().SlotId], "approve", "Human review of the fixed candidate", Json("{}")),
                submitted.ProjectVersion, "t13-review-0001", "r4", null, CancellationToken.None);
            Assert.Equal("approved", approved.Data.State);
            var published = await governance.PublishChangeSetAsync(author, project.ProjectId, proposal.Data.ChangeSetId,
                new(submitted.Data.SubmitDigest, project.CurrentBaselineId), approved.ProjectVersion, "t13-publish-0001", "r5", null, CancellationToken.None);
            Assert.Equal(1, published.Data.Number);
            var nextRead = await policy.CreateReadContextAsync(author.PrincipalId, project.ProjectId, new(ReadMode.Published, ReadPurpose.Understand, null, null, null), CancellationToken.None);
            Assert.Single((await knowledge.ListUnitsAsync(nextRead, null, null, null, 30, CancellationToken.None)).Items);
            await AllowHistoryAsync(administrator.ConnectionString, project.ProjectId, author.PrincipalId, proposal.Data.Items[0].UnitId, author.PrincipalId);
            Assert.Single(await governance.DiffBaselineAsync(author.PrincipalId, project.ProjectId, project.CurrentBaselineId, published.Data.BaselineId, CancellationToken.None));
            Assert.Equal(2, (await governance.ListBaselinesAsync(author.PrincipalId, project.ProjectId, null, 30, CancellationToken.None)).Items.Count);
            Assert.Contains((await governance.ListChangeSetsAsync(author.PrincipalId, project.ProjectId, "published", false, null, 30, CancellationToken.None)).Items, item => item.ChangeSetId == proposal.Data.ChangeSetId);

            var second = await governance.CreateChangeSetAsync(author, project.ProjectId, new("Second candidate", published.Data.BaselineId), "t13-second-0001", "r6", null, CancellationToken.None);
            var revisedInput = input with { UnitId = proposal.Data.Items[0].UnitId, Title = "Candidate rule v2" };
            var revision = await knowledge.ReviseDraftAsync(author, project.ProjectId, second.Data.ChangeSetId, proposal.Data.Items[0].UnitId, revisedInput, second.ProjectVersion, "t13-revision-0001", "r7", null, CancellationToken.None);
            var returned = await governance.SubmitChangeSetAsync(author, project.ProjectId, second.Data.ChangeSetId, new([], []), revision.ProjectVersion, "t13-submit-0002", "r8", null, CancellationToken.None);
            var returnedReview = await governance.ReviewChangeSetAsync(reviewer, project.ProjectId, second.Data.ChangeSetId,
                new(returned.Data.SubmitDigest!, returned.Data.SubmittedSeq!.Value, [returned.Data.Approvals.Single().SlotId], "request_changes", "Clarify candidate", Json("{}")), returned.ProjectVersion, "t13-review-0002", "r9", null, CancellationToken.None);
            var reopened = await governance.ReopenChangeSetAsync(author, project.ProjectId, second.Data.ChangeSetId, new("Apply human review", null), returnedReview.ProjectVersion, "t13-reopen-0001", "r10", null, CancellationToken.None);
            var resubmitted = await governance.SubmitChangeSetAsync(author, project.ProjectId, second.Data.ChangeSetId, new([], []), reopened.ProjectVersion, "t13-submit-0003", "r11", null, CancellationToken.None);
            Assert.NotEqual(returned.Data.SubmitDigest, resubmitted.Data.SubmitDigest);
            Assert.All(resubmitted.Data.Approvals, slot => Assert.Equal("pending", slot.Status));
            var restore = await governance.PlanBaselineRestoreAsync(author, project.ProjectId, project.CurrentBaselineId, new("Restore the pre-change baseline through governance", published.Data.BaselineId), "t13-restore-0001", "r12", null, CancellationToken.None);
            Assert.Equal("draft", restore.Data.State);
            Assert.Equal("retire", Assert.Single(restore.Data.Items).Action);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"; await command.ExecuteNonQueryAsync();
        }
    }

    [PostgreSqlFact]
    public async Task AT_T14_Concurrent_review_authorization_rollback_and_canonical_baseline_hash()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t14_{Guid.NewGuid():N}";
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
            var knowledge = new PostgresKnowledgeStore(runtime, access);
            var governance = new PostgresGovernanceStore(runtime, knowledge, access);
            var author = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "author", PrincipalKind.Human, CancellationToken.None);
            var firstReviewer = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "reviewer-a", PrincipalKind.Human, CancellationToken.None);
            var secondReviewer = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "reviewer-b", PrincipalKind.Human, CancellationToken.None);
            var publisher = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "publisher", PrincipalKind.Service, CancellationToken.None);
            var project = await projects.CreateProjectAsync(author, new("Governance conflicts", $"governance-{Guid.NewGuid():N}", "t14-project-0001"), "r1", null, CancellationToken.None);
            await AddMembershipAsync(administrator.ConnectionString, project.ProjectId, firstReviewer.PrincipalId, ["BusinessOwner"]);
            await AddMembershipAsync(administrator.ConnectionString, project.ProjectId, secondReviewer.PrincipalId, ["BusinessOwner"]);
            await AddMembershipAsync(administrator.ConnectionString, project.ProjectId, publisher.PrincipalId, ["KnowledgePublisher"]);
            await AddRolesAsync(administrator.ConnectionString, project.ProjectId, author.PrincipalId, ["KnowledgePublisher"]);

            var proposal = await governance.SubmitProposalAsync(author, project.ProjectId, new(project.CurrentBaselineId, "AT-T14 candidates", null, [Input("Rule A"), Input("Rule B")], []), "t14-proposal-0001", "r2", null, CancellationToken.None);
            var items = proposal.Data.Items.Select((item, index) => new ChangeItemInput(item.UnitId, item.Action, item.BaseRevisionId, item.ProposedRevisionId, $"group-{index}", item.Reason)).ToArray();
            var amended = await governance.AmendChangeSetAsync(author, project.ProjectId, proposal.Data.ChangeSetId, new(null, items, null), proposal.ProjectVersion, "t14-amend-0001", "r3", null, CancellationToken.None);
            var submitted = await governance.SubmitChangeSetAsync(author, project.ProjectId, proposal.Data.ChangeSetId, new([], []), amended.ProjectVersion, "t14-submit-0001", "r4", null, CancellationToken.None);
            Assert.Equal(2, submitted.Data.Approvals.Count);
            var slots = submitted.Data.Approvals.OrderBy(slot => slot.SlotId, StringComparer.Ordinal).ToArray();

            var selfReview = await Assert.ThrowsAsync<ProjectIdentityException>(() => governance.ReviewChangeSetAsync(author, project.ProjectId, proposal.Data.ChangeSetId,
                new(submitted.Data.SubmitDigest!, submitted.Data.SubmittedSeq!.Value, [slots[0].SlotId], "approve", "Self approval must fail", Json("{}")), submitted.ProjectVersion, "t14-self-0001", "r5", null, CancellationToken.None));
            Assert.Equal(403, selfReview.StatusCode);

            var reviews = await Task.WhenAll(
                ReviewAsync(governance, firstReviewer, project.ProjectId, proposal.Data.ChangeSetId, submitted, slots[0].SlotId, "t14-review-a"),
                ReviewAsync(governance, secondReviewer, project.ProjectId, proposal.Data.ChangeSetId, submitted, slots[1].SlotId, "t14-review-b"));
            Assert.True(reviews.Count(result => result.Error is null) == 1, string.Join("; ", reviews.Select(result => $"{result.Error?.StatusCode}: {result.Error?.Message}")));
            Assert.Equal(412, Assert.Single(reviews, result => result.Error is not null).Error!.StatusCode);
            var partiallyApproved = await governance.GetChangeSetAsync(author.PrincipalId, project.ProjectId, proposal.Data.ChangeSetId, CancellationToken.None);
            Assert.Equal("in_review", partiallyApproved.State);
            var pending = Assert.Single(partiallyApproved.Approvals, slot => slot.Status == "pending");
            var completed = await governance.ReviewChangeSetAsync(secondReviewer, project.ProjectId, proposal.Data.ChangeSetId,
                new(submitted.Data.SubmitDigest!, submitted.Data.SubmittedSeq!.Value, [pending.SlotId], "approve", "Fresh CAS review", Json("{}")), reviews.Single(result => result.Error is null).Version, "t14-review-final", "r8", null, CancellationToken.None);
            Assert.Equal("approved", completed.Data.State);

            var forbiddenPublisher = await Assert.ThrowsAsync<ProjectIdentityException>(() => governance.PublishChangeSetAsync(publisher, project.ProjectId, proposal.Data.ChangeSetId,
                new(submitted.Data.SubmitDigest!, project.CurrentBaselineId), completed.ProjectVersion, "t14-service-publish", "r9", null, CancellationToken.None));
            Assert.Equal(403, forbiddenPublisher.StatusCode);

            await InstallPublishFailureTriggerAsync(administrator.ConnectionString);
            await Assert.ThrowsAsync<PostgresException>(() => governance.PublishChangeSetAsync(author, project.ProjectId, proposal.Data.ChangeSetId,
                new(submitted.Data.SubmitDigest!, project.CurrentBaselineId), completed.ProjectVersion, "t14-rollback-publish", "r10", null, CancellationToken.None));
            var rolledBack = await governance.GetChangeSetAsync(author.PrincipalId, project.ProjectId, proposal.Data.ChangeSetId, CancellationToken.None);
            Assert.Equal("approved", rolledBack.State);
            Assert.Equal(completed.Data.Version, rolledBack.Version);
            Assert.Empty(await ScalarListAsync(administrator.ConnectionString, "SELECT baseline_id FROM gov.baseline WHERE change_set_id=@id", proposal.Data.ChangeSetId));
            Assert.Empty(await ScalarListAsync(administrator.ConnectionString, "SELECT event_id FROM ops.outbox WHERE event_type='BaselinePublished.v1' AND payload->>'changeSetId'=@id::text", proposal.Data.ChangeSetId));
            Assert.Empty(await ScalarListAsync(administrator.ConnectionString, "SELECT principal_id FROM ops.idempotency WHERE operation_id='publishChangeSet' AND idempotency_key='t14-rollback-publish'"));
            await DropPublishFailureTriggerAsync(administrator.ConnectionString);

            var published = await governance.PublishChangeSetAsync(author, project.ProjectId, proposal.Data.ChangeSetId,
                new(submitted.Data.SubmitDigest!, project.CurrentBaselineId), completed.ProjectVersion, "t14-publish-0001", "r11", null, CancellationToken.None);
            var entries = submitted.Data.Items.OrderBy(item => item.UnitId).Select(item => $"{{\"revisionId\":\"{item.ProposedRevisionId}\",\"unitId\":\"{item.UnitId}\"}}");
            var canonical = $"{{\"entries\":[{string.Join(',', entries)}],\"number\":1,\"parentBaselineId\":\"{project.CurrentBaselineId}\",\"projectId\":\"{project.ProjectId}\"}}";
            var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            Assert.Equal(expectedHash, published.Data.Digest);
            Assert.Equal(published.Data.Digest, (await governance.ListBaselinesAsync(author.PrincipalId, project.ProjectId, null, 10, CancellationToken.None)).Items.Single(item => item.BaselineId == published.Data.BaselineId).Digest);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"; await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<(ProjectIdentityException? Error, long Version)> ReviewAsync(PostgresGovernanceStore governance, AtlasPrincipal reviewer, Guid projectId, Guid changeSetId, GovernanceMutation<ChangeSetView> submitted, string slotId, string key)
    {
        try
        {
            var result = await governance.ReviewChangeSetAsync(reviewer, projectId, changeSetId, new(submitted.Data.SubmitDigest!, submitted.Data.SubmittedSeq!.Value, [slotId], "approve", "Concurrent human review", Json("{}")), submitted.ProjectVersion, key, key, null, CancellationToken.None);
            return (null, result.ProjectVersion);
        }
        catch (ProjectIdentityException exception) { return (exception, submitted.ProjectVersion); }
    }

    private static async Task InstallPublishFailureTriggerAsync(string connection)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE FUNCTION ops.fail_t14_publish() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'AT-T14 forced final-step failure'; END; $$;
            CREATE TRIGGER fail_t14_publish BEFORE INSERT ON ops.audit_event FOR EACH ROW WHEN (NEW.action='publishChangeSet') EXECUTE FUNCTION ops.fail_t14_publish();
            """; await command.ExecuteNonQueryAsync();
    }

    private static async Task DropPublishFailureTriggerAsync(string connection)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand();
        command.CommandText = "DROP TRIGGER fail_t14_publish ON ops.audit_event; DROP FUNCTION ops.fail_t14_publish();"; await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<Guid>> ScalarListAsync(string connection, string sql, Guid? id = null)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = sql;
        if (id.HasValue) command.Parameters.AddWithValue("id", id.Value);
        var result = new List<Guid>(); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) result.Add(reader.GetGuid(0)); return result;
    }

    private static RevisionInput Input(string title) => new(null, title, "Source", "1.0", null, "normative", "unverified", Json("{}"),
        JsonSerializer.SerializeToElement(new { sourceKind = "human", locator = new { kind = "human", description = "Automated test source" }, provenance = "AT-T13" }),
        Json("[]"), Json("[]"), Json("[]"), Json("[\"source\"]"));
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static async Task AddMembershipAsync(string connection, Guid project, Guid principal, string[] roles)
    { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = "INSERT INTO identity.membership(project_id,principal_id,roles) VALUES(@project,@principal,@roles);"; command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("principal", principal); command.Parameters.AddWithValue("roles", roles); await command.ExecuteNonQueryAsync(); }
    private static async Task AllowHistoryAsync(string connection, Guid project, Guid principal, Guid unit, Guid creator)
    { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = "INSERT INTO access.grant(project_id,grant_id,unit_id,principal_id,action,field_paths,effect,created_by) VALUES(@project,@grant,@unit,@principal,'read_history',ARRAY['*'],'allow',@creator);"; command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("grant", Guid.NewGuid()); command.Parameters.AddWithValue("unit", unit); command.Parameters.AddWithValue("principal", principal); command.Parameters.AddWithValue("creator", creator); await command.ExecuteNonQueryAsync(); }
    private static async Task AddRolesAsync(string connection, Guid project, Guid principal, string[] roles)
    { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = "UPDATE identity.membership SET roles=roles||@roles WHERE project_id=@project AND principal_id=@principal;"; command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("principal", principal); command.Parameters.AddWithValue("roles", roles); await command.ExecuteNonQueryAsync(); }
    private static async Task MigrateAsync(string connection) { var options = new DbContextOptionsBuilder<BuildingBlocks.Persistence.AtlasDbContext>().UseNpgsql(connection).Options; await using var context = new BuildingBlocks.Persistence.AtlasDbContext(options); await context.Database.MigrateAsync(); }
}
