using System.Text;
using System.Text.Json;
using AccessApplicability.Application;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using ImportEvidence.Application;
using ImportEvidence.Blob;
using ImportEvidence.Infrastructure;
using KnowledgeCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using ProjectIdentity.Infrastructure;

namespace Atlas.Integration.Tests;

public sealed class ImportEvidenceTests
{
    [PostgreSqlFact]
    public async Task AT_T12_Private_sources_scanning_hash_replay_and_atomic_unapproved_import()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t12_{Guid.NewGuid():N}"; var root = Path.Combine(Path.GetTempPath(), "atlas-t12-" + Guid.NewGuid().ToString("N"));
        await using (var connection = new NpgsqlConnection(administrator.ConnectionString)) { await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE {database}"; await command.ExecuteNonQueryAsync(); }
        administrator.Database = database;
        try
        {
            await MigrateAsync(administrator.ConnectionString);
            var runtime = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Options = "-c role=atlas_app" }.ConnectionString;
            var projects = new PostgresProjectIdentityStore(runtime); var access = new PostgresAccessApplicabilityStore(runtime); var policy = new PolicyService(access); var knowledge = new PostgresKnowledgeStore(runtime, access);
            var store = new PostgresImportEvidenceStore(runtime, new PrivateBlobStorage(root), new BuiltInContentScanner(), access, knowledge);
            var issuer = "https://identity.test/" + Guid.NewGuid().ToString("N"); var owner = await projects.ResolvePrincipalAsync(issuer, "owner", PrincipalKind.Human, CancellationToken.None); var outsider = await projects.ResolvePrincipalAsync(issuer, "outsider", PrincipalKind.Human, CancellationToken.None);
            var first = await projects.CreateProjectAsync(owner, new("First", "first", "t12-first-0001"), "r1", null, CancellationToken.None); var second = await projects.CreateProjectAsync(owner, new("Second", "second", "t12-second-0001"), "r2", null, CancellationToken.None);
            var bytes = Encoding.UTF8.GetBytes("Business Atlas automated T12 source"); var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            var upload = await store.UploadSourceAsync(owner, first.ProjectId, "self.txt", "text/plain", "document", hash, new MemoryStream(bytes), "t12-source-0001", "r3", null, CancellationToken.None);
            Assert.Equal("ready", upload.State); Assert.Equal(hash, upload.Sha256);
            var uploadRow = await RowAsync(administrator.ConnectionString, "SELECT cs.change_set_id,r.revision_id FROM core.unit u JOIN core.revision r ON r.project_id=u.project_id AND r.unit_id=u.unit_id JOIN gov.change_item ci ON ci.project_id=u.project_id AND ci.unit_id=u.unit_id JOIN gov.change_set cs ON cs.project_id=ci.project_id AND cs.change_set_id=ci.change_set_id WHERE u.project_id=@project AND u.unit_id=@unit", first.ProjectId, upload.UploadId);
            var read = await policy.CreateReadContextAsync(owner.PrincipalId, first.ProjectId, new(ReadMode.Draft, ReadPurpose.Change, null, uploadRow.ChangeSet, null), CancellationToken.None);
            var source = await store.GetSourceAsync(read, upload.UploadId, CancellationToken.None); Assert.Equal("unverified", source.Verification);
            using (var content = await store.ReadSourceContentAsync(read, upload.UploadId, CancellationToken.None)) { using var memory = new MemoryStream(); await content.Content.CopyToAsync(memory); Assert.Equal(bytes, memory.ToArray()); Assert.Equal(hash, content.Sha256); }
            var secondRead = await policy.CreateReadContextAsync(owner.PrincipalId, second.ProjectId, new(ReadMode.Published, ReadPurpose.Understand, null, null, null), CancellationToken.None);
            Assert.Equal(404, (await Assert.ThrowsAsync<ProjectIdentityException>(() => store.ReadSourceContentAsync(secondRead, upload.UploadId, CancellationToken.None))).StatusCode);
            Assert.Equal(404, (await Assert.ThrowsAsync<ProjectIdentityException>(() => policy.CreateReadContextAsync(outsider.PrincipalId, first.ProjectId, new(ReadMode.Draft, ReadPurpose.Change, null, uploadRow.ChangeSet, null), CancellationToken.None))).StatusCode);
            var rejected = await store.UploadSourceAsync(owner, first.ProjectId, "bad.exe", "application/octet-stream", "document", null, new MemoryStream(Encoding.UTF8.GetBytes("MZ executable")), "t12-source-bad1", "r4", null, CancellationToken.None); Assert.Equal("rejected", rejected.State);

            var packageBytes = Package(); var imported = await store.UploadPackageAsync(owner, first.ProjectId, "self.akp.json", "application/json", first.CurrentBaselineId, new MemoryStream(packageBytes), "t12-import-0001", "r5", null, CancellationToken.None);
            var replay = await store.UploadPackageAsync(owner, first.ProjectId, "self.akp.json", "application/json", first.CurrentBaselineId, new MemoryStream(packageBytes), "t12-import-0002", "r6", null, CancellationToken.None);
            Assert.Equal(imported.ImportId, replay.ImportId); Assert.All(imported.Entries, e => Assert.Equal("new", e.Status));
            var staged = await store.StageAsync(owner, first.ProjectId, imported.ImportId, null, imported.Version, "t12-stage-0001", "r7", null, CancellationToken.None);
            var batch = await store.PreviewAsync(owner.PrincipalId, first.ProjectId, imported.ImportId, CancellationToken.None); Assert.Equal("staged", batch.State); Assert.Equal(staged.ChangeSetId, batch.ChangeSetId);
            await using var verification = new NpgsqlConnection(administrator.ConnectionString); await verification.OpenAsync();
            Assert.Equal(0, await ScalarAsync(verification, "SELECT count(*) FROM core.revision WHERE project_id=@project AND verification='verified'", first.ProjectId));
            Assert.Equal(0, await ScalarAsync(verification, "SELECT count(*) FROM gov.baseline_entry WHERE project_id=@project", first.ProjectId));
            Assert.Equal(2, await ScalarAsync(verification, "SELECT count(*) FROM gov.change_item WHERE project_id=@project AND change_set_id=@changeSet", first.ProjectId, staged.ChangeSetId));
            Assert.Equal(1, await ScalarAsync(verification, "SELECT count(*) FROM core.evidence_link WHERE project_id=@project", first.ProjectId));
            var changed = packageBytes.Select((b, i) => i == packageBytes.Length - 2 ? (byte)'2' : b).ToArray();
            await Assert.ThrowsAsync<ProjectIdentityException>(() => store.UploadPackageAsync(owner, first.ProjectId, "self.akp.json", "application/json", first.CurrentBaselineId, new MemoryStream(changed), "t12-import-0003", "r8", null, CancellationToken.None));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres"; await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"; await command.ExecuteNonQueryAsync(); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static byte[] Package() => JsonSerializer.SerializeToUtf8Bytes(new { manifest = new { formatVersion = "AKP/0.1", packageId = "AKP-T12", sourceProjectId = "BusinessAtlas-Self", projectName = "Business Atlas", generatedAt = DateTimeOffset.UtcNow, producer = new { kind = "tool", identity = "AT-T12 automated test" }, sourceRevision = "test", importPurpose = "T12 integration verification", declaredScope = new[] { "test" } }, entities = new[] { new { stableExternalId = "T12-DOC", typeId = "Document", name = "T12 candidate", properties = new { }, categoryAssignments = Array.Empty<string>(), applicableContext = new { }, verificationStatus = "observed", sourceIds = new[] { "T12-SOURCE" } } }, assertions = Array.Empty<object>(), businessStructures = new { }, technicalStructures = new { }, sources = new[] { new { sourceId = "T12-SOURCE", sourceType = "document", path = "docs/t12.txt", revision = "v1", captureMethod = "automated test" } }, unknownsAndConflicts = Array.Empty<object>(), importProvenance = new { agentRunId = "AT-T12", toolExecutionRefs = Array.Empty<string>(), sourceFileRevisions = new { }, extractedAt = DateTimeOffset.UtcNow, extractionStrategy = "direct test package" } });
    private static async Task MigrateAsync(string connection) { var options = new DbContextOptionsBuilder<BuildingBlocks.Persistence.AtlasDbContext>().UseNpgsql(connection).Options; await using var context = new BuildingBlocks.Persistence.AtlasDbContext(options); await context.Database.MigrateAsync(); }
    private static async Task<int> ScalarAsync(NpgsqlConnection connection, string sql, Guid project, Guid? changeSet = null) { await using var command = connection.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("project", project); if (changeSet.HasValue) command.Parameters.AddWithValue("changeSet", changeSet); return Convert.ToInt32(await command.ExecuteScalarAsync()); }
    private static async Task<(Guid ChangeSet, Guid Revision)> RowAsync(string connection, string sql, Guid project, Guid unit) { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("project", project); command.Parameters.AddWithValue("unit", unit); await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); return (reader.GetGuid(0), reader.GetGuid(1)); }
}
