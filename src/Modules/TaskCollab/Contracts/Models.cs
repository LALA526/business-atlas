using System.Text.Json;
using System.Text.Json.Serialization;
using AccessApplicability.Contracts;

namespace TaskCollab.Contracts;

public sealed record RevisionRef([property: JsonRequired] Guid UnitId, [property: JsonRequired] Guid RevisionId);
public sealed record TargetChange([property: JsonRequired] string Action, [property: JsonRequired] string Description, [property: JsonRequired] string Reason, RevisionRef? Current = null, RevisionRef? Candidate = null);
public sealed record Unknown(string Field, string Question, string Reason);
public sealed record TaskView(Guid TaskId, string Title, string Intent, Guid? RootUnitId, Guid BaseBaselineId, string Classification, string State, long Version,
    IReadOnlyList<TargetChange> TargetChanges, IReadOnlyList<RevisionRef> Preserve, IReadOnlyList<Unknown> Unknowns, string? NextAction, Guid? ContextId, JsonElement ExternalRef);
public sealed record TaskPage(IReadOnlyList<TaskView> Items, string? NextCursor);
public sealed record CreateTaskRequest([property: JsonRequired] string Intent, string? Title, Guid? RootUnitId, IReadOnlyList<RevisionRef>? SourceRefs, Guid? BaseBaselineId);
public sealed record AmendTaskRequest(string? Title, IReadOnlyList<TargetChange>? TargetChanges, IReadOnlyList<RevisionRef>? Preserve, JsonElement? ExternalRef);
public sealed record AnalyzeIntentRequest([property: JsonRequired] ReadContextRequest ReadContext, [property: JsonRequired] IReadOnlyList<RevisionRef> SourceRefs);
public sealed record FindImpactsRequest([property: JsonRequired] IReadOnlyList<UnitRef> RootRefs, [property: JsonRequired] string ChangeType, [property: JsonRequired] ReadContextRequest ReadContext);
public sealed record PlanChangeRequest([property: JsonRequired] Guid ContextId, [property: JsonRequired] IReadOnlyList<Guid> InvestigationIds);
public sealed record TransitionTaskRequest([property: JsonRequired] string Event, [property: JsonRequired] string Reason, Guid? ContextId);
public sealed record ImpactView(Guid ImpactId, Guid TaskId, UnitRef? Target, JsonElement Locator, IReadOnlyList<JsonElement> Path, string Disposition, string InspectionState,
    Guid? InvestigationId, string Reason, string Risk, Guid? AssignedTaskId, IReadOnlyList<JsonElement> Evidence, string CloseState, long Version);
public sealed record ImpactPage(IReadOnlyList<ImpactView> Items, string? NextCursor);
public sealed record DecideImpactRequest([property: JsonRequired] string Disposition, [property: JsonRequired] string InspectionState, Guid? InvestigationId,
    [property: JsonRequired] string Reason, [property: JsonRequired] string Risk, Guid? AssignedTaskId, [property: JsonRequired] IReadOnlyList<JsonElement> Evidence, [property: JsonRequired] string CloseState);
public sealed record InvestigationInput(Guid? InvestigationId, [property: JsonRequired] Guid TaskId, [property: JsonRequired] Guid BaselineId, string? Repo, string? CommitSha,
    [property: JsonRequired] IReadOnlyList<string> SearchedPaths, [property: JsonRequired] IReadOnlyList<string> Methods, [property: JsonRequired] string Coverage,
    [property: JsonRequired] IReadOnlyList<string> Exclusions, [property: JsonRequired] string CaptureKind, [property: JsonRequired] IReadOnlyList<JsonElement> Evidence, Guid? RunId);
public sealed record JobView(Guid JobId, string Kind, string State, long Version, int Attempt, double? Progress, string? ResultRef, string? ErrorCode, IReadOnlyList<Unknown> Unknowns);
public sealed record ActivityInput(Guid? UnitId, Guid? RevisionId, Guid? TaskId, Guid? ChangeSetId, JsonElement Anchor, [property: JsonRequired] string Body);
public sealed record ActivityView(Guid Id, string State, string Body, DateTimeOffset CreatedAt, long Version, ActivityInput Target, Guid AuthorId);
public sealed record ActivityPage(IReadOnlyList<ActivityView> Items, string? NextCursor);
public sealed record ResolveActivityRequest([property: JsonRequired] string Reason, Guid? ChangeSetId, Guid? TaskId);
public sealed record AuditEntryView(Guid AuditId, string Action, JsonElement ResourceRef, string ResultCode, DateTimeOffset OccurredAt, string RequestId, Guid? ActorId);
public sealed record AuditEntryPage(IReadOnlyList<AuditEntryView> Items, string? NextCursor);
