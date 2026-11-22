using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BuildingBlocks.Persistence.Migrations;

[DbContext(typeof(AtlasDbContext))]
[Migration("20261122000005_Assistant")]
public sealed class Assistant : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE assistant.event (
          project_id uuid NOT NULL,
          conversation_id uuid NOT NULL,
          seq bigint GENERATED ALWAYS AS IDENTITY,
          event_id uuid NOT NULL DEFAULT gen_random_uuid(),
          kind text NOT NULL,
          payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object'),
          created_at timestamptz NOT NULL DEFAULT now(),
          PRIMARY KEY(project_id,seq),
          UNIQUE(project_id,event_id),
          FOREIGN KEY(project_id,conversation_id) REFERENCES assistant.conversation(project_id,conversation_id)
        );
        CREATE INDEX ix_assistant_event_conversation ON assistant.event(project_id,conversation_id,seq);
        ALTER TABLE assistant.event ENABLE ROW LEVEL SECURITY;
        ALTER TABLE assistant.event FORCE ROW LEVEL SECURITY;
        CREATE POLICY project_boundary ON assistant.event USING(project_id=access.current_project()) WITH CHECK(project_id=access.current_project());
        GRANT SELECT,INSERT ON assistant.event TO atlas_app;
        GRANT SELECT,INSERT ON assistant.event TO atlas_worker;
        GRANT USAGE ON SCHEMA assistant,agent TO atlas_worker;
        GRANT SELECT,INSERT,UPDATE ON assistant.provider,assistant.conversation,assistant.message,assistant.citation TO atlas_worker;
        GRANT SELECT,INSERT,UPDATE ON agent.run TO atlas_worker;
        GRANT SELECT,INSERT ON agent.run_event TO atlas_worker;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE IF EXISTS assistant.event;");
}
