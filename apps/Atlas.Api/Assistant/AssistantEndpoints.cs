using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AccessApplicability.Application;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using Assistant.Contracts;
using Assistant.Infrastructure;
using Atlas.Api.Auth;
using BuildingBlocks.Commands;
using PlatformOps.Jobs;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;

namespace Atlas.Api.Assistant;

public static class AssistantEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static WebApplication MapAssistantEndpoints(this WebApplication app)
    {
        var read = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasRead");
        read.MapGet("/conversations", (HttpContext http, Guid projectId, string? cursor, int? limit, CancellationToken ct) => Execute(http, async actor => Ok(http, await Store(http).ListConversationsAsync(actor.PrincipalId, projectId, cursor, Limit(limit), ct), await Context(http, actor, projectId, null, ct)), ct));
        read.MapPost("/conversations", (HttpContext http, Guid projectId, CreateConversationRequest? request, CancellationToken ct) => Execute(http, async actor =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A conversation body is required.");
            var context = await Context(http, actor, projectId, request.ReadContext, ct);
            if (request.RootUnitId.HasValue) await Access(http).AuthorizeReadAsync(context, request.RootUnitId.Value, "read", null, ct);
            var normalized = request with { ReadContext = request.ReadContext with { BaselineId = context.BaselineId } };
            var result = await Store(http).CreateConversationAsync(actor.PrincipalId, projectId, normalized, Key(http), RequestId(http), TraceId(http), ct);
            http.Response.Headers.ETag = $"\"{result.Version}\"";
            return Results.Json(Envelope(http, result, context), statusCode: 201);
        }, ct));
        read.MapGet("/conversations/{conversationId:guid}/messages", (HttpContext http, Guid projectId, Guid conversationId, string? cursor, int? limit, CancellationToken ct) => Execute(http, async actor => Ok(http, await Store(http).ListMessagesAsync(actor.PrincipalId, projectId, conversationId, cursor, Limit(limit), ct), await Context(http, actor, projectId, null, ct)), ct));
        read.MapPost("/conversations/{conversationId:guid}/messages", (HttpContext http, Guid projectId, Guid conversationId, SendMessageRequest? request, CancellationToken ct) => Execute(http, async actor =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A message body is required.");
            var context = await Context(http, actor, projectId, request.ReadContext, ct);
            var normalized = request with { ReadContext = request.ReadContext with { BaselineId = context.BaselineId } };
            var job = await Store(http).SendMessageAsync(actor.PrincipalId, projectId, conversationId, normalized, Expected(http), Key(http), RequestId(http), TraceId(http), ct);
            http.Response.Headers.ETag = $"\"{Expected(http) + 1}\"";
            return Results.Json(Envelope(http, JobDto(job), context), statusCode: 202);
        }, ct));
        read.MapPost("/conversations/{conversationId:guid}/cancel", (HttpContext http, Guid projectId, Guid conversationId, CancelConversationRequest? request, CancellationToken ct) => Execute(http, async actor =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A cancellation body is required.");
            var context = await Context(http, actor, projectId, null, ct);
            var job = await Store(http).CancelAsync(actor.PrincipalId, projectId, conversationId, request.JobId, Expected(http), "Canceled by the conversation user.", Key(http), RequestId(http), TraceId(http), ct);
            return Ok(http, JobDto(job), context);
        }, ct));
        read.MapGet("/conversations/{conversationId:guid}/events", Events);

        var admin = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasAdmin");
        admin.MapGet("/providers", (HttpContext http, Guid projectId, string? cursor, int? limit, CancellationToken ct) => Execute(http, async actor => Ok(http, await Store(http).ListProvidersAsync(actor.PrincipalId, projectId, cursor, Limit(limit), ct), await Context(http, actor, projectId, null, ct)), ct));
        admin.MapPut("/providers/{providerId:guid}", (HttpContext http, Guid projectId, Guid providerId, ProviderConfig? request, CancellationToken ct) => Execute(http, async actor =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A provider body is required.");
            var context = await Context(http, actor, projectId, null, ct);
            var result = await Store(http).ConfigureProviderAsync(actor.PrincipalId, projectId, providerId, request, Expected(http), Key(http), RequestId(http), TraceId(http), ct);
            http.Response.Headers.ETag = $"\"{result.Version}\"";
            return Ok(http, result, context);
        }, ct));
        return app;
    }

    private static async Task Events(HttpContext http, Guid projectId, Guid conversationId, string? after, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store"; http.Response.Headers.Vary = "Authorization, Cookie";
        try
        {
            var actor = await http.ResolveAtlasPrincipalAsync(ct);
            if (!long.TryParse(after ?? http.Request.Headers["Last-Event-ID"].ToString(), out var cursor) || cursor < 0) cursor = 0;
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.Connection = "keep-alive";
            await http.Response.StartAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                var events = await Store(http).EventsAfterAsync(actor.PrincipalId, projectId, conversationId, cursor, 100, ct);
                foreach (var item in events)
                {
                    cursor = item.Seq;
                    var payload = JsonSerializer.Serialize(new { item.EventId, item.Kind, item.Payload, item.CreatedAt }, Json);
                    await http.Response.WriteAsync($"id: {item.Seq}\nevent: {item.Kind}\ndata: {payload}\n\n", Encoding.UTF8, ct);
                    await http.Response.Body.FlushAsync(ct);
                }
                if (events.Any(e => e.Kind is "message.completed" or "run.failed" or "conversation.cancel_requested")) return;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                if (events.Count == 0) await http.Response.WriteAsync(": keep-alive\n\n", Encoding.UTF8, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is ProjectIdentityException or AtlasCommandException or UnauthorizedAccessException)
        {
            if (!http.Response.HasStarted) http.Response.StatusCode = error is ProjectIdentityException e ? e.StatusCode : error is AtlasCommandException a ? a.StatusCode : 401;
        }
    }

    private static async Task<IResult> Execute(HttpContext http, Func<AtlasPrincipal, Task<IResult>> action, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store"; http.Response.Headers.Vary = "Authorization, Cookie";
        try { return await action(await http.ResolveAtlasPrincipalAsync(ct)); }
        catch (ProjectIdentityException error) { return Error(http, error.Code, error.Message, error.StatusCode); }
        catch (AtlasCommandException error) { return Error(http, error.Code, error.Message, error.StatusCode); }
        catch (JsonException error) { return Error(http, "VALIDATION_FAILED", error.Message, 400); }
        catch (UnauthorizedAccessException error) { return Error(http, "UNAUTHORIZED", error.Message, 401); }
    }
    private static IResult Error(HttpContext http, string code, string message, int status) => Results.Json(new { code, message, requestId = RequestId(http), traceId = TraceId(http) }, statusCode: status);
    private static IResult Ok(HttpContext http, object data, ResolvedReadContext context) => Results.Ok(Envelope(http, data, context));
    private static object Envelope(HttpContext http, object data, ResolvedReadContext context) => new { code = "OK", requestId = RequestId(http), traceId = TraceId(http), context, data };
    private static object JobDto(JobInfo job) => new { job.JobId, job.Kind, job.State, job.Version, job.Attempt, job.Progress, job.ResultRef, job.ErrorCode };
    private static Task<ResolvedReadContext> Context(HttpContext http, AtlasPrincipal actor, Guid project, ReadContextRequest? request, CancellationToken ct) => Policy(http).CreateReadContextAsync(actor.PrincipalId, project, request ?? new(ReadMode.Published, ReadPurpose.Understand, null, null, null), ct);
    private static PostgresAssistantStore Store(HttpContext http) => http.RequestServices.GetRequiredService<PostgresAssistantStore>();
    private static PolicyService Policy(HttpContext http) => http.RequestServices.GetRequiredService<PolicyService>();
    private static PostgresAccessApplicabilityStore Access(HttpContext http) => http.RequestServices.GetRequiredService<PostgresAccessApplicabilityStore>();
    private static int Limit(int? limit) => limit is null ? 30 : limit is >= 1 and <= 100 ? limit.Value : throw ProjectIdentityException.Invalid("Page limit must be between 1 and 100.");
    private static string Key(HttpContext http) => http.Request.Headers["Idempotency-Key"].ToString();
    private static long Expected(HttpContext http) { var value = http.Request.Headers.IfMatch.ToString(); if (value.Length >= 3 && value[0] == '"' && value[^1] == '"' && long.TryParse(value[1..^1], out var version) && version >= 0) return version; throw new ProjectIdentityException("PRECONDITION_REQUIRED", "A valid quoted aggregate version is required in If-Match.", 428); }
    private static string RequestId(HttpContext http) => Activity.Current?.Id ?? http.TraceIdentifier;
    private static string? TraceId(HttpContext http) => Activity.Current?.TraceId.ToString();
}
