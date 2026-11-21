using System.Diagnostics;
using BuildingBlocks;
using PlatformOps;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<HealthState>();
builder.Services.AddPlatformOps(builder.Configuration.GetConnectionString("Atlas"));

var app = builder.Build();
app.Lifetime.ApplicationStopping.Register(app.Services.GetRequiredService<HealthState>().BeginShutdown);

app.MapGet("/health/live", (HttpContext context) => HealthResponse(context, "ok"));
app.MapGet("/health/ready", (HttpContext context, HealthState state) =>
    state.IsReady
        ? Results.Ok(HealthBody(context, "ok"))
        : Results.Json(ErrorBody(context, "DEPENDENCY_UNAVAILABLE", "Service is shutting down"), statusCode: 503));

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
