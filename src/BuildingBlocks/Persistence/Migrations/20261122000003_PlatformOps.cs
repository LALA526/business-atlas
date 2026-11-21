using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BuildingBlocks.Persistence.Migrations;

[DbContext(typeof(AtlasDbContext))]
[Migration("20261122000003_PlatformOps")]
public sealed class PlatformOps : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE FUNCTION ops.worker_projects()
            RETURNS TABLE(project_id uuid)
            LANGUAGE sql STABLE SECURITY DEFINER
            SET search_path = pg_catalog, ops
            AS $$
              SELECT DISTINCT q.project_id FROM (
                SELECT j.project_id FROM ops.job j WHERE j.state IN ('queued','running','cancel_requested')
                UNION SELECT o.project_id FROM ops.outbox o WHERE o.state IN ('queued','running')
              ) q
            $$;
            REVOKE ALL ON FUNCTION ops.worker_projects() FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION ops.worker_projects() TO atlas_worker;
            GRANT SELECT, INSERT ON ops.inbox TO atlas_worker;
            GRANT INSERT ON ops.audit_event TO atlas_worker;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            REVOKE INSERT ON ops.audit_event FROM atlas_worker;
            REVOKE SELECT, INSERT ON ops.inbox FROM atlas_worker;
            DROP FUNCTION IF EXISTS ops.worker_projects();
            """);
    }
}
