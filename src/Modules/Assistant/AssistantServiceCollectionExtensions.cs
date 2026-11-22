using System.Net;
using Assistant.Infrastructure;
using Assistant.Providers;
using Assistant.Reasoning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant;

public static class AssistantServiceCollectionExtensions
{
    public static IServiceCollection AddAssistant(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("Atlas") ?? string.Empty;
        services.AddSingleton(new PostgresAssistantStore(connection));
        services.AddSingleton<AssistantReasoningService>();
        services.AddSingleton<ISecretRefResolver, ConfigurationSecretRefResolver>();
        services.AddSingleton<IProviderPort, OpenAiCompatibleProvider>();
        services.AddScoped<ConversationJobHandler>();
        services.AddHttpClient("AtlasProvider", client => client.Timeout = TimeSpan.FromSeconds(120))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10), AutomaticDecompression = DecompressionMethods.All });
        return services;
    }
}
