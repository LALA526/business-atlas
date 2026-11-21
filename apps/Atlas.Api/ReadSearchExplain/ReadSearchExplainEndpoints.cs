using System.Diagnostics;
using System.Text.Json;
using AccessApplicability.Application;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using Atlas.Api.Auth;
using PlatformOps.Jobs;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using ReadSearchExplain.Contracts;
using ReadSearchExplain.Export;
using ReadSearchExplain.Search;

namespace Atlas.Api.ReadSearchExplain;

public static class ReadSearchExplainEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static WebApplication MapReadSearchExplainEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasRead");
        group.MapGet("/dashboard", (HttpContext http, Guid projectId, string? mode, Guid? baselineId, Guid? changeSetId, string? purpose, string? context, CancellationToken ct) => Execute(http, async actor =>
        {
            var read = await Context(http, actor, projectId, mode, baselineId, changeSetId, purpose, context, ct);
            return Ok(http, await Reading(http).OverviewAsync(read, ct), read);
        }, ct));
        group.MapGet("/search", (HttpContext http, Guid projectId, string q, string? kind, string? mode, Guid? baselineId, Guid? changeSetId, string? purpose, string? context, string? cursor, int? limit, CancellationToken ct) => Execute(http, async actor =>
        {
            var read = await Context(http, actor, projectId, mode, baselineId, changeSetId, purpose, context, ct);
            return Ok(http, await Search(http).SearchAsync(read, q, kind, cursor, limit ?? 30, ct), read);
        }, ct));
        group.MapPost("/reading-plans", (HttpContext http, Guid projectId, ReadingPlanRequest? request, CancellationToken ct) => Execute(http, async actor =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A reading-plan body is required.");
            var read = await Context(http, actor, projectId, request.ReadContext, ct);
            return Ok(http, await Reading(http).BuildAsync(read, request.RootUnitId, request.Profile, request.Cursor, ct), read);
        }, ct));
        group.MapPost("/vocabulary/resolve", (HttpContext http, Guid projectId, VocabularyRequest? request, CancellationToken ct) => Execute(http, async actor =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A vocabulary body is required.");
            var read = await Context(http, actor, projectId, request.ReadContext, ct);
            return Ok(http, await Search(http).ResolveVocabularyAsync(read, request.Term, request.ContextKey, ct), read);
        }, ct));
        group.MapPost("/coverage", (HttpContext http, Guid projectId, CoverageRequest? request, CancellationToken ct) => Execute(http, async actor =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A coverage body is required.");
            var read = await Context(http, actor, projectId, request.ReadContext, ct);
            return Ok(http, await Reading(http).CoverageAsync(read, request.RootUnitId, ct), read);
        }, ct));
        group.MapPost("/content/review", (HttpContext http, Guid projectId, ContentReviewRequest? request, CancellationToken ct) => Execute(http, async actor =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A content-review body is required."); _ = Key(http);
            var read = await Context(http, actor, projectId, request.ReadContext, ct);
            if (request.Revision.UnitId.HasValue) await Access(http).AuthorizeReadAsync(read, request.Revision.UnitId.Value, "read", null, ct);
            var job = await Jobs(http).EnqueueAsync(projectId, actor.PrincipalId, new("knowledge.content_review", JsonSerializer.SerializeToElement(request, Json)), cancellationToken: ct);
            return Results.Json(Envelope(http, JobDto(job), read), statusCode: 202);
        }, ct));
        group.MapPost("/exports", (HttpContext http, Guid projectId, CreateExportRequest? request, CancellationToken ct) => Execute(http, async actor =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("An export body is required.");
            var read = await Context(http, actor, projectId, request.ReadContext, ct);
            var job = await Exports(http).CreateAsync(read, request, Key(http), ct);
            return Results.Json(Envelope(http, JobDto(job), read), statusCode: 202);
        }, ct));
        group.MapGet("/exports/{uploadId:guid}/content", (HttpContext http, Guid projectId, Guid uploadId, CancellationToken ct) => Execute(http, async actor =>
        {
            var artifact = await Exports(http).DownloadAsync(actor.PrincipalId, projectId, uploadId, ct);
            http.Response.Headers.CacheControl = "private, no-store"; http.Response.Headers.ContentDisposition = $"attachment; filename=\"{artifact.FileName}\"";
            return Results.File(artifact.Content, artifact.ContentType);
        }, ct));
        return app;
    }
    private static Task<ResolvedReadContext> Context(HttpContext http, AtlasPrincipal actor, Guid projectId, ReadContextRequest request, CancellationToken ct) => Policy(http).CreateReadContextAsync(actor.PrincipalId, projectId, request, ct);
    private static Task<ResolvedReadContext> Context(HttpContext http, AtlasPrincipal actor, Guid projectId, string? mode, Guid? baseline, Guid? changeSet, string? purpose, string? context, CancellationToken ct)
    {
        var readMode = Enum.TryParse<ReadMode>(mode ?? "published", true, out var parsedMode) ? parsedMode : throw ProjectIdentityException.Invalid("The read mode is invalid.");
        var readPurpose = Enum.TryParse<ReadPurpose>(purpose ?? "understand", true, out var parsedPurpose) ? parsedPurpose : throw ProjectIdentityException.Invalid("The read purpose is invalid.");
        ApplicableContext? applicable = null;
        if (!string.IsNullOrWhiteSpace(context)) applicable = JsonSerializer.Deserialize<ApplicableContext>(context, Json) ?? throw ProjectIdentityException.Invalid("The applicable context is invalid.");
        return Context(http, actor, projectId, new(readMode, readPurpose, baseline, changeSet, applicable), ct);
    }
    private static async Task<IResult> Execute(HttpContext http, Func<AtlasPrincipal, Task<IResult>> action, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store"; http.Response.Headers.Vary = "Authorization, Cookie";
        try { return await action(await http.ResolveAtlasPrincipalAsync(ct)); }
        catch (ProjectIdentityException error) { return Results.Json(new { code = error.Code, message = error.Message, requestId = RequestId(http), traceId = TraceId(http) }, statusCode: error.StatusCode); }
        catch (JsonException error) { return Results.Json(new { code = "VALIDATION_FAILED", message = error.Message, requestId = RequestId(http), traceId = TraceId(http) }, statusCode: 400); }
        catch (BuildingBlocks.Commands.AtlasCommandException error) { return Results.Json(new { code = error.Code, message = error.Message, requestId = RequestId(http), traceId = TraceId(http) }, statusCode: error.StatusCode); }
    }
    private static IResult Ok(HttpContext http, object data, ResolvedReadContext read) => Results.Ok(Envelope(http, data, read));
    private static object Envelope(HttpContext http, object data, ResolvedReadContext read) => new { code = "OK", requestId = RequestId(http), traceId = TraceId(http), context = read, data };
    private static object JobDto(JobInfo job) => new { job.JobId, job.Kind, job.State, job.Version, job.Attempt, job.Progress, job.ResultRef, job.ErrorCode };
    private static string Key(HttpContext http) { var key = http.Request.Headers["Idempotency-Key"].ToString(); if (key.Length is < 8 or > 128) throw ProjectIdentityException.Invalid("Idempotency-Key must contain 8-128 characters."); return key; }
    private static PolicyService Policy(HttpContext http) => http.RequestServices.GetRequiredService<PolicyService>();
    private static PostgresAccessApplicabilityStore Access(HttpContext http) => http.RequestServices.GetRequiredService<PostgresAccessApplicabilityStore>();
    private static PostgresReadSearchService Search(HttpContext http) => http.RequestServices.GetRequiredService<PostgresReadSearchService>();
    private static ReadingProjectionService Reading(HttpContext http) => http.RequestServices.GetRequiredService<ReadingProjectionService>();
    private static PostgresExportService Exports(HttpContext http) => http.RequestServices.GetRequiredService<PostgresExportService>();
    private static IJobQueue Jobs(HttpContext http) => http.RequestServices.GetRequiredService<IJobQueue>();
    private static string RequestId(HttpContext http) => Activity.Current?.Id ?? http.TraceIdentifier;
    private static string? TraceId(HttpContext http) => Activity.Current?.TraceId.ToString();
}
