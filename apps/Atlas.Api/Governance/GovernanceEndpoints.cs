using System.Diagnostics;
using System.Text.Json;
using AccessApplicability.Contracts;
using Atlas.Api.Auth;
using Governance.Contracts;
using Governance.Infrastructure;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;

namespace Atlas.Api.Governance;

public static class GovernanceEndpoints
{
    public static WebApplication MapGovernanceEndpoints(this WebApplication app)
    {
        var read = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasRead");
        read.MapGet("/change-sets", (HttpContext context, Guid projectId, string? state, bool? mine, string? cursor, int? limit, CancellationToken ct) => Execute(context, async principal =>
        {
            var data = await Store(context).ListChangeSetsAsync(principal.PrincipalId, projectId, state, mine == true, cursor, PageLimit(limit), ct);
            return Results.Ok(Envelope(context, data));
        }, ct));
        read.MapGet("/change-sets/{changeSetId:guid}", (HttpContext context, Guid projectId, Guid changeSetId, CancellationToken ct) => Execute(context, async principal =>
        {
            var data = await Store(context).GetChangeSetAsync(principal.PrincipalId, projectId, changeSetId, ct);
            return Results.Ok(Envelope(context, data));
        }, ct));
        read.MapGet("/baselines", (HttpContext context, Guid projectId, string? cursor, int? limit, CancellationToken ct) => Execute(context, async principal =>
        {
            var data = await Store(context).ListBaselinesAsync(principal.PrincipalId, projectId, cursor, PageLimit(limit), ct);
            return Results.Ok(Envelope(context, data));
        }, ct));
        read.MapGet("/baselines/{baselineId:guid}/diff", (HttpContext context, Guid projectId, Guid baselineId, Guid? against, CancellationToken ct) => Execute(context, async principal =>
        {
            if (!against.HasValue) throw ProjectIdentityException.Invalid("The against baseline query parameter is required.");
            var data = await Store(context).DiffBaselineAsync(principal.PrincipalId, projectId, baselineId, against.Value, ct);
            return Results.Ok(Envelope(context, data));
        }, ct));

        var propose = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasPropose");
        propose.MapPost("/change-sets", (HttpContext context, Guid projectId, CreateChangeSetRequest? request, CancellationToken ct) => Execute(context, async principal =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A change-set body is required.");
            var result = await Store(context).CreateChangeSetAsync(principal, projectId, request, Key(context), RequestId(context), TraceId(context), ct);
            return Mutation(context, result, StatusCodes.Status201Created);
        }, ct));
        propose.MapPatch("/change-sets/{changeSetId:guid}", (HttpContext context, Guid projectId, Guid changeSetId, AmendChangeSetRequest? request, CancellationToken ct) => Execute(context, async principal =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("An amendment body is required.");
            var result = await Store(context).AmendChangeSetAsync(principal, projectId, changeSetId, request, ExpectedVersion(context), Key(context), RequestId(context), TraceId(context), ct);
            return Mutation(context, result);
        }, ct));
        propose.MapPost("/change-sets/{changeSetId:guid}/submit", (HttpContext context, Guid projectId, Guid changeSetId, SubmitChangeSetRequest? request, CancellationToken ct) => Execute(context, async principal =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A submission body is required.");
            var result = await Store(context).SubmitChangeSetAsync(principal, projectId, changeSetId, request, ExpectedVersion(context), Key(context), RequestId(context), TraceId(context), ct);
            return Mutation(context, result);
        }, ct));
        propose.MapPost("/change-sets/{changeSetId:guid}/reopen", (HttpContext context, Guid projectId, Guid changeSetId, ReopenChangeSetRequest? request, CancellationToken ct) => Execute(context, async principal =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A reopen body is required.");
            var result = await Store(context).ReopenChangeSetAsync(principal, projectId, changeSetId, request, ExpectedVersion(context), Key(context), RequestId(context), TraceId(context), ct);
            return Mutation(context, result);
        }, ct));
        propose.MapPost("/baselines/{baselineId:guid}/restore-proposal", (HttpContext context, Guid projectId, Guid baselineId, RestoreBaselineRequest? request, CancellationToken ct) => Execute(context, async principal =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A restore proposal body is required.");
            var result = await Store(context).PlanBaselineRestoreAsync(principal, projectId, baselineId, request, Key(context), RequestId(context), TraceId(context), ct);
            return Mutation(context, result, StatusCodes.Status201Created);
        }, ct));
        propose.MapPost("/proposals", (HttpContext context, Guid projectId, SubmitProposalRequest? request, CancellationToken ct) => Execute(context, async principal =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A proposal body is required.");
            var result = await Store(context).SubmitProposalAsync(principal, projectId, request, Key(context), RequestId(context), TraceId(context), ct);
            return Mutation(context, result, StatusCodes.Status201Created);
        }, ct));

        var review = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasReview");
        review.MapPost("/change-sets/{changeSetId:guid}/review", (HttpContext context, Guid projectId, Guid changeSetId, ReviewChangeSetRequest? request, CancellationToken ct) => Execute(context, async principal =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A review body is required.");
            var result = await Store(context).ReviewChangeSetAsync(principal, projectId, changeSetId, request, ExpectedVersion(context), Key(context), RequestId(context), TraceId(context), ct);
            return Mutation(context, result);
        }, ct));

        var publish = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasPublish");
        publish.MapPost("/change-sets/{changeSetId:guid}/publish", (HttpContext context, Guid projectId, Guid changeSetId, PublishChangeSetRequest? request, CancellationToken ct) => Execute(context, async principal =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A publication body is required.");
            var result = await Store(context).PublishChangeSetAsync(principal, projectId, changeSetId, request, ExpectedVersion(context), Key(context), RequestId(context), TraceId(context), ct);
            return Mutation(context, result);
        }, ct));
        return app;
    }

    private static IResult Mutation<T>(HttpContext context, GovernanceMutation<T> result, int status = StatusCodes.Status200OK)
    { context.Response.Headers.ETag = $"\"{result.ProjectVersion}\""; return Results.Json(Envelope(context, result.Data), statusCode: status); }
    private static async Task<IResult> Execute(HttpContext context, Func<AtlasPrincipal, Task<IResult>> action, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.Vary = "Authorization, Cookie";
        try { return await action(await context.ResolveAtlasPrincipalAsync(ct)); }
        catch (ProjectIdentityException exception) { return Results.Json(new { code = exception.Code, message = exception.Message, requestId = RequestId(context), traceId = TraceId(context) }, statusCode: exception.StatusCode); }
        catch (JsonException exception) { return Results.Json(new { code = "VALIDATION_FAILED", message = exception.Message, requestId = RequestId(context), traceId = TraceId(context) }, statusCode: 400); }
        catch (UnauthorizedAccessException exception) { return Results.Json(new { code = "UNAUTHORIZED", message = exception.Message, requestId = RequestId(context), traceId = TraceId(context) }, statusCode: 401); }
    }
    private static PostgresGovernanceStore Store(HttpContext context) => context.RequestServices.GetRequiredService<PostgresGovernanceStore>();
    private static object Envelope(HttpContext context, object? data) => new { code = "OK", requestId = RequestId(context), traceId = TraceId(context), data };
    private static string RequestId(HttpContext context) => Activity.Current?.Id ?? context.TraceIdentifier;
    private static string? TraceId(HttpContext context) => Activity.Current?.TraceId.ToString();
    private static string Key(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();
    private static int PageLimit(int? limit) => limit is null ? 30 : limit is >= 1 and <= 100 ? limit.Value : throw ProjectIdentityException.Invalid("Page limit must be between 1 and 100.");
    private static long ExpectedVersion(HttpContext context) { var value = context.Request.Headers.IfMatch.ToString(); if (value.Length >= 3 && value[0] == '"' && value[^1] == '"' && long.TryParse(value[1..^1], out var version) && version > 0) return version; throw new ProjectIdentityException("PRECONDITION_REQUIRED", "A valid quoted project version is required in If-Match.", 428); }
}
