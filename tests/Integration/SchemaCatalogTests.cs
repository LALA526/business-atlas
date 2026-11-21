using System.Text.Json;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectIdentity.Contracts;
using ProjectIdentity.Infrastructure;
using SchemaCatalog.Contracts;
using SchemaCatalog.Infrastructure;

namespace Atlas.Integration.Tests;

public sealed class SchemaCatalogTests
{
    [PostgreSqlFact]
    public async Task AT_T07_Registry_catalog_draft_proposal_and_conflicts_work_on_PostgreSql()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t07_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(administrator.ConnectionString))
        { await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE {database}"; await command.ExecuteNonQueryAsync(); }
        administrator.Database = database;
        try
        {
            var options = new DbContextOptionsBuilder<BuildingBlocks.Persistence.AtlasDbContext>().UseNpgsql(administrator.ConnectionString).Options;
            await using (var context = new BuildingBlocks.Persistence.AtlasDbContext(options)) await context.Database.MigrateAsync();
            var runtime = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Options = "-c role=atlas_app" }.ConnectionString;
            var identities = new PostgresProjectIdentityStore(runtime);
            var policies = new PostgresAccessApplicabilityStore(runtime);
            var store = new PostgresSchemaCatalogStore(runtime, policies);
            var owner = await identities.ResolvePrincipalAsync($"https://identity.test/{Guid.NewGuid():N}", "owner", PrincipalKind.Human, CancellationToken.None);
            var project = await identities.CreateProjectAsync(owner, new CreateProjectCommand("Registry self test", "registry-self-test", "create-registry-0001"), "request-1", null, CancellationToken.None);
            var changeSetId = Guid.NewGuid();
            await using (var connection = new NpgsqlConnection(administrator.ConnectionString))
            {
                await connection.OpenAsync(); await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO gov.change_set(project_id,change_set_id,base_baseline_id,title,state,created_by) VALUES (@project,@changeSet,@baseline,'Registry test','draft',@owner)";
                command.Parameters.AddWithValue("project", project.ProjectId); command.Parameters.AddWithValue("changeSet", changeSetId);
                command.Parameters.AddWithValue("baseline", project.CurrentBaselineId); command.Parameters.AddWithValue("owner", owner.PrincipalId); await command.ExecuteNonQueryAsync();
            }

            var published = await policies.CreateReadContextAsync(owner.PrincipalId, project.ProjectId, new(ReadMode.Published, ReadPurpose.Understand, null, null, null), CancellationToken.None);
            var types = await store.ListTypesAsync(published, null, 100, CancellationToken.None);
            Assert.Contains(types.Items, item => item.Kind == "TypeDefinition" && item.Title == "BusinessRule");
            var catalog = await store.GetCatalogAsync(published, CancellationToken.None);
            Assert.Equal(5, catalog.Count);
            Assert.All(catalog, node => Assert.NotEmpty(node.Children!));

            var proposal = new RegistryProposal(changeSetId, new RevisionInput(null, "Warranty package", "TypeDefinition", "1.0.0", "normative", "unverified",
                Element(new { }), Element(new { typeKey = "WarrantyPackage", version = "1.0.0", profile = "standard", schema = new { type = "object", properties = new { code = new { type = "string" } } }, category = "business" }),
                Element(Array.Empty<object>()), Element(Array.Empty<object>()), Element(Array.Empty<object>()), Element(new[] { "business.warranty" })));
            var created = await store.ProposeTypeAsync(owner, project.ProjectId, proposal, project.Version, "registry-type-0001", "request-2", null, CancellationToken.None);
            Assert.Equal(1, created.Revision.RevNo);
            var replay = await store.ProposeTypeAsync(owner, project.ProjectId, proposal, project.Version, "registry-type-0001", "request-3", null, CancellationToken.None);
            Assert.Equal(created.Revision.RevisionId, replay.Revision.RevisionId);

            var draft = await policies.CreateReadContextAsync(owner.PrincipalId, project.ProjectId, new(ReadMode.Draft, ReadPurpose.Change, null, changeSetId, null), CancellationToken.None);
            Assert.Contains((await store.ListTypesAsync(draft, null, 100, CancellationToken.None)).Items, item => item.RevisionId == created.Revision.RevisionId);
            var draftCatalog = await store.GetCatalogAsync(draft, CancellationToken.None);
            Assert.Contains(draftCatalog.Single(node => node.Key == "business").Children!, node => node.Key == "type:WarrantyPackage");

            var duplicatePredicate = new RegistryProposal(changeSetId, new RevisionInput(null, "Duplicate mapping", "PredicateDefinition", "1.0.0", "normative", "unverified",
                Element(new { }), Element(new { predicate = "semanticMapping", fromKinds = new[] { "BusinessAttribute", "DomainConcept", "BusinessObject" }, toKinds = new[] { "DataField", "DataEntity", "Api", "Operation", "Implementation" }, meaning = "业务到实现语义映射", impactDirections = new[] { "forward" } }),
                Element(Array.Empty<object>()), Element(Array.Empty<object>()), Element(Array.Empty<object>()), Element(new[] { "knowledge.registry" })));
            var conflict = await Assert.ThrowsAsync<ProjectIdentity.Application.ProjectIdentityException>(() => store.ProposePredicateAsync(owner, project.ProjectId, duplicatePredicate,
                created.ProjectVersion, "registry-pred-0001", "request-4", null, CancellationToken.None));
            Assert.Equal("CONFLICT", conflict.Code);
            Assert.Contains("mapsTo", conflict.Message);

            await using var verification = new NpgsqlConnection(administrator.ConnectionString); await verification.OpenAsync();
            await using (var count = verification.CreateCommand())
            {
                count.CommandText = "SELECT count(*) FROM gov.baseline_entry WHERE project_id=@project AND unit_id=@unit";
                count.Parameters.AddWithValue("project", project.ProjectId); count.Parameters.AddWithValue("unit", created.Revision.UnitId);
                Assert.Equal(0L, await count.ExecuteScalarAsync());
            }

            // Simulate the separate human-governed publisher completing this draft, then verify the exact sealed baseline remains readable.
            var historicalBaseline = Guid.NewGuid();
            await using (var publish = verification.CreateCommand())
            {
                publish.CommandText = """
                    UPDATE core.revision SET sealed=true WHERE project_id=@project AND revision_id=@revision;
                    INSERT INTO gov.baseline(project_id,baseline_id,baseline_no,parent_baseline_id,change_set_id,registry_version,digest,sealed,created_by)
                    VALUES (@project,@baseline,1,@parent,@changeSet,'atlas-registry/1.0',@digest,false,@owner);
                    INSERT INTO gov.baseline_entry(project_id,baseline_id,unit_id,revision_id) VALUES (@project,@baseline,@unit,@revision);
                    UPDATE gov.baseline SET sealed=true WHERE project_id=@project AND baseline_id=@baseline;
                    INSERT INTO access.grant(project_id,grant_id,unit_id,principal_id,action,field_paths,effect,created_by)
                    VALUES (@project,@grant,@unit,@owner,'read_history',ARRAY[]::text[],'allow',@owner);
                    """;
                publish.Parameters.AddWithValue("project", project.ProjectId); publish.Parameters.AddWithValue("revision", created.Revision.RevisionId);
                publish.Parameters.AddWithValue("baseline", historicalBaseline); publish.Parameters.AddWithValue("parent", project.CurrentBaselineId);
                publish.Parameters.AddWithValue("changeSet", changeSetId); publish.Parameters.AddWithValue("digest", new string('d', 64));
                publish.Parameters.AddWithValue("owner", owner.PrincipalId); publish.Parameters.AddWithValue("unit", created.Revision.UnitId);
                publish.Parameters.AddWithValue("grant", Guid.NewGuid()); await publish.ExecuteNonQueryAsync();
            }
            var historical = await policies.CreateReadContextAsync(owner.PrincipalId, project.ProjectId,
                new(ReadMode.Historical, ReadPurpose.Review, historicalBaseline, null, null), CancellationToken.None);
            var historicalTypes = await store.ListTypesAsync(historical, null, 100, CancellationToken.None);
            Assert.Contains(historicalTypes.Items, item => item.RevisionId == created.Revision.RevisionId && item.Body.GetProperty("version").GetString() == "1.0.0");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"; await command.ExecuteNonQueryAsync();
        }
    }

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToDocument(value).RootElement.Clone();
}
