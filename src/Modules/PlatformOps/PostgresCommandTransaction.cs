using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuildingBlocks.Commands;
using BuildingBlocks.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace PlatformOps;

public sealed class PostgresCommandTransaction : ICommandTransaction
{
    private static readonly Regex Identifier = new("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly HashSet<(string Schema, string Table, string Key)> CasTargets =
    [
        ("identity", "project", "project_id"),
        ("core", "unit", "unit_id"),
        ("access", "grant", "grant_id"),
        ("gov", "change_set", "change_set_id"),
        ("intake", "blob", "blob_id"),
        ("intake", "upload", "upload_id"),
        ("intake", "import_batch", "import_id"),
        ("intake", "import_entry", "entry_id"),
        ("collab", "task", "task_id"),
        ("collab", "issue", "issue_id"),
        ("collab", "feedback", "feedback_id"),
        ("collab", "comment", "comment_id"),
        ("collab", "impact", "impact_id"),
        ("delivery", "knowledge_debt", "debt_id"),
        ("agent", "run", "run_id"),
        ("agent", "work_request", "work_id"),
        ("assistant", "provider", "provider_id"),
        ("assistant", "conversation", "conversation_id"),
        ("ops", "job", "job_id"),
        ("ops", "notification", "notification_id"),
    ];

    private readonly AtlasTransaction transaction;
    private bool completed;

    private PostgresCommandTransaction(AtlasTransaction transaction, CommandContext commandContext)
    {
        this.transaction = transaction;
        CommandContext = commandContext;
    }

    public CommandContext CommandContext { get; }

    internal static async Task<PostgresCommandTransaction> BeginAsync(
        AtlasDbContext context,
        CommandContext commandContext,
        CancellationToken cancellationToken)
    {
        var transaction = await AtlasTransaction.BeginAsync(
            context,
            commandContext.ProjectId,
            commandContext.PrincipalId,
            cancellationToken);
        return new PostgresCommandTransaction(transaction, commandContext);
    }

    public async Task<IdempotencyMatch?> FindIdempotencyAsync(CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT request_sha256, result_ref
            FROM ops.idempotency
            WHERE project_id = @project AND principal_id = @principal
              AND operation_id = @operation AND idempotency_key = @key
            """);
        AddIdentityParameters(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadIdempotency(reader);
    }

    public async Task SaveIdempotencyAsync(
        string requestSha256,
        StoredCommandResult result,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO ops.idempotency
              (project_id, principal_id, operation_id, idempotency_key, request_sha256, result_ref)
            VALUES
              (@project, @principal, @operation, @key, @digest, @result::jsonb)
            ON CONFLICT (project_id, principal_id, operation_id, idempotency_key) DO NOTHING
            """);
        AddIdentityParameters(command);
        AddParameter(command, "digest", requestSha256);
        AddParameter(command, "result", JsonSerializer.Serialize(new StoredResultDto(result.StatusCode, result.Data)), NpgsqlDbType.Jsonb);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            return;
        }

