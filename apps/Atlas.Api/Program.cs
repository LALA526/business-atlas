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
using ProjectIdentity.Infrastructure;
using Atlas.Api.Auth;
using Atlas.Api.Platform;
using Atlas.Api.SchemaCatalog;
using Atlas.Api.KnowledgeCore;
using Atlas.Api.ImportEvidence;
using Atlas.Api.Governance;
using Atlas.Api.ReadSearchExplain;
using ReadSearchExplain.Export;
using ReadSearchExplain.Search;
using Governance.Infrastructure;
using ImportEvidence.Application;
using ImportEvidence.Blob;
using ImportEvidence.Infrastructure;
using KnowledgeCore.Infrastructure;
using SchemaCatalog.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<HealthState>();
builder.Services.AddPlatformOps(builder.Configuration.GetConnectionString("Atlas"));
builder.Services.AddSingleton(new PostgresProjectIdentityStore(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty));
builder.Services.AddSingleton(new PostgresAccessApplicabilityStore(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty));
builder.Services.AddSingleton<PostgresSchemaCatalogStore>();
builder.Services.AddSingleton(services => new PostgresKnowledgeStore(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty, services.GetRequiredService<PostgresAccessApplicabilityStore>()));
builder.Services.AddSingleton(services => new PostgresGovernanceStore(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty, services.GetRequiredService<PostgresKnowledgeStore>(), services.GetRequiredService<PostgresAccessApplicabilityStore>()));
builder.Services.AddSingleton<PolicyService>();
builder.Services.AddSingleton<IPrivateBlobStorage>(new PrivateBlobStorage(builder.Configuration["ImportEvidence:PrivateBlobRoot"] ?? throw new InvalidOperationException("ImportEvidence:PrivateBlobRoot is required.")));
builder.Services.AddSingleton<IContentScanner, BuiltInContentScanner>();
builder.Services.AddSingleton<PostgresImportEvidenceStore>();
builder.Services.AddSingleton(services => new PostgresReadSearchService(builder.Configuration.GetConnectionString("Atlas") ?? string.Empty, services.GetRequiredService<PostgresKnowledgeStore>(), services.GetRequiredService<PostgresAccessApplicabilityStore>(), builder.Configuration["ReadSearchExplain:CursorKey"] ?? throw new InvalidOperationException("ReadSearchExplain:CursorKey is required.")));
builder.Services.AddSingleton<ReadingProjectionService>();
builder.Services.AddScoped<PostgresExportService>();
builder.Services.AddSingleton<IPolicyService>(services => services.GetRequiredService<PolicyService>());
builder.Services.AddSingleton<IReadContextFactory>(services => services.GetRequiredService<PolicyService>());
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));
builder.Services.AddAtlasAuthentication(builder.Configuration, builder.Environment);

var app = builder.Build();
if (args.Contains("--migrate-only", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AtlasDbContext>().Database.MigrateAsync();
    return;
}

app.Lifetime.ApplicationStopping.Register(app.Services.GetRequiredService<HealthState>().BeginShutdown);
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
