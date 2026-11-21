using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BuildingBlocks.Persistence.Migrations;

[DbContext(typeof(AtlasDbContext))]
[Migration("20261122000002_ProjectIdentity")]
public sealed class ProjectIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE identity.project_profile (
              project_id uuid PRIMARY KEY REFERENCES identity.project(project_id),
              profile_kind text NOT NULL CHECK (profile_kind IN ('balanced', 'business', 'technical')),
              policy_version text NOT NULL CHECK (length(trim(policy_version)) > 0),
              created_at timestamptz NOT NULL DEFAULT now()
            );
            ALTER TABLE identity.project_profile ENABLE ROW LEVEL SECURITY;
            ALTER TABLE identity.project_profile FORCE ROW LEVEL SECURITY;
            CREATE POLICY project_boundary ON identity.project_profile
              USING (project_id = access.current_project())
              WITH CHECK (project_id = access.current_project());
            GRANT SELECT, INSERT ON identity.project_profile TO atlas_app;

            CREATE FUNCTION identity.resolve_principal(p_issuer text, p_subject text, p_kind text)
            RETURNS TABLE (out_principal_id uuid, out_issuer text, out_subject text, out_kind text, out_active boolean)
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = pg_catalog, identity
            AS $$
            BEGIN
              IF p_issuer IS NULL OR length(trim(p_issuer)) = 0 OR length(p_issuer) > 2048
                 OR p_subject IS NULL OR length(trim(p_subject)) = 0 OR length(p_subject) > 512
                 OR p_kind IS NULL OR p_kind NOT IN ('human', 'agent', 'service') THEN
                RAISE EXCEPTION 'Invalid Atlas principal' USING ERRCODE = '22023';
              END IF;

              INSERT INTO identity.principal (issuer, subject, kind)
              VALUES (p_issuer, p_subject, p_kind)
              ON CONFLICT (issuer, subject) DO NOTHING;

              RETURN QUERY
                SELECT p.principal_id, p.issuer, p.subject, p.kind, p.active
                FROM identity.principal p
                WHERE p.issuer = p_issuer AND p.subject = p_subject;
            END;
            $$;
            REVOKE ALL ON FUNCTION identity.resolve_principal(text, text, text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION identity.resolve_principal(text, text, text) TO atlas_app;

            CREATE POLICY own_create_project_idempotency ON ops.idempotency FOR SELECT
              USING (principal_id = access.current_subject() AND operation_id = 'createProject');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY own_create_project_idempotency ON ops.idempotency;
            DROP FUNCTION identity.resolve_principal(text, text, text);
            DROP TABLE identity.project_profile;
            """);
    }
}
