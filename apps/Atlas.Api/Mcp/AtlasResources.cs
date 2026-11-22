using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using AccessApplicability.Application;
using AccessApplicability.Contracts;
using Atlas.Api.Auth;
using KnowledgeCore.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProjectIdentity.Application;

namespace Atlas.Api.Mcp;

[McpServerResourceType]
public sealed class AtlasResources
{
    [McpServerResource(UriTemplate = "atlas://projects/{projectId}/units/{unitId}{?baseline}", Name = "Authorized Atlas knowledge unit", MimeType = "application/json")]
    [Description("Reads an exact authorized knowledge revision. The URI never grants access; membership and resource policy are checked on every read.")]
    public static async Task<TextResourceContents> ReadUnit(
        string projectId, string unitId, string? baseline, ClaimsPrincipal user, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (user.Identity?.AuthenticationType != AtlasAuthentication.BearerScheme ||
            !user.FindAll("scope").Concat(user.FindAll("scp")).SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Contains("atlas.read", StringComparer.Ordinal))
            throw new UnauthorizedAccessException("An OAuth bearer token with atlas.read is required.");
        if (!Guid.TryParse(projectId, out var project) || !Guid.TryParse(unitId, out var unit) ||
            (baseline is not null && !Guid.TryParse(baseline, out _))) throw new McpException("The Atlas resource URI is invalid.");
        var actor = await user.ResolveAtlasPrincipalAsync(services, cancellationToken);
        var read = await services.GetRequiredService<PolicyService>().CreateReadContextAsync(actor.PrincipalId, project,
            new(baseline is null ? ReadMode.Published : ReadMode.Historical, ReadPurpose.Understand,
                baseline is null ? null : System.Guid.Parse(baseline), null, null), cancellationToken);
        var revision = await services.GetRequiredService<PostgresKnowledgeStore>().GetUnitAsync(read, unit, cancellationToken);
        return new TextResourceContents
        {
            Uri = $"atlas://projects/{project}/units/{unit}" + (baseline is null ? string.Empty : $"?baseline={baseline}"),
            MimeType = "application/json",
            Text = JsonSerializer.Serialize(new { code = "OK", context = read, data = revision })
        };
    }
}
