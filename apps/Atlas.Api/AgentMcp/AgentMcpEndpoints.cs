using System.Diagnostics;
using System.Text.Json;
using AgentMcp.Contracts;
using AgentMcp.Infrastructure;
using Atlas.Api.Auth;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;

namespace Atlas.Api.AgentMcp;

public static class AgentMcpEndpoints
{
    public static WebApplication MapAgentMcpEndpoints(this WebApplication app)
    {
        var read = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasRead");
        read.MapGet("/agent-runs", (HttpContext c, Guid projectId, Guid? taskId, string? cursor, int? limit, CancellationToken ct) =>
            Execute(c, async p => Ok(c, await Store(c).ListRunsAsync(p.PrincipalId, projectId, taskId, cursor, Limit(limit), ct)), ct));
        read.MapGet("/agent-runs/{runId:guid}/events", (HttpContext c, Guid projectId, Guid runId, string? cursor, int? limit, CancellationToken ct) =>
            Execute(c, async p => Ok(c, await Store(c).ListEventsAsync(p.PrincipalId, projectId, runId, cursor, Limit(limit), ct)), ct));

        var agent = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasAgent");
        agent.MapGet("/agent-work-requests", (HttpContext c, Guid projectId, string? cursor, int? limit, CancellationToken ct) =>
            Execute(c, async p => Ok(c, await Store(c).ListWorkAsync(p.PrincipalId, projectId, cursor, Limit(limit), ct)), ct));
        agent.MapPost("/agent-runs", (HttpContext c, Guid projectId, StartAgentRunRequest? request, CancellationToken ct) =>
            Execute(c, async p => Mutation(c, await Store(c).StartRunAsync(p, projectId, Body(request), Key(c), RequestId(c), TraceId(c), ct), 201), ct));
        agent.MapPost("/agent-runs/{runId:guid}/events", (HttpContext c, Guid projectId, Guid runId, RunEventView? request, CancellationToken ct) =>
            Execute(c, async p => Mutation(c, await Store(c).AppendEventAsync(p, projectId, runId, Body(request), Key(c), RequestId(c), TraceId(c), ct), 201), ct));
        agent.MapPost("/tasks/{taskId:guid}/investigations/request", (HttpContext c, Guid projectId, Guid taskId, InvestigateImpactRequest? request, CancellationToken ct) =>
            Execute(c, async p => Mutation(c, await Store(c).InvestigateAsync(p, projectId, taskId, Body(request), Expected(c), Key(c), RequestId(c), TraceId(c), ct), 201), ct));
        agent.MapPost("/agent-work-requests/{workId:guid}/claim", (HttpContext c, Guid projectId, Guid workId, ClaimWorkRequest? request, CancellationToken ct) =>
            Execute(c, async p => Mutation(c, await Store(c).ClaimAsync(p, projectId, workId, Body(request), Expected(c), Key(c), RequestId(c), TraceId(c), ct)), ct));
        agent.MapPost("/agent-work-requests/{workId:guid}/heartbeat", (HttpContext c, Guid projectId, Guid workId, HeartbeatWorkRequest? request, CancellationToken ct) =>
            Execute(c, async p => Mutation(c, await Store(c).HeartbeatAsync(p, projectId, workId, Body(request), Key(c), RequestId(c), TraceId(c), ct)), ct));
        agent.MapPost("/agent-work-requests/{workId:guid}/finish", (HttpContext c, Guid projectId, Guid workId, FinishWorkRequest? request, CancellationToken ct) =>
            Execute(c, async p => Mutation(c, await Store(c).FinishAsync(p, projectId, workId, Body(request), Key(c), RequestId(c), TraceId(c), ct)), ct));

        var evidence = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasEvidence");
        evidence.MapPost("/agent-work-requests", (HttpContext c, Guid projectId, CreateWorkRequest? request, CancellationToken ct) =>
            Execute(c, async p => Mutation(c, await Store(c).CreateWorkAsync(p, projectId, Body(request), Key(c), RequestId(c), TraceId(c), ct), 201), ct));
        return app;
    }

    private static async Task<IResult> Execute(HttpContext context, Func<AtlasPrincipal, Task<IResult>> action, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.Vary = "Authorization, Cookie";
        try { return await action(await context.ResolveAtlasPrincipalAsync(ct)); }
        catch (ProjectIdentityException exception) { return Results.Json(Error(context, exception.Code, exception.Message), statusCode: exception.StatusCode); }
        catch (JsonException exception) { return Results.Json(Error(context, "VALIDATION_FAILED", exception.Message), statusCode: 400); }
        catch (UnauthorizedAccessException exception) { return Results.Json(Error(context, "UNAUTHORIZED", exception.Message), statusCode: 401); }
    }
    private static object Error(HttpContext c, string code, string message) => new { code, message, requestId = RequestId(c), traceId = TraceId(c) };
    private static IResult Ok(HttpContext c, object data) => Results.Ok(Envelope(c, data));
    private static IResult Mutation(HttpContext c, object data, int status = 200)
    {
        if (data is WorkRequestView work) c.Response.Headers.ETag = $"\"{work.Version}\"";
        else if (data is AgentRunView run) c.Response.Headers.ETag = $"\"{run.Version}\"";
        return Results.Json(Envelope(c, data), statusCode: status);
    }
    private static object Envelope(HttpContext c, object data) => new { code = "OK", requestId = RequestId(c), traceId = TraceId(c), data };
    private static PostgresAgentMcpStore Store(HttpContext c) => c.RequestServices.GetRequiredService<PostgresAgentMcpStore>();
    private static T Body<T>(T? body) where T : class => body ?? throw ProjectIdentityException.Invalid("A request body is required.");
    private static string Key(HttpContext c) => c.Request.Headers["Idempotency-Key"].ToString();
    private static string RequestId(HttpContext c) => Activity.Current?.Id ?? c.TraceIdentifier;
    private static string? TraceId(HttpContext c) => Activity.Current?.TraceId.ToString();
    private static int Limit(int? limit) => limit is null ? 30 : limit is >= 1 and <= 100 ? limit.Value : throw ProjectIdentityException.Invalid("Page limit must be between 1 and 100.");
    private static long Expected(HttpContext c)
    {
        var value = c.Request.Headers.IfMatch.ToString();
        if (value.Length >= 3 && value[0] == '"' && value[^1] == '"' && long.TryParse(value[1..^1], out var version) && version > 0) return version;
        throw new ProjectIdentityException("PRECONDITION_REQUIRED", "A valid quoted aggregate version is required in If-Match.", 428);
    }
}
