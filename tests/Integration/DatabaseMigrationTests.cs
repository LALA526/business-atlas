using System.Data.Common;
using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Atlas.Integration.Tests;

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING")))
        {
            Skip = "Set ATLAS_TEST_CONNECTIONSTRING to a PostgreSQL 17 administrator connection to run AT-T03.";
        }
    }
}

public sealed class DatabaseMigrationTests
{
    private static readonly Guid Principal = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ProjectA = Guid.Parse("20000000-0000-0000-0000-00000000000a");
    private static readonly Guid ProjectB = Guid.Parse("20000000-0000-0000-0000-00000000000b");
    private static readonly Guid UnitA = Guid.Parse("30000000-0000-0000-0000-00000000000a");
    private static readonly Guid UnitB = Guid.Parse("30000000-0000-0000-0000-00000000000b");
    private static readonly Guid RevisionA = Guid.Parse("40000000-0000-0000-0000-00000000000a");

    [PostgreSqlFact]
    public async Task AT_T03_Empty_upgrade_FK_RLS_and_sealing_negative_cases()
    {
        var administrator = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t03_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(administrator.ConnectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, $"CREATE DATABASE {database}");
        }

        administrator.Database = database;
        try
        {
            await MigrateAndVerifyAsync(administrator.ConnectionString);
            await SeedTwoProjectsAsync(administrator.ConnectionString);
            await VerifyForeignKeysAndSealingAsync(administrator.ConnectionString);
            await VerifyRuntimeRoleAndRlsAsync(administrator.ConnectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString);
            await connection.OpenAsync();
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
    }

    private static async Task MigrateAndVerifyAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AtlasDbContext>().UseNpgsql(connectionString).Options;
        await using var context = new AtlasDbContext(options);
        await context.Database.MigrateAsync();
        await context.Database.MigrateAsync();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var history = await ScalarAsync<string>(connection,
            "SELECT string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\") FROM public.\"__EFMigrationsHistory\"");
        Assert.Equal("20261122000000_InitialAtlas,20261122000001_RuntimeRoles,20261122000002_ProjectIdentity", history);

        var unprotected = await ScalarAsync<long>(connection, """
            SELECT count(*) FROM pg_class c
            JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE c.relkind='r' AND n.nspname IN ('identity','access','core','gov','intake','collab','delivery','assistant','agent','ops','read_model')
              AND c.relname <> 'principal' AND NOT (c.relrowsecurity AND c.relforcerowsecurity)
            """);
        Assert.Equal(0, unprotected);

