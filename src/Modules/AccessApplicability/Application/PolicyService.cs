using System.Text.Json;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using ProjectIdentity.Application;

namespace AccessApplicability.Application;

public sealed class PolicyService(PostgresAccessApplicabilityStore store) : IReadContextFactory, IPolicyService
{
    public Task<ResolvedReadContext> CreateAsync(Guid principalId, Guid projectId, ReadContextRequest request, CancellationToken cancellationToken) =>
        store.CreateReadContextAsync(principalId, projectId, request, cancellationToken);

    public Task<ResolvedReadContext> CreateReadContextAsync(Guid principalId, Guid projectId, ReadContextRequest request, CancellationToken cancellationToken) =>
        store.CreateReadContextAsync(principalId, projectId, request, cancellationToken);

    public Task<ResourceAccess> AuthorizeReadAsync(ResolvedReadContext context, Guid unitId, string action, CancellationToken cancellationToken) =>
        store.AuthorizeReadAsync(context, unitId, action, null, cancellationToken);

    public async Task<(ResourceAccess From, ResourceAccess To)> AuthorizeRelationAsync(
        ResolvedReadContext context, Guid fromUnitId, Guid toUnitId, CancellationToken cancellationToken)
    {
        // Both calls use a fresh policy epoch check. Neither an invisible title nor a relation count is returned.
        var from = await store.AuthorizeReadAsync(context, fromUnitId, "read", null, cancellationToken);
        var to = await store.AuthorizeReadAsync(context, toUnitId, "read", null, cancellationToken);
        return (from, to);
    }

    public void AuthorizeCommand(ResolvedReadContext context, string action)
    {
        var allowed = action switch
        {
            "create_task" or "propose" => HasAny(context, "Contributor", "AgentProposer", "ProjectAdmin"),
            "review" => HasAny(context, "BusinessOwner", "TechOwner", "Verifier", "QA", "SecurityOwner", "DataOwner"),
            "publish" => HasAny(context, "KnowledgePublisher"),
            "administer" => HasAny(context, "ProjectAdmin"),
            _ => false
        };
        if (!allowed) throw ProjectIdentityException.Forbidden();
    }

    public async Task<JsonElement> ProjectFieldsAsync(ResolvedReadContext context, Guid unitId, JsonElement payload, CancellationToken cancellationToken)
    {
        var access = await store.AuthorizeReadAsync(context, unitId, "read", null, cancellationToken);
        var projected = Filter(payload, string.Empty, access.AllowedFieldPaths, access.DeniedFieldPaths);
        await store.EnsureCurrentAsync(context, cancellationToken);
        return projected;
    }

    public Task EnsureCurrentAsync(ResolvedReadContext context, CancellationToken cancellationToken) => store.EnsureCurrentAsync(context, ct: cancellationToken);

    public static JsonElement ProjectAuthorizedFields(ResourceAccess access, JsonElement payload) =>
        Filter(payload, string.Empty, access.AllowedFieldPaths, access.DeniedFieldPaths);

    private static bool HasAny(ResolvedReadContext context, params string[] roles) => context.Roles.Any(role => roles.Contains(role, StringComparer.Ordinal));

    private static JsonElement Filter(JsonElement value, string path, IReadOnlyList<string> allowed, IReadOnlyList<string> denied)
    {
        if (IsDenied(path, denied)) return Null();
        if (value.ValueKind == JsonValueKind.Object)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    var childPath = path + "/" + Escape(property.Name);
                    if (!MayBeAllowed(childPath, allowed) || IsDenied(childPath, denied)) continue;
                    var child = Filter(property.Value, childPath, allowed, denied);
                    if (IsEmptyUnauthorizedObject(property.Value, child)) continue;
                    writer.WritePropertyName(property.Name);
                    child.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    var childPath = path + "/" + index++;
                    if (MayBeAllowed(childPath, allowed) && !IsDenied(childPath, denied)) Filter(item, childPath, allowed, denied).WriteTo(writer);
                }
                writer.WriteEndArray();
            }
            return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
        }
        return MayBeAllowed(path, allowed) ? value.Clone() : Null();
    }

    private static bool MayBeAllowed(string path, IReadOnlyList<string> allowed)
    {
        if (allowed.Count == 0 || allowed.Contains("*", StringComparer.Ordinal)) return true;
        return allowed.Any(pointer => pointer == path || pointer.StartsWith(path + "/", StringComparison.Ordinal) ||
            path.Length > 0 && path.StartsWith(pointer + "/", StringComparison.Ordinal));
    }

    private static bool IsDenied(string path, IReadOnlyList<string> denied) => denied.Any(pointer =>
        pointer == "*" || pointer == path || path.StartsWith(pointer + "/", StringComparison.Ordinal));

    private static bool IsEmptyUnauthorizedObject(JsonElement original, JsonElement projected) =>
        original.ValueKind == JsonValueKind.Object && original.EnumerateObject().Any() && projected.ValueKind == JsonValueKind.Object && !projected.EnumerateObject().Any();
    private static JsonElement Null() => JsonDocument.Parse("null").RootElement.Clone();
    private static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
