using System.Text.Json;
using KnowledgeCore.Contracts;

namespace Governance.Contracts;

public sealed record ChangeItemInput(Guid UnitId, string Action, Guid? BaseRevisionId, Guid? ProposedRevisionId, string DependencyGroup, string Reason);
public sealed record RevisionRefView(Guid UnitId, Guid RevisionId, long RevNo, string Title, string ContentSha256);
public sealed record ChangeItemView(Guid UnitId, string Action, Guid? BaseRevisionId, Guid? ProposedRevisionId, string DependencyGroup, string Reason, RevisionRefView? Before, RevisionRefView? After);
public sealed record ApprovalSlotView(string SlotId, string Role, IReadOnlyList<string> Scope, string Risk, string Status);
public sealed record ChangeSetView(
    Guid ChangeSetId, Guid BaseBaselineId, string Title, long Version, string State,
    long? SubmittedSeq, string? SubmitDigest, IReadOnlyList<ChangeItemView> Items,
    IReadOnlyList<ApprovalSlotView> Approvals, IReadOnlyList<JsonElement> Summary,
    IReadOnlyList<JsonElement> Unknowns, Guid? PublishedBaselineId);
public sealed record ChangeSetPage(IReadOnlyList<ChangeSetView> Items, string? NextCursor);
public sealed record BaselineView(Guid BaselineId, long Number, Guid? ParentBaselineId, Guid? ChangeSetId, string RegistryVersion, string Digest, DateTimeOffset CreatedAt);
public sealed record BaselinePage(IReadOnlyList<BaselineView> Items, string? NextCursor);
public sealed record DiffItemView(Guid UnitId, string Action, RevisionRefView? Before, RevisionRefView? After, string Summary);
public sealed record GovernanceMutation<T>(T Data, long ProjectVersion);

public sealed record CreateChangeSetRequest(string Title, Guid BaseBaselineId);
public sealed record AmendChangeSetRequest(string? Title, IReadOnlyList<ChangeItemInput>? Items, IReadOnlyList<JsonElement>? Summary);
public sealed record SubmitChangeSetRequest(IReadOnlyList<JsonElement> Summary, IReadOnlyList<Guid> ImpactIds);
public sealed record ReviewChangeSetRequest(string SubmitDigest, long SubmittedSeq, IReadOnlyList<string> SlotIds, string Decision, string Reason, JsonElement Anchor);
public sealed record ReopenChangeSetRequest(string Reason, Guid? NewBaseBaselineId);
public sealed record PublishChangeSetRequest(string SubmitDigest, Guid BaseBaselineId);
public sealed record RestoreBaselineRequest(string Reason, Guid CurrentBaselineId);
public sealed record SubmitProposalRequest(Guid BaseBaselineId, string Title, Guid? TaskId, IReadOnlyList<RevisionInput> Revisions, IReadOnlyList<Guid> ImpactIds);
