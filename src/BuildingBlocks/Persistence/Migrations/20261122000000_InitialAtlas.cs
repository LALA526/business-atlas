using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BuildingBlocks.Persistence.Migrations;

[DbContext(typeof(AtlasDbContext))]
[Migration("20261122000000_InitialAtlas")]
public sealed class InitialAtlas : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(InitialAtlasSql.Up);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP SCHEMA IF EXISTS read_model CASCADE;
            DROP SCHEMA IF EXISTS ops CASCADE;
            DROP SCHEMA IF EXISTS assistant CASCADE;
            DROP SCHEMA IF EXISTS agent CASCADE;
            DROP SCHEMA IF EXISTS delivery CASCADE;
            DROP SCHEMA IF EXISTS collab CASCADE;
            DROP SCHEMA IF EXISTS intake CASCADE;
            DROP SCHEMA IF EXISTS gov CASCADE;
            DROP SCHEMA IF EXISTS core CASCADE;
            DROP SCHEMA IF EXISTS access CASCADE;
            DROP SCHEMA IF EXISTS identity CASCADE;
            DROP EXTENSION IF EXISTS pg_trgm;
            """);
    }
}
