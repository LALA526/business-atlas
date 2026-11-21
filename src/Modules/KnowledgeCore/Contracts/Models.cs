using System.Text.Json;
using System.Text.Json.Serialization;
using AccessApplicability.Contracts;

namespace KnowledgeCore.Contracts;

public sealed record ExternalIdentity(string Namespace, string ExternalType, string Id);

public sealed record RevisionInput(
    Guid? UnitId,
    string Title,
    string Kind,
    string SchemaVersion,
    Guid? SchemaDefinitionRevisionId,
    string FactKind,
    string Verification,
    JsonElement Applicability,
    JsonElement Body,
    JsonElement Blocks,
    JsonElement Evidence,
    JsonElement Unknowns,
    JsonElement CategoryKeys,
    ExternalIdentity? ExternalId = null);

public sealed record RevisionView(
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
    JsonElement Evidence,
    JsonElement Unknowns,
    string Sha256,
    DateTimeOffset CreatedAt,
    Guid CreatedBy,
    IReadOnlyList<string> CategoryKeys)
{
    [JsonIgnore] public bool Sealed { get; init; }
    [JsonIgnore] public Guid? ParentRevisionId { get; init; }
}

public sealed record RevisionViewPage(IReadOnlyList<RevisionView> Items, string? NextCursor);
public sealed record RevisionMutation(RevisionView Revision, long ProjectVersion);
public sealed record ProposalBatchResult(Guid ChangeSetId, long ProjectVersion, IReadOnlyList<RevisionView> Revisions);
public sealed record RuleEvaluationRequest(ReadContextRequest ReadContext, JsonElement Facts);
public sealed record RuleCondition(string RowId, string Value, string Reason);
public sealed record RuleResult(string Status, IReadOnlyList<JsonElement> Outcomes,
    IReadOnlyList<RuleCondition> Conditions, IReadOnlyList<JsonElement> Unknowns,
    ResolvedReadContext Context, IReadOnlyList<JsonElement> Evidence);
