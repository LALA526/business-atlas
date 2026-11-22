using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Contracts;
using BuildingBlocks.Commands;
using Npgsql;
using NpgsqlTypes;
using PlatformOps.Jobs;
using ProjectIdentity.Application;

namespace Assistant.Infrastructure;

/// <summary>M10 persistence. All reads and writes execute inside the caller's project/subject RLS context.</summary>
public sealed class PostgresAssistantStore(string connectionString)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string connectionString = connectionString;

    public async Task<ConversationPage> ListConversationsAsync(Guid principal, Guid project, string? cursor, int limit, CancellationToken ct)
    {
        var offset = Offset(cursor);
        await using var db = await Open(project, principal, ct);
        await using var command = Command(db, "SELECT conversation_id,root_unit_id,baseline_id,title,version FROM assistant.conversation WHERE project_id=@project ORDER BY created_at DESC,conversation_id DESC OFFSET @offset LIMIT @limit");
        Add(command, "project", project); Add(command, "offset", offset); Add(command, "limit", limit + 1);
        var items = new List<ConversationView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetInt64(4)));
        return Page(items, limit, offset);
    }

    public async Task<MessagePage> ListMessagesAsync(Guid principal, Guid project, Guid conversation, string? cursor, int limit, CancellationToken ct)
    {
        var offset = Offset(cursor);
        await using var db = await Open(project, principal, ct);
        await RequireConversation(db, conversation, ct);
        await using var command = Command(db, "SELECT message_id,seq,kind,body,created_at,run_id FROM assistant.message WHERE project_id=@project AND conversation_id=@conversation ORDER BY seq OFFSET @offset LIMIT @limit");
        Add(command, "project", project); Add(command, "conversation", conversation); Add(command, "offset", offset); Add(command, "limit", limit + 1);
        var items = new List<MessageView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            using var body = JsonDocument.Parse(reader.GetString(3));
            var root = body.RootElement;
            items.Add(new(reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), Text(root, "text"), root.TryGetProperty("answer", out var answer) ? answer.Clone() : null, reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetGuid(5)));
        }
        return Page(items, limit, offset);
    }

    public async Task<ProviderConfigPage> ListProvidersAsync(Guid principal, Guid project, string? cursor, int limit, CancellationToken ct)
    {
        var offset = Offset(cursor);
        await using var db = await Open(project, principal, ct);
        await RequireAdmin(db, ct);
        await using var command = Command(db, "SELECT provider_id,provider_key,endpoint,model,secret_ref,allowed,options,version FROM assistant.provider WHERE project_id=@project ORDER BY provider_key OFFSET @offset LIMIT @limit");
        Add(command, "project", project); Add(command, "offset", offset); Add(command, "limit", limit + 1);
        var items = new List<ProviderConfig>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), JsonDocument.Parse(reader.GetString(6)).RootElement.Clone(), reader.GetInt64(7)));
        return Page(items, limit, offset);
    }

    public async Task<ConversationView> CreateConversationAsync(Guid principal, Guid project, CreateConversationRequest request, string key, string requestId, string? traceId, CancellationToken ct)
    {
        ValidateKey(key); ValidateContext(request.ReadContext);
        var title = string.IsNullOrWhiteSpace(request.Title) ? "Project assistant" : request.Title.Trim();
        if (title.Length > 200) throw ProjectIdentityException.Invalid("The conversation title must contain at most 200 characters.");
        var digest = RequestDigest.Compute(request);
        await using var db = await Open(project, principal, ct, "createConversation", key, requestId, traceId);
        if (await Replay<ConversationView>(db, digest, ct) is { } replay) return replay;
        if (request.RootUnitId.HasValue)
        {
            await using var unit = Command(db, "SELECT EXISTS(SELECT 1 FROM core.unit WHERE project_id=@project AND unit_id=@unit AND archived_at IS NULL)");
            Add(unit, "project", project); Add(unit, "unit", request.RootUnitId.Value);
            if (await unit.ExecuteScalarAsync(ct) is not true) throw ProjectIdentityException.NotFound();
        }
        var id = Guid.NewGuid();
        await using (var command = Command(db, "INSERT INTO assistant.conversation(project_id,conversation_id,root_unit_id,baseline_id,title,owner_id,created_by) VALUES (@project,@id,@root,@baseline,@title,@principal,@principal) RETURNING version"))
        {
            Add(command, "project", project); Add(command, "id", id); Add(command, "root", request.RootUnitId, NpgsqlDbType.Uuid); Add(command, "baseline", request.ReadContext.BaselineId ?? throw ProjectIdentityException.Invalid("An explicit baselineId is required."));
            Add(command, "title", title); Add(command, "principal", principal);
            await command.ExecuteScalarAsync(ct);
        }
        var result = new ConversationView(id, request.RootUnitId, request.ReadContext.BaselineId.Value, title, 1);
        await SaveReplay(db, digest, result, 201, ct);
        await Emit(db, project, id, "conversation.created", new { conversationId = id }, ct);
        await db.Transaction.CommitAsync(ct);
        return result;
    }

    public async Task<JobInfo> SendMessageAsync(Guid principal, Guid project, Guid conversation, SendMessageRequest request, long expected, string key, string requestId, string? traceId, CancellationToken ct)
    {
        ValidateKey(key); ValidateContext(request.ReadContext);
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 32000) throw ProjectIdentityException.Invalid("Message text must contain 1-32000 characters.");
        var digest = RequestDigest.Compute(new { conversation, request, expected });
        await using var db = await Open(project, principal, ct, "sendMessage", key, requestId, traceId);
        if (await Replay<JobInfo>(db, digest, ct) is { } replay) return replay;
        var current = await RequireConversation(db, conversation, ct, true);
        if (current.Version != expected) throw AtlasCommandException.CasMismatch();
        if (current.BaselineId != request.ReadContext.BaselineId) throw new AtlasCommandException("CONTEXT_CONFLICT", "The message read context must use the conversation baseline.", 409);
        var active = await Scalar<bool>(db, "SELECT EXISTS(SELECT 1 FROM ops.job WHERE project_id=@project AND input->>'conversationId'=@conversation AND state IN ('queued','running','waiting_external','cancel_requested'))", ("project", project), ("conversation", conversation.ToString()));
        if (active) throw new AtlasCommandException("CONVERSATION_BUSY", "Cancel or await the active conversation job before sending another message.", 409);
        var runId = Guid.NewGuid(); var jobId = Guid.NewGuid(); var messageId = Guid.NewGuid();
        var seq = await Scalar<long>(db, "SELECT COALESCE(MAX(seq),-1)+1 FROM assistant.message WHERE project_id=@project AND conversation_id=@conversation", ("project", project), ("conversation", conversation));
        await Execute(db, "INSERT INTO agent.run(project_id,run_id,executor_id,state,provider,model,checkpoint,budget,usage,created_by) VALUES (@project,@run,@principal,'queued',NULL,NULL,'{}'::jsonb,@budget::jsonb,'{}'::jsonb,@principal)", ("project", project), ("run", runId), ("principal", principal), ("budget", JsonSerializer.Serialize(new { maxInputTokens = 24000, maxOutputTokens = 4096, maxToolCalls = 20, maxDurationSeconds = 120 })));
        await Execute(db, "INSERT INTO assistant.message(project_id,message_id,conversation_id,run_id,seq,kind,body,created_by) VALUES (@project,@message,@conversation,@run,@seq,'user',@body::jsonb,@principal)", ("project", project), ("message", messageId), ("conversation", conversation), ("run", runId), ("seq", seq), ("body", JsonSerializer.Serialize(new { text = request.Text })), ("principal", principal));
        var input = JsonSerializer.SerializeToElement(new { conversationId = conversation, runId, text = request.Text, readContext = request.ReadContext, budget = new { maxDurationSeconds = 120, maxInputTokens = 24000, maxOutputTokens = 4096, maxToolCalls = 20 } }, Json);
        await Execute(db, "INSERT INTO ops.job(project_id,job_id,kind,requester_id,state,input,max_attempt) VALUES (@project,@job,'assistant.conversation',@principal,'queued',@input::jsonb,3)", ("project", project), ("job", jobId), ("principal", principal), ("input", input.GetRawText()));
        await Execute(db, "UPDATE assistant.conversation SET version=version+1 WHERE project_id=@project AND conversation_id=@conversation", ("project", project), ("conversation", conversation));
        var job = await ReadJob(db, project, jobId, ct);
        await SaveReplay(db, digest, job, 202, ct);
        await Emit(db, project, conversation, "message.accepted", new { jobId, runId, messageId, seq }, ct);
        await db.Transaction.CommitAsync(ct);
        return job;
    }

    public async Task<JobInfo> CancelAsync(Guid principal, Guid project, Guid conversation, Guid jobId, long expected, string reason, string key, string requestId, string? traceId, CancellationToken ct)
    {
        ValidateKey(key);
        var digest = RequestDigest.Compute(new { conversation, jobId, expected, reason });
        await using var db = await Open(project, principal, ct, "cancelConversation", key, requestId, traceId);
        if (await Replay<JobInfo>(db, digest, ct) is { } replay) return replay;
        var current = await RequireConversation(db, conversation, ct, true);
        if (current.Version != expected) throw AtlasCommandException.CasMismatch();
        var actualJob = await ReadJob(db, project, jobId, ct);
        if (actualJob.State is not (JobState.Queued or JobState.Running or JobState.WaitingExternal or JobState.CancelRequested)) throw new AtlasCommandException("INVALID_JOB_STATE", "Only active jobs can be canceled.", 409);
        var rows = await Execute(db, "UPDATE ops.job SET state=CASE WHEN state='running' THEN 'cancel_requested' ELSE 'canceled' END,version=version+1,error_code='CANCELED' WHERE project_id=@project AND job_id=@job AND kind='assistant.conversation' AND input->>'conversationId'=@conversation", ("project", project), ("job", jobId), ("conversation", conversation.ToString()));
        if (rows != 1) throw ProjectIdentityException.NotFound();
        await Execute(db, "UPDATE agent.run SET state='canceled',version=version+1 WHERE project_id=@project AND run_id IN (SELECT run_id FROM assistant.message WHERE project_id=@project AND conversation_id=@conversation AND run_id IS NOT NULL) AND state IN ('queued','running') AND EXISTS(SELECT 1 FROM ops.job WHERE project_id=@project AND job_id=@job AND input->>'runId'=agent.run.run_id::text)", ("project", project), ("conversation", conversation), ("job", jobId));
        var job = await ReadJob(db, project, jobId, ct);
        await SaveReplay(db, digest, job, 200, ct);
        await Emit(db, project, conversation, "conversation.cancel_requested", new { jobId, reason }, ct);
        await db.Transaction.CommitAsync(ct);
        return job;
    }

    public async Task<ProviderConfig> ConfigureProviderAsync(Guid principal, Guid project, Guid providerId, ProviderConfig request, long expected, string key, string requestId, string? traceId, CancellationToken ct)
    {
        ValidateKey(key); ValidateProvider(request);
        var digest = RequestDigest.Compute(new { providerId, request, expected });
        await using var db = await Open(project, principal, ct, "configureProvider", key, requestId, traceId);
        await RequireAdmin(db, ct);
        if (await Replay<ProviderConfig>(db, digest, ct) is { } replay) return replay;
        var options = request.Options.GetRawText();
        int rows;
        if (expected == 0 && !await Exists(db, "assistant.provider", "provider_id", providerId, ct))
        {
            rows = await Execute(db, "INSERT INTO assistant.provider(project_id,provider_id,provider_key,endpoint,model,secret_ref,allowed,options,created_by) VALUES (@project,@id,@key,@endpoint,@model,@secret,@allowed,@options::jsonb,@principal)", ("project", project), ("id", providerId), ("key", request.ProviderKey), ("endpoint", request.Endpoint), ("model", request.Model), ("secret", request.SecretRef), ("allowed", request.Allowed), ("options", options), ("principal", principal));
        }
        else
        {
            rows = await Execute(db, "UPDATE assistant.provider SET provider_key=@key,endpoint=@endpoint,model=@model,secret_ref=@secret,allowed=@allowed,options=@options::jsonb,version=version+1 WHERE project_id=@project AND provider_id=@id AND version=@expected", ("project", project), ("id", providerId), ("key", request.ProviderKey), ("endpoint", request.Endpoint), ("model", request.Model), ("secret", request.SecretRef), ("allowed", request.Allowed), ("options", options), ("expected", expected));
        }
        if (rows != 1) throw AtlasCommandException.CasMismatch();
        await using var read = Command(db, "SELECT provider_id,provider_key,endpoint,model,secret_ref,allowed,options,version FROM assistant.provider WHERE project_id=@project AND provider_id=@id");
        Add(read, "project", project); Add(read, "id", providerId);
        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        var result = new ProviderConfig(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), JsonDocument.Parse(reader.GetString(6)).RootElement.Clone(), reader.GetInt64(7));
        await reader.DisposeAsync();
        await SaveReplay(db, digest, result, 200, ct);
        await db.Transaction.CommitAsync(ct);
        return result;
    }

    public async Task<ProviderConfig> GetActiveProviderAsync(Guid project, Guid principal, CancellationToken ct)
    {
        await using var db = await Open(project, principal, ct);
        await using var command = Command(db, "SELECT provider_id,provider_key,endpoint,model,secret_ref,allowed,options,version FROM assistant.provider WHERE project_id=@project AND allowed ORDER BY created_at LIMIT 1");
        Add(command, "project", project);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new AtlasCommandException("DEPENDENCY_UNAVAILABLE", "No enabled model provider is configured.", 503);
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), JsonDocument.Parse(reader.GetString(6)).RootElement.Clone(), reader.GetInt64(7));
    }

    public async Task<(Guid ConversationId, Guid BaselineId, Guid? RootUnitId)> GetRunBindingAsync(Guid project, Guid principal, Guid runId, CancellationToken ct)
    {
        await using var db = await Open(project, principal, ct);
        await using var command = Command(db, "SELECT c.conversation_id,c.baseline_id,c.root_unit_id FROM assistant.conversation c JOIN agent.run r ON r.project_id=c.project_id AND r.run_id=@run WHERE c.project_id=@project AND c.created_by=@principal AND r.created_by=@principal");
        Add(command, "project", project); Add(command, "run", runId); Add(command, "principal", principal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        return (reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2));
    }

    public async Task<JsonElement?> GetCompletedResultAsync(Guid project, Guid principal, Guid runId, CancellationToken ct)
    {
        await using var db = await Open(project, principal, ct);
        await using var command = Command(db, "SELECT checkpoint FROM agent.run WHERE project_id=@project AND run_id=@run AND state='succeeded'");
        Add(command, "project", project); Add(command, "run", runId);
        var raw = await command.ExecuteScalarAsync(ct) as string;
        return raw is null ? null : JsonDocument.Parse(raw).RootElement.Clone();
    }

    public async Task<bool> StartRunAsync(Guid project, Guid principal, Guid runId, ProviderConfig provider, CancellationToken ct)
    {
        var binding = await GetRunBindingAsync(project, principal, runId, ct);
        await using var db = await Open(project, principal, ct);
        var updated = await Execute(db, "UPDATE agent.run SET state='running',provider=@provider,model=@model,version=version+1 WHERE project_id=@project AND run_id=@run AND state IN ('queued','failed')", ("project", project), ("run", runId), ("provider", provider.ProviderKey), ("model", provider.Model));
        if (updated != 1) { await db.Transaction.RollbackAsync(ct); return false; }
        await Emit(db, project, binding.ConversationId, "run.started", new { runId, provider = provider.ProviderKey, model = provider.Model }, ct);
        await Capture(db, project, principal, runId, "provider.request", new { provider = provider.ProviderKey, model = provider.Model }, ct);
        await db.Transaction.CommitAsync(ct);
        return true;
    }

    public async Task AppendDeltaAsync(Guid project, Guid principal, Guid runId, string delta, CancellationToken ct)
    {
        if (delta.Length == 0) return;
        var binding = await GetRunBindingAsync(project, principal, runId, ct);
        await using var db = await Open(project, principal, ct);
        await Emit(db, project, binding.ConversationId, "message.delta", new { runId, delta }, ct);
        await db.Transaction.CommitAsync(ct);
    }

    public async Task<MessageView> CompleteRunAsync(Guid project, Guid principal, Guid runId, JsonElement answer, IReadOnlyList<CitationSource> citations, ProviderUsage? usage, string? providerRequestId, long latencyMs, CancellationToken ct)
    {
        var binding = await GetRunBindingAsync(project, principal, runId, ct);
        await using var db = await Open(project, principal, ct);
        var id = Guid.NewGuid();
        var checkpoint = JsonSerializer.SerializeToElement(new { resultRef = $"conversation:{binding.ConversationId}:message:{id}", providerRequestId, latencyMs }, Json);
        var completed = await Execute(db, "UPDATE agent.run SET state='succeeded',usage=@usage::jsonb,checkpoint=@checkpoint::jsonb,version=version+1 WHERE project_id=@project AND run_id=@run AND state='running'", ("project", project), ("run", runId), ("usage", JsonSerializer.Serialize(usage, Json)), ("checkpoint", checkpoint.GetRawText()));
        if (completed != 1) throw new OperationCanceledException("The run was canceled or is no longer active.");
        var seq = await Scalar<long>(db, "SELECT COALESCE(MAX(seq),-1)+1 FROM assistant.message WHERE project_id=@project AND conversation_id=@conversation", ("project", project), ("conversation", binding.ConversationId));
        var text = string.Join("\n\n", answer.GetProperty("claims").EnumerateArray().Select(c => Text(c, "text")).Where(t => !string.IsNullOrWhiteSpace(t)));
        var body = JsonSerializer.Serialize(new { text, answer }, Json);
        await Execute(db, "INSERT INTO assistant.message(project_id,message_id,conversation_id,run_id,seq,kind,body,created_by) VALUES (@project,@message,@conversation,@run,@seq,'assistant',@body::jsonb,@principal)", ("project", project), ("message", id), ("conversation", binding.ConversationId), ("run", runId), ("seq", seq), ("body", body), ("principal", principal));
        foreach (var citation in citations)
            await Execute(db, "INSERT INTO assistant.citation(project_id,citation_id,message_id,revision_id,locator,block_id) VALUES (@project,@id,@message,@revision,@locator::jsonb,@block)", ("project", project), ("id", Guid.NewGuid()), ("message", id), ("revision", citation.RevisionId), ("locator", JsonSerializer.Serialize(new { citation.UnitId, citation.RevisionId, href = $"/projects/{project}/knowledge/{citation.UnitId}?revisionId={citation.RevisionId}" })), ("block", citation.RefId));
        await Emit(db, project, binding.ConversationId, "message.completed", new { runId, messageId = id, seq, usage, providerRequestId, latencyMs }, ct);
        await Capture(db, project, principal, runId, "provider.response", new { messageId = id, usage, providerRequestId, latencyMs }, ct);
        await db.Transaction.CommitAsync(ct);
        return new(id, seq, "assistant", text, answer, DateTimeOffset.UtcNow, runId);
    }

    public async Task FailRunAsync(Guid project, Guid principal, Guid runId, string code, string safeMessage, CancellationToken ct)
    {
        var binding = await GetRunBindingAsync(project, principal, runId, ct);
        await using var db = await Open(project, principal, ct);
        await Execute(db, "UPDATE agent.run SET state='failed',version=version+1 WHERE project_id=@project AND run_id=@run AND state IN ('queued','running')", ("project", project), ("run", runId));
        await Emit(db, project, binding.ConversationId, "run.failed", new { runId, code, message = safeMessage }, ct);
        await Capture(db, project, principal, runId, "provider.error", new { code, message = safeMessage }, ct);
        await db.Transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ConversationEvent>> EventsAfterAsync(Guid principal, Guid project, Guid conversation, long after, int limit, CancellationToken ct)
    {
        await using var db = await Open(project, principal, ct);
        await RequireConversation(db, conversation, ct);
        await using var command = Command(db, "SELECT seq,event_id,kind,payload,created_at FROM assistant.event WHERE project_id=@project AND conversation_id=@conversation AND seq>@after ORDER BY seq LIMIT @limit");
        Add(command, "project", project); Add(command, "conversation", conversation); Add(command, "after", after); Add(command, "limit", limit);
        var events = new List<ConversationEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) events.Add(new(reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), JsonDocument.Parse(reader.GetString(3)).RootElement.Clone(), reader.GetFieldValue<DateTimeOffset>(4)));
        return events;
    }

    private async Task<Db> Open(Guid project, Guid principal, CancellationToken ct, string? operation = null, string? key = null, string? requestId = null, string? traceId = null)
    {
        _ = requestId; _ = traceId;
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var transaction = await connection.BeginTransactionAsync(ct);
        await using var set = connection.CreateCommand(); set.Transaction = transaction;
        set.CommandText = "SELECT set_config('atlas.project_id',@project,true),set_config('atlas.subject_id',@principal,true);";
        set.Parameters.AddWithValue("project", project.ToString()); set.Parameters.AddWithValue("principal", principal.ToString());
        await set.ExecuteNonQueryAsync(ct);
        return new(connection, transaction, operation, key, requestId, traceId);
    }

    private static async Task RequireAdmin(Db db, CancellationToken ct)
    {
        if (!await Scalar<bool>(db, "SELECT EXISTS(SELECT 1 FROM identity.membership WHERE project_id=current_setting('atlas.project_id')::uuid AND principal_id=current_setting('atlas.subject_id')::uuid AND active AND roles @> ARRAY['ProjectAdmin'])")) throw ProjectIdentityException.Forbidden();
    }

    private static async Task<ConversationView> RequireConversation(Db db, Guid id, CancellationToken ct, bool update = false)
    {
        await using var command = Command(db, $"SELECT conversation_id,root_unit_id,baseline_id,title,version FROM assistant.conversation WHERE project_id=current_setting('atlas.project_id')::uuid AND conversation_id=@id{(update ? " FOR UPDATE" : "")}");
        Add(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        return new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetInt64(4));
    }

    private static async Task<T?> Replay<T>(Db db, string digest, CancellationToken ct) where T : class
    {
        await using var command = Command(db, "SELECT request_sha256,result_ref FROM ops.idempotency WHERE project_id=current_setting('atlas.project_id')::uuid AND principal_id=current_setting('atlas.subject_id')::uuid AND operation_id=@operation AND idempotency_key=@key");
        Add(command, "operation", db.Operation!); Add(command, "key", db.Key!);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (reader.GetString(0) != digest) throw AtlasCommandException.Conflict();
        return JsonSerializer.Deserialize<StoredResult<T>>(reader.GetString(1), Json)?.Data;
    }

    private static async Task SaveReplay<T>(Db db, string digest, T result, int status, CancellationToken ct)
    {
        await Execute(db, "INSERT INTO ops.idempotency(project_id,principal_id,operation_id,idempotency_key,request_sha256,result_ref) VALUES (current_setting('atlas.project_id')::uuid,current_setting('atlas.subject_id')::uuid,@operation,@key,@digest,@result::jsonb)", ("operation", db.Operation!), ("key", db.Key!), ("digest", digest), ("result", JsonSerializer.Serialize(new StoredResult<T>(result, status), Json)));
    }

    private static async Task<JobInfo> ReadJob(Db db, Guid project, Guid id, CancellationToken ct)
    {
        await using var command = Command(db, "SELECT * FROM ops.job WHERE project_id=@project AND job_id=@id"); Add(command, "project", project); Add(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw ProjectIdentityException.NotFound();
        JsonElement JsonValue(string name) => JsonDocument.Parse(reader.GetString(reader.GetOrdinal(name))).RootElement.Clone();
        var state = reader.GetString(reader.GetOrdinal("state")) switch { "queued" => JobState.Queued, "running" => JobState.Running, "waiting_external" => JobState.WaitingExternal, "succeeded" => JobState.Succeeded, "failed" => JobState.Failed, "canceled" => JobState.Canceled, "cancel_requested" => JobState.CancelRequested, _ => throw new DataException() };
        var result = JsonValue("result"); var checkpoint = JsonValue("checkpoint");
        return new(project, id, reader.GetString(reader.GetOrdinal("kind")), state, reader.GetInt64(reader.GetOrdinal("version")), reader.GetInt32(reader.GetOrdinal("attempt")), reader.GetInt32(reader.GetOrdinal("max_attempt")), checkpoint.TryGetProperty("progress", out var p) && p.TryGetDouble(out var progress) ? progress : null, result.TryGetProperty("resultRef", out var r) ? r.GetString() : null, reader.IsDBNull(reader.GetOrdinal("error_code")) ? null : reader.GetString(reader.GetOrdinal("error_code")), JsonValue("input"), checkpoint, reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("available_at")), reader.IsDBNull(reader.GetOrdinal("lease_until")) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("lease_until")), reader.GetInt64(reader.GetOrdinal("fence")), reader.GetGuid(reader.GetOrdinal("requester_id")), reader.IsDBNull(reader.GetOrdinal("context_id")) ? null : reader.GetGuid(reader.GetOrdinal("context_id")));
    }

    private static async Task Emit(Db db, Guid project, Guid conversation, string kind, object payload, CancellationToken ct) =>
        await Execute(db, "INSERT INTO assistant.event(project_id,conversation_id,event_id,kind,payload) VALUES (@project,@conversation,@id,@kind,@payload::jsonb)", ("project", project), ("conversation", conversation), ("id", Guid.NewGuid()), ("kind", kind), ("payload", JsonSerializer.Serialize(payload, Json)));

    private static async Task Capture(Db db, Guid project, Guid principal, Guid runId, string kind, object payload, CancellationToken ct)
    {
        var seq = await Scalar<long>(db, "SELECT COALESCE(MAX(seq),-1)+1 FROM agent.run_event WHERE project_id=@project AND run_id=@run", ("project", project), ("run", runId));
        await Execute(db, "INSERT INTO agent.run_event(project_id,event_id,run_id,seq,external_event_id,capture_kind,event_type,event_data,occurred_at,created_by) VALUES (@project,@event,@run,@seq,@external,'rawCaptured',@kind,@payload::jsonb,now(),@principal)", ("project", project), ("event", Guid.NewGuid()), ("run", runId), ("seq", seq), ("external", Guid.NewGuid().ToString()), ("kind", kind), ("payload", JsonSerializer.Serialize(payload, Json)), ("principal", principal));
    }

    private static async Task<bool> Exists(Db db, string table, string column, Guid id, CancellationToken ct) => await Scalar<bool>(db, $"SELECT EXISTS(SELECT 1 FROM {table} WHERE project_id=current_setting('atlas.project_id')::uuid AND {column}=@id)", ("id", id));
    private static NpgsqlCommand Command(Db db, string sql) { var command = db.Connection.CreateCommand(); command.Transaction = db.Transaction; command.CommandText = sql; return command; }
    private static async Task<T> Scalar<T>(Db db, string sql, params (string Name, object? Value)[] parameters) { await using var command = Command(db, sql); foreach (var p in parameters) Add(command, p.Name, p.Value); return (T)(await command.ExecuteScalarAsync())!; }
    private static async Task<int> Execute(Db db, string sql, params (string Name, object? Value)[] parameters) { await using var command = Command(db, sql); foreach (var p in parameters) Add(command, p.Name, p.Value); return await command.ExecuteNonQueryAsync(); }
    private static void Add(NpgsqlCommand command, string name, object? value, NpgsqlDbType? type = null) { var parameter = command.Parameters.AddWithValue(name, type ?? NpgsqlDbType.Unknown, value ?? DBNull.Value); if (type is null && value is Guid) parameter.NpgsqlDbType = NpgsqlDbType.Uuid; }
    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static int Offset(string? cursor) { if (string.IsNullOrEmpty(cursor)) return 0; try { var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)); return int.TryParse(value, out var offset) && offset >= 0 ? offset : throw new FormatException(); } catch { throw ProjectIdentityException.Invalid("The page cursor is invalid."); } }
    private static string Cursor(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString()));
    private static (IReadOnlyList<T>, string?) Page<T>(List<T> items, int limit, int offset) { var more = items.Count > limit; if (more) items.RemoveAt(items.Count - 1); return (items, more ? Cursor(offset + limit) : null); }
    private static ConversationPage Page(List<ConversationView> items, int limit, int offset) { var page = Page<ConversationView>(items, limit, offset); return new(page.Item1, page.Item2); }
    private static MessagePage Page(List<MessageView> items, int limit, int offset) { var page = Page<MessageView>(items, limit, offset); return new(page.Item1, page.Item2); }
    private static ProviderConfigPage Page(List<ProviderConfig> items, int limit, int offset) { var page = Page<ProviderConfig>(items, limit, offset); return new(page.Item1, page.Item2); }
    private static void ValidateKey(string key) { if (key.Length is < 8 or > 128) throw ProjectIdentityException.Invalid("Idempotency-Key must contain 8-128 characters."); }
    private static void ValidateContext(AccessApplicability.Contracts.ReadContextRequest context) { if (context.BaselineId is null) throw ProjectIdentityException.Invalid("An explicit baselineId is required for model generation."); }
    private static void ValidateProvider(ProviderConfig request)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderKey) || request.ProviderKey.Length > 100 || string.IsNullOrWhiteSpace(request.Model) || request.Model.Length > 200 || string.IsNullOrWhiteSpace(request.SecretRef) || !(request.SecretRef.StartsWith("env:") || request.SecretRef.StartsWith("config:"))) throw ProjectIdentityException.Invalid("A bounded provider key, model, and env:/config: secretRef are required.");
        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.UserInfo.Length > 0) throw ProjectIdentityException.Invalid("A provider endpoint must be an absolute HTTPS URL without credentials, query, or fragment.");
        if (request.Options.ValueKind != JsonValueKind.Object || request.Options.GetRawText().Length > 8192) throw ProjectIdentityException.Invalid("Provider options must be a bounded JSON object.");
    }

    private sealed record StoredResult<T>(T Data, int StatusCode);
    private sealed class Db : IAsyncDisposable
    {
        public Db(NpgsqlConnection connection, NpgsqlTransaction transaction, string? operation, string? key, string? requestId, string? traceId)
        { Connection = connection; Transaction = transaction; Operation = operation; Key = key; RequestId = requestId; TraceId = traceId; }
        public NpgsqlConnection Connection { get; }
        public NpgsqlTransaction Transaction { get; }
        public string? Operation { get; }
        public string? Key { get; }
        public string? RequestId { get; }
        public string? TraceId { get; }
        public async ValueTask DisposeAsync() { await Transaction.DisposeAsync(); await Connection.DisposeAsync(); }
    }
}
