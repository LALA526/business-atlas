using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMcp.Contracts;

public sealed record AgentRunView(Guid RunId, Guid? TaskId, Guid? ContextId, string State, long Version,
    string? Provider, string? Model, JsonElement Usage, JsonElement Checkpoint);
public sealed record AgentRunPage(IReadOnlyList<AgentRunView> Items, string? NextCursor);
public sealed record StartAgentRunRequest(Guid? TaskId, Guid? ContextId, [property: JsonRequired] JsonElement Budget);

public sealed record RunEventView([property: JsonRequired] string EventId, [property: JsonRequired] long Seq,
    [property: JsonRequired] string EventType, [property: JsonRequired] string CaptureKind,
    [property: JsonRequired] DateTimeOffset OccurredAt, [property: JsonRequired] JsonElement Data, string? ArtifactSha256);
public sealed record RunEventPage(IReadOnlyList<RunEventView> Items, string? NextCursor);
public sealed record IdView(Guid Id);

public sealed record WorkRequestView(Guid WorkId, Guid TaskId, Guid ContextId, string Kind, string State,
    JsonElement Scope, JsonElement Budget, long Version, long Fence, DateTimeOffset? LeaseUntil,
    Guid? RunId, DateTimeOffset? Deadline, [property: JsonIgnore] int Attempt,
    [property: JsonIgnore] JsonElement Checkpoint, [property: JsonIgnore] JsonElement ResultRef,
    [property: JsonIgnore] Guid? HolderId = null);
public sealed record WorkRequestPage(IReadOnlyList<WorkRequestView> Items, string? NextCursor);
public sealed record CreateWorkRequest([property: JsonRequired] Guid TaskId, [property: JsonRequired] Guid ContextId,
    [property: JsonRequired] string Kind, [property: JsonRequired] JsonElement Scope,
    [property: JsonRequired] JsonElement Budget, [property: JsonRequired] DateTimeOffset? Deadline);
public sealed record InvestigateImpactRequest([property: JsonRequired] Guid ContextId,
    [property: JsonRequired] JsonElement Scope, [property: JsonRequired] JsonElement Budget,
    [property: JsonRequired] IReadOnlyList<Guid> ImpactIds);
public sealed record ClaimWorkRequest([property: JsonRequired] IReadOnlyList<string> ExecutorCapabilities);
public sealed record HeartbeatWorkRequest([property: JsonRequired] long Fence, [property: JsonRequired] JsonElement Checkpoint);
public sealed record FinishWorkRequest([property: JsonRequired] long Fence, [property: JsonRequired] string Result,
    [property: JsonRequired] Guid RunId, [property: JsonRequired] JsonElement ResultRef);
