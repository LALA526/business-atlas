using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BuildingBlocks.Persistence.Migrations;

[DbContext(typeof(AtlasDbContext))]
[Migration("20261122000004_ReadSearchExplain")]
public sealed class ReadSearchExplain : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE read_model.export (
          project_id uuid NOT NULL REFERENCES identity.project(project_id),
          upload_id uuid NOT NULL DEFAULT gen_random_uuid(),
          requester_id uuid NOT NULL REFERENCES identity.principal(principal_id),
          file_name text NOT NULL,
          content_type text NOT NULL,
          content bytea NOT NULL,
          sha256 text NOT NULL CHECK(sha256 ~ '^[0-9a-f]{64}$'),
          included jsonb NOT NULL CHECK(jsonb_typeof(included)='array'),
          context jsonb NOT NULL CHECK(jsonb_typeof(context)='object'),
          created_at timestamptz NOT NULL DEFAULT now(),
          PRIMARY KEY(project_id,upload_id)
        );
        ALTER TABLE read_model.export ENABLE ROW LEVEL SECURITY;
        ALTER TABLE read_model.export FORCE ROW LEVEL SECURITY;
        CREATE POLICY project_boundary ON read_model.export USING(project_id=access.current_project()) WITH CHECK(project_id=access.current_project());
        GRANT SELECT,INSERT ON read_model.export TO atlas_app;
        GRANT USAGE ON SCHEMA identity,core,gov,read_model TO atlas_worker;
        GRANT SELECT ON identity.project,identity.membership,core.unit,core.revision,core.relation_index,core.revision_ref,gov.baseline,gov.baseline_entry,gov.change_item TO atlas_worker;
        GRANT SELECT ON access.grant TO atlas_worker;
        GRANT SELECT,INSERT ON read_model.export TO atlas_worker;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE IF EXISTS read_model.export;");
}
