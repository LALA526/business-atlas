using Microsoft.AspNetCore.Http.Features;
using ModelContextProtocol.AspNetCore;

namespace Atlas.Api.Mcp;

public static class McpEndpoints
{
    public static IServiceCollection AddAtlasMcp(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var catalog = new AtlasToolCatalog(environment);
        services.AddHttpContextAccessor();
        services.AddSingleton(catalog);
        services.AddSingleton<AtlasMcpDispatcher>();
        services.AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = "Business Atlas", Version = "1.0" };
            options.ServerInstructions = "Use the business-atlas-project skill. Every call is reauthorized. No MCP tool can approve, publish, access a shell, or write approved knowledge.";
        })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools(catalog.Tools)
            .WithResources<AtlasResources>();
        return services;
    }

    public static WebApplication MapAtlasMcp(this WebApplication app)
    {
        app.UseWhen(context => context.Request.Path.StartsWithSegments("/mcp"), branch => branch.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Vary = "Authorization, Origin";
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 1024 * 1024;
            if (!AllowedHost(context))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !AllowedOrigin(context, origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next();
        }));
        app.MapGet("/.well-known/oauth-protected-resource/mcp", (HttpContext context, IConfiguration configuration) =>
        {
            var authority = configuration["Authentication:Bearer:Authority"];
            if (string.IsNullOrWhiteSpace(authority)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var resource = $"{context.Request.Scheme}://{context.Request.Host}/mcp";
            return Results.Json(new
            {
                resource,
                authorization_servers = new[] { authority },
                scopes_supported = new[] { "atlas.read", "atlas.propose", "atlas.task", "atlas.evidence", "atlas.agent" },
                bearer_methods_supported = new[] { "header" },
                resource_documentation = $"{context.Request.Scheme}://{context.Request.Host}/skills/business-atlas-project/SKILL.md"
            });
        });
        app.MapGet("/skills/business-atlas-project/SKILL.md", (IHostEnvironment environment) =>
        {
            var path = Path.Combine(environment.ContentRootPath, "skills", "business-atlas-project", "SKILL.md");
            if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "skills", "business-atlas-project", "SKILL.md");
            return Results.File(path, "text/markdown; charset=utf-8");
        });
        app.MapMcp("/mcp").RequireAuthorization("McpBearer");
        return app;
    }

    private static bool AllowedHost(HttpContext context)
    {
        var host = context.Request.Host.Host;
        if (string.IsNullOrWhiteSpace(host)) return false;
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var configured = configuration.GetSection("Mcp:AllowedHosts").Get<string[]>() ??
            (configuration["AllowedHosts"] ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (configured.Contains(host, StringComparer.OrdinalIgnoreCase)) return true;
        return configured.Contains("*") && context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment() &&
               (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "::1" or "[::1]");
    }

    private static bool AllowedOrigin(HttpContext context, string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        var configured = context.RequestServices.GetRequiredService<IConfiguration>().GetSection("Mcp:AllowedOrigins").Get<string[]>() ?? [];
        if (configured.Contains(origin.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)) return true;
        var originPort = uri.IsDefaultPort ? (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80) : uri.Port;
        var requestPort = context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80);
        return uri.Scheme.Equals(context.Request.Scheme, StringComparison.OrdinalIgnoreCase) &&
               uri.Host.Equals(context.Request.Host.Host, StringComparison.OrdinalIgnoreCase) && originPort == requestPort;
    }
}
