EF Core migrations in src/BuildingBlocks/Persistence/Migrations are the only executable migration chain.
0001 embeds the frozen T1.0 database.sql as BusinessAtlas.db.migrations.sql.0001_initial.sql and runs it in the EF migration transaction together with migration history.
0002 provisions NOLOGIN, NOBYPASSRLS atlas_app and atlas_worker roles with least privileges.
Apply with a dedicated migration-owner connection using AtlasDbContext.Database.MigrateAsync(). API and worker credentials must not own database objects or have DDL/TRUNCATE/BYPASSRLS privileges.