        var unsafeRoles = await ScalarAsync<long>(connection,
            "SELECT count(*) FROM pg_roles WHERE rolname IN ('atlas_app','atlas_worker') AND (rolsuper OR rolbypassrls OR rolcanlogin OR rolcreatedb OR rolcreaterole)");
        Assert.Equal(0, unsafeRoles);
        Assert.False(await ScalarAsync<bool>(connection, "SELECT has_schema_privilege('atlas_app','core','CREATE')"));
        Assert.False(await ScalarAsync<bool>(connection, "SELECT has_table_privilege('atlas_app','core.unit','TRUNCATE')"));
        Assert.False(await ScalarAsync<bool>(connection, "SELECT has_table_privilege('atlas_worker','core.unit','SELECT')"));
    }

    private static async Task SeedTwoProjectsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection,
            "INSERT INTO identity.principal(principal_id,issuer,subject,kind) VALUES (@id,'atlas-test','owner','human')",
            new NpgsqlParameter("id", Principal));
        foreach (var project in new[] { ProjectA, ProjectB })
        {
            await SetContextAsync(connection, project);
            await ExecuteAsync(connection,
                "INSERT INTO identity.project(project_id,slug,display_name,owner_id) VALUES (@project,@slug,@name,@owner)",
                new NpgsqlParameter("project", project),
                new NpgsqlParameter("slug", project == ProjectA ? "project-a" : "project-b"),
                new NpgsqlParameter("name", project == ProjectA ? "Project A" : "Project B"),
                new NpgsqlParameter("owner", Principal));
            await ExecuteAsync(connection,
                "INSERT INTO identity.membership(project_id,principal_id,roles) VALUES (@project,@principal,ARRAY['owner'])",
                new NpgsqlParameter("project", project), new NpgsqlParameter("principal", Principal));
            await ExecuteAsync(connection,
                "INSERT INTO core.unit(project_id,unit_id,kind,owner_id,created_by) VALUES (@project,@unit,'Requirement',@owner,@owner)",
                new NpgsqlParameter("project", project),
                new NpgsqlParameter("unit", project == ProjectA ? UnitA : UnitB),
                new NpgsqlParameter("owner", Principal));
        }
    }

    private static async Task VerifyForeignKeysAndSealingAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SetContextAsync(connection, ProjectA);

        var crossProjectFk = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
            RevisionInsertSql + " VALUES (@project,@revision,@unit,1,'Requirement','v1','normative','unverified','{}','{}',@hash,false,@owner)",
            new NpgsqlParameter("project", ProjectA), new NpgsqlParameter("revision", Guid.NewGuid()),
            new NpgsqlParameter("unit", UnitB), new NpgsqlParameter("hash", new string('a', 64)),
            new NpgsqlParameter("owner", Principal)));
        Assert.Equal("23503", crossProjectFk.SqlState);

        await ExecuteAsync(connection,
            RevisionInsertSql + " VALUES (@project,@revision,@unit,1,'Requirement','v1','normative','unverified','{}','{}',@hash,false,@owner)",
            new NpgsqlParameter("project", ProjectA), new NpgsqlParameter("revision", RevisionA),
            new NpgsqlParameter("unit", UnitA), new NpgsqlParameter("hash", new string('a', 64)),
            new NpgsqlParameter("owner", Principal));
        await ExecuteAsync(connection,
            "UPDATE core.revision SET sealed=true WHERE project_id=@project AND revision_id=@revision",
            new NpgsqlParameter("project", ProjectA), new NpgsqlParameter("revision", RevisionA));

        var changedSeal = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
            "UPDATE core.revision SET payload='{\"changed\":true}' WHERE project_id=@project AND revision_id=@revision",
            new NpgsqlParameter("project", ProjectA), new NpgsqlParameter("revision", RevisionA)));
        Assert.Equal("55000", changedSeal.SqlState);
        var deleteSeal = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
            "DELETE FROM core.revision WHERE project_id=@project AND revision_id=@revision",
            new NpgsqlParameter("project", ProjectA), new NpgsqlParameter("revision", RevisionA)));
        Assert.Equal("55000", deleteSeal.SqlState);
        var lateReference = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
            "INSERT INTO core.revision_ref(project_id,source_revision_id,ref_key,ref_kind,target_unit_id) VALUES (@project,@revision,'late','semantic',@unit)",
            new NpgsqlParameter("project", ProjectA), new NpgsqlParameter("revision", RevisionA),
            new NpgsqlParameter("unit", UnitA)));
        Assert.Equal("55000", lateReference.SqlState);

        var unsealedRevision = Guid.NewGuid();
        var baseline = Guid.NewGuid();
        await ExecuteAsync(connection,
            RevisionInsertSql + " VALUES (@project,@revision,@unit,2,'Requirement','v1','normative','unverified','{}','{}',@hash,false,@owner)",
            new NpgsqlParameter("project", ProjectA), new NpgsqlParameter("revision", unsealedRevision),
            new NpgsqlParameter("unit", UnitA), new NpgsqlParameter("hash", new string('b', 64)),
            new NpgsqlParameter("owner", Principal));
        await ExecuteAsync(connection,
            "INSERT INTO gov.baseline(project_id,baseline_id,baseline_no,registry_version,digest,created_by) VALUES (@project,@baseline,0,'atlas-registry/1.0',@digest,@owner)",
            new NpgsqlParameter("project", ProjectA), new NpgsqlParameter("baseline", baseline),
            new NpgsqlParameter("digest", new string('c', 64)), new NpgsqlParameter("owner", Principal));
        var unsealedEntry = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
            "INSERT INTO gov.baseline_entry(project_id,baseline_id,unit_id,revision_id) VALUES (@project,@baseline,@unit,@revision)",
            new NpgsqlParameter("project", ProjectA), new NpgsqlParameter("baseline", baseline),
            new NpgsqlParameter("unit", UnitA), new NpgsqlParameter("revision", unsealedRevision)));
        Assert.Equal("55000", unsealedEntry.SqlState);

        await ExecuteAsync(connection,
            "UPDATE core.revision SET sealed=true WHERE project_id=@project AND revision_id=@revision",
            new NpgsqlParameter("project", ProjectA), new NpgsqlParameter("revision", unsealedRevision));
        await ExecuteAsync(connection,
            "INSERT INTO gov.baseline_entry(project_id,baseline_id,unit_id,revision_id) VALUES (@project,@baseline,@unit,@revision)",
            new NpgsqlParameter("project", ProjectA), new NpgsqlParameter("baseline", baseline),
            new NpgsqlParameter("unit", UnitA), new NpgsqlParameter("revision", unsealedRevision));
        await ExecuteAsync(connection,
            "UPDATE gov.baseline SET sealed=true WHERE project_id=@project AND baseline_id=@baseline",
            new NpgsqlParameter("project", ProjectA), new NpgsqlParameter("baseline", baseline));
        var lateEntry = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
            "INSERT INTO gov.baseline_entry(project_id,baseline_id,unit_id,revision_id) VALUES (@project,@baseline,@unit,@revision)",
            new NpgsqlParameter("project", ProjectA), new NpgsqlParameter("baseline", baseline),
            new NpgsqlParameter("unit", UnitA), new NpgsqlParameter("revision", RevisionA)));
        Assert.Equal("55000", lateEntry.SqlState);
    }

    private static async Task VerifyRuntimeRoleAndRlsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "SET ROLE atlas_app");

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetContextAsync(connection, ProjectA, transaction);
            Assert.Equal(UnitA, await ScalarAsync<Guid>(connection,
                "SELECT unit_id FROM core.unit", transaction));
            var crossProjectWrite = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, transaction,
                "INSERT INTO core.unit(project_id,unit_id,kind,owner_id,created_by) VALUES (@project,@unit,'Requirement',@owner,@owner)",
                new NpgsqlParameter("project", ProjectB), new NpgsqlParameter("unit", Guid.NewGuid()),
                new NpgsqlParameter("owner", Principal)));
            Assert.Equal("42501", crossProjectWrite.SqlState);
            await transaction.RollbackAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.Equal(0, await ScalarAsync<long>(connection, "SELECT count(*) FROM core.unit", transaction));
            await SetContextAsync(connection, ProjectA, transaction);
            Assert.Equal(1, await ScalarAsync<long>(connection, "SELECT count(*) FROM core.unit", transaction));
            await transaction.CommitAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            // SET LOCAL was removed with the previous transaction.
            Assert.Equal(0, await ScalarAsync<long>(connection, "SELECT count(*) FROM core.unit", transaction));
            await transaction.RollbackAsync();
        }

        var ddl = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
            "CREATE TABLE core.forbidden_application_ddl(id uuid)"));
        Assert.Equal("42501", ddl.SqlState);
        var truncate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection,
            "TRUNCATE core.unit"));
        Assert.Equal("42501", truncate.SqlState);
    }

    private const string RevisionInsertSql = """
        INSERT INTO core.revision(project_id,revision_id,unit_id,rev_no,schema_type,schema_version,fact_kind,verification,payload,applicability,content_sha256,sealed,created_by)
        """;

    private static async Task SetContextAsync(DbConnection connection, Guid project, DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT set_config('atlas.project_id',@project,@local),set_config('atlas.subject_id',@subject,@local)";
        command.Transaction = transaction;
        command.Parameters.Add(new NpgsqlParameter("project", project.ToString()));
        command.Parameters.Add(new NpgsqlParameter("subject", Principal.ToString()));
        command.Parameters.Add(new NpgsqlParameter("local", transaction is not null));
        await command.ExecuteNonQueryAsync();
    }

    private static Task<int> ExecuteAsync(DbConnection connection, string sql, params NpgsqlParameter[] parameters) =>
        ExecuteAsync(connection, null, sql, parameters);

    private static async Task<int> ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(DbConnection connection, string sql, DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        var result = await command.ExecuteScalarAsync();
        return (T)result!;
    }
}