        var existing = await FindIdempotencyAsync(cancellationToken)
            ?? throw new InvalidOperationException("The conflicting idempotency record could not be read.");
        throw new IdempotencyRaceException(existing);
    }

    public async Task AppendAuditAsync(AuditEntryInput entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var command = CreateCommand(
            """
            INSERT INTO ops.audit_event
              (project_id, actor_id, action, resource_ref, result_code, request_id, trace_id, safe_metadata)
            VALUES
              (@project, @principal, @action, @resource::jsonb, @result, @request, @trace, @metadata::jsonb)
            """);
        AddIdentityParameters(command);
        AddParameter(command, "action", entry.Action);
        AddParameter(command, "resource", entry.ResourceRef.GetRawText(), NpgsqlDbType.Jsonb);
        AddParameter(command, "result", entry.ResultCode);
        AddParameter(command, "request", CommandContext.RequestId);
        AddParameter(command, "trace", CommandContext.TraceId);
        AddParameter(command, "metadata", entry.SafeMetadata.GetRawText(), NpgsqlDbType.Jsonb);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendOutboxAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ProjectId != CommandContext.ProjectId || message.EventId == Guid.Empty)
        {
            throw new ArgumentException("The outbox event must belong to the command project and have an event ID.", nameof(message));
        }

        if (string.IsNullOrWhiteSpace(message.EventType) || string.IsNullOrWhiteSpace(message.SchemaVersion))
        {
            throw new ArgumentException("An outbox event type and schema version are required.", nameof(message));
        }

        await using var command = CreateCommand(
            """
            INSERT INTO ops.outbox
              (project_id, event_id, event_type, aggregate_id, aggregate_revision,
               schema_version, payload, occurred_at)
            VALUES
              (@project, @event, @type, @aggregate, @revision, @schema, @payload::jsonb, @occurred)
            """);
        AddParameter(command, "project", message.ProjectId);
        AddParameter(command, "event", message.EventId);
        AddParameter(command, "type", message.EventType);
        AddParameter(command, "aggregate", message.AggregateId);
        AddParameter(command, "revision", message.AggregateRevision);
        AddParameter(command, "schema", message.SchemaVersion);
        AddParameter(command, "payload", message.Payload.GetRawText(), NpgsqlDbType.Jsonb);
        AddParameter(command, "occurred", message.OccurredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryRecordInboxAsync(
        string consumerKey,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(consumerKey) || eventId == Guid.Empty)
        {
            throw new ArgumentException("A consumer key and event ID are required.", nameof(consumerKey));
        }

        await using var command = CreateCommand(
            """
            INSERT INTO ops.inbox (project_id, consumer_key, event_id)
            VALUES (@project, @consumer, @event)
            ON CONFLICT (project_id, consumer_key, event_id) DO NOTHING
            """);
        AddParameter(command, "project", CommandContext.ProjectId);
        AddParameter(command, "consumer", consumerKey);
        AddParameter(command, "event", eventId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<long> CompareAndSwapAsync(CasUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.ExpectedVersion <= 0 || update.Values.Count == 0 ||
            !CasTargets.Contains((update.Schema, update.Table, update.KeyColumn)))
        {
            throw new ArgumentException("The CAS target or expected version is invalid.", nameof(update));
        }

        var assignments = new List<string>();
        await using var command = CreateCommand(string.Empty);
        var index = 0;
        foreach (var (column, value) in update.Values.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!Identifier.IsMatch(column) || column is "project_id" or "version" || column == update.KeyColumn)
            {
                throw new ArgumentException($"Column '{column}' cannot be changed by this CAS operation.", nameof(update));
            }

            var parameter = $"value{index++}";
            assignments.Add($"\"{column}\" = @{parameter}");
            AddParameter(command, parameter, value);
        }

        command.CommandText =
            $"""
             UPDATE "{update.Schema}"."{update.Table}"
             SET {string.Join(", ", assignments)}, version = version + 1
             WHERE project_id = @project AND "{update.KeyColumn}" = @key AND version = @expected
             """;
        AddParameter(command, "project", CommandContext.ProjectId);
        AddParameter(command, "key", update.KeyValue);
        AddParameter(command, "expected", update.ExpectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw AtlasCommandException.CasMismatch();
        }

        return checked(update.ExpectedVersion + 1);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken);
        completed = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (!completed)
        {
            await transaction.RollbackAsync(cancellationToken);
            completed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await transaction.DisposeAsync();
        completed = true;
    }

    internal DbCommand CreateCommand(string sql)
    {
        var dbTransaction = transaction.ConnectionTransaction;
        var command = dbTransaction.Connection!.CreateCommand();
        command.Transaction = dbTransaction;
        command.CommandText = sql;
        return command;
    }

    private void AddIdentityParameters(DbCommand command)
    {
        AddParameter(command, "project", CommandContext.ProjectId);
        AddParameter(command, "principal", CommandContext.PrincipalId);
        AddParameter(command, "operation", CommandContext.OperationId);
        AddParameter(command, "key", CommandContext.IdempotencyKey);
    }

    private static IdempotencyMatch ReadIdempotency(DbDataReader reader)
    {
        var result = JsonSerializer.Deserialize<StoredResultDto>(reader.GetString(1))
            ?? throw new JsonException("The stored idempotency result is empty.");
        return new IdempotencyMatch(
            reader.GetString(0),
            new StoredCommandResult(result.Data.Clone(), result.StatusCode));
    }

    internal static void AddParameter(
        DbCommand command,
        string name,
        object? value,
        NpgsqlDbType? type = null)
    {
        if (value is JsonElement json)
        {
            value = json.GetRawText();
            type = NpgsqlDbType.Jsonb;
        }

        var parameter = new NpgsqlParameter
        {
            ParameterName = name,
            Value = value ?? DBNull.Value,
        };
        if (type.HasValue)
        {
            parameter.NpgsqlDbType = type.Value;
        }

        command.Parameters.Add(parameter);
    }

    private sealed record StoredResultDto(int StatusCode, JsonElement Data);
}
