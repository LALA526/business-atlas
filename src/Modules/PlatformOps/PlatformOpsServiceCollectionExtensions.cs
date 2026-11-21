using BuildingBlocks.Commands;
using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace PlatformOps;

public static class PlatformOpsServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformOps(this IServiceCollection services, string? connectionString)
    {
        services.AddDbContext<AtlasDbContext>(options => options.UseNpgsql(connectionString ?? string.Empty));
        services.AddSingleton<ICommandTransactionFactory, PostgresCommandTransactionFactory>();
        services.AddSingleton<ICommandAuthorizer, DenyAllCommandAuthorizer>();
        services.AddSingleton<CommandPipeline>();
        services.AddScoped<AuditQueryService>();
        services.AddScoped<PostgresOutboxStore>();
        return services;
    }
}
