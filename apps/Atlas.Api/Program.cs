using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AccessApplicability.Application;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using Atlas.Api.Access;
using BuildingBlocks;
using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using PlatformOps;
using PlatformOps.Observability;
using ProjectIdentity.Infrastructure;
using Atlas.Api.Auth;
using Atlas.Api.Platform;
using Atlas.Api.SchemaCatalog;
using Atlas.Api.KnowledgeCore;
using Atlas.Api.ImportEvidence;
using Atlas.Api.Governance;
using Atlas.Api.ReadSearchExplain;
using Atlas.Api.TaskCollab;
using Atlas.Api.ContractDelivery;
using Atlas.Api.AgentMcp;
using AgentMcp.Infrastructure;
using ContractDelivery.Infrastructure;
using Atlas.Api.Hubs;
using TaskCollab.Infrastructure;
using ReadSearchExplain.Export;
using ReadSearchExplain.Search;
using Governance.Infrastructure;
using ImportEvidence.Application;
using ImportEvidence.Blob;
using ImportEvidence.Infrastructure;
using KnowledgeCore.Infrastructure;
using SchemaCatalog.Infrastructure;
using Assistant;
using Assistant.Infrastructure;
using Atlas.Api.Assistant;
using Atlas.Api.Mcp;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(options => { options.IncludeScopes = true; options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; });
builder.Services.AddSingleton<HealthState>();
builder.Services.AddPlatformOps(builder.Configuration.GetConnectionString("Atlas"));
builder.Services.AddSingleton(new PostgresProjectIdentityStore(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty));
builder.Services.AddSingleton(new PostgresAccessApplicabilityStore(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty));
builder.Services.AddSingleton(services => new PostgresSchemaCatalogStore(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty, services.GetRequiredService<PostgresAccessApplicabilityStore>()));
builder.Services.AddSingleton(services => new PostgresKnowledgeStore(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty, services.GetRequiredService<PostgresAccessApplicabilityStore>()));
builder.Services.AddSingleton(services => new PostgresGovernanceStore(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty, services.GetRequiredService<PostgresKnowledgeStore>(), services.GetRequiredService<PostgresAccessApplicabilityStore>()));
builder.Services.AddSingleton<PolicyService>();
builder.Services.AddAssistant(builder.Configuration);
builder.Services.AddSingleton<IPrivateBlobStorage>(new PrivateBlobStorage(builder.Configuration["ImportEvidence:PrivateBlobRoot"] ?? throw new InvalidOperationException("ImportEvidence:PrivateBlobRoot is required.")));
builder.Services.AddSingleton<IContentScanner, BuiltInContentScanner>();
builder.Services.AddSingleton(services => new PostgresImportEvidenceStore(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty, services.GetRequiredService<IPrivateBlobStorage>(), services.GetRequiredService<IContentScanner>(), services.GetRequiredService<PostgresAccessApplicabilityStore>(), services.GetRequiredService<PostgresKnowledgeStore>()));
builder.Services.AddSingleton(services => new PostgresReadSearchService(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty, services.GetRequiredService<PostgresKnowledgeStore>(), services.GetRequiredService<PostgresAccessApplicabilityStore>(), builder.Configuration["ReadSearchExplain:CursorKey"] ?? throw new InvalidOperationException("ReadSearchExplain:CursorKey is required.")));
builder.Services.AddSingleton<ReadingProjectionService>();
builder.Services.AddScoped(services => new PostgresExportService(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty, services.GetRequiredService<PostgresKnowledgeStore>(), services.GetRequiredService<PostgresAccessApplicabilityStore>(), services.GetRequiredService<PostgresReadSearchService>(), services.GetRequiredService<PlatformOps.Jobs.IJobQueue>()));
builder.Services.AddSingleton(services => new PostgresTaskCollabStore(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty, services.GetRequiredService<PostgresAccessApplicabilityStore>()));
builder.Services.AddSingleton(services => new PostgresContractDeliveryStore(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty, services.GetRequiredService<PostgresAccessApplicabilityStore>(), services.GetRequiredService<IPrivateBlobStorage>()));
builder.Services.AddSingleton(services => new PostgresAgentMcpStore(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty, services.GetRequiredService<PostgresAccessApplicabilityStore>()));
builder.Services.AddSignalR();
builder.Services.AddSingleton<ProjectHubConnections>();
builder.Services.AddSingleton<ProjectRealtimeNotifier>();
builder.Services.AddSingleton<IPolicyService>(services => services.GetRequiredService<PolicyService>());
builder.Services.AddSingleton<IReadContextFactory>(services => services.GetRequiredService<PolicyService>());
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));
builder.Services.AddAtlasAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAtlasMcp(builder.Configuration, builder.Environment);

var app = builder.Build();
if (args.Contains("--migrate-only", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AtlasDbContext>().Database.MigrateAsync();
    return;
}

app.Lifetime.ApplicationStopping.Register(app.Services.GetRequiredService<HealthState>().BeginShutdown);
app.Use(AtlasObservability.MeasureHttpRequests);
app.UseAuthentication();
app.UseAtlasCsrfProtection();
app.UseAuthorization();
app.MapIdentityEndpoints();
app.MapAccessEndpoints();
app.MapPlatformEndpoints();
app.MapSchemaCatalogEndpoints();
app.MapKnowledgeEndpoints();
app.MapImportEvidenceEndpoints();
app.MapGovernanceEndpoints();
app.MapReadSearchExplainEndpoints();
app.MapTaskCollabEndpoints();
app.MapContractDeliveryEndpoints();
app.MapAgentMcpEndpoints();
app.MapAssistantEndpoints();
app.MapAtlasMcp();
app.MapHub<ProjectHub>("/hubs/project");

// Internal-only operational scrape endpoint; Caddy intentionally has no /metrics route.
app.MapGet("/metrics", (AtlasDbContext database, CancellationToken cancellationToken) => AtlasObservability.Metrics(database, cancellationToken));
app.MapGet("/health/live", (HttpContext context) => HealthResponse(context, "ok"));
app.MapGet("/health/ready", async (HttpContext context, HealthState state, AtlasDbContext database, CancellationToken ct) =>
{
    if (!state.IsReady) return Results.Json(ErrorBody(context, "DEPENDENCY_UNAVAILABLE", "Service is shutting down"), statusCode: 503);
    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        return await database.Database.CanConnectAsync(timeout.Token)
            ? Results.Ok(HealthBody(context, "ok"))
            : Results.Json(ErrorBody(context, "DEPENDENCY_UNAVAILABLE", "Database is unavailable"), statusCode: 503);
    }
    catch
    {
        return Results.Json(ErrorBody(context, "DEPENDENCY_UNAVAILABLE", "Database is unavailable"), statusCode: 503);
    }
});


app.Run();

static IResult HealthResponse(HttpContext context, string status) => Results.Ok(HealthBody(context, status));

static object HealthBody(HttpContext context, string status) => new
{
    code = "OK",
    requestId = Activity.Current?.Id ?? context.TraceIdentifier,
    traceId = Activity.Current?.TraceId.ToString(),
    data = new { status }
};

static object ErrorBody(HttpContext context, string code, string message) => new
{
    code,
    message,
    requestId = Activity.Current?.Id ?? context.TraceIdentifier,
    traceId = Activity.Current?.TraceId.ToString()
};

public partial class Program;
