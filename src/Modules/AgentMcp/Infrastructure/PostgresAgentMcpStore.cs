using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessApplicability.Application;
using AccessApplicability.Contracts;
using AccessApplicability.Infrastructure;
using AgentMcp.Contracts;
using AgentMcp.Runs;
using AgentMcp.Work;
using Npgsql;
using NpgsqlTypes;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;

namespace AgentMcp.Infrastructure;

public sealed class PostgresAgentMcpStore(string connectionString, PostgresAccessApplicabilityStore access, TimeSpan? leaseDuration = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeSpan lease = leaseDuration ?? TimeSpan.FromMinutes(2);

    public async Task<AgentRunPage> ListRunsAsync(Guid principal, Guid project, Guid? task, string? cursor, int limit, CancellationToken ct)
    {
        var after = GuidCursor(cursor);
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await Context(tx, project, principal, ct); await RequireMember(tx, project, principal, ct);
        var ids = new List<Guid>(); await using (var command = Command(tx))
        {
            command.CommandText = "SELECT run_id FROM agent.run WHERE project_id=@project AND (@after IS NULL OR run_id>@after) AND (@task IS NULL OR task_id=@task) ORDER BY run_id LIMIT @limit";
            Add(command, "project", project); Add(command, "after", after, NpgsqlDbType.Uuid); Add(command, "task", task, NpgsqlDbType.Uuid); Add(command, "limit", limit + 1);
            await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        }
        var next = ids.Count > limit ? Encode(ids[limit - 1]) : null; if (ids.Count > limit) ids.RemoveAt(limit);
        var items = new List<AgentRunView>();
        foreach (var id in ids) { var run = await ReadRun(tx, project, id, false, ct); if (run.TaskId.HasValue && !await CanReadTask(tx, principal, project, run.TaskId.Value, ct)) continue; items.Add(run); }
        return new(items, next);
    }

    public async Task<AgentRunView> StartRunAsync(AtlasPrincipal actor, Guid project, StartAgentRunRequest request, string key, string requestId, string? trace, CancellationToken ct)
    {
        RequireExecutor(actor); if (request.Budget.ValueKind != JsonValueKind.Object) throw ProjectIdentityException.Invalid("budget must be an object.");
        _ = WorkScopePolicy.MaxAttempts(request.Budget); var digest = Digest(request);
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await Context(tx, project, actor.PrincipalId, ct); await RequireMember(tx, project, actor.PrincipalId, ct, true);
        if (await Replay<AgentRunView>(tx, project, actor.PrincipalId, "startAgentRun", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        if (request.TaskId.HasValue) await AuthorizeTask(tx, actor.PrincipalId, project, request.TaskId.Value, ct);
        if (request.ContextId.HasValue)
        {
            if (!request.TaskId.HasValue || !await ValidContext(tx, project, request.TaskId.Value, request.ContextId.Value, ct)) throw ProjectIdentityException.Conflict("The task context is not executable or is stale.");
        }
        var id = Guid.NewGuid();
        await Execute(tx, "INSERT INTO agent.run(project_id,run_id,task_id,context_id,executor_id,state,budget,created_by) VALUES(@project,@id,@task,@context,@actor,'queued',@budget::jsonb,@actor)", ct,
            ("project", project), ("id", id), ("task", Db(request.TaskId)), ("context", Db(request.ContextId)), ("actor", actor.PrincipalId), ("budget", request.Budget.GetRawText()));
        var result = await ReadRun(tx, project, id, false, ct);
        await Save(tx, project, actor.PrincipalId, "startAgentRun", key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    public async Task<RunEventPage> ListEventsAsync(Guid principal, Guid project, Guid run, string? cursor, int limit, CancellationToken ct)
    {
        var after = LongCursor(cursor);
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await Context(tx, project, principal, ct); await RequireMember(tx, project, principal, ct);
        var runView = await ReadRun(tx, project, run, false, ct); if (runView.TaskId.HasValue) await AuthorizeTask(tx, principal, project, runView.TaskId.Value, ct);
        var items = new List<RunEventView>(); await using var command = Command(tx);
        command.CommandText = "SELECT external_event_id,seq,event_type,capture_kind,occurred_at,event_data,artifact_sha256 FROM agent.run_event WHERE project_id=@project AND run_id=@run AND (@after IS NULL OR seq>@after) ORDER BY seq LIMIT @limit";
        Add(command, "project", project); Add(command, "run", run); Add(command, "after", after, NpgsqlDbType.Bigint); Add(command, "limit", limit + 1);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetDateTime(4), Element(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6)));
        var next = items.Count > limit ? Encode(items[limit - 1].Seq) : null; if (items.Count > limit) items.RemoveAt(limit); return new(items, next);
    }

    public async Task<IdView> AppendEventAsync(AtlasPrincipal actor, Guid project, Guid run, RunEventView request, string key, string requestId, string? trace, CancellationToken ct)
    {
        RunEvidencePolicy.ValidateEvent(actor, request); var digest = Digest(request);
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await Context(tx, project, actor.PrincipalId, ct); await RequireMember(tx, project, actor.PrincipalId, ct, true);
        if (await Replay<IdView>(tx, project, actor.PrincipalId, "appendRunEvent", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        Guid? task; string state;
        await using (var lockCommand = Command(tx))
        {
            lockCommand.CommandText = "SELECT executor_id,state,task_id FROM agent.run WHERE project_id=@project AND run_id=@run FOR UPDATE"; Add(lockCommand, "project", project); Add(lockCommand, "run", run);
            await using var reader = await lockCommand.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
            if (reader.GetGuid(0) != actor.PrincipalId) throw ProjectIdentityException.NotFound();
            state = reader.GetString(1); task = reader.IsDBNull(2) ? (Guid?)null : reader.GetGuid(2);
        }
        if (task.HasValue) await AuthorizeTask(tx, actor.PrincipalId, project, task.Value, ct);
        await using (var existing = Command(tx))
        {
            existing.CommandText = "SELECT external_event_id,seq,event_type,capture_kind,occurred_at,event_data,artifact_sha256,event_id FROM agent.run_event WHERE project_id=@project AND run_id=@run AND (external_event_id=@external OR seq=@seq)";
            Add(existing, "project", project); Add(existing, "run", run); Add(existing, "external", request.EventId); Add(existing, "seq", request.Seq);
            await using var reader = await existing.ExecuteReaderAsync(ct); if (await reader.ReadAsync(ct))
            {
                var same = reader.GetString(0) == request.EventId && reader.GetInt64(1) == request.Seq && reader.GetString(2) == request.EventType && reader.GetString(3) == request.CaptureKind && new DateTimeOffset(reader.GetDateTime(4)) == request.OccurredAt && JsonElement.DeepEquals(Element(reader.GetString(5)), request.Data) &&
                    (reader.IsDBNull(6) ? null : reader.GetString(6)) == request.ArtifactSha256;
                if (!same) throw ProjectIdentityException.Conflict("Run event sequence and external IDs are immutable and cannot be reused for different evidence.");
                var result = new IdView(reader.GetGuid(7)); await reader.DisposeAsync(); await Save(tx, project, actor.PrincipalId, "appendRunEvent", key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
            }
        }
        if (state is "succeeded" or "failed" or "canceled" or "stale") throw ProjectIdentityException.Conflict("Events cannot be appended to a terminal run.");
        var id = Guid.NewGuid(); var outbox = Guid.NewGuid();
        await Execute(tx, """
            INSERT INTO agent.run_event(project_id,event_id,run_id,seq,external_event_id,capture_kind,event_type,event_data,artifact_sha256,occurred_at,created_by)
              VALUES(@project,@id,@run,@seq,@external,@capture,@type,@data::jsonb,@artifact,@occurred,@actor);
            UPDATE agent.run SET state='running',version=version+1 WHERE project_id=@project AND run_id=@run;
            INSERT INTO ops.outbox(project_id,event_id,event_type,aggregate_id,aggregate_revision,schema_version,payload,state)
              VALUES(@project,@outbox,'AgentRunEventAdded.v1',@run,@seq,'v1',@payload::jsonb,'queued')
            """, ct, ("project", project), ("id", id), ("run", run), ("seq", request.Seq), ("external", request.EventId), ("capture", request.CaptureKind), ("type", request.EventType),
            ("data", request.Data.GetRawText()), ("artifact", Db(request.ArtifactSha256)), ("occurred", request.OccurredAt), ("actor", actor.PrincipalId), ("outbox", outbox),
            ("payload", JsonSerializer.Serialize(new { runId = run, eventId = id, externalEventId = request.EventId, seq = request.Seq, captureKind = request.CaptureKind }, Json)));
        var view = new IdView(id); await Save(tx, project, actor.PrincipalId, "appendRunEvent", key, digest, view, requestId, trace, ct); await tx.CommitAsync(ct); return view;
    }

    public async Task<WorkRequestPage> ListWorkAsync(Guid principal, Guid project, string? cursor, int limit, CancellationToken ct)
    {
        var after = GuidCursor(cursor); await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await Context(tx, project, principal, ct); await RequireMember(tx, project, principal, ct);
        var ids = new List<Guid>(); await using (var command = Command(tx))
        {
            command.CommandText = "SELECT work_id FROM agent.work_request WHERE project_id=@project AND (@after IS NULL OR work_id>@after) ORDER BY work_id LIMIT @limit";
            Add(command, "project", project); Add(command, "after", after, NpgsqlDbType.Uuid); Add(command, "limit", limit + 1);
            await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        }
        var next = ids.Count > limit ? Encode(ids[limit - 1]) : null; if (ids.Count > limit) ids.RemoveAt(limit);
        var items = new List<WorkRequestView>(); foreach (var id in ids) { var work = await ReadWork(tx, project, id, false, ct); if (await CanReadTask(tx, principal, project, work.TaskId, ct)) items.Add(work); }
        return new(items, next);
    }

    public Task<WorkRequestView> CreateWorkAsync(AtlasPrincipal actor, Guid project, CreateWorkRequest request, string key, string requestId, string? trace, CancellationToken ct) =>
        CreateWork(actor, project, request, "createWorkRequest", null, null, key, requestId, trace, ct);

    public Task<WorkRequestView> InvestigateAsync(AtlasPrincipal actor, Guid project, Guid task, InvestigateImpactRequest request, long expected, string key, string requestId, string? trace, CancellationToken ct)
    {
        if (request.ImpactIds.Count == 0 || request.ImpactIds.Count > 500 || request.ImpactIds.Distinct().Count() != request.ImpactIds.Count) throw ProjectIdentityException.Invalid("impactIds must contain distinct candidate IDs.");
        var create = new CreateWorkRequest(task, request.ContextId, "investigate", request.Scope, request.Budget, DateTimeOffset.UtcNow.AddDays(7));
        return CreateWork(actor, project, create, "investigateImpact", expected, request.ImpactIds, key, requestId, trace, ct);
    }

    private async Task<WorkRequestView> CreateWork(AtlasPrincipal actor, Guid project, CreateWorkRequest request, string operation, long? expected, IReadOnlyList<Guid>? impacts, string key, string requestId, string? trace, CancellationToken ct)
    {
        _ = WorkScopePolicy.ValidateCreate(request);
        var digest = operation == "investigateImpact"
            ? Digest(new { request.TaskId, request.ContextId, request.Scope, request.Budget, impacts })
            : Digest(new { request, impacts });
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await Context(tx, project, actor.PrincipalId, ct); await RequireMember(tx, project, actor.PrincipalId, ct, true);
        if (await Replay<WorkRequestView>(tx, project, actor.PrincipalId, operation, key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        await AuthorizeTask(tx, actor.PrincipalId, project, request.TaskId, ct);
        if (expected.HasValue) await RequireTaskVersion(tx, project, request.TaskId, expected.Value, ct);
        if (!await ValidContext(tx, project, request.TaskId, request.ContextId, ct)) throw ProjectIdentityException.Conflict("The task context is not executable or is stale.");
        if (impacts is not null)
        {
            await using var command = Command(tx); command.CommandText = "SELECT count(*) FROM collab.impact WHERE project_id=@project AND task_id=@task AND impact_id=ANY(@ids)";
            Add(command, "project", project); Add(command, "task", request.TaskId); Add(command, "ids", impacts.ToArray());
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != impacts.Count) throw ProjectIdentityException.Invalid("Every impact must belong to the investigation task.");
        }
        var id = Guid.NewGuid();
        await Execute(tx, "INSERT INTO agent.work_request(project_id,work_id,task_id,context_id,kind,scope,budget,deadline,state,created_by) VALUES(@project,@id,@task,@context,@kind,@scope::jsonb,@budget::jsonb,@deadline,'queued',@actor)", ct,
            ("project", project), ("id", id), ("task", request.TaskId), ("context", request.ContextId), ("kind", request.Kind), ("scope", request.Scope.GetRawText()), ("budget", request.Budget.GetRawText()), ("deadline", Db(request.Deadline)), ("actor", actor.PrincipalId));
        var result = await ReadWork(tx, project, id, false, ct); await Save(tx, project, actor.PrincipalId, operation, key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    public async Task<WorkRequestView> ClaimAsync(AtlasPrincipal actor, Guid project, Guid work, ClaimWorkRequest request, long expected, string key, string requestId, string? trace, CancellationToken ct)
    {
        RequireExecutor(actor); var digest = Digest(new { request, expected });
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await Context(tx, project, actor.PrincipalId, ct); await RequireMember(tx, project, actor.PrincipalId, ct, true);
        if (await Replay<WorkRequestView>(tx, project, actor.PrincipalId, "claimWork", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        var current = await LockWork(tx, project, work, actor.PrincipalId, ct); if (current.Version != expected) throw new ProjectIdentityException("CAS_MISMATCH", "The work request version does not match If-Match.", 412);
        if (!await ValidContext(tx, project, current.TaskId, current.ContextId, ct)) throw ProjectIdentityException.Conflict("The work context is stale and cannot be claimed.");
        var maxAttempts = WorkScopePolicy.MaxAttempts(current.Budget);
        var expired = current.State is "leased" or "running" && current.LeaseUntil <= DateTimeOffset.UtcNow;
        var retry = current.State == "failed" && current.Attempt < maxAttempts;
        if (current.State != "queued" && !expired && !retry) throw ProjectIdentityException.Conflict("Work is not available for claim.");
        if (current.Deadline <= DateTimeOffset.UtcNow) throw ProjectIdentityException.Conflict("The work deadline has passed.");
        var create = new CreateWorkRequest(current.TaskId, current.ContextId, current.Kind, current.Scope, current.Budget, current.Deadline);
        WorkScopePolicy.ValidateCapabilities(create, request.ExecutorCapabilities);
        await Execute(tx, "UPDATE agent.work_request SET state='leased',holder_id=@actor,lease_until=@lease,fence=fence+1,attempt=attempt+1,run_id=NULL,version=version+1 WHERE project_id=@project AND work_id=@work AND version=@expected", ct,
            ("actor", actor.PrincipalId), ("lease", DateTimeOffset.UtcNow.Add(lease)), ("project", project), ("work", work), ("expected", expected));
        var result = await ReadWork(tx, project, work, false, ct); await Save(tx, project, actor.PrincipalId, "claimWork", key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    public async Task<WorkRequestView> HeartbeatAsync(AtlasPrincipal actor, Guid project, Guid work, HeartbeatWorkRequest request, string key, string requestId, string? trace, CancellationToken ct)
    {
        RequireExecutor(actor); if (request.Checkpoint.ValueKind != JsonValueKind.Object || request.Fence < 1) throw ProjectIdentityException.Invalid("A positive fence and checkpoint object are required."); var digest = Digest(request);
        await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await Context(tx, project, actor.PrincipalId, ct); await RequireMember(tx, project, actor.PrincipalId, ct, true);
        if (await Replay<WorkRequestView>(tx, project, actor.PrincipalId, "heartbeatWork", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        var current = await LockWork(tx, project, work, actor.PrincipalId, ct); RequireLease(actor, current, request.Fence);
        if (!await ValidContext(tx, project, current.TaskId, current.ContextId, ct)) throw ProjectIdentityException.Conflict("The work context became stale; checkpoint was retained but the lease was not renewed.");
        await Execute(tx, "UPDATE agent.work_request SET state='running',lease_until=@lease,checkpoint=@checkpoint::jsonb,version=version+1 WHERE project_id=@project AND work_id=@work; UPDATE agent.run SET checkpoint=@checkpoint::jsonb,version=version+1 WHERE project_id=@project AND run_id=@run AND executor_id=@actor", ct,
            ("lease", DateTimeOffset.UtcNow.Add(lease)), ("checkpoint", request.Checkpoint.GetRawText()), ("project", project), ("work", work), ("run", Db(current.RunId)), ("actor", actor.PrincipalId));
        var result = await ReadWork(tx, project, work, false, ct); await Save(tx, project, actor.PrincipalId, "heartbeatWork", key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    public async Task<WorkRequestView> FinishAsync(AtlasPrincipal actor, Guid project, Guid work, FinishWorkRequest request, string key, string requestId, string? trace, CancellationToken ct)
    {
        RequireExecutor(actor); RunEvidencePolicy.ValidateResult(request.ResultRef);
        if (request.Fence < 1 || request.Result is not ("succeeded" or "failed" or "blocked" or "waiting_external")) throw ProjectIdentityException.Invalid("A valid fence and work result are required.");
        var digest = Digest(request); await using var connection = await OpenAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
        await Context(tx, project, actor.PrincipalId, ct); await RequireMember(tx, project, actor.PrincipalId, ct, true);
        if (await Replay<WorkRequestView>(tx, project, actor.PrincipalId, "finishWork", key, digest, ct) is { } replay) { await tx.CommitAsync(ct); return replay; }
        var current = await LockWork(tx, project, work, actor.PrincipalId, ct); RequireLease(actor, current, request.Fence);
        if (!await ValidContext(tx, project, current.TaskId, current.ContextId, ct)) throw ProjectIdentityException.Conflict("A stale work result cannot be committed.");
        var run = await ReadRun(tx, project, request.RunId, true, ct);
        if (run.TaskId != current.TaskId || run.ContextId != current.ContextId || run.State is "succeeded" or "failed" or "canceled" or "stale") throw ProjectIdentityException.Conflict("The result run does not match this active work request.");
        await RequireRunExecutor(tx, project, request.RunId, actor.PrincipalId, ct);
        if (request.Result == "succeeded" && current.Kind == "investigate" && !await HasExecutedEvidence(tx, project, request.RunId, ct))
            throw ProjectIdentityException.Conflict("A successful repository investigation requires captured tool evidence; reconstructed summaries are insufficient.");
        var maxAttempts = WorkScopePolicy.MaxAttempts(current.Budget); var retry = request.Result == "failed" && current.Attempt < maxAttempts;
        var state = retry ? "queued" : request.Result; var runState = request.Result switch { "succeeded" => "succeeded", "failed" => "failed", "blocked" => "waiting_external", _ => "waiting_external" };
        await Execute(tx, """
            UPDATE agent.work_request SET state=@state,result_ref=@result::jsonb,run_id=@run,holder_id=CASE WHEN @retry THEN NULL ELSE holder_id END,
              lease_until=CASE WHEN @retry OR @terminal THEN NULL ELSE lease_until END,version=version+1 WHERE project_id=@project AND work_id=@work;
            UPDATE agent.run SET state=@runState,version=version+1 WHERE project_id=@project AND run_id=@run
            """, ct, ("state", state), ("result", request.ResultRef.GetRawText()), ("run", request.RunId), ("retry", retry), ("terminal", request.Result is "succeeded" or "failed" or "blocked"),
            ("project", project), ("work", work), ("runState", runState));
        var result = await ReadWork(tx, project, work, false, ct); await Save(tx, project, actor.PrincipalId, "finishWork", key, digest, result, requestId, trace, ct); await tx.CommitAsync(ct); return result;
    }

    private static void RequireExecutor(AtlasPrincipal actor) { if (actor.Kind is not (PrincipalKind.Agent or PrincipalKind.Service)) throw ProjectIdentityException.Forbidden("External work requires an authenticated agent or controlled runner identity."); }
    private static void RequireLease(AtlasPrincipal actor, WorkRequestView work, long fence)
    {
        if (work.HolderId != actor.PrincipalId) throw ProjectIdentityException.NotFound();
        if (work.Fence != fence) throw ProjectIdentityException.Conflict("The fencing token is stale; reload and claim the current work before writing.");
        if (work.State is not ("leased" or "running" or "waiting_external")) throw ProjectIdentityException.Conflict("Work is not actively leased.");
        if (work.LeaseUntil <= DateTimeOffset.UtcNow) throw ProjectIdentityException.Conflict("The lease expired; reclaim the work to obtain a new fence.");
    }

    private async Task<WorkRequestView> LockWork(NpgsqlTransaction tx, Guid project, Guid work, Guid principal, CancellationToken ct)
    {
        var result = await ReadWork(tx, project, work, true, ct); await AuthorizeTask(tx, principal, project, result.TaskId, ct); return result;
    }
    private async Task<AgentRunView> ReadRun(NpgsqlTransaction tx, Guid project, Guid run, bool write, CancellationToken ct)
    {
        await using var command = Command(tx); command.CommandText = "SELECT run_id,task_id,context_id,state,version,provider,model,usage,checkpoint,executor_id FROM agent.run WHERE project_id=@project AND run_id=@run" + (write ? " FOR UPDATE" : "");
        Add(command, "project", project); Add(command, "run", run); await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        return new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), Element(reader.GetString(7)), Element(reader.GetString(8)));
    }
    private async Task<WorkRequestView> ReadWork(NpgsqlTransaction tx, Guid project, Guid work, bool write, CancellationToken ct)
    {
        await using var command = Command(tx); command.CommandText = "SELECT work_id,task_id,context_id,kind,state,scope,budget,version,fence,lease_until,run_id,deadline,attempt,checkpoint,result_ref,holder_id FROM agent.work_request WHERE project_id=@project AND work_id=@work" + (write ? " FOR UPDATE" : "");
        Add(command, "project", project); Add(command, "work", work); await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        var holder = reader.IsDBNull(15) ? (Guid?)null : reader.GetGuid(15);
        var result = new WorkRequestView(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), Element(reader.GetString(5)), Element(reader.GetString(6)), reader.GetInt64(7), reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetDateTime(9), reader.IsDBNull(10) ? null : reader.GetGuid(10), reader.IsDBNull(11) ? null : reader.GetDateTime(11), reader.GetInt32(12), Element(reader.GetString(13)), Element(reader.GetString(14)));
        return result with { HolderId = holder };
    }
    private static async Task RequireRunExecutor(NpgsqlTransaction tx, Guid project, Guid run, Guid actor, CancellationToken ct)
    { await using var command = Command(tx); command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent.run WHERE project_id=@project AND run_id=@run AND executor_id=@actor)"; Add(command, "project", project); Add(command, "run", run); Add(command, "actor", actor); if (await command.ExecuteScalarAsync(ct) is not true) throw ProjectIdentityException.NotFound(); }
    private static async Task<bool> HasExecutedEvidence(NpgsqlTransaction tx, Guid project, Guid run, CancellationToken ct)
    { await using var command = Command(tx); command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent.run_event WHERE project_id=@project AND run_id=@run AND capture_kind IN ('rawCaptured','verifiedScript'))"; Add(command, "project", project); Add(command, "run", run); return await command.ExecuteScalarAsync(ct) is true; }
    private static async Task RequireTaskVersion(NpgsqlTransaction tx, Guid project, Guid task, long expected, CancellationToken ct)
    { await using var command = Command(tx); command.CommandText = "SELECT version FROM collab.task WHERE project_id=@project AND task_id=@task FOR UPDATE"; Add(command, "project", project); Add(command, "task", task); if (await command.ExecuteScalarAsync(ct) is not long version) throw ProjectIdentityException.NotFound(); if (version != expected) throw new ProjectIdentityException("CAS_MISMATCH", "The work request version does not match If-Match.", 412); }
    private static async Task<bool> ValidContext(NpgsqlTransaction tx, Guid project, Guid task, Guid context, CancellationToken ct)
    {
        await using var command = Command(tx); command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM delivery.task_context c JOIN identity.project p ON p.project_id=c.project_id
              WHERE c.project_id=@project AND c.task_id=@task AND c.context_id=@context AND c.phase='executable' AND c.policy_epoch=p.policy_epoch
                AND (c.expires_at IS NULL OR c.expires_at>now()) AND NOT EXISTS(SELECT 1 FROM delivery.context_invalidation i WHERE i.project_id=c.project_id AND i.context_id=c.context_id)
                AND NOT EXISTS(SELECT 1 FROM delivery.context_dependency d LEFT JOIN gov.baseline_entry e ON e.project_id=d.project_id AND e.baseline_id=p.current_baseline_id AND e.unit_id=d.unit_id
                  WHERE d.project_id=c.project_id AND d.context_id=c.context_id AND d.required AND (e.revision_id IS NULL OR e.revision_id<>d.revision_id)))
            """; Add(command, "project", project); Add(command, "task", task); Add(command, "context", context); return await command.ExecuteScalarAsync(ct) is true;
    }
    private async Task AuthorizeTask(NpgsqlTransaction tx, Guid principal, Guid project, Guid task, CancellationToken ct)
    {
        await using var command = Command(tx); command.CommandText = "SELECT root_unit_id FROM collab.task WHERE project_id=@project AND task_id=@task"; Add(command, "project", project); Add(command, "task", task);
        var root = await command.ExecuteScalarAsync(ct); if (root is null or DBNull) { if (root is null) throw ProjectIdentityException.NotFound(); return; }
        var read = await access.CreateReadContextAsync(principal, project, new(ReadMode.Published, ReadPurpose.Change, null, null, null), ct); await access.AuthorizeReadAsync(read, (Guid)root, "read", null, ct);
    }
    private async Task<bool> CanReadTask(NpgsqlTransaction tx, Guid principal, Guid project, Guid task, CancellationToken ct) { try { await AuthorizeTask(tx, principal, project, task, ct); return true; } catch (ProjectIdentityException e) when (e.StatusCode is 403 or 404) { return false; } }
    private static async Task RequireMember(NpgsqlTransaction tx, Guid project, Guid principal, CancellationToken ct, bool write = false)
    {
        await using var command = Command(tx); command.CommandText = "SELECT m.roles FROM identity.project p JOIN identity.membership m ON m.project_id=p.project_id AND m.principal_id=@principal AND m.active WHERE p.project_id=@project AND p.archived_at IS NULL";
        Add(command, "project", project); Add(command, "principal", principal); var roles = await command.ExecuteScalarAsync(ct) as string[] ?? throw ProjectIdentityException.NotFound();
        if (write && !roles.Any(role => role is "Contributor" or "AgentProposer" or "ProjectAdmin")) throw ProjectIdentityException.Forbidden();
    }
    private static async Task Save<T>(NpgsqlTransaction tx, Guid project, Guid actor, string operation, string key, string digest, T result, string request, string? trace, CancellationToken ct)
    {
        ValidateKey(key); await using var command = Command(tx); command.CommandText = "INSERT INTO ops.idempotency(project_id,principal_id,operation_id,idempotency_key,request_sha256,result_ref) VALUES(@project,@actor,@operation,@key,@digest,@result::jsonb); INSERT INTO ops.audit_event(project_id,actor_id,action,resource_ref,result_code,request_id,trace_id) VALUES(@project,@actor,@operation,@result::jsonb,'OK',@request,@trace)";
        Add(command, "project", project); Add(command, "actor", actor); Add(command, "operation", operation); Add(command, "key", key); Add(command, "digest", digest); Add(command, "result", JsonSerializer.Serialize(result, Json), NpgsqlDbType.Jsonb); Add(command, "request", request); Add(command, "trace", trace); await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task<T?> Replay<T>(NpgsqlTransaction tx, Guid project, Guid actor, string operation, string key, string digest, CancellationToken ct) where T : class
    {
        ValidateKey(key); await using var command = Command(tx); command.CommandText = "SELECT request_sha256,result_ref FROM ops.idempotency WHERE project_id=@project AND principal_id=@actor AND operation_id=@operation AND idempotency_key=@key";
        Add(command, "project", project); Add(command, "actor", actor); Add(command, "operation", operation); Add(command, "key", key); await using var reader = await command.ExecuteReaderAsync(ct); if (!await reader.ReadAsync(ct)) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(reader.GetString(0)), Encoding.ASCII.GetBytes(digest))) throw ProjectIdentityException.Conflict("The idempotency key was already used with a different request."); return JsonSerializer.Deserialize<T>(reader.GetString(1), Json);
    }
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) { if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("ConnectionStrings:Atlas is required for Agent MCP."); var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(ct); return connection; }
    private static async Task Context(NpgsqlTransaction tx, Guid project, Guid principal, CancellationToken ct) { await using var command = Command(tx); command.CommandText = "SELECT set_config('atlas.project_id',@project,true),set_config('atlas.subject_id',@principal,true)"; Add(command, "project", project.ToString()); Add(command, "principal", principal.ToString()); await command.ExecuteNonQueryAsync(ct); }
    private static async Task Execute(NpgsqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters) { await using var command = Command(tx); command.CommandText = sql; foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value); await command.ExecuteNonQueryAsync(ct); }
    private static NpgsqlCommand Command(NpgsqlTransaction tx) { var command = tx.Connection!.CreateCommand(); command.Transaction = tx; return command; }
    private static void Add(NpgsqlCommand command, string name, object? value, NpgsqlDbType? type = null) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; if (type.HasValue) parameter.NpgsqlDbType = type.Value; command.Parameters.Add(parameter); }
    private static object Db(object? value) => value ?? DBNull.Value;
    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json))).ToLowerInvariant();
    private static void ValidateKey(string key) { if (key.Length is < 8 or > 128) throw ProjectIdentityException.Invalid("Idempotency-Key must contain 8-128 characters."); }
    private static string Encode(Guid id) => Convert.ToBase64String(id.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Encode(long id) => Convert.ToBase64String(BitConverter.GetBytes(id)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Guid? GuidCursor(string? value) { if (string.IsNullOrEmpty(value)) return null; try { return new Guid(Decode(value)); } catch (Exception e) when (e is FormatException or ArgumentException) { throw ProjectIdentityException.Invalid("The pagination cursor is invalid."); } }
    private static long? LongCursor(string? value) { if (string.IsNullOrEmpty(value)) return null; try { return BitConverter.ToInt64(Decode(value)); } catch (Exception e) when (e is FormatException or ArgumentException) { throw ProjectIdentityException.Invalid("The pagination cursor is invalid."); } }
    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
}

