using System.Data.Common;
using System.Text.Json;
using BuildingBlocks.Commands;
using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace PlatformOps.Jobs;

/// <summary>PostgreSQL-backed durable jobs. Every execution write is guarded by job ID and fencing token.</summary>
public sealed class PostgresJobStore(AtlasDbContext database) : IJobQueue
{
    public async Task<JobInfo> EnqueueAsync(Guid projectId, Guid requesterId, EnqueueJobCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Kind) || command.Kind.Length > 200 || command.MaxAttempt is < 1 or > 100)
            throw new AtlasCommandException("VALIDATION_FAILED", "A job kind and maxAttempt between 1 and 100 are required.", 400);
        if (command.Input.ValueKind != JsonValueKind.Object)
            throw new AtlasCommandException("VALIDATION_FAILED", "Job input must be an object.", 400);

        await using var transaction = await Begin(projectId, requesterId, "enqueueJob", $"enqueue-{Guid.NewGuid():N}", cancellationToken);
        await RequireMember(transaction, cancellationToken);
        var id = command.JobId ?? Guid.NewGuid();
        await using (var commandDb = transaction.CreateCommand("""
            INSERT INTO ops.job(project_id,job_id,kind,requester_id,context_id,state,input,max_attempt,available_at)
            VALUES (@project,@id,@kind,@requester,@context,'queued',@input::jsonb,@max,@available)
            """))
        {
            Add(commandDb, "project", projectId); Add(commandDb, "id", id); Add(commandDb, "kind", command.Kind);
            Add(commandDb, "requester", requesterId); Add(commandDb, "context", command.ContextId);
            Add(commandDb, "input", command.Input.GetRawText(), NpgsqlDbType.Jsonb); Add(commandDb, "max", command.MaxAttempt);
            Add(commandDb, "available", command.AvailableAt ?? DateTimeOffset.UtcNow);
            await commandDb.ExecuteNonQueryAsync(cancellationToken);
        }
        var job = await Read(transaction, id, cancellationToken) ?? throw new InvalidOperationException("Inserted job was not visible.");
        await AppendLifecycle(transaction, job, "job.queued", null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return job;
    }

    public async Task<JobInfo> GetAsync(Guid projectId, Guid principalId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await Begin(projectId, principalId, "getJob", $"read-{Guid.NewGuid():N}", cancellationToken);
        var job = await ReadAuthorized(transaction, jobId, cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return job;
    }

    public async Task<JsonElement> GetResultAsync(Guid projectId, Guid principalId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await Begin(projectId, principalId, "getJobResult", $"read-{Guid.NewGuid():N}", cancellationToken);
        var job = await ReadAuthorized(transaction, jobId, cancellationToken);
        if (job.State != JobState.Succeeded)
            throw new AtlasCommandException("JOB_NOT_COMPLETE", "The job result is available after successful completion.", 409);
        await using var command = transaction.CreateCommand("SELECT result FROM ops.job WHERE project_id=@project AND job_id=@id");
        Add(command, "project", projectId); Add(command, "id", jobId);
        var raw = (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        await transaction.RollbackAsync(cancellationToken);
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    public Task<JobInfo> CancelAsync(Guid projectId, Guid principalId, Guid jobId, long expectedVersion, string reason,
        string idempotencyKey, string requestId, string? traceId, CancellationToken cancellationToken = default) =>
        Control(projectId, principalId, jobId, expectedVersion, reason, idempotencyKey, requestId, traceId, true, cancellationToken);

    public Task<JobInfo> RetryAsync(Guid projectId, Guid principalId, Guid jobId, long expectedVersion, string reason,
        string idempotencyKey, string requestId, string? traceId, CancellationToken cancellationToken = default) =>
        Control(projectId, principalId, jobId, expectedVersion, reason, idempotencyKey, requestId, traceId, false, cancellationToken);

    private async Task<JobInfo> Control(Guid projectId, Guid principalId, Guid jobId, long expectedVersion, string reason,
        string idempotencyKey, string requestId, string? traceId, bool cancel, CancellationToken cancellationToken)
    {
        if (expectedVersion < 1) throw new AtlasCommandException("PRECONDITION_REQUIRED", "A valid job version is required.", 428);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            throw new AtlasCommandException("VALIDATION_FAILED", "A reason of at most 1000 characters is required.", 400);
        var operation = cancel ? "cancelJob" : "retryJob";
        await using var transaction = await Begin(projectId, principalId, operation, idempotencyKey, cancellationToken, requestId, traceId);
        await RequireMember(transaction, cancellationToken);
        var digest = RequestDigest.Compute(new { jobId, expectedVersion, reason });
        var existing = await transaction.FindIdempotencyAsync(cancellationToken);
        if (existing is not null)
        {
            if (existing.RequestSha256 != digest) throw AtlasCommandException.Conflict();
            var replay = existing.Result.Data.Deserialize<JobInfo>()!;
            await transaction.RollbackAsync(cancellationToken);
            return replay;
        }

        var sql = cancel ? """
            UPDATE ops.job SET state=CASE WHEN state='running' THEN 'cancel_requested' ELSE 'canceled' END,
              holder=CASE WHEN state IN ('running','cancel_requested') THEN holder ELSE NULL END,
              lease_until=CASE WHEN state IN ('running','cancel_requested') THEN lease_until ELSE NULL END,version=version+1,error_code='CANCELED'
            WHERE project_id=@project AND job_id=@id AND version=@expected
              AND state IN ('queued','running','waiting_external','cancel_requested')
            RETURNING job_id
            """ : """
            UPDATE ops.job SET state='queued',holder=NULL,lease_until=NULL,available_at=now(),attempt=0,
              error_code=NULL,result='{}'::jsonb,version=version+1
            WHERE project_id=@project AND job_id=@id AND version=@expected AND state IN ('failed','canceled')
            RETURNING job_id
            """;
        await using (var command = transaction.CreateCommand(sql))
        {
            Add(command, "project", projectId); Add(command, "id", jobId); Add(command, "expected", expectedVersion);
            if (await command.ExecuteScalarAsync(cancellationToken) is not Guid)
            {
                var current = await ReadAuthorized(transaction, jobId, cancellationToken);
                if (current.Version != expectedVersion) throw AtlasCommandException.CasMismatch();
                throw new AtlasCommandException("INVALID_JOB_STATE", cancel ? "Only active jobs can be canceled." : "Only failed or canceled jobs can be retried.", 409);
            }
        }
        var job = await Read(transaction, jobId, cancellationToken) ?? throw new InvalidOperationException("Updated job was not visible.");
        await AppendLifecycle(transaction, job, cancel ? "job.cancel_requested" : "job.retry", reason, cancellationToken);
        await transaction.SaveIdempotencyAsync(digest, new StoredCommandResult(JsonSerializer.SerializeToElement(job), 200), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return job;
    }

    public async Task<IReadOnlyList<Guid>> ListWorkerProjectsAsync(CancellationToken cancellationToken)
    {
        // This SECURITY DEFINER directory exposes opaque project IDs only, never project content.
        await using var connection = database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT project_id FROM ops.worker_projects() ORDER BY project_id";
        var projects = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) projects.Add(reader.GetGuid(0));
        return projects;
    }

    public async Task<ClaimedJob?> ClaimAsync(Guid projectId, Guid workerPrincipalId, string holder, TimeSpan lease, CancellationToken cancellationToken)
    {
        if (lease <= TimeSpan.Zero || string.IsNullOrWhiteSpace(holder)) throw new ArgumentOutOfRangeException(nameof(lease));
        await using var transaction = await Begin(projectId, workerPrincipalId, "job.claim", $"claim-{Guid.NewGuid():N}", cancellationToken);
        await using var command = transaction.CreateCommand("""
            WITH candidate AS (
              SELECT project_id,job_id FROM ops.job
              WHERE project_id=@project AND available_at<=now()
                AND (state='queued' OR (state='running' AND lease_until<now()))
              ORDER BY available_at,job_id FOR UPDATE SKIP LOCKED LIMIT 1
            ), updated AS (
              UPDATE ops.job target SET state='running',holder=@holder,lease_until=now()+@lease,
                fence=target.fence+1,attempt=target.attempt+1,version=target.version+1,error_code=NULL
              FROM candidate WHERE target.project_id=candidate.project_id AND target.job_id=candidate.job_id
              RETURNING target.*
            ) SELECT * FROM updated
            """);
        Add(command, "project", projectId); Add(command, "holder", holder); Add(command, "lease", lease);
        ClaimedJob? claimed = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken)) claimed = new ClaimedJob(ReadJob(reader), holder, reader.GetInt64(reader.GetOrdinal("fence")), reader.GetInt32(reader.GetOrdinal("attempt")));
        }
        if (claimed is not null)
        {
            await AppendWorkerAudit(transaction, claimed.Job.RequesterId, "job.claimed", claimed.Job.JobId, new { claimed.Fence, claimed.Attempt }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else await transaction.RollbackAsync(cancellationToken);
        return claimed;
    }

    public async Task<bool> HeartbeatAsync(ClaimedJob claimed, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var transaction = await Begin(claimed.Job.ProjectId, claimed.Job.RequesterId, "job.heartbeat", $"heartbeat-{Guid.NewGuid():N}", cancellationToken);
        await using var command = transaction.CreateCommand("""
            UPDATE ops.job SET lease_until=now()+@lease,version=version+1
            WHERE project_id=@project AND job_id=@id AND fence=@fence AND holder=@holder AND state='running'
            """);
        Add(command, "lease", lease); Add(command, "project", claimed.Job.ProjectId); Add(command, "id", claimed.Job.JobId);
        Add(command, "fence", claimed.Fence); Add(command, "holder", claimed.Holder);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await (updated ? transaction.CommitAsync(cancellationToken) : transaction.RollbackAsync(cancellationToken));
        return updated;
    }

    public async Task<bool> SaveCheckpointAsync(ClaimedJob claimed, JobCheckpoint value, CancellationToken cancellationToken)
    {
        if (value.Progress is < 0 or > 1) throw new AtlasCommandException("VALIDATION_FAILED", "Progress must be between zero and one.", 400);
        var checkpoint = value.Checkpoint.Clone();
        if (value.Progress is { } progress)
        {
            var fields = checkpoint.ValueKind == JsonValueKind.Object
                ? checkpoint.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : [];
            fields["progress"] = JsonSerializer.SerializeToElement(progress);
            checkpoint = JsonSerializer.SerializeToElement(fields);
        }
        await using var transaction = await Begin(claimed.Job.ProjectId, claimed.Job.RequesterId, "job.checkpoint", $"checkpoint-{Guid.NewGuid():N}", cancellationToken);
        await using var command = transaction.CreateCommand("""
            UPDATE ops.job SET checkpoint=@checkpoint::jsonb,version=version+1
            WHERE project_id=@project AND job_id=@id AND fence=@fence AND holder=@holder AND state='running'
            """);
        Add(command, "checkpoint", checkpoint.GetRawText()); Add(command, "project", claimed.Job.ProjectId); Add(command, "id", claimed.Job.JobId);
        Add(command, "fence", claimed.Fence); Add(command, "holder", claimed.Holder);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (updated) await transaction.AppendAuditAsync(Audit("job.checkpoint", claimed.Job.JobId, new { claimed.Fence, value.Progress }), cancellationToken);
        await (updated ? transaction.CommitAsync(cancellationToken) : transaction.RollbackAsync(cancellationToken));
        return updated;
    }

    public async Task<bool> CompleteAsync(ClaimedJob claimed, JobCompletion completion, CancellationToken cancellationToken)
    {
        await using var transaction = await Begin(claimed.Job.ProjectId, claimed.Job.RequesterId, "job.complete", $"complete-{Guid.NewGuid():N}", cancellationToken);
        await using var command = transaction.CreateCommand("""
            UPDATE ops.job SET state='succeeded',result=@result::jsonb,checkpoint=COALESCE(@checkpoint::jsonb,checkpoint),
              holder=NULL,lease_until=NULL,error_code=NULL,version=version+1
            WHERE project_id=@project AND job_id=@id AND fence=@fence AND holder=@holder AND state='running'
            """);
        Add(command, "result", completion.Result.GetRawText()); Add(command, "checkpoint", completion.Checkpoint?.GetRawText());
        Add(command, "project", claimed.Job.ProjectId); Add(command, "id", claimed.Job.JobId); Add(command, "fence", claimed.Fence); Add(command, "holder", claimed.Holder);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (updated)
        {
            var job = await Read(transaction, claimed.Job.JobId, cancellationToken);
            await AppendLifecycle(transaction, job!, "job.succeeded", null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else await transaction.RollbackAsync(cancellationToken);
        return updated;
    }

    public async Task<bool> FailAsync(ClaimedJob claimed, string code, bool transient, TimeSpan delay, CancellationToken cancellationToken)
    {
        await using var transaction = await Begin(claimed.Job.ProjectId, claimed.Job.RequesterId, "job.fail", $"fail-{Guid.NewGuid():N}", cancellationToken);
        var retry = transient && claimed.Attempt < claimed.Job.MaxAttempt;
        await using var command = transaction.CreateCommand("""
            UPDATE ops.job SET state=CASE WHEN @retry THEN 'queued' ELSE 'failed' END,
              available_at=CASE WHEN @retry THEN now()+@delay ELSE available_at END,
              holder=NULL,lease_until=NULL,error_code=CASE WHEN @retry THEN NULL ELSE @code END,version=version+1
            WHERE project_id=@project AND job_id=@id AND fence=@fence AND holder=@holder AND state='running'
            """);
        Add(command, "retry", retry); Add(command, "delay", delay); Add(command, "code", code);
        Add(command, "project", claimed.Job.ProjectId); Add(command, "id", claimed.Job.JobId); Add(command, "fence", claimed.Fence); Add(command, "holder", claimed.Holder);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (updated)
        {
            var job = await Read(transaction, claimed.Job.JobId, cancellationToken);
            await AppendLifecycle(transaction, job!, retry ? "job.retry_scheduled" : "job.failed", code, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else await transaction.RollbackAsync(cancellationToken);
        return updated;
    }

    public async Task<bool> AcknowledgeCancelAsync(ClaimedJob claimed, CancellationToken cancellationToken)
    {
        await using var transaction = await Begin(claimed.Job.ProjectId, claimed.Job.RequesterId, "job.canceled", $"cancel-{Guid.NewGuid():N}", cancellationToken);
        await using var command = transaction.CreateCommand("""
            UPDATE ops.job SET state='canceled',holder=NULL,lease_until=NULL,error_code='CANCELED',version=version+1
            WHERE project_id=@project AND job_id=@id AND fence=@fence AND holder=@holder AND state='cancel_requested'
            """);
        Add(command, "project", claimed.Job.ProjectId); Add(command, "id", claimed.Job.JobId); Add(command, "fence", claimed.Fence); Add(command, "holder", claimed.Holder);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (updated)
        {
            var job = await Read(transaction, claimed.Job.JobId, cancellationToken);
            await AppendLifecycle(transaction, job!, "job.canceled", null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else await transaction.RollbackAsync(cancellationToken);
        return updated;
    }

    public async Task<bool> IsCancelRequestedAsync(ClaimedJob claimed, CancellationToken cancellationToken)
    {
        await using var transaction = await Begin(claimed.Job.ProjectId, claimed.Job.RequesterId, "job.cancel_check", $"cancel-check-{Guid.NewGuid():N}", cancellationToken);
        await using var command = transaction.CreateCommand("SELECT state='cancel_requested' OR state='canceled' OR fence<>@fence FROM ops.job WHERE project_id=@project AND job_id=@id");
        Add(command, "project", claimed.Job.ProjectId); Add(command, "id", claimed.Job.JobId); Add(command, "fence", claimed.Fence);
        var result = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
        await transaction.RollbackAsync(cancellationToken);
        return result;
    }

    private async Task<JobInfo> ReadAuthorized(PostgresCommandTransaction transaction, Guid jobId, CancellationToken ct)
    {
        await RequireMember(transaction, ct);
        return await Read(transaction, jobId, ct) ?? throw new AtlasCommandException("NOT_FOUND", "The job was not found.", 404);
    }

    private static async Task RequireMember(PostgresCommandTransaction transaction, CancellationToken ct)
    {
        await using var command = transaction.CreateCommand("SELECT EXISTS(SELECT 1 FROM identity.membership WHERE project_id=@project AND principal_id=@principal AND active)");
        Add(command, "project", transaction.CommandContext.ProjectId); Add(command, "principal", transaction.CommandContext.PrincipalId);
        if (await command.ExecuteScalarAsync(ct) is not true) throw new AtlasCommandException("NOT_FOUND", "The project or resource was not found.", 404);
    }

    private static async Task<JobInfo?> Read(PostgresCommandTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var command = transaction.CreateCommand("SELECT * FROM ops.job WHERE project_id=@project AND job_id=@id");
        Add(command, "project", transaction.CommandContext.ProjectId); Add(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadJob(reader) : null;
    }

    private static JobInfo ReadJob(DbDataReader reader)
    {
        JsonElement Json(string name) => JsonDocument.Parse(reader.GetString(reader.GetOrdinal(name))).RootElement.Clone();
        var checkpoint = Json("checkpoint");
        double? progress = checkpoint.ValueKind == JsonValueKind.Object && checkpoint.TryGetProperty("progress", out var p) && p.TryGetDouble(out var value) ? value : null;
        var result = Json("result");
        return new JobInfo(reader.GetGuid(reader.GetOrdinal("project_id")), reader.GetGuid(reader.GetOrdinal("job_id")), reader.GetString(reader.GetOrdinal("kind")),
            reader.GetString(reader.GetOrdinal("state")) switch
            {
                "queued" => JobState.Queued, "running" => JobState.Running, "waiting_external" => JobState.WaitingExternal,
                "succeeded" => JobState.Succeeded, "failed" => JobState.Failed, "canceled" => JobState.Canceled,
                "cancel_requested" => JobState.CancelRequested, var stateValue => throw new InvalidOperationException($"Unknown job state {stateValue}.")
            }, reader.GetInt64(reader.GetOrdinal("version")), reader.GetInt32(reader.GetOrdinal("attempt")),
            reader.GetInt32(reader.GetOrdinal("max_attempt")), progress,
            result.ValueKind == JsonValueKind.Object && result.TryGetProperty("resultRef", out var reference) && reference.ValueKind == JsonValueKind.String ? reference.GetString() : null,
            reader.IsDBNull(reader.GetOrdinal("error_code")) ? null : reader.GetString(reader.GetOrdinal("error_code")), Json("input"), checkpoint,
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("available_at")), reader.IsDBNull(reader.GetOrdinal("lease_until")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("lease_until")),
            reader.GetInt64(reader.GetOrdinal("fence")), reader.GetGuid(reader.GetOrdinal("requester_id")), reader.IsDBNull(reader.GetOrdinal("context_id")) ? null : reader.GetGuid(reader.GetOrdinal("context_id")));
    }

    private static Task AppendLifecycle(PostgresCommandTransaction transaction, JobInfo job, string action, string? detail, CancellationToken ct) =>
        transaction.AppendOutboxAsync(new OutboxMessage(job.ProjectId, Guid.NewGuid(), "JobStateChanged.v1", job.JobId, job.Version, "v1",
            JsonSerializer.SerializeToElement(new { jobId = job.JobId, kind = job.Kind, state = job.State.ToString().ToLowerInvariant(), attempt = job.Attempt, detail }), DateTimeOffset.UtcNow), ct);

    private static AuditEntryInput Audit(string action, Guid jobId, object metadata) => new(action, JsonSerializer.SerializeToElement(new { jobId }), "OK", JsonSerializer.SerializeToElement(metadata));
    private static async Task AppendWorkerAudit(PostgresCommandTransaction transaction, Guid actor, string action, Guid jobId, object metadata, CancellationToken ct)
    {
        await using var command = transaction.CreateCommand("INSERT INTO ops.audit_event(project_id,actor_id,action,resource_ref,result_code,request_id,trace_id,safe_metadata) VALUES (@project,@actor,@action,@resource::jsonb,'OK',@request,NULL,@metadata::jsonb)");
        Add(command, "project", transaction.CommandContext.ProjectId); Add(command, "actor", actor); Add(command, "action", action);
        Add(command, "resource", JsonSerializer.Serialize(new { jobId })); Add(command, "request", transaction.CommandContext.RequestId); Add(command, "metadata", JsonSerializer.Serialize(metadata));
        await command.ExecuteNonQueryAsync(ct);
    }
    private Task<PostgresCommandTransaction> Begin(Guid project, Guid principal, string operation, string key, CancellationToken ct, string? request = null, string? trace = null) =>
        PostgresCommandTransaction.BeginAsync(database, new CommandContext(project, principal, operation, key, request ?? $"worker-{Guid.NewGuid():N}", trace), ct);
    private static void Add(DbCommand command, string name, object? value, NpgsqlDbType? type = null) => PostgresCommandTransaction.AddParameter(command, name, value, type);
}
