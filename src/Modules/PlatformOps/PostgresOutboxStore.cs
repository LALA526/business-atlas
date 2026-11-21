using System.Data.Common;
using System.Text.Json;
using BuildingBlocks.Commands;
using BuildingBlocks.Persistence;

namespace PlatformOps;

public sealed record ClaimedOutboxMessage(
    Guid EventId,
    string EventType,
    Guid? AggregateId,
    long AggregateRevision,
    string SchemaVersion,
    JsonElement Payload,
    DateTimeOffset OccurredAt,
    long Fence,
    int Attempt);

/// <summary>Short-transaction outbox leasing. Dispatch occurs only after the claim commits.</summary>
public sealed class PostgresOutboxStore(AtlasDbContext database)
{
    public async Task<ClaimedOutboxMessage?> ClaimAsync(
        Guid projectId,
        Guid workerPrincipalId,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        if (lease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lease));
        }

        await using var transaction = await PostgresCommandTransaction.BeginAsync(
            database,
            WorkerContext(projectId, workerPrincipalId, "outbox.claim"),
            cancellationToken);
        ClaimedOutboxMessage? claimed = null;
        await using (var command = CreateCommand(transaction,
            """
            WITH candidate AS (
              SELECT project_id, event_id
              FROM ops.outbox
              WHERE project_id = @project AND available_at <= now()
                AND (state = 'queued' OR (state = 'running' AND lease_until < now()))
              ORDER BY available_at, event_id
              FOR UPDATE SKIP LOCKED
              LIMIT 1
            ), updated AS (
              UPDATE ops.outbox AS target
              SET state = 'running', lease_until = now() + @lease,
                  fence = target.fence + 1, attempt = target.attempt + 1
              FROM candidate
              WHERE target.project_id = candidate.project_id AND target.event_id = candidate.event_id
              RETURNING target.event_id, target.event_type, target.aggregate_id,
                        target.aggregate_revision, target.schema_version, target.payload,
                        target.occurred_at, target.fence, target.attempt
            )
            SELECT * FROM updated
            """))
        {
            AddParameter(command, "project", projectId);
            AddParameter(command, "lease", lease);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                claimed = new ClaimedOutboxMessage(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.GetInt64(3),
                    reader.GetString(4),
                    JsonDocument.Parse(reader.GetString(5)).RootElement.Clone(),
                    reader.GetFieldValue<DateTimeOffset>(6),
                    reader.GetInt64(7),
                    reader.GetInt32(8));
            }
        }

        if (claimed is not null)
        {
            await transaction.AppendAuditAsync(OutboxAudit("outbox.claim", claimed.EventId, claimed.Fence), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    public Task<bool> CompleteAsync(
        Guid projectId,
        Guid workerPrincipalId,
        Guid eventId,
        long fence,
        CancellationToken cancellationToken = default) =>
        FinishAsync(projectId, workerPrincipalId, eventId, fence, null, cancellationToken);

    public Task<bool> RetryAsync(
        Guid projectId,
        Guid workerPrincipalId,
        Guid eventId,
        long fence,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        return FinishAsync(projectId, workerPrincipalId, eventId, fence, delay, cancellationToken);
    }

    private async Task<bool> FinishAsync(
        Guid projectId,
        Guid workerPrincipalId,
        Guid eventId,
        long fence,
        TimeSpan? retryDelay,
        CancellationToken cancellationToken)
    {
        var action = retryDelay.HasValue ? "outbox.retry" : "outbox.complete";
        await using var transaction = await PostgresCommandTransaction.BeginAsync(
            database,
            WorkerContext(projectId, workerPrincipalId, action),
            cancellationToken);
        var state = retryDelay.HasValue ? "queued" : "succeeded";
        await using var command = CreateCommand(transaction,
            $"""
             UPDATE ops.outbox
             SET state = @state, lease_until = NULL,
                 available_at = CASE WHEN @delay::interval IS NULL THEN available_at ELSE now() + @delay::interval END
             WHERE project_id = @project AND event_id = @event AND fence = @fence AND state = 'running'
             """);
        AddParameter(command, "state", state);
        AddParameter(command, "delay", retryDelay);
        AddParameter(command, "project", projectId);
        AddParameter(command, "event", eventId);
        AddParameter(command, "fence", fence);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (updated)
        {
            await transaction.AppendAuditAsync(OutboxAudit(action, eventId, fence), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private static CommandContext WorkerContext(Guid projectId, Guid principalId, string operation) =>
        new(projectId, principalId, operation, $"worker-{Guid.NewGuid():N}", Guid.NewGuid().ToString("N"));

    private static AuditEntryInput OutboxAudit(string action, Guid eventId, long fence) =>
        new(
            action,
            JsonSerializer.SerializeToElement(new { eventId }),
            "OK",
            JsonSerializer.SerializeToElement(new { fence }));

    private static DbCommand CreateCommand(PostgresCommandTransaction transaction, string sql) =>
        transaction.CreateCommand(sql);

    private static void AddParameter(DbCommand command, string name, object? value) =>
        PostgresCommandTransaction.AddParameter(command, name, value);
}
