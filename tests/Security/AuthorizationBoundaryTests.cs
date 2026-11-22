using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using Atlas.Api.Auth;
using ContractDelivery.Contracts;
using ImportEvidence.Blob;
using ImportEvidence.Application;
using ImportEvidence.Infrastructure;
using KnowledgeCore.Contracts;
using KnowledgeCore.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Npgsql;
using NpgsqlTypes;
using PlatformOps.Jobs;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using ProjectIdentity.Infrastructure;
using ReadSearchExplain.Export;
using ReadSearchExplain.Search;
using TaskCollab.Infrastructure;

namespace Atlas.Security.Tests;

public sealed class SecurityPostgreSqlFactAttribute : FactAttribute
{
    public SecurityPostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING")))
            Skip = "Set ATLAS_TEST_CONNECTIONSTRING to run the real AT-T42 authorization boundary.";
    }
}

public sealed class AuthorizationBoundaryTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Issuer = "https://identity.test";
    private const string Audience = "business-atlas-api";
    private const string RestrictedTitle = "Q-00218 restricted procurement rule";
    private const string RestrictedField = "T42-FIELD-MUST-NOT-LEAK";
    private static readonly SymmetricSecurityKey SigningKey = new("atlas-t42-automated-test-signing-key-0123456789"u8.ToArray());
    private readonly WebApplicationFactory<Program> factory;
    public AuthorizationBoundaryTests(WebApplicationFactory<Program> factory) => this.factory = factory;

    [SecurityPostgreSqlFact]
    public async Task AT_T42_Real_two_project_field_source_cache_export_and_MCP_boundaries()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t42_security_{Guid.NewGuid():N}";
        var blobRoot = Path.Combine(Path.GetTempPath(), $"atlas-t42-security-{Guid.NewGuid():N}");
        await using (var connection = new NpgsqlConnection(administrator.ConnectionString))
        { await connection.OpenAsync(); await Execute(connection, $"CREATE DATABASE {database}"); }
        administrator.Database = database;
        try
        {
            var options = new DbContextOptionsBuilder<BuildingBlocks.Persistence.AtlasDbContext>().UseNpgsql(administrator.ConnectionString).Options;
            await using (var context = new BuildingBlocks.Persistence.AtlasDbContext(options)) await context.Database.MigrateAsync();
            var runtime = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Options = "-c role=atlas_app" }.ConnectionString;
            var projects = new PostgresProjectIdentityStore(runtime);
            var access = new PostgresAccessApplicabilityStore(runtime);
            var knowledge = new PostgresKnowledgeStore(runtime, access);
            var search = new PostgresReadSearchService(runtime, knowledge, access, "t42-automated-cursor-key");
            var collab = new PostgresTaskCollabStore(runtime, access);
            var blobs = new PrivateBlobStorage(blobRoot);
            var sources = new PostgresImportEvidenceStore(runtime, blobs, new BuiltInContentScanner(), access, knowledge);
            var owner = await projects.ResolvePrincipalAsync(Issuer, "t42-owner", PrincipalKind.Human, CancellationToken.None);
            var reader = await projects.ResolvePrincipalAsync(Issuer, "t42-reader", PrincipalKind.Human, CancellationToken.None);
            var first = await projects.CreateProjectAsync(owner, new("Same display name", $"t42-first-{Guid.NewGuid():N}", "t42-first-0001"), "t42-r1", null, CancellationToken.None);
            var second = await projects.CreateProjectAsync(owner, new("Same display name", $"t42-second-{Guid.NewGuid():N}", "t42-second-0001"), "t42-r2", null, CancellationToken.None);
            await Memberships(administrator.ConnectionString, first.ProjectId, owner.PrincipalId, reader.PrincipalId);

            var allowed = Guid.NewGuid(); var restricted = Guid.NewGuid(); var relation = Guid.NewGuid(); var source = Guid.NewGuid();
            await SeedKnowledge(administrator.ConnectionString, first, owner.PrincipalId, allowed, restricted, relation, source);
            var otherRevision = Guid.NewGuid();
            await SeedOtherProject(administrator.ConnectionString, second, owner.PrincipalId, allowed, otherRevision);
            await Grant(administrator.ConnectionString, first.ProjectId, allowed, reader.PrincipalId, "read", ["*"], "allow", owner.PrincipalId);
            await Grant(administrator.ConnectionString, first.ProjectId, allowed, reader.PrincipalId, "read", ["/body/restricted"], "deny", owner.PrincipalId);
            await Grant(administrator.ConnectionString, first.ProjectId, allowed, reader.PrincipalId, "export", ["*"], "allow", owner.PrincipalId);
            await Grant(administrator.ConnectionString, first.ProjectId, restricted, reader.PrincipalId, "read", ["*"], "deny", owner.PrincipalId);
            await Grant(administrator.ConnectionString, first.ProjectId, relation, reader.PrincipalId, "read", ["*"], "allow", owner.PrincipalId);
            await Grant(administrator.ConnectionString, first.ProjectId, source, reader.PrincipalId, "read", ["*"], "allow", owner.PrincipalId);
            var sourceRevision = await Scalar<Guid>(administrator.ConnectionString, "SELECT revision_id FROM core.revision WHERE project_id=@project AND unit_id=@unit", first.ProjectId, source);
            var bytes = Encoding.UTF8.GetBytes("T42-SOURCE-BYTES-MUST-NOT-LEAK");
            StoredBlob stored;
            await using (var content = new MemoryStream(bytes)) stored = await blobs.StoreAsync(first.ProjectId, content, bytes.Length, CancellationToken.None);
            await LinkSource(administrator.ConnectionString, first.ProjectId, sourceRevision, stored, owner.PrincipalId);

            var read = await access.CreateReadContextAsync(reader.PrincipalId, first.ProjectId,
                new(ReadMode.Published, ReadPurpose.Understand, null, null, null), CancellationToken.None);
            var projected = await search.ProjectAsync(read, await knowledge.GetUnitAsync(read, allowed, CancellationToken.None), CancellationToken.None);
            var projectedJson = JsonSerializer.Serialize(projected);
            Assert.DoesNotContain(RestrictedField, projectedJson, StringComparison.Ordinal);
            Assert.Contains("Approved public definition", projectedJson, StringComparison.Ordinal);
            Assert.Equal(404, (await Assert.ThrowsAsync<ProjectIdentityException>(() => knowledge.GetUnitAsync(read, restricted, CancellationToken.None))).StatusCode);
            Assert.Equal("Published procurement term", (await knowledge.GetUnitAsync(read, allowed, CancellationToken.None)).Title);
            Assert.Equal(404, (await Assert.ThrowsAsync<ProjectIdentityException>(() => access.CreateReadContextAsync(reader.PrincipalId, second.ProjectId,
                new(ReadMode.Published, ReadPurpose.Understand, null, null, null), CancellationToken.None))).StatusCode);
            Assert.Empty((await knowledge.ListRelationsAsync(read, allowed, "both", null, null, 30, CancellationToken.None)).Items);
            Assert.Empty((await search.SearchAsync(read, RestrictedTitle, null, null, 30, CancellationToken.None)).Items);
            Assert.Empty((await search.SearchAsync(read, RestrictedField, null, null, 30, CancellationToken.None)).Items);
            Assert.Equal(403, (await Assert.ThrowsAsync<ProjectIdentityException>(() => collab.CreateTaskAsync(reader, first.ProjectId,
                new("Reader must not create work", null, allowed, null, null), "t42-reader-task", "t42-r3", null, CancellationToken.None))).StatusCode);
            var sourceDenied = await Assert.ThrowsAsync<ProjectIdentityException>(() => sources.ReadSourceContentAsync(read, source, CancellationToken.None));
            Assert.Contains(sourceDenied.StatusCode, new[] { 403, 404 });

            await using (var client = await McpClient(runtime, "atlas.read atlas.agent"))
            {
                var result = await client.CallToolAsync("knowledge.search", new Dictionary<string, object?>
                { ["projectId"] = first.ProjectId, ["q"] = RestrictedTitle, ["limit"] = 30 });
                var raw = result.StructuredContent?.GetRawText() ?? string.Join("\n", result.Content.OfType<TextContentBlock>().Select(item => item.Text));
                Assert.True(result.IsError != true, raw);
                Assert.DoesNotContain(RestrictedTitle, raw, StringComparison.Ordinal);
                Assert.DoesNotContain(RestrictedField, raw, StringComparison.Ordinal);
                var otherProject = await client.CallToolAsync("project.discover", new Dictionary<string, object?> { ["projectId"] = second.ProjectId });
                Assert.True(otherProject.IsError);
            }

            var exports = new PostgresExportService(runtime, knowledge, access, search, new NoopQueue());
            var exportInput = JsonSerializer.SerializeToElement(new
            { readContext = new ReadContextRequest(ReadMode.Published, ReadPurpose.Export, null, null, null), format = "json", rootUnitIds = new[] { allowed } });
            var artifact = await exports.GenerateAsync(first.ProjectId, reader.PrincipalId, exportInput, CancellationToken.None);
            var downloaded = await exports.DownloadAsync(reader.PrincipalId, first.ProjectId, artifact.UploadId, CancellationToken.None);
            Assert.DoesNotContain(RestrictedField, Encoding.UTF8.GetString(downloaded.Content), StringComparison.Ordinal);

            var version = await Scalar<long>(administrator.ConnectionString, "SELECT version FROM identity.project WHERE project_id=@project", first.ProjectId);
            await access.SetGrantAsync(owner, first.ProjectId, new(null, allowed, reader.PrincipalId, "export", ["*"], "deny", null),
                version, "t42-export-revoke", "t42-r4", null, CancellationToken.None);
            var cached = await Assert.ThrowsAsync<ProjectIdentityException>(() => knowledge.GetUnitAsync(read, allowed, CancellationToken.None));
            Assert.Equal("POLICY_CONTEXT_STALE", cached.Code);
            var oldArtifact = await Assert.ThrowsAsync<ProjectIdentityException>(() => exports.DownloadAsync(reader.PrincipalId, first.ProjectId, artifact.UploadId, CancellationToken.None));
            Assert.Contains(oldArtifact.StatusCode, new[] { 403, 404 });

            version = await Scalar<long>(administrator.ConnectionString, "SELECT version FROM identity.project WHERE project_id=@project", first.ProjectId);
            await access.SetGrantAsync(owner, first.ProjectId, new(null, source, reader.PrincipalId, "read_source", ["*"], "allow", null),
                version, "t42-source-allow", "t42-r5", null, CancellationToken.None);
            read = await access.CreateReadContextAsync(reader.PrincipalId, first.ProjectId,
                new(ReadMode.Published, ReadPurpose.Understand, null, null, null), CancellationToken.None);
            var authorizedSource = await sources.ReadSourceContentAsync(read, source, CancellationToken.None);
            await using (authorizedSource.Content)
            using (var contentReader = new StreamReader(authorizedSource.Content))
                Assert.Equal(bytes, Encoding.UTF8.GetBytes(await contentReader.ReadToEndAsync()));
            version = await Scalar<long>(administrator.ConnectionString, "SELECT version FROM identity.project WHERE project_id=@project", first.ProjectId);
            await access.SetGrantAsync(owner, first.ProjectId, new(null, source, reader.PrincipalId, "read_source", ["*"], "deny", null),
                version, "t42-source-revoke", "t42-r6", null, CancellationToken.None);
            var revoked = await Assert.ThrowsAsync<ProjectIdentityException>(() => sources.ReadSourceContentAsync(read, source, CancellationToken.None));
            Assert.Equal("POLICY_CONTEXT_STALE", revoked.Code);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
            await Execute(connection, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
            if (Directory.Exists(blobRoot)) Directory.Delete(blobRoot, true);
        }
    }

    private async Task<McpClient> McpClient(string connection, string scope)
    {
        var host = factory.WithWebHostBuilder(builder => builder
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Bearer:Authority"] = Issuer, ["Authentication:Bearer:Audience"] = Audience,
                ["Authentication:Oidc:Authority"] = Issuer, ["Authentication:Oidc:ClientId"] = "atlas-test",
                ["Authentication:DataProtectionKeyPath"] = "",
                ["ConnectionStrings:Atlas"] = connection
            }))
            .ConfigureServices(services =>
            {
                var projectStore = new PostgresProjectIdentityStore(connection);
                var accessStore = new PostgresAccessApplicabilityStore(connection);
                var knowledgeStore = new PostgresKnowledgeStore(connection, accessStore);
                var searchService = new PostgresReadSearchService(connection, knowledgeStore, accessStore, "t42-automated-cursor-key");
                services.AddSingleton(projectStore); services.AddSingleton(accessStore); services.AddSingleton(knowledgeStore);
                services.AddSingleton(searchService); services.AddSingleton(new ReadingProjectionService(knowledgeStore, searchService));
                services.PostConfigureAll<OpenIdConnectOptions>(options =>
                {
                    options.Authority = null; options.MetadataAddress = null;
                    options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(options.Configuration);
                });
                services.PostConfigure<JwtBearerOptions>(AtlasAuthentication.BearerScheme, options =>
                {
                    options.Configuration = new OpenIdConnectConfiguration();
                    options.TokenValidationParameters.ValidIssuer = Issuer;
                    options.TokenValidationParameters.ValidAudience = Audience;
                    options.TokenValidationParameters.IssuerSigningKey = SigningKey;
                });
            }));
        var http = host.CreateClient();
        var now = DateTime.UtcNow;
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience,
            [new Claim("sub", "t42-reader"), new Claim("scope", scope)], now.AddMinutes(-2), now.AddMinutes(10),
            new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http);
        return await ModelContextProtocol.Client.McpClient.CreateAsync(transport);
    }

    private static async Task Memberships(string connection, Guid project, Guid owner, Guid reader)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await Execute(db, "UPDATE identity.membership SET roles=ARRAY['ProjectAdmin','SecurityOwner','DataOwner'] WHERE project_id=@project AND principal_id=@owner", ("project", project), ("owner", owner));
        await Execute(db, "INSERT INTO identity.membership(project_id,principal_id,roles) VALUES(@project,@reader,ARRAY['ProjectReader'])", ("project", project), ("reader", reader));
    }

    private static async Task SeedKnowledge(string connection, ProjectSummary project, Guid actor, Guid allowed, Guid restricted, Guid relation, Guid source)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await Execute(db, "SET session_replication_role=replica");
        await Unit(db, project.ProjectId, project.CurrentBaselineId, allowed, "BusinessRule", "Published procurement term",
            JsonSerializer.Serialize(new { definition = "Approved public definition", restricted = RestrictedField }), actor);
        await Unit(db, project.ProjectId, project.CurrentBaselineId, restricted, "BusinessRule", RestrictedTitle,
            """{"definition":"T42-RESTRICTED-DEFINITION"}""", actor);
        await Unit(db, project.ProjectId, project.CurrentBaselineId, relation, "Relation", "Restricted relation",
            JsonSerializer.Serialize(new { from = new { unitId = allowed }, to = new { unitId = restricted } }), actor);
        var relationRevision = await Scalar<Guid>(connection, "SELECT revision_id FROM core.revision WHERE project_id=@project AND unit_id=@unit", project.ProjectId, relation);
        await Execute(db, "INSERT INTO core.relation_index(project_id,relation_revision_id,predicate,from_unit_id,to_unit_id) VALUES(@project,@revision,'references',@from,@to)",
            ("project", project.ProjectId), ("revision", relationRevision), ("from", allowed), ("to", restricted));
        await Unit(db, project.ProjectId, project.CurrentBaselineId, source, "Source", "Controlled source",
            """{"locator":{"path":"controlled.txt"}}""", actor);
    }

    private static async Task SeedOtherProject(string connection, ProjectSummary project, Guid actor, Guid unit, Guid revision)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await Execute(db, "SET session_replication_role=replica");
        await Execute(db, "INSERT INTO core.unit(project_id,unit_id,kind,owner_id,created_by) VALUES(@project,@unit,'BusinessRule',@actor,@actor)",
            ("project", project.ProjectId), ("unit", unit), ("actor", actor));
        await Execute(db, "INSERT INTO core.revision(project_id,revision_id,unit_id,rev_no,schema_type,schema_version,fact_kind,verification,payload,content_sha256,sealed,created_by) VALUES(@project,@revision,@unit,1,'BusinessRule','1.0','normative','verified',@payload::jsonb,@hash,true,@actor)",
            ("project", project.ProjectId), ("revision", revision), ("unit", unit), ("payload", """{"title":"Other project private title","body":{"definition":"Other project private body"}}"""), ("hash", new string('e', 64)), ("actor", actor));
        await Execute(db, "INSERT INTO gov.baseline_entry(project_id,baseline_id,unit_id,revision_id) VALUES(@project,@baseline,@unit,@revision)",
            ("project", project.ProjectId), ("baseline", project.CurrentBaselineId), ("unit", unit), ("revision", revision));
    }

    private static async Task Unit(NpgsqlConnection db, Guid project, Guid baseline, Guid unit, string kind, string title, string body, Guid actor)
    {
        var revision = Guid.NewGuid();
        await Execute(db, "INSERT INTO core.unit(project_id,unit_id,kind,owner_id,created_by) VALUES(@project,@unit,@kind,@actor,@actor)",
            ("project", project), ("unit", unit), ("kind", kind), ("actor", actor));
        await Execute(db, "INSERT INTO core.revision(project_id,revision_id,unit_id,rev_no,schema_type,schema_version,fact_kind,verification,payload,content_sha256,sealed,created_by) VALUES(@project,@revision,@unit,1,@kind,'1.0','normative','verified',@payload::jsonb,@hash,true,@actor)",
            ("project", project), ("revision", revision), ("unit", unit), ("kind", kind),
            ("payload", JsonSerializer.Serialize(new { title, body = JsonDocument.Parse(body).RootElement })), ("hash", new string('a', 64)), ("actor", actor));
        await Execute(db, "INSERT INTO gov.baseline_entry(project_id,baseline_id,unit_id,revision_id) VALUES(@project,@baseline,@unit,@revision)",
            ("project", project), ("baseline", baseline), ("unit", unit), ("revision", revision));
    }

    private static async Task Grant(string connection, Guid project, Guid unit, Guid principal, string action, string[] fields, string effect, Guid actor)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await using var command = new NpgsqlCommand("INSERT INTO access.grant(project_id,grant_id,unit_id,principal_id,action,field_paths,effect,created_by) VALUES(@project,gen_random_uuid(),@unit,@principal,@action,@fields,@effect,@actor)", db);
        command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("unit", unit); command.Parameters.AddWithValue("principal", principal);
        command.Parameters.AddWithValue("action", action); command.Parameters.AddWithValue("effect", effect); command.Parameters.AddWithValue("actor", actor);
        command.Parameters.Add(new NpgsqlParameter("fields", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = fields });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task LinkSource(string connection, Guid project, Guid revision, StoredBlob stored, Guid actor)
    {
        var blob = Guid.NewGuid();
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await Execute(db, "SET session_replication_role=replica");
        await Execute(db, "INSERT INTO intake.blob(project_id,blob_id,sha256,byte_size,storage_key,scan_status,created_by) VALUES(@project,@blob,@hash,@size,@key,'clean',@actor)",
            ("project", project), ("blob", blob), ("hash", stored.Sha256), ("size", stored.ByteSize), ("key", stored.StorageKey), ("actor", actor));
        await Execute(db, "INSERT INTO intake.source_blob_link(project_id,source_revision_id,blob_id) VALUES(@project,@revision,@blob)",
            ("project", project), ("revision", revision), ("blob", blob));
    }

    private static async Task<T> Scalar<T>(string connection, string sql, params object[] values)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = new NpgsqlCommand(sql, db);
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue(index == 0 ? "project" : "unit", values[index]);
        return (T)(await command.ExecuteScalarAsync())!;
    }
    private static async Task Execute(NpgsqlConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }
    private sealed class NoopQueue : IJobQueue
    {
        public Task<JobInfo> EnqueueAsync(Guid p, Guid r, EnqueueJobCommand c, CancellationToken t = default) => throw new NotSupportedException();
        public Task<JobInfo> GetAsync(Guid p, Guid r, Guid j, CancellationToken t = default) => throw new NotSupportedException();
        public Task<JobInfo> CancelAsync(Guid p, Guid r, Guid j, long v, string reason, string key, string req, string? trace, CancellationToken t = default) => throw new NotSupportedException();
        public Task<JobInfo> RetryAsync(Guid p, Guid r, Guid j, long v, string reason, string key, string req, string? trace, CancellationToken t = default) => throw new NotSupportedException();
    }
}
