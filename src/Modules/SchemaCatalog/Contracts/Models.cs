using System.Text.Json;

namespace SchemaCatalog.Contracts;

public sealed record RegistryDimension(string Key, string Label);
public sealed record CatalogNode(string Key, string Label, IReadOnlyList<CatalogNode>? Children = null, string? Description = null);

public sealed record RevisionInput(
    Guid? UnitId,
    string? Title,
    string? Kind,
    string? SchemaVersion,
    string? FactKind,
    string? Verification,
    JsonElement Applicability,
    JsonElement Body,
    JsonElement Blocks,
    JsonElement Evidence,
    JsonElement Unknowns,
    JsonElement CategoryKeys);

public sealed record RegistryProposal(Guid ChangeSetId, RevisionInput? Revision);

public sealed record RegistryRevision(
    Guid UnitId,
    Guid RevisionId,
    long RevNo,
    string Title,
    string Kind,
    string SchemaVersion,
    string FactKind,
    string Verification,
    JsonElement Applicability,
    JsonElement Body,
    JsonElement Blocks,
    JsonElement Unknowns,
    string Sha256,
    DateTimeOffset CreatedAt,
    Guid CreatedBy,
    IReadOnlyList<string> CategoryKeys);

public sealed record RegistryRevisionPage(IReadOnlyList<RegistryRevision> Items, string? NextCursor);
public sealed record RegistryMutation(RegistryRevision Revision, long ProjectVersion);
