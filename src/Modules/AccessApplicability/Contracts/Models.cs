using System.Text.Json;
using System.Text.Json.Serialization;

namespace AccessApplicability.Contracts;

public enum ReadMode { Published, Historical, Draft }

public enum ReadPurpose { Understand, Implement, Change, Verify, Review, Export }

public sealed record ApplicableContext(
    string? ContextKey,
    string? Organization,
    string? Product,
    string? Environment,
    DateTimeOffset? AsOf,
    IReadOnlyDictionary<string, JsonElement>? Facts);

public sealed record ReadContextRequest(
    [property: JsonRequired] ReadMode Mode,
    [property: JsonRequired] ReadPurpose Purpose,
    Guid? BaselineId,
    Guid? ChangeSetId,
    ApplicableContext? Context);

public sealed record ResolvedReadContext(
    Guid ProjectId,
    Guid PrincipalId,
    Guid BaselineId,
    ReadMode Mode,
    Guid? ChangeSetId,
    long PolicyEpoch,
    IReadOnlyList<string> Roles,
    int Clearance,
    string ContextHash,
    DateTimeOffset AsOf,
    ReadPurpose Purpose,
    ApplicableContext Context);

public sealed record UnitRef(Guid UnitId, Guid? RevisionId);

public sealed record ResourceGrant(
    Guid? GrantId,
    [property: JsonRequired] Guid UnitId,
    [property: JsonRequired] Guid PrincipalId,
    [property: JsonRequired] string Action,
    [property: JsonRequired] IReadOnlyList<string> FieldPaths,
    [property: JsonRequired] string Effect,
    long? Version);

public sealed record ResourceGrantPage(IReadOnlyList<ResourceGrant> Items, string? NextCursor);
public sealed record GrantMutation(ResourceGrant Grant, long PolicyVersion);

public enum ApplicabilityStatus { Applicable, NotApplicable, Unknown, Conflict }

public sealed record ApplicabilityResult(ApplicabilityStatus Status, IReadOnlyList<string> Reasons);

public sealed record ResourceAccess(
    Guid UnitId,
    Guid RevisionId,
    JsonElement Applicability,
    IReadOnlyList<string> AllowedFieldPaths,
    IReadOnlyList<string> DeniedFieldPaths);

public interface IReadContextFactory
{
    Task<ResolvedReadContext> CreateAsync(
        Guid principalId, Guid projectId, ReadContextRequest request, CancellationToken cancellationToken);
}

public interface IPolicyService
{
    Task<ResolvedReadContext> CreateReadContextAsync(
        Guid principalId, Guid projectId, ReadContextRequest request, CancellationToken cancellationToken);

    Task<ResourceAccess> AuthorizeReadAsync(
        ResolvedReadContext context, Guid unitId, string action, CancellationToken cancellationToken);

    Task<(ResourceAccess From, ResourceAccess To)> AuthorizeRelationAsync(
        ResolvedReadContext context, Guid fromUnitId, Guid toUnitId, CancellationToken cancellationToken);

    void AuthorizeCommand(ResolvedReadContext context, string action);
    Task<JsonElement> ProjectFieldsAsync(ResolvedReadContext context, Guid unitId, JsonElement payload, CancellationToken cancellationToken);
    Task EnsureCurrentAsync(ResolvedReadContext context, CancellationToken cancellationToken);
}
