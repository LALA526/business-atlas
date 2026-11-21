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
