using System.Text.Json;
using System.Text.Json.Serialization;
using AccessApplicability.Contracts;
using TaskCollab.Contracts;

namespace ContractDelivery.Contracts;

public sealed record ContextUnknown([property: JsonRequired] string Key, [property: JsonRequired] string Description,
    [property: JsonRequired] string Severity, [property: JsonRequired] string Reason, string? NextAction = null, IReadOnlyList<RevisionRef>? References = null);
public sealed record ContextEvidence([property: JsonRequired] RevisionRef Source, [property: JsonRequired] JsonElement Locator,
    [property: JsonRequired] string Support, [property: JsonRequired] string CaptureKind);
public sealed record TaskContextView(Guid ContextId, Guid ProjectId, Guid TaskId, Guid BaselineId, long PolicyEpoch, string Phase, string Digest,
    IReadOnlyList<TargetChange> Target, IReadOnlyList<RevisionRef> Preserve, IReadOnlyList<RevisionRef> RequiredRefs,
    IReadOnlyList<ContextEvidence> Evidence, IReadOnlyList<Guid> ImpactIds, IReadOnlyList<Guid> ApprovalIds,
    IReadOnlyList<RevisionRef> AcceptanceCases, IReadOnlyList<string> KnowledgeRequirements, IReadOnlyList<ContextUnknown> Unknowns,
    string Status, DateTimeOffset ExpiresAt);
public sealed record BuildContextRequest([property: JsonRequired] ReadContextRequest ReadContext, [property: JsonRequired] string Phase);
public sealed record ConformanceCheckRequest([property: JsonRequired] RevisionRef NormativeRevision, [property: JsonRequired] RevisionRef ObservedRevision,
    [property: JsonRequired] ReadContextRequest ReadContext, [property: JsonRequired] string BuildDigest, [property: JsonRequired] string Environment);
public sealed record ConformanceView(Guid? CheckId, RevisionRef NormativeRevision, RevisionRef ObservedRevision, string BuildDigest, string Environment,
    string ContextHash, string EvidenceHash, IReadOnlyList<ContextEvidence> Evidence, JsonElement Expected, JsonElement Actual, string Method, string Verdict);
public sealed record AcceptanceInput([property: JsonRequired] Guid ContextId, [property: JsonRequired] string TestId,
    [property: JsonRequired] JsonElement Expected, JsonElement? Observed, [property: JsonRequired] string Result,
    string? BuildDigest, string? Environment, [property: JsonRequired] IReadOnlyList<ContextEvidence> Evidence);
public sealed record IdView(Guid Id);
public sealed record ClosureCheck(string Key, string Status, bool Critical, string Reason, IReadOnlyList<ContextEvidence> Evidence, string? NextAction = null);
public sealed record ClosureView(Guid TaskId, Guid ContextId, string State, IReadOnlyList<ClosureCheck> Checks, IReadOnlyList<ContextUnknown> Unknowns);
public sealed record CloseTaskRequest([property: JsonRequired] Guid ContextId, [property: JsonRequired] string Reason);
public sealed record DeploymentInput(Guid? DeploymentId, [property: JsonRequired] string Environment, [property: JsonRequired] string RepoKey,
    [property: JsonRequired] string CommitSha, [property: JsonRequired] string BuildDigest, [property: JsonRequired] string MigrationVersion,
    [property: JsonRequired] string ConfigDigest, [property: JsonRequired] DateTimeOffset DeployedAt, Guid? PreviousDeploymentId,
    [property: JsonRequired] IReadOnlyList<ContextEvidence> Evidence);
public sealed record DeliveryStatus(Guid DeliveryId, Guid TaskId, string CommitSha, string BuildDigest, Guid? ChangeSetId,
    Guid? PublishedBaselineId, string Status, IReadOnlyList<ContextUnknown> Unknowns);
public sealed record DeliveryPage(IReadOnlyList<DeliveryStatus> Items, string? NextCursor);
public sealed record ReconcileRequest([property: JsonRequired] Guid TaskId, string? Environment);
