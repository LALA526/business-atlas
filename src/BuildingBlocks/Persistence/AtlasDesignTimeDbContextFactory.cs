using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BuildingBlocks.Persistence;

public sealed class AtlasDesignTimeDbContextFactory : IDesignTimeDbContextFactory<AtlasDbContext>
{
    public AtlasDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ATLAS_MIGRATION_CONNECTIONSTRING")
            ?? "Host=localhost;Database=atlas_design;Username=atlas_migration";
        return new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(connectionString)
            .Options);
    }
}
