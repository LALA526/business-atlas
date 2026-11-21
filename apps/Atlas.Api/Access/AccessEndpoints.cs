using System.Diagnostics;
using AccessApplicability.Application;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using Atlas.Api.Auth;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;

namespace Atlas.Api.Access;

public static class AccessEndpoints
{
    public static WebApplication MapAccessEndpoints(this WebApplication app)
    {
        var grants = app.MapGroup("/api/v1/projects/{projectId:guid}/grants").RequireAuthorization("AtlasAdmin");
        grants.MapPost("", async (HttpContext context, Guid projectId, ResourceGrant? request, CancellationToken ct) => Execute(context, async principal =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A grant request body is required.");
            var mutation = await Store(context).SetGrantAsync(principal, projectId, request, ExpectedVersion(context),
                IdempotencyKey(context), RequestId(context), TraceId(context), ct);
            context.Response.Headers.ETag = $"\"{mutation.PolicyVersion}\"";
            var readContext = await Policy(context).CreateReadContextAsync(principal.PrincipalId, projectId,
                new ReadContextRequest(ReadMode.Published, ReadPurpose.Understand, null, null, null), ct);
            return Results.Ok(Envelope(context, mutation.Grant, readContext));
        }, ct));
        grants.MapGet("", async (HttpContext context, Guid projectId, string? cursor, int? limit, CancellationToken ct) => Execute(context, async principal =>
        {
            var page = await Store(context).ListGrantsAsync(principal.PrincipalId, projectId, cursor, PageLimit(limit), ct);
            var readContext = await Policy(context).CreateReadContextAsync(principal.PrincipalId, projectId,
                new ReadContextRequest(ReadMode.Published, ReadPurpose.Understand, null, null, null), ct);
            return Results.Ok(Envelope(context, page, readContext));
        }, ct));

        app.MapPost("/api/v1/projects/{projectId:guid}/applicability/evaluate", async (HttpContext context,
            Guid projectId, ApplicabilityRequest? request, CancellationToken ct) => Execute(context, async principal =>
        {
            if (request?.Unit is null || request.ReadContext is null) throw ProjectIdentityException.Invalid("unit and readContext are required.");
            var readContext = await Policy(context).CreateReadContextAsync(principal.PrincipalId, projectId, request.ReadContext, ct);
            var access = await Store(context).AuthorizeReadAsync(readContext, request.Unit.UnitId, "read", request.Unit.RevisionId, ct);
            var result = ApplicabilityEvaluator.Evaluate(access.Applicability, readContext.Context);
            await Policy(context).EnsureCurrentAsync(readContext, ct);
            return Results.Ok(Envelope(context, result, readContext));
        }, ct)).RequireAuthorization("AtlasRead");
        return app;
    }

    private static async Task<IResult> Execute(HttpContext context, Func<AtlasPrincipal, Task<IResult>> action, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Vary = "Authorization, Cookie";
        try { return await action(await context.ResolveAtlasPrincipalAsync(ct)); }
        catch (ProjectIdentityException exception)
        {
            return Results.Json(new { code = exception.Code, message = exception.Message, requestId = RequestId(context), traceId = TraceId(context) }, statusCode: exception.StatusCode);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Results.Json(new { code = "UNAUTHORIZED", message = exception.Message, requestId = RequestId(context), traceId = TraceId(context) }, statusCode: 401);
        }
    }

    private static PostgresAccessApplicabilityStore Store(HttpContext context) => context.RequestServices.GetRequiredService<PostgresAccessApplicabilityStore>();
    private static IPolicyService Policy(HttpContext context) => context.RequestServices.GetRequiredService<IPolicyService>();
    private static object Envelope(HttpContext context, object data, ResolvedReadContext resolved) => new { code = "OK", requestId = RequestId(context), traceId = TraceId(context), context = new
    {
        resolved.ProjectId, resolved.BaselineId, mode = resolved.Mode, resolved.ChangeSetId, resolved.PolicyEpoch,
        contextHash = resolved.ContextHash, resolved.AsOf, purpose = resolved.Purpose
    }, data };
    private static string RequestId(HttpContext context) => Activity.Current?.Id ?? context.TraceIdentifier;
    private static string? TraceId(HttpContext context) => Activity.Current?.TraceId.ToString();
    private static string IdempotencyKey(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();
    private static int PageLimit(int? limit) => limit is null ? 30 : limit is >= 1 and <= 100 ? limit.Value : throw ProjectIdentityException.Invalid("Page limit must be between 1 and 100.");
    private static long ExpectedVersion(HttpContext context)
    {
        var value = context.Request.Headers.IfMatch.ToString();
        if (value.Length >= 3 && value[0] == '"' && value[^1] == '"' && long.TryParse(value[1..^1], out var version) && version > 0) return version;
        throw new ProjectIdentityException("PRECONDITION_REQUIRED", "A valid quoted project version is required in If-Match.", 428);
    }

    private sealed record ApplicabilityRequest(UnitRef? Unit, ReadContextRequest? ReadContext);
}
