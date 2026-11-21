using System.Diagnostics;
using System.Text.Json;
using AccessApplicability.Contracts;
using Atlas.Api.Auth;
using KnowledgeCore.Contracts;
using KnowledgeCore.Infrastructure;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;

namespace Atlas.Api.KnowledgeCore;

public static class KnowledgeEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static WebApplication MapKnowledgeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasRead");
        group.MapGet("/units", (HttpContext context, Guid projectId, string? kind, string? categoryKey, string? cursor, int? limit, CancellationToken ct) => Execute(context, async principal =>
        {
            var read = await ReadContextAsync(context, principal.PrincipalId, projectId, ct);
            var data = await Store(context).ListUnitsAsync(read, kind, categoryKey, cursor, PageLimit(limit), ct);
            return Results.Ok(Envelope(context, data, read));
        }, ct));
        group.MapGet("/units/{unitId:guid}", (HttpContext context, Guid projectId, Guid unitId, CancellationToken ct) => Execute(context, async principal =>
        {
            var read = await ReadContextAsync(context, principal.PrincipalId, projectId, ct);
            var data = await Store(context).GetUnitAsync(read, unitId, ct);
            return Results.Ok(Envelope(context, data, read));
        }, ct));
        group.MapGet("/units/{unitId:guid}/revisions", (HttpContext context, Guid projectId, Guid unitId, string? cursor, int? limit, CancellationToken ct) => Execute(context, async principal =>
        {
            var read = await ReadContextAsync(context, principal.PrincipalId, projectId, ct);
            var data = await Store(context).ListRevisionsAsync(read, unitId, cursor, PageLimit(limit), ct);
            return Results.Ok(Envelope(context, data, read));
        }, ct));
        group.MapGet("/relations", (HttpContext context, Guid projectId, Guid rootUnitId, string? direction, string? predicate, string? cursor, int? limit, CancellationToken ct) => Execute(context, async principal =>
        {
            var read = await ReadContextAsync(context, principal.PrincipalId, projectId, ct);
            var data = await Store(context).ListRelationsAsync(read, rootUnitId, direction ?? "both", predicate, cursor, PageLimit(limit), ct);
            return Results.Ok(Envelope(context, data, read));
        }, ct));
        group.MapPost("/change-sets/{changeSetId:guid}/units", (HttpContext context, Guid projectId, Guid changeSetId, RevisionInput? request, CancellationToken ct) => Execute(context, async principal =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A revision body is required.");
            var mutation = await Store(context).CreateDraftAsync(principal, projectId, changeSetId, request, ExpectedVersion(context), IdempotencyKey(context), RequestId(context), TraceId(context), ct);
            context.Response.Headers.ETag = $"\"{mutation.ProjectVersion}\"";
            var read = await Policy(context).CreateReadContextAsync(principal.PrincipalId, projectId, new(ReadMode.Draft, ReadPurpose.Change, null, changeSetId, null), ct);
            return Results.Json(Envelope(context, mutation.Revision, read), statusCode: StatusCodes.Status201Created);
        }, ct)).RequireAuthorization("AtlasPropose");
        group.MapPost("/change-sets/{changeSetId:guid}/units/{unitId:guid}/revisions", (HttpContext context, Guid projectId, Guid changeSetId, Guid unitId, RevisionInput? request, CancellationToken ct) => Execute(context, async principal =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A revision body is required.");
            var mutation = await Store(context).ReviseDraftAsync(principal, projectId, changeSetId, unitId, request, ExpectedVersion(context), IdempotencyKey(context), RequestId(context), TraceId(context), ct);
            context.Response.Headers.ETag = $"\"{mutation.ProjectVersion}\"";
            var read = await Policy(context).CreateReadContextAsync(principal.PrincipalId, projectId, new(ReadMode.Draft, ReadPurpose.Change, null, changeSetId, null), ct);
            return Results.Json(Envelope(context, mutation.Revision, read), statusCode: StatusCodes.Status201Created);
        }, ct)).RequireAuthorization("AtlasPropose");
        group.MapPost("/rules/{unitId:guid}/evaluate", (HttpContext context, Guid projectId, Guid unitId, RuleEvaluationRequest? request, CancellationToken ct) => Execute(context, async principal =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("readContext and facts are required.");
            var read = await Policy(context).CreateReadContextAsync(principal.PrincipalId, projectId, request.ReadContext, ct);
            var data = await Store(context).EvaluateRuleAsync(read, unitId, request.Facts, ct);
            return Results.Ok(Envelope(context, data, read));
        }, ct));
        return app;
    }

    private static async Task<ResolvedReadContext> ReadContextAsync(HttpContext context, Guid principalId, Guid projectId, CancellationToken ct)
    {
        var query = context.Request.Query;
        ApplicableContext? applicable = null;
        if (query["context"].FirstOrDefault() is { Length: > 0 } raw)
        {
            try { applicable = JsonSerializer.Deserialize<ApplicableContext>(raw, Json); }
            catch (JsonException) { throw ProjectIdentityException.Invalid("context must be a valid ApplicableContext JSON object."); }
        }
        return await Policy(context).CreateReadContextAsync(principalId, projectId,
            new(ParseEnum(query["mode"], ReadMode.Published, "mode"), ParseEnum(query["purpose"], ReadPurpose.Understand, "purpose"), ParseGuid(query["baselineId"], "baselineId"), ParseGuid(query["changeSetId"], "changeSetId"), applicable), ct);
    }

    private static async Task<IResult> Execute(HttpContext context, Func<AtlasPrincipal, Task<IResult>> action, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.Vary = "Authorization, Cookie";
        try { return await action(await context.ResolveAtlasPrincipalAsync(ct)); }
        catch (ProjectIdentityException exception) { return Results.Json(new { code = exception.Code, message = exception.Message, requestId = RequestId(context), traceId = TraceId(context) }, statusCode: exception.StatusCode); }
        catch (JsonException exception) { return Results.Json(new { code = "VALIDATION_FAILED", message = exception.Message, requestId = RequestId(context), traceId = TraceId(context) }, statusCode: 400); }
        catch (UnauthorizedAccessException exception) { return Results.Json(new { code = "UNAUTHORIZED", message = exception.Message, requestId = RequestId(context), traceId = TraceId(context) }, statusCode: 401); }
    }
    private static T ParseEnum<T>(string? value, T fallback, string name) where T : struct, Enum { if (string.IsNullOrEmpty(value)) return fallback; if (Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed)) return parsed; throw ProjectIdentityException.Invalid($"Query parameter {name} is invalid."); }
    private static Guid? ParseGuid(string? value, string name) { if (string.IsNullOrEmpty(value)) return null; if (Guid.TryParse(value, out var id)) return id; throw ProjectIdentityException.Invalid($"Query parameter {name} must be a UUID."); }
    private static PostgresKnowledgeStore Store(HttpContext context) => context.RequestServices.GetRequiredService<PostgresKnowledgeStore>();
    private static IPolicyService Policy(HttpContext context) => context.RequestServices.GetRequiredService<IPolicyService>();
    private static object Envelope(HttpContext context, object data, ResolvedReadContext resolved) => new { code = "OK", requestId = RequestId(context), traceId = TraceId(context), context = new { resolved.ProjectId, resolved.BaselineId, mode = resolved.Mode, resolved.ChangeSetId, resolved.PolicyEpoch, resolved.ContextHash, resolved.AsOf, purpose = resolved.Purpose }, data };
    private static string RequestId(HttpContext context) => Activity.Current?.Id ?? context.TraceIdentifier;
    private static string? TraceId(HttpContext context) => Activity.Current?.TraceId.ToString();
    private static string IdempotencyKey(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();
    private static int PageLimit(int? limit) => limit is null ? 30 : limit is >= 1 and <= 100 ? limit.Value : throw ProjectIdentityException.Invalid("Page limit must be between 1 and 100.");
    private static long ExpectedVersion(HttpContext context) { var value = context.Request.Headers.IfMatch.ToString(); if (value.Length >= 3 && value[0] == '"' && value[^1] == '"' && long.TryParse(value[1..^1], out var version) && version > 0) return version; throw new ProjectIdentityException("PRECONDITION_REQUIRED", "A valid quoted project version is required in If-Match.", 428); }
}
