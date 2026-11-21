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
using PlatformOps.Jobs;
using ProjectIdentity.Contracts;
using ProjectIdentity.Application;
using ProjectIdentity.Infrastructure;
using ReadSearchExplain.Export;
using ReadSearchExplain.Search;
using BuildingBlocks.Persistence;

namespace Atlas.Integration.Tests;

public sealed class ReadSearchExplainTests
{
    [PostgreSqlFact]
    public async Task AT_T18_Chinese_aliases_context_acl_and_export_reauthorization()
    {
        var admin = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t18_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(admin.ConnectionString)) { await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE {database}"; await command.ExecuteNonQueryAsync(); }
        admin.Database = database;
        try
        {
            var options = new DbContextOptionsBuilder<AtlasDbContext>().UseNpgsql(admin.ConnectionString).Options;
            await using (var db = new AtlasDbContext(options)) await db.Database.MigrateAsync();
            var runtime = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Options = "-c role=atlas_app" }.ConnectionString;
            var projects = new PostgresProjectIdentityStore(runtime); var access = new PostgresAccessApplicabilityStore(runtime);
            var policy = new PolicyService(access); var knowledge = new PostgresKnowledgeStore(runtime, access); var governance = new PostgresGovernanceStore(runtime, knowledge, access);
            var search = new PostgresReadSearchService(runtime, knowledge, access, "test-cursor-key");
            var actor = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "reader", PrincipalKind.Human, CancellationToken.None);
            var project = await projects.CreateProjectAsync(actor, new("Read", $"read-{Guid.NewGuid():N}", "t18-project-0001"), "r1", null, CancellationToken.None);
            await AddRoles(admin.ConnectionString, project.ProjectId, actor.PrincipalId, ["KnowledgePublisher"]);
            var proposal = await governance.SubmitProposalAsync(actor, project.ProjectId, new(project.CurrentBaselineId, "Terms", null, [Term("报价", "sales"), Term("报价", "manufacturing")], []), "t18-proposal-0001", "r3", null, CancellationToken.None);
            var submitted = await governance.SubmitChangeSetAsync(actor, project.ProjectId, proposal.Data.ChangeSetId, new([], []), proposal.ProjectVersion, "t18-submit-0001", "r4", null, CancellationToken.None);
            // The test uses the normal human approval endpoint only with a separate authorized reviewer.
            var reviewer = await projects.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "reviewer", PrincipalKind.Human, CancellationToken.None);
            await AddMembership(admin.ConnectionString, project.ProjectId, reviewer.PrincipalId, ["BusinessOwner"]);
            var approved = await governance.ReviewChangeSetAsync(reviewer, project.ProjectId, proposal.Data.ChangeSetId, new(submitted.Data.SubmitDigest!, submitted.Data.SubmittedSeq!.Value, submitted.Data.Approvals.Select(a => a.SlotId).ToArray(), "approve", "Human test reviewer approves fixtures", Json("{}")), submitted.ProjectVersion, "t18-review-0001", "r6", null, CancellationToken.None);
            await governance.PublishChangeSetAsync(actor, project.ProjectId, proposal.Data.ChangeSetId, new(submitted.Data.SubmitDigest!, project.CurrentBaselineId), approved.ProjectVersion, "t18-publish-0001", "r7", null, CancellationToken.None);
            var read = await policy.CreateReadContextAsync(actor.PrincipalId, project.ProjectId, new(ReadMode.Published, ReadPurpose.Understand, null, null, null), CancellationToken.None);
            Assert.Equal(2, (await search.SearchAsync(read, "报价", null, null, 30, CancellationToken.None)).Items.Count);
            Assert.Equal("ambiguous", (await search.ResolveVocabularyAsync(read, "报价", null, CancellationToken.None)).Status);
            Assert.Equal("resolved", (await search.ResolveVocabularyAsync(read, "报价", "sales", CancellationToken.None)).Status);
            var id = proposal.Data.Items[0].UnitId;
            var exportInput = JsonSerializer.SerializeToElement(new { readContext = new ReadContextRequest(ReadMode.Published, ReadPurpose.Export, null, null, null), format = "json", rootUnitIds = new[] { id } });
            var queue = new NoopQueue(); var exports = new PostgresExportService(runtime, knowledge, access, search, queue);
            var artifact = await exports.GenerateAsync(project.ProjectId, actor.PrincipalId, exportInput, CancellationToken.None);
            Assert.NotEmpty((await exports.DownloadAsync(actor.PrincipalId, project.ProjectId, artifact.UploadId, CancellationToken.None)).Content);
            var version = await ProjectVersion(admin.ConnectionString, project.ProjectId);
            await access.SetGrantAsync(actor, project.ProjectId, new(null, id, actor.PrincipalId, "export", [], "deny", null), version, "t18-revoke-0001", "r8", null, CancellationToken.None);
            var error = await Assert.ThrowsAsync<ProjectIdentityException>(() => exports.DownloadAsync(actor.PrincipalId, project.ProjectId, artifact.UploadId, CancellationToken.None));
            Assert.Contains(error.StatusCode, new[] { 403, 404 });
        }
        finally { NpgsqlConnection.ClearAllPools(); admin.Database = "postgres"; await using var connection = new NpgsqlConnection(admin.ConnectionString); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"; await command.ExecuteNonQueryAsync(); }
    }
    private static RevisionInput Term(string title, string context) => new(null, title, "DomainTerm", "1.0", null, "normative", "verified", Json("{\"unrestricted\":true}"), Json($"{{\"contextKey\":\"{context}\",\"aliases\":[\"报价单\"],\"definition\":\"测试术语\"}}"), Json("[]"), Json("[]"), Json("[]"), Json("[]"));
    private static JsonElement Json(string value) => System.Text.Json.JsonDocument.Parse(value).RootElement.Clone();
    private static async Task<long> ProjectVersion(string connection, Guid id) { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = "SELECT version FROM identity.project WHERE project_id=@id"; command.Parameters.AddWithValue("id", id); return (long)(await command.ExecuteScalarAsync())!; }
    private static async Task AddRoles(string connection, Guid project, Guid principal, string[] roles) { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = "UPDATE identity.membership SET roles=array_cat(roles,@roles),version=version+1 WHERE project_id=@project AND principal_id=@principal"; command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("principal", principal); command.Parameters.AddWithValue("roles", roles); await command.ExecuteNonQueryAsync(); }
    private static async Task AddMembership(string connection, Guid project, Guid principal, string[] roles) { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = "INSERT INTO identity.membership(project_id,principal_id,roles) VALUES(@project,@principal,@roles)"; command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("principal", principal); command.Parameters.AddWithValue("roles", roles); await command.ExecuteNonQueryAsync(); }
    private sealed class NoopQueue : IJobQueue { public Task<JobInfo> EnqueueAsync(Guid p, Guid r, EnqueueJobCommand c, CancellationToken t = default) => throw new NotSupportedException(); public Task<JobInfo> GetAsync(Guid p, Guid r, Guid j, CancellationToken t = default) => throw new NotSupportedException(); public Task<JobInfo> CancelAsync(Guid p, Guid r, Guid j, long v, string reason, string key, string req, string? trace, CancellationToken t = default) => throw new NotSupportedException(); public Task<JobInfo> RetryAsync(Guid p, Guid r, Guid j, long v, string reason, string key, string req, string? trace, CancellationToken t = default) => throw new NotSupportedException(); }
}
