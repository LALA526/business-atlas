using System.Diagnostics;
using System.Text.Json;
using Atlas.Api.Auth;
using Atlas.Api.Hubs;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using TaskCollab.Contracts;
using TaskCollab.Infrastructure;

namespace Atlas.Api.TaskCollab;

public static class TaskCollabEndpoints
{
    public static WebApplication MapTaskCollabEndpoints(this WebApplication app)
    {
        var read = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasRead");
        read.MapGet("/tasks", (HttpContext c, Guid projectId, string? cursor, int? limit, CancellationToken ct) => Execute(c, async p => Ok(c, await Store(c).ListTasksAsync(p.PrincipalId, projectId, cursor, Limit(limit), ct)), ct));
        read.MapGet("/tasks/{taskId:guid}", (HttpContext c, Guid projectId, Guid taskId, CancellationToken ct) => Execute(c, async p => Ok(c, await Store(c).GetTaskAsync(p.PrincipalId, projectId, taskId, ct)), ct));
        read.MapGet("/tasks/{taskId:guid}/impacts", (HttpContext c, Guid projectId, Guid taskId, string? cursor, int? limit, CancellationToken ct) => Execute(c, async p => Ok(c, await Store(c).ListImpactsAsync(p.PrincipalId, projectId, taskId, cursor, Limit(limit), ct)), ct));
        read.MapGet("/issues", (HttpContext c, Guid projectId, Guid? taskId, Guid? unitId, Guid? changeSetId, string? cursor, int? limit, CancellationToken ct) => Execute(c, async p => Ok(c, await Store(c).ListActivitiesAsync(p.PrincipalId, projectId, "issue", taskId, unitId, changeSetId, cursor, Limit(limit), ct)), ct));
        read.MapGet("/feedback", (HttpContext c, Guid projectId, Guid? taskId, Guid? unitId, Guid? changeSetId, string? cursor, int? limit, CancellationToken ct) => Execute(c, async p => Ok(c, await Store(c).ListActivitiesAsync(p.PrincipalId, projectId, "feedback", taskId, unitId, changeSetId, cursor, Limit(limit), ct)), ct));
        read.MapGet("/comments", (HttpContext c, Guid projectId, Guid? taskId, Guid? unitId, Guid? changeSetId, string? cursor, int? limit, CancellationToken ct) => Execute(c, async p => Ok(c, await Store(c).ListActivitiesAsync(p.PrincipalId, projectId, "comment", taskId, unitId, changeSetId, cursor, Limit(limit), ct)), ct));
        read.MapGet("/activity", (HttpContext c, Guid projectId, string? cursor, int? limit, CancellationToken ct) => Execute(c, async p => Ok(c, await Store(c).ListActivityAsync(p.PrincipalId, projectId, cursor, Limit(limit), ct)), ct));

        var write = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasPropose");
        write.MapPost("/tasks", (HttpContext c, Guid projectId, CreateTaskRequest? request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).CreateTaskAsync(p, projectId, RequireBody(request)!, Key(c), RequestId(c), TraceId(c), ct), 201), ct));
        write.MapPatch("/tasks/{taskId:guid}", (HttpContext c, Guid projectId, Guid taskId, AmendTaskRequest? request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).AmendTaskAsync(p, projectId, taskId, RequireBody(request)!, Expected(c), Key(c), RequestId(c), TraceId(c), ct)), ct));
        write.MapPost("/tasks/{taskId:guid}/analyze-intent", (HttpContext c, Guid projectId, Guid taskId, AnalyzeIntentRequest? request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).AnalyzeIntentAsync(p, projectId, taskId, RequireBody(request)!, Expected(c), Key(c), RequestId(c), TraceId(c), ct), 202), ct));
        write.MapPost("/tasks/{taskId:guid}/impact-candidates", (HttpContext c, Guid projectId, Guid taskId, FindImpactsRequest? request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).FindImpactsAsync(p, projectId, taskId, RequireBody(request)!, Expected(c), Key(c), RequestId(c), TraceId(c), ct), 202), ct));
        write.MapPost("/tasks/{taskId:guid}/investigations", (HttpContext c, Guid projectId, Guid taskId, InvestigationInput? request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).SubmitInvestigationAsync(p, projectId, taskId, RequireBody(request)!, Expected(c), Key(c), RequestId(c), TraceId(c), ct), 201), ct));
        write.MapPut("/tasks/{taskId:guid}/impacts/{impactId:guid}", (HttpContext c, Guid projectId, Guid taskId, Guid impactId, DecideImpactRequest? request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).DecideImpactAsync(p, projectId, taskId, impactId, RequireBody(request)!, Expected(c), Key(c), RequestId(c), TraceId(c), ct)), ct));
        write.MapPost("/tasks/{taskId:guid}/plan-change", (HttpContext c, Guid projectId, Guid taskId, PlanChangeRequest? request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).PlanChangeAsync(p, projectId, taskId, RequireBody(request)!, Expected(c), Key(c), RequestId(c), TraceId(c), ct), 202), ct));
        write.MapPost("/tasks/{taskId:guid}/transition", (HttpContext c, Guid projectId, Guid taskId, TransitionTaskRequest? request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).TransitionTaskAsync(p, projectId, taskId, RequireBody(request)!, Expected(c), Key(c), RequestId(c), TraceId(c), ct)), ct));
        MapActivity(write, "issues", "issue"); MapActivity(write, "feedback", "feedback"); MapActivity(write, "comments", "comment");
        return app;
    }

    private static void MapActivity(RouteGroupBuilder group, string path, string kind)
    {
        group.MapPost(path, (HttpContext c, Guid projectId, ActivityInput? request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).CreateActivityAsync(p, projectId, kind, RequireBody(request)!, Key(c), RequestId(c), TraceId(c), ct), 201), ct));
        group.MapPost(path + "/{activityId:guid}/resolve", (HttpContext c, Guid projectId, Guid activityId, ResolveActivityRequest? request, CancellationToken ct) => Execute(c, async p => Mutation(c, await Store(c).ResolveActivityAsync(p, projectId, kind, activityId, RequireBody(request)!, Expected(c), Key(c), RequestId(c), TraceId(c), ct)), ct));
    }

    private static async Task<IResult> Execute(HttpContext context, Func<AtlasPrincipal, Task<IResult>> action, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.Vary = "Authorization, Cookie";
        try
        {
            var result = await action(await context.ResolveAtlasPrincipalAsync(ct));
            if (!HttpMethods.IsGet(context.Request.Method) && Guid.TryParse(context.Request.RouteValues["projectId"]?.ToString(), out var projectId))
            {
                var path = context.Request.Path.Value ?? string.Empty;
                var kind = path.Contains("/comments", StringComparison.Ordinal) ? "comment.changed" :
                    path.Contains("/feedback", StringComparison.Ordinal) ? "feedback.changed" :
                    path.Contains("/issues", StringComparison.Ordinal) ? "issue.changed" : "task.changed";
                await context.RequestServices.GetRequiredService<ProjectRealtimeNotifier>().PublishAsync(projectId, kind, ct);
            }
            return result;
        }
        catch (ProjectIdentityException exception) { return Results.Json(Error(context, exception.Code, exception.Message), statusCode: exception.StatusCode); }
        catch (JsonException exception) { return Results.Json(Error(context, "VALIDATION_FAILED", exception.Message), statusCode: 400); }
        catch (UnauthorizedAccessException exception) { return Results.Json(Error(context, "UNAUTHORIZED", exception.Message), statusCode: 401); }
    }
    private static object Error(HttpContext c, string code, string message) => new { code, message, requestId = RequestId(c), traceId = TraceId(c) };
    private static IResult Ok(HttpContext c, object data) => Results.Ok(Envelope(c, data));
    private static IResult Mutation(HttpContext c, object? data, int status = 200) { if (data is null) throw ProjectIdentityException.Invalid("A request body is required."); if (data is TaskView task) c.Response.Headers.ETag = $"\"{task.Version}\""; else if (data is ImpactView impact) c.Response.Headers.ETag = $"\"{impact.Version}\""; else if (data is ActivityView activity) c.Response.Headers.ETag = $"\"{activity.Version}\""; return Results.Json(Envelope(c, data), statusCode: status); }
    private static object Envelope(HttpContext c, object data) => new { code = "OK", requestId = RequestId(c), traceId = TraceId(c), data };
    private static PostgresTaskCollabStore Store(HttpContext c) => c.RequestServices.GetRequiredService<PostgresTaskCollabStore>();
    private static T RequireBody<T>(T? body) where T : class => body ?? throw ProjectIdentityException.Invalid("A request body is required.");
    private static string Key(HttpContext c) => c.Request.Headers["Idempotency-Key"].ToString();
    private static string RequestId(HttpContext c) => Activity.Current?.Id ?? c.TraceIdentifier;
    private static string? TraceId(HttpContext c) => Activity.Current?.TraceId.ToString();
    private static int Limit(int? limit) => limit is null ? 30 : limit is >= 1 and <= 100 ? limit.Value : throw ProjectIdentityException.Invalid("Page limit must be between 1 and 100.");
    private static long Expected(HttpContext c) { var value = c.Request.Headers.IfMatch.ToString(); if (value.Length >= 3 && value[0] == '"' && value[^1] == '"' && long.TryParse(value[1..^1], out var version) && version > 0) return version; throw new ProjectIdentityException("PRECONDITION_REQUIRED", "A valid quoted aggregate version is required in If-Match.", 428); }
}
