using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BuildingBlocks.Persistence.Migrations;

[DbContext(typeof(AtlasDbContext))]
[Migration("20261122000001_RuntimeRoles")]
public sealed class RuntimeRoles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'atlas_app') THEN
                CREATE ROLE atlas_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS INHERIT;
              END IF;
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'atlas_worker') THEN
                CREATE ROLE atlas_worker NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS INHERIT;
              END IF;
            END
            $$;

            GRANT USAGE ON SCHEMA identity, access, core, gov, intake, collab, delivery, assistant, agent, ops, read_model TO atlas_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity, access, core, gov, intake, collab, delivery, assistant, agent, ops, read_model TO atlas_app;
            REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON identity.principal FROM atlas_app;
            REVOKE ALL ON public."__EFMigrationsHistory" FROM atlas_app;

            GRANT USAGE ON SCHEMA access, ops TO atlas_worker;
            GRANT SELECT, UPDATE ON ops.job, ops.outbox TO atlas_worker;
            REVOKE ALL ON public."__EFMigrationsHistory" FROM atlas_worker;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP ROLE IF EXISTS atlas_worker;
            DROP ROLE IF EXISTS atlas_app;
            """);
    }
}
