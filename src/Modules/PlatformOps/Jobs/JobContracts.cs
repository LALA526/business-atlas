using System.Text.Json;
using BuildingBlocks.Commands;

namespace PlatformOps.Jobs;

public enum JobState { Queued, Running, WaitingExternal, Succeeded, Failed, Canceled, CancelRequested }

public sealed record JobInfo(
    Guid ProjectId,
    Guid JobId,
    string Kind,
    JobState State,
    long Version,
    int Attempt,
    int MaxAttempt,
    double? Progress,
    string? ResultRef,
    string? ErrorCode,
    JsonElement Input,
    JsonElement Checkpoint,
    DateTimeOffset AvailableAt,
    DateTimeOffset? LeaseUntil,
    long Fence,
    Guid RequesterId,
    Guid? ContextId);

public sealed record EnqueueJobCommand(
    string Kind,
    JsonElement Input,
    Guid? ContextId = null,
    int MaxAttempt = 5,
    DateTimeOffset? AvailableAt = null,
    Guid? JobId = null);

public sealed record ClaimedJob(
    JobInfo Job,
    string Holder,
    long Fence,
    int Attempt);

public sealed record JobCompletion(JsonElement Result, JsonElement? Checkpoint = null, double Progress = 1);
public sealed record JobCheckpoint(JsonElement Checkpoint, double? Progress = null);

public interface IJobQueue
{
    Task<JobInfo> EnqueueAsync(Guid projectId, Guid requesterId, EnqueueJobCommand command, CancellationToken cancellationToken = default);
    Task<JobInfo> GetAsync(Guid projectId, Guid principalId, Guid jobId, CancellationToken cancellationToken = default);
    Task<JobInfo> CancelAsync(Guid projectId, Guid principalId, Guid jobId, long expectedVersion, string reason, string idempotencyKey, string requestId, string? traceId, CancellationToken cancellationToken = default);
    Task<JobInfo> RetryAsync(Guid projectId, Guid principalId, Guid jobId, long expectedVersion, string reason, string idempotencyKey, string requestId, string? traceId, CancellationToken cancellationToken = default);
}

/// <summary>A domain adapter executes only its registered job kind and commits business writes through its own ports.</summary>
public interface IJobHandler
{
    string Kind { get; }
    Task<JobCompletion> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken);
}

public sealed class JobExecutionContext(ClaimedJob job, PostgresJobStore store, TimeSpan budget)
{
    public ClaimedJob Job { get; } = job;
    public TimeSpan Budget { get; } = budget;

    /// <summary>Stable across retries; domain handlers must pass this to their side-effecting application command.</summary>
    public string IdempotencyKey(string step, string inputDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputDigest);
        return $"{Job.Job.ProjectId}:{Job.Job.JobId}:{step}:{inputDigest}";
    }

    public Task<bool> CheckpointAsync(JsonElement checkpoint, double? progress, CancellationToken cancellationToken = default) =>
        store.SaveCheckpointAsync(Job, new JobCheckpoint(checkpoint, progress), cancellationToken);

    public Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default) =>
        store.HeartbeatAsync(Job, TimeSpan.FromMinutes(2), cancellationToken);

    public Task<bool> IsCancellationRequestedAsync(CancellationToken cancellationToken = default) =>
        store.IsCancelRequestedAsync(Job, cancellationToken);
}

public sealed class JobExecutionException(string code, string message, bool transient = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Transient { get; } = transient;
}
