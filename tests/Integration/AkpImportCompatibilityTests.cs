using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using ImportEvidence.Application;
using ImportEvidence.Blob;
using ImportEvidence.Contracts;
using ImportEvidence.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectIdentity.Infrastructure;

namespace Atlas.Integration.Tests;

public sealed class AkpImportCompatibilityTests
{
    [PostgreSqlFact]
    public async Task AT_T16_Legacy_real_sample_cancel_conflict_hash_and_atomic_failure()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t16_{Guid.NewGuid():N}"; var root = Path.Combine(Path.GetTempPath(), "atlas-t16-" + Guid.NewGuid().ToString("N"));
        await using (var connection = new NpgsqlConnection(administrator.ConnectionString)) { await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE {database}"; await command.ExecuteNonQueryAsync(); }
        administrator.Database = database;
        try
        {
            var options = new DbContextOptionsBuilder<BuildingBlocks.Persistence.AtlasDbContext>().UseNpgsql(administrator.ConnectionString).Options;
            await using (var context = new BuildingBlocks.Persistence.AtlasDbContext(options)) await context.Database.MigrateAsync();
            var runtime = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Options = "-c role=atlas_app" }.ConnectionString;
            var projects = new PostgresProjectIdentityStore(runtime); var access = new AccessApplicability.Infrastructure.PostgresAccessApplicabilityStore(runtime);
            var knowledge = new KnowledgeCore.Infrastructure.PostgresKnowledgeStore(runtime, access);
            var store = new PostgresImportEvidenceStore(runtime, new PrivateBlobStorage(root), new BuiltInContentScanner(), access, knowledge);
            var issuer = "https://identity.test/" + Guid.NewGuid().ToString("N");
            var owner = await projects.ResolvePrincipalAsync(issuer, "owner", ProjectIdentity.Contracts.PrincipalKind.Human, CancellationToken.None);
            var project = await projects.CreateProjectAsync(owner, new("Business Atlas Self", "self-t16", "t16-project-0001"), "r1", null, CancellationToken.None);

            var smoke = ReadArchive("./fixtures/import-smoke.akp.json");
            var cancelled = await store.UploadPackageAsync(owner, project.ProjectId, "smoke.akp.json", "application/json", project.CurrentBaselineId, new MemoryStream(smoke), "t16-cancel-0001", "r2", null, CancellationToken.None);
            var discarded = await store.DiscardAsync(owner, project.ProjectId, cancelled.ImportId, "Automated cancellation check", cancelled.Version, "t16-discard-001", "r3", null, CancellationToken.None);
            Assert.Equal("discarded", discarded.State);
            Assert.Equal(0, await ScalarAsync(administrator.ConnectionString, "SELECT count(*) FROM gov.change_set WHERE project_id=@project", project.ProjectId));

            var conflictBytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(smoke).Replace("V9.4 import smoke", "V9.4 altered smoke", StringComparison.Ordinal));
            var hashConflict = await Assert.ThrowsAsync<ProjectIdentity.Application.ProjectIdentityException>(() => store.UploadPackageAsync(owner, project.ProjectId, "smoke.akp.json", "application/json", project.CurrentBaselineId, new MemoryStream(conflictBytes), "t16-hash-00001", "r4", null, CancellationToken.None));
            Assert.Equal(409, hashConflict.StatusCode);

            var failureBytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(smoke).Replace("AKP-SMOKE-V94", "AKP-FAILURE-T16", StringComparison.Ordinal));
            var failure = await store.UploadPackageAsync(owner, project.ProjectId, "failure.akp.json", "application/json", project.CurrentBaselineId, new MemoryStream(failureBytes), "t16-failure-001", "r5", null, CancellationToken.None);
            var source = failure.Entries.Single(e => e.Kind == "Source");
            failure = await store.DecideAsync(owner, project.ProjectId, failure.ImportId, new(new[] { new ImportDecisionInput(source.EntryId, "reject", "Force unresolved evidence for atomicity test") }), failure.Version, "t16-fdecide-001", "r6", null, CancellationToken.None);
            await Assert.ThrowsAsync<ProjectIdentity.Application.ProjectIdentityException>(() => store.StageAsync(owner, project.ProjectId, failure.ImportId, null, failure.Version, "t16-fstage-0001", "r7", null, CancellationToken.None));
            Assert.Equal(0, await ScalarAsync(administrator.ConnectionString, "SELECT count(*) FROM gov.change_set WHERE project_id=@project", project.ProjectId));
            Assert.Equal(0, await ScalarAsync(administrator.ConnectionString, "SELECT count(*) FROM core.unit WHERE project_id=@project", project.ProjectId));

            var legacy = ReadArchive("./packages/business-atlas-self.akp.json");
            var imported = await store.UploadPackageAsync(owner, project.ProjectId, "self.akp.json", "application/json", project.CurrentBaselineId, new MemoryStream(legacy), "t16-self-00001", "r8", null, CancellationToken.None);
            Assert.Equal(166, imported.Entries.Count);
            Assert.Equal(6, imported.Unknowns.Count);
            var replay = await store.UploadPackageAsync(owner, project.ProjectId, "self.akp.json", "application/json", project.CurrentBaselineId, new MemoryStream(legacy), "t16-self-replay1", "r9", null, CancellationToken.None);
            Assert.Equal(imported.ImportId, replay.ImportId);
            var staged = await store.StageAsync(owner, project.ProjectId, imported.ImportId, null, imported.Version, "t16-self-stage01", "r10", null, CancellationToken.None);
            Assert.Equal(166, await ScalarAsync(administrator.ConnectionString, "SELECT count(*) FROM gov.change_item WHERE project_id=@project AND change_set_id=@changeSet", project.ProjectId, staged.ChangeSetId));
            Assert.Equal(0, await ScalarAsync(administrator.ConnectionString, "SELECT count(*) FROM core.revision WHERE project_id=@project AND verification='verified'", project.ProjectId));
            Assert.Equal(0, await ScalarAsync(administrator.ConnectionString, "SELECT count(*) FROM gov.baseline_entry WHERE project_id=@project", project.ProjectId));
            Assert.Equal(1, await ScalarAsync(administrator.ConnectionString, "SELECT count(*) FROM ops.outbox WHERE project_id=@project AND event_type='import.staged'", project.ProjectId));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres"; await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"; await command.ExecuteNonQueryAsync(); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static byte[] ReadArchive(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BusinessAtlas.slnx"))) directory = directory.Parent;
        using var file = File.OpenRead(Path.Combine(directory!.FullName, "docs/ui-reference/working-v9.4.tar.gz")); using var gzip = new GZipStream(file, CompressionMode.Decompress); using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry) if (entry.Name == path) { using var output = new MemoryStream(); entry.DataStream!.CopyTo(output); return output.ToArray(); }
        throw new FileNotFoundException(path);
    }
    private static async Task<int> ScalarAsync(string connectionString, string sql, Guid project, Guid? changeSet = null)
    { await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("project", project); if (changeSet.HasValue) command.Parameters.AddWithValue("changeSet", changeSet); return Convert.ToInt32(await command.ExecuteScalarAsync()); }
}
