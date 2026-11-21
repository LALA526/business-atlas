using System.Diagnostics;
using System.Security.Claims;
using Atlas.Api.Auth;
using BuildingBlocks.Commands;
using PlatformOps;
using PlatformOps.Jobs;
using ProjectIdentity.Contracts;
using ProjectIdentity.Application;

namespace Atlas.Api.Platform;

public static class PlatformEndpoints
{
    public static WebApplication MapPlatformEndpoints(this WebApplication app)
    {
        var patterns = app.MapGroup("/api/v1/projects/{projectId:guid}/error-patterns").RequireAuthorization("AtlasRead");
        patterns.MapGet("", async (HttpContext context, Guid projectId, string? cursor, int? limit, string? kind, string? path, CancellationToken ct) =>
            Execute(context, async principal => Results.Ok(Envelope(context, await Patterns(context).ListAsync(projectId, principal.PrincipalId, cursor, limit ?? 30, kind, path, ct))), ct));
        patterns.MapPost("", async (HttpContext context, Guid projectId, ErrorPattern? request, CancellationToken ct) => Execute(context, async principal =>
        {
            RequireScope(context, "atlas.propose");
            if (request is null) throw new AtlasCommandException("VALIDATION_FAILED", "An error pattern is required.", 400);
            var result = await Patterns(context).ProposeAsync(projectId, principal.PrincipalId, request, Key(context), RequestId(context), TraceId(context), ct);
            return Results.Created($"/api/v1/projects/{projectId}/error-patterns/{result.PatternId}", Envelope(context, result));
        }, ct));
        patterns.MapPost("/{patternId:guid}/review", async (HttpContext context, Guid projectId, Guid patternId, ErrorPatternReviewRequest? request, CancellationToken ct) => Execute(context, async principal =>
        {
            RequireScope(context, "atlas.review");
            if (request is null) throw new AtlasCommandException("VALIDATION_FAILED", "A review decision and reason are required.", 400);
            var result = await Patterns(context).ReviewAsync(projectId, principal.PrincipalId, principal.Kind == PrincipalKind.Human, patternId,
                ExpectedRevision(context), request.Decision, request.Reason, Key(context), RequestId(context), TraceId(context), ct);
            context.Response.Headers.ETag = $"\"{result.PatternRevision}\"";
            return Results.Ok(Envelope(context, result));
        }, ct));

        var jobs = app.MapGroup("/api/v1/projects/{projectId:guid}/jobs").RequireAuthorization("AtlasRead");
        jobs.MapGet("/{jobId:guid}", async (HttpContext context, Guid projectId, Guid jobId, CancellationToken ct) => Execute(context, async principal =>
        {
            var result = await Jobs(context).GetAsync(projectId, principal.PrincipalId, jobId, ct); context.Response.Headers.ETag = $"\"{result.Version}\"";
            return Results.Ok(Envelope(context, JobDto(result)));
        }, ct));
        jobs.MapGet("/{jobId:guid}/result", async (HttpContext context, Guid projectId, Guid jobId, CancellationToken ct) => Execute(context, async principal =>
            Results.Ok(Envelope(context, await Jobs(context).GetResultAsync(projectId, principal.PrincipalId, jobId, ct))), ct));
        jobs.MapPost("/{jobId:guid}/cancel", async (HttpContext context, Guid projectId, Guid jobId, JobControlRequest? request, CancellationToken ct) => Execute(context, async principal =>
        {
            if (request is null) throw new AtlasCommandException("VALIDATION_FAILED", "A cancellation reason is required.", 400);
            var result = await Jobs(context).CancelAsync(projectId, principal.PrincipalId, jobId, ExpectedVersion(context), request.Reason, Key(context), RequestId(context), TraceId(context), ct);
            context.Response.Headers.ETag = $"\"{result.Version}\""; return Results.Ok(Envelope(context, JobDto(result)));
        }, ct));
        jobs.MapPost("/{jobId:guid}/retry", async (HttpContext context, Guid projectId, Guid jobId, JobControlRequest? request, CancellationToken ct) => Execute(context, async principal =>
        {
            if (request is null) throw new AtlasCommandException("VALIDATION_FAILED", "A retry reason is required.", 400);
            var result = await Jobs(context).RetryAsync(projectId, principal.PrincipalId, jobId, ExpectedVersion(context), request.Reason, Key(context), RequestId(context), TraceId(context), ct);
            context.Response.Headers.ETag = $"\"{result.Version}\""; return Results.Ok(Envelope(context, JobDto(result)));
        }, ct));

        app.MapGet("/api/v1/projects/{projectId:guid}/audit", async (HttpContext context, Guid projectId, string? cursor, int? limit, CancellationToken ct) => Execute(context, async principal =>
        {
            RequireScope(context, "atlas.admin");
            return Results.Ok(Envelope(context, await context.RequestServices.GetRequiredService<AuditQueryService>().ListAsync(projectId, principal.PrincipalId,
                Scopes(context, includeCookieAdministrativeAccess: true).ToArray(), cursor, limit ?? 30, ct)));
        }, ct)).RequireAuthorization("AtlasRead");
        return app;
    }

