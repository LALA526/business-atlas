using System.Diagnostics;
using System.Text.Json;
using AccessApplicability.Contracts;
using Atlas.Api.Auth;
using ImportEvidence.Contracts;
using ImportEvidence.Infrastructure;
using Microsoft.AspNetCore.StaticFiles;
using ProjectIdentity.Application;

namespace Atlas.Api.ImportEvidence;

public static class ImportEvidenceEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static WebApplication MapImportEvidenceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/projects/{projectId:guid}").RequireAuthorization("AtlasRead");
        group.MapPost("/sources/uploads", UploadSource).RequireAuthorization("AtlasPropose");
        group.MapGet("/sources/uploads/{uploadId:guid}", (HttpContext context, Guid projectId, Guid uploadId, CancellationToken ct) => Execute(context, async principal =>
        { var data = await Store(context).GetUploadAsync(principal.PrincipalId, projectId, uploadId, ct); context.Response.Headers.ETag = $"\"{data.Version}\""; return Results.Ok(Envelope(context, data)); }, ct)).RequireAuthorization("AtlasPropose");
        group.MapGet("/sources/{sourceId:guid}", (HttpContext context, Guid projectId, Guid sourceId, CancellationToken ct) => Execute(context, async principal =>
        { var read = await ReadContextAsync(context, principal.PrincipalId, projectId, ct); var data = await Store(context).GetSourceAsync(read, sourceId, ct); return Results.Ok(Envelope(context, data, read)); }, ct));
        group.MapGet("/sources/{sourceId:guid}/content", (HttpContext context, Guid projectId, Guid sourceId, CancellationToken ct) => Execute(context, async principal =>
        { var read = await ReadContextAsync(context, principal.PrincipalId, projectId, ct); var data = await Store(context).ReadSourceContentAsync(read, sourceId, ct); context.Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = data.FileName }.ToString(); context.Response.Headers["X-Content-SHA256"] = data.Sha256; context.Response.ContentLength = data.ByteSize; return Results.Stream(data.Content, data.ContentType); }, ct));
        group.MapPost("/imports/upload", UploadPackage).RequireAuthorization("AtlasPropose");
        group.MapGet("/imports", (HttpContext context, Guid projectId, string? cursor, int? limit, CancellationToken ct) => Execute(context, async principal =>
        { var data = await Store(context).ListImportsAsync(principal.PrincipalId, projectId, cursor, PageLimit(limit), ct); return Results.Ok(Envelope(context, data)); }, ct)).RequireAuthorization("AtlasPropose");
        group.MapGet("/imports/{importId:guid}/preview", (HttpContext context, Guid projectId, Guid importId, CancellationToken ct) => Execute(context, async principal =>
        { var data = await Store(context).PreviewAsync(principal.PrincipalId, projectId, importId, ct); context.Response.Headers.ETag = $"\"{data.Version}\""; return Results.Ok(Envelope(context, data)); }, ct)).RequireAuthorization("AtlasPropose");
        group.MapPost("/imports/{importId:guid}/decisions", (HttpContext context, Guid projectId, Guid importId, ImportDecisionsRequest? request, CancellationToken ct) => Execute(context, async principal =>
        { if (request is null) throw ProjectIdentityException.Invalid("Decisions are required."); var data = await Store(context).DecideAsync(principal, projectId, importId, request, ExpectedVersion(context), IdempotencyKey(context), RequestId(context), TraceId(context), ct); context.Response.Headers.ETag = $"\"{data.Version}\""; return Results.Ok(Envelope(context, data)); }, ct)).RequireAuthorization("AtlasPropose");
        group.MapPost("/imports/{importId:guid}/stage", (HttpContext context, Guid projectId, Guid importId, StageImportRequest? request, CancellationToken ct) => Execute(context, async principal =>
        { var data = await Store(context).StageAsync(principal, projectId, importId, request?.ChangeSetId, ExpectedVersion(context), IdempotencyKey(context), RequestId(context), TraceId(context), ct); return Results.Ok(Envelope(context, data)); }, ct)).RequireAuthorization("AtlasPropose");
        group.MapPost("/imports/{importId:guid}/discard", (HttpContext context, Guid projectId, Guid importId, DiscardImportRequest? request, CancellationToken ct) => Execute(context, async principal =>
        { if (request is null) throw ProjectIdentityException.Invalid("A discard reason is required."); var data = await Store(context).DiscardAsync(principal, projectId, importId, request.Reason, ExpectedVersion(context), IdempotencyKey(context), RequestId(context), TraceId(context), ct); context.Response.Headers.ETag = $"\"{data.Version}\""; return Results.Ok(Envelope(context, data)); }, ct)).RequireAuthorization("AtlasPropose");
        return app;
    }
    private static Task<IResult> UploadSource(HttpContext context, Guid projectId, CancellationToken ct) => Execute(context, async principal =>
    {
        var form = await context.Request.ReadFormAsync(ct); var file = SingleFile(form); var kind = Required(form, "sourceKind"); var hash = form["sha256"].FirstOrDefault();
        await using var stream = file.OpenReadStream();
        var data = await Store(context).UploadSourceAsync(principal, projectId, file.FileName, file.ContentType, kind, hash, stream, IdempotencyKey(context), RequestId(context), TraceId(context), ct);
        return Results.Json(Envelope(context, data), statusCode: StatusCodes.Status202Accepted);
    }, ct);
    private static Task<IResult> UploadPackage(HttpContext context, Guid projectId, CancellationToken ct) => Execute(context, async principal =>
    {
        var form = await context.Request.ReadFormAsync(ct); var file = SingleFile(form); if (!Guid.TryParse(Required(form, "baseBaselineId"), out var baseline)) throw ProjectIdentityException.Invalid("baseBaselineId must be a UUID.");
        await using var stream = file.OpenReadStream();
        var data = await Store(context).UploadPackageAsync(principal, projectId, file.FileName, file.ContentType, baseline, stream, IdempotencyKey(context), RequestId(context), TraceId(context), ct);
        return Results.Json(Envelope(context, data), statusCode: StatusCodes.Status202Accepted);
    }, ct);
    private static IFormFile SingleFile(IFormCollection form) { if (form.Files.Count != 1 || form.Files[0].Length == 0 || form.Files[0].Name != "file") throw ProjectIdentityException.Invalid("Exactly one non-empty multipart field named 'file' is required."); return form.Files[0]; }
    private static string Required(IFormCollection form, string key) => form[key].FirstOrDefault() is { Length: > 0 } value ? value : throw ProjectIdentityException.Invalid($"Multipart field '{key}' is required.");
    private static async Task<ResolvedReadContext> ReadContextAsync(HttpContext context, Guid principal, Guid project, CancellationToken ct)
    {
        var query = context.Request.Query; ApplicableContext? applicable = null;
        if (query["context"].FirstOrDefault() is { Length: > 0 } raw) { try { applicable = JsonSerializer.Deserialize<ApplicableContext>(raw, Json); } catch (JsonException) { throw ProjectIdentityException.Invalid("context must be valid ApplicableContext JSON."); } }
        return await context.RequestServices.GetRequiredService<IPolicyService>().CreateReadContextAsync(principal, project, new(ParseEnum(query["mode"], ReadMode.Published, "mode"), ParseEnum(query["purpose"], ReadPurpose.Understand, "purpose"), ParseGuid(query["baselineId"], "baselineId"), ParseGuid(query["changeSetId"], "changeSetId"), applicable), ct);
    }
    private static async Task<IResult> Execute(HttpContext context, Func<ProjectIdentity.Contracts.AtlasPrincipal, Task<IResult>> action, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.Vary = "Authorization, Cookie";
        try { return await action(await context.ResolveAtlasPrincipalAsync(ct)); }
        catch (ProjectIdentityException exception) { return Results.Json(new { code = exception.Code, message = exception.Message, requestId = RequestId(context), traceId = TraceId(context) }, statusCode: exception.StatusCode); }
        catch (JsonException exception) { return Results.Json(new { code = "VALIDATION_FAILED", message = exception.Message, requestId = RequestId(context), traceId = TraceId(context) }, statusCode: 400); }
        catch (UnauthorizedAccessException exception) { return Results.Json(new { code = "UNAUTHORIZED", message = exception.Message, requestId = RequestId(context), traceId = TraceId(context) }, statusCode: 401); }
        catch (InvalidDataException exception) { return Results.Json(new { code = "VALIDATION_FAILED", message = exception.Message, requestId = RequestId(context), traceId = TraceId(context) }, statusCode: 400); }
    }
    private static PostgresImportEvidenceStore Store(HttpContext context) => context.RequestServices.GetRequiredService<PostgresImportEvidenceStore>();
    private static object Envelope(HttpContext context, object data, ResolvedReadContext? read = null) => new { code = "OK", requestId = RequestId(context), traceId = TraceId(context), context = read is null ? null : new { read.ProjectId, read.BaselineId, mode = read.Mode, read.ChangeSetId, read.PolicyEpoch, read.ContextHash, read.AsOf, read.Purpose }, data };
    private static string RequestId(HttpContext context) => Activity.Current?.Id ?? context.TraceIdentifier; private static string? TraceId(HttpContext context) => Activity.Current?.TraceId.ToString(); private static string IdempotencyKey(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();
    private static long ExpectedVersion(HttpContext context) { var value = context.Request.Headers.IfMatch.ToString(); if (value.Length >= 3 && value[0] == '"' && value[^1] == '"' && long.TryParse(value[1..^1], out var version) && version > 0) return version; throw new ProjectIdentityException("PRECONDITION_REQUIRED", "A valid quoted import version is required in If-Match.", 428); }
    private static int PageLimit(int? limit) => limit is null ? 30 : limit is >= 1 and <= 100 ? limit.Value : throw ProjectIdentityException.Invalid("Page limit must be between 1 and 100.");
    private static T ParseEnum<T>(string? value, T fallback, string name) where T : struct, Enum { if (string.IsNullOrEmpty(value)) return fallback; if (Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed)) return parsed; throw ProjectIdentityException.Invalid($"Query parameter {name} is invalid."); }
    private static Guid? ParseGuid(string? value, string name) { if (string.IsNullOrEmpty(value)) return null; if (Guid.TryParse(value, out var id)) return id; throw ProjectIdentityException.Invalid($"Query parameter {name} must be a UUID."); }
}
