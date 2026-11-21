using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using ProjectIdentity.Infrastructure;
using Atlas.Api.Hubs;

namespace Atlas.Api.Auth;

public static class IdentityEndpoints
{
    public static WebApplication MapIdentityEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/login", (string? returnUrl) =>
        {
            var target = SafeReturnUrl(returnUrl);
            return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [AtlasAuthentication.OidcScheme]);
        }).AllowAnonymous();

        app.MapGet("/auth/session", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var principal = await context.ResolveAtlasPrincipalAsync(cancellationToken);
            var ticket = await context.AuthenticateAsync(AtlasAuthentication.CookieScheme);
            var csrf = context.User.FindFirst(AtlasAuthentication.CsrfClaim)?.Value;
            if (csrf is null || !ticket.Succeeded || ticket.Properties is null) return Results.Unauthorized();
            return Results.Ok(new
            {
                principalId = principal.PrincipalId,
                displayName = context.User.Identity?.Name ?? context.User.FindFirst("sub")!.Value,
                expiresAt = ticket.Properties.ExpiresUtc,
                csrfToken = csrf
            });
        }).RequireAuthorization();

        app.MapPost("/auth/logout", () => Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [AtlasAuthentication.CookieScheme, AtlasAuthentication.OidcScheme])).RequireAuthorization();

        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization("AtlasRead");
        projects.MapGet("/", async (HttpContext context, string? cursor, int? limit, CancellationToken ct) => await Execute(context, async principal =>
            Results.Ok(Envelope(context, await Store(context).ListProjectsAsync(principal.PrincipalId, cursor, PageLimit(limit), ct))), ct));
        projects.MapPost("/", async (HttpContext context, CreateProjectRequest? request, CancellationToken ct) => await Execute(context, async principal =>
        {
            if (request is null) throw ProjectIdentityException.Invalid("A project request body is required.");
            RequireAdminScope(context);
            var result = await Store(context).CreateProjectAsync(principal,
                new CreateProjectCommand(request.DisplayName, request.Slug, IdempotencyKey(context)), RequestId(context), TraceId(context), ct);
            context.Response.Headers.ETag = ETag(result.Version);
            return Results.Created($"/api/v1/projects/{result.ProjectId}", Envelope(context, result));
        }, ct));

        projects.MapGet("/{projectId:guid}", async (HttpContext context, Guid projectId, CancellationToken ct) => await Execute(context, async principal =>
        {
            var result = await Store(context).GetProjectAsync(principal.PrincipalId, projectId, ct);
            context.Response.Headers.ETag = ETag(result.Version);
            return Results.Ok(Envelope(context, result));
        }, ct));

        projects.MapPatch("/{projectId:guid}", async (HttpContext context, Guid projectId, UpdateProjectRequest? request, CancellationToken ct) => await Execute(context, async principal =>
        {
            RequireAdminScope(context);
            if (request is null) throw ProjectIdentityException.Invalid("A project request body is required.");
            var result = await Store(context).UpdateProjectAsync(principal, projectId,
                new UpdateProjectCommand(request.DisplayName, ExpectedVersion(context), IdempotencyKey(context)), RequestId(context), TraceId(context), ct);
            context.Response.Headers.ETag = ETag(result.Version);
            return Results.Ok(Envelope(context, result));
        }, ct));

        projects.MapGet("/{projectId:guid}/members", async (HttpContext context, Guid projectId, string? cursor, int? limit, CancellationToken ct) => await Execute(context, async principal =>
        {
            RequireAdminScope(context);
            return Results.Ok(Envelope(context, await Store(context).ListMembersAsync(principal.PrincipalId, projectId, cursor, PageLimit(limit), ct)));
        }, ct));

        projects.MapPut("/{projectId:guid}/members/{principalId:guid}", async (HttpContext context, Guid projectId, Guid principalId, MembershipRequest? request, CancellationToken ct) => await Execute(context, async principal =>
        {
            RequireAdminScope(context);
            if (request is null) throw ProjectIdentityException.Invalid("A membership request body is required.");
            var result = await Store(context).ChangeMembershipAsync(principal, projectId,
                new ChangeMembershipCommand(principalId, request.Roles, request.Clearance, request.Active, ExpectedVersion(context), IdempotencyKey(context)),
                RequestId(context), TraceId(context), ct);
            context.Response.Headers.ETag = ETag(result.PolicyVersion);
            await context.RequestServices.GetRequiredService<ProjectRealtimeNotifier>().RevokePrincipalAsync(projectId, principalId, ct);
            return Results.Ok(Envelope(context, result.Membership));
        }, ct));

        projects.MapDelete("/{projectId:guid}/members/{principalId:guid}", async (HttpContext context, Guid projectId, Guid principalId, CancellationToken ct) => await Execute(context, async principal =>
        {
            RequireAdminScope(context);
            var version = await Store(context).RemoveMembershipAsync(principal, projectId, principalId,
                ExpectedVersion(context), IdempotencyKey(context), RequestId(context), TraceId(context), ct);
            context.Response.Headers.ETag = ETag(version);
            await context.RequestServices.GetRequiredService<ProjectRealtimeNotifier>().RevokePrincipalAsync(projectId, principalId, ct);
            return Results.Ok(Envelope(context, new { id = principalId, version }));
        }, ct));
        return app;
    }

    private static async Task<IResult> Execute(HttpContext context, Func<AtlasPrincipal, Task<IResult>> action, CancellationToken cancellationToken)
    {
        try
        {
            var principal = await context.ResolveAtlasPrincipalAsync(cancellationToken);
            return await action(principal);
        }
        catch (ProjectIdentityException exception)
        {
            return Error(context, exception.Code, exception.Message, exception.StatusCode);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Error(context, "UNAUTHORIZED", exception.Message, StatusCodes.Status401Unauthorized);
        }
    }

    private static PostgresProjectIdentityStore Store(HttpContext context) => context.RequestServices.GetRequiredService<PostgresProjectIdentityStore>();
    private static string RequestId(HttpContext context) => Activity.Current?.Id ?? context.TraceIdentifier;
    private static string? TraceId(HttpContext context) => Activity.Current?.TraceId.ToString();
    private static object Envelope(HttpContext context, object data) => new { code = "OK", requestId = RequestId(context), traceId = TraceId(context), data };
    private static IResult Error(HttpContext context, string code, string message, int status) => Results.Json(new { code, message, requestId = RequestId(context), traceId = TraceId(context) }, statusCode: status);
    private static string ETag(long version) => $"\"{version}\"";
    private static int PageLimit(int? limit) => limit is null ? 30 : limit is >= 1 and <= 100 ? limit.Value : throw ProjectIdentityException.Invalid("Page limit must be between 1 and 100.");
    private static string IdempotencyKey(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();
    private static long ExpectedVersion(HttpContext context)
    {
        var value = context.Request.Headers.IfMatch.ToString();
        if (value.Length >= 3 && value[0] == '"' && value[^1] == '"' && long.TryParse(value[1..^1], out var version) && version > 0) return version;
        throw new ProjectIdentityException("PRECONDITION_REQUIRED", "A valid quoted project version is required in If-Match.", 428);
    }
    private static string SafeReturnUrl(string? returnUrl) => string.IsNullOrEmpty(returnUrl) || !Uri.IsWellFormedUriString(returnUrl, UriKind.Relative) ||
        !returnUrl.StartsWith('/') || returnUrl.StartsWith("//") || returnUrl.Contains('\\') ? "/" : returnUrl;
    private static void RequireAdminScope(HttpContext context)
    {
        if (context.User.Identity?.AuthenticationType == AtlasAuthentication.CookieScheme) return;
        var scopes = context.User.FindAll("scope").Concat(context.User.FindAll("scp"))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!scopes.Contains("atlas.admin", StringComparer.Ordinal)) throw ProjectIdentityException.Forbidden();
    }

    private sealed record CreateProjectRequest(string DisplayName, string Slug);
    private sealed record UpdateProjectRequest(string DisplayName);
    private sealed record MembershipRequest(IReadOnlyList<string> Roles, int Clearance, bool Active);
}