    private static object JobDto(JobInfo job) => new { job.JobId, job.Kind, job.State, job.Version, job.Attempt, job.Progress, job.ResultRef, job.ErrorCode };
    private static PostgresJobStore Jobs(HttpContext context) => context.RequestServices.GetRequiredService<PostgresJobStore>();
    private static PostgresErrorPatternStore Patterns(HttpContext context) => context.RequestServices.GetRequiredService<PostgresErrorPatternStore>();
    private static object Envelope(HttpContext context, object data) => new { code = "OK", requestId = RequestId(context), traceId = TraceId(context), data };
    private static string RequestId(HttpContext context) => Activity.Current?.Id ?? context.TraceIdentifier;
    private static string? TraceId(HttpContext context) => Activity.Current?.TraceId.ToString();
    private static string Key(HttpContext context)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 128)
            throw new AtlasCommandException("VALIDATION_FAILED", "Idempotency-Key must contain 8 to 128 characters.", 400);
        return key;
    }
    private static long ExpectedVersion(HttpContext context) => Expected(context);
    private static int ExpectedRevision(HttpContext context) => checked((int)Expected(context));
    private static long Expected(HttpContext context)
    {
        var etag = context.Request.Headers.IfMatch.ToString();
        if (etag.Length >= 3 && etag[0] == '"' && etag[^1] == '"' && long.TryParse(etag[1..^1], out var value) && value > 0) return value;
        throw new AtlasCommandException("PRECONDITION_REQUIRED", "A valid quoted resource version is required in If-Match.", 428);
    }
    private static IEnumerable<string> Scopes(HttpContext context, bool includeCookieAdministrativeAccess = false)
    {
        var scopes = context.User.FindAll("scope").Concat(context.User.FindAll("scp"))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (includeCookieAdministrativeAccess && context.User.Identity?.AuthenticationType == AtlasAuthentication.CookieScheme)
            scopes = scopes.Append("atlas.admin");
        return scopes;
    }
    private static void RequireScope(HttpContext context, string scope)
    {
        if (context.User.Identity?.AuthenticationType == AtlasAuthentication.CookieScheme) return;
        if (!Scopes(context).Contains(scope, StringComparer.Ordinal)) throw new AtlasCommandException("FORBIDDEN", "The required scope is not granted.", 403);
    }
    private static async Task<IResult> Execute(HttpContext context, Func<AtlasPrincipal, Task<IResult>> action, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.Vary = "Authorization, Cookie";
        try { return await action(await context.ResolveAtlasPrincipalAsync(ct)); }
        catch (AtlasCommandException error) { return Error(context, error.Code, error.Message, error.StatusCode); }
        catch (ProjectIdentityException error) { return Error(context, error.Code, error.Message, error.StatusCode); }
        catch (UnauthorizedAccessException error) { return Error(context, "UNAUTHORIZED", error.Message, 401); }
    }
    private static IResult Error(HttpContext context, string code, string message, int status) => Results.Json(new { code, message, requestId = RequestId(context), traceId = TraceId(context) }, statusCode: status);
    private sealed record JobControlRequest(string Reason);
    private sealed record ErrorPatternReviewRequest(string Reason, string Decision);
}
