using System.Diagnostics;
using System.Text.Json;
using Atlas.Api.Auth;
using Atlas.Api.Hubs;
using ContractDelivery.Contracts;
using ContractDelivery.Infrastructure;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;

namespace Atlas.Api.ContractDelivery;

public static class ContractDeliveryEndpoints
{
    public static WebApplication MapContractDeliveryEndpoints(this WebApplication app)
    {
        var read = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasRead");
        read.MapGet("/contexts/{contextId:guid}", (HttpContext c, Guid projectId, Guid contextId, CancellationToken ct) => Execute(c, async p => Ok(c, await Store(c).GetContextAsync(p.PrincipalId, projectId, contextId, ct)), ct));
        read.MapPost("/conformance/check", (HttpContext c, Guid projectId, ConformanceCheckRequest? request, CancellationToken ct) => Execute(c, async p => Ok(c, await Store(c).CheckConformanceAsync(p.PrincipalId, projectId, Body(request), ct)), ct));
        read.MapGet("/tasks/{taskId:guid}/closure", (HttpContext c, Guid projectId, Guid taskId, Guid? contextId, CancellationToken ct) => Execute(c, async p => Ok(c, await Store(c).CheckClosureAsync(p.PrincipalId, projectId, taskId, contextId ?? throw ProjectIdentityException.Invalid("contextId is required."), ct)), ct));
        read.MapGet("/knowledge-delivery", (HttpContext c, Guid projectId, string? cursor, int? limit, CancellationToken ct) => Execute(c, async p => Ok(c, await Store(c).ListDeliveriesAsync(p.PrincipalId, projectId, cursor, Limit(limit), ct)), ct));
        read.MapPost("/contexts/{contextId:guid}/export", (HttpContext c, Guid projectId, Guid contextId, CancellationToken ct) => Execute(c, async p => Ok(c, await Store(c).ExportContextAsync(p, projectId, contextId, Key(c), RequestId(c), TraceId(c), ct)), ct));

        var taskWrite = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasTask");
        taskWrite.MapPost("/tasks/{taskId:guid}/contexts", (HttpContext c, Guid projectId, Guid taskId, BuildContextRequest? request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).BuildContextAsync(p, projectId, taskId, Body(request), Expected(c), Key(c), RequestId(c), TraceId(c), ct), 201), ct));
        var propose = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasPropose");
        propose.MapPost("/knowledge-delivery/proposals", (HttpContext c, Guid projectId, JsonElement request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).IngestProposalAsync(p, projectId, request, Key(c), RequestId(c), TraceId(c), ct), 201), ct));
        var evidence = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasEvidence");
        evidence.MapPost("/conformance", (HttpContext c, Guid projectId, ConformanceView? request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).RecordConformanceAsync(p, projectId, Body(request), Key(c), RequestId(c), TraceId(c), ct), 201), ct));
        evidence.MapPost("/tasks/{taskId:guid}/acceptance", (HttpContext c, Guid projectId, Guid taskId, AcceptanceInput? request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).RecordAcceptanceAsync(p, projectId, taskId, Body(request), Expected(c), Key(c), RequestId(c), TraceId(c), ct), 201), ct));
        evidence.MapPost("/reconcile", (HttpContext c, Guid projectId, ReconcileRequest? request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).ReconcileAsync(p, projectId, Body(request), Key(c), RequestId(c), TraceId(c), ct), 202), ct));
        var ci = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasCi");
        ci.MapPost("/knowledge-delivery/manifests", (HttpContext c, Guid projectId, JsonElement request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).IngestManifestAsync(p, projectId, request, Key(c), RequestId(c), TraceId(c), ct), 201), ct));
        ci.MapPost("/deployments", (HttpContext c, Guid projectId, DeploymentInput? request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).RecordDeploymentAsync(p, projectId, Body(request), Key(c), RequestId(c), TraceId(c), ct), 201), ct));
        var review = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasReview");
        review.MapPost("/tasks/{taskId:guid}/close", (HttpContext c, Guid projectId, Guid taskId, CloseTaskRequest? request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).RequestClosureAsync(p, projectId, taskId, Body(request), Expected(c), Key(c), RequestId(c), TraceId(c), ct)), ct));
        return app;
    }

    private static async Task<IResult> Execute(HttpContext context, Func<AtlasPrincipal, Task<IResult>> action, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.Vary = "Authorization, Cookie";
        try
        {
            var result = await action(await context.ResolveAtlasPrincipalAsync(ct));
            if (!HttpMethods.IsGet(context.Request.Method) && Guid.TryParse(context.Request.RouteValues["projectId"]?.ToString(), out var projectId))
                await context.RequestServices.GetRequiredService<ProjectRealtimeNotifier>().PublishAsync(projectId, "task.changed", ct);
            return result;
        }
        catch (ProjectIdentityException exception) { return Results.Json(Error(context, exception.Code, exception.Message), statusCode: exception.StatusCode); }
        catch (JsonException exception) { return Results.Json(Error(context, "VALIDATION_FAILED", exception.Message), statusCode: 400); }
        catch (UnauthorizedAccessException exception) { return Results.Json(Error(context, "UNAUTHORIZED", exception.Message), statusCode: 401); }
        catch (Npgsql.PostgresException exception) when (exception.SqlState == "23505") { return Results.Json(Error(context, "CONFLICT", "This external delivery or proposal is already recorded."), statusCode: 409); }
    }
    private static global::ContractDelivery.Infrastructure.PostgresContractDeliveryStore Store(HttpContext c) => c.RequestServices.GetRequiredService<global::ContractDelivery.Infrastructure.PostgresContractDeliveryStore>();
    private static T Body<T>(T? body) where T : class => body ?? throw ProjectIdentityException.Invalid("A request body is required.");
    private static string Key(HttpContext c) => c.Request.Headers["Idempotency-Key"].ToString();
    private static string RequestId(HttpContext c) => Activity.Current?.Id ?? c.TraceIdentifier;
    private static string? TraceId(HttpContext c) => Activity.Current?.TraceId.ToString();
    private static int Limit(int? limit) => limit is null ? 30 : limit is >= 1 and <= 100 ? limit.Value : throw ProjectIdentityException.Invalid("Page limit must be between 1 and 100.");
    private static long Expected(HttpContext c) { var value = c.Request.Headers.IfMatch.ToString(); if (value.Length >= 3 && value[0] == '"' && value[^1] == '"' && long.TryParse(value[1..^1], out var version) && version > 0) return version; throw new ProjectIdentityException("PRECONDITION_REQUIRED", "A valid quoted aggregate version is required in If-Match.", 428); }
    private static object Error(HttpContext c, string code, string message) => new { code, message, requestId = RequestId(c), traceId = TraceId(c) };
    private static IResult Ok(HttpContext c, object data) => Results.Ok(Envelope(c, data));
    private static IResult Mutation(HttpContext c, object data, int status = 200) => Results.Json(Envelope(c, data), statusCode: status);
    private static object Envelope(HttpContext c, object data) => new { code = "OK", requestId = RequestId(c), traceId = TraceId(c), data };
}
