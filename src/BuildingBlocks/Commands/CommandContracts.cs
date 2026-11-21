using System.Text.Json;

namespace BuildingBlocks.Commands;

/// <summary>Server-verified values for one command. Never populate these from client role headers.</summary>
public sealed record CommandContext(
    Guid ProjectId,
    Guid PrincipalId,
    string OperationId,
    string IdempotencyKey,
    string RequestId,
    string? TraceId = null);

public sealed record CommandResult(object Data, int StatusCode = 200)
{
    public JsonElement ResourceRef { get; init; } = JsonSerializer.SerializeToElement(new { });
    public JsonElement SafeMetadata { get; init; } = JsonSerializer.SerializeToElement(new { });
}

public sealed record StoredCommandResult(JsonElement Data, int StatusCode);
public sealed record IdempotencyMatch(string RequestSha256, StoredCommandResult Result);

public sealed record AuditEntryInput(
    string Action,
    JsonElement ResourceRef,
    string ResultCode,
    JsonElement SafeMetadata);

public sealed record OutboxMessage(
    Guid ProjectId,
    Guid EventId,
    string EventType,
    Guid? AggregateId,
    long AggregateRevision,
    string SchemaVersion,
    JsonElement Payload,
    DateTimeOffset OccurredAt);

public sealed record CasUpdate(
    string Schema,
    string Table,
    string KeyColumn,
    Guid KeyValue,
    long ExpectedVersion,
    IReadOnlyDictionary<string, object?> Values);

public interface ICommandTransaction : IAsyncDisposable
{
    CommandContext CommandContext { get; }
    Task<IdempotencyMatch?> FindIdempotencyAsync(CancellationToken cancellationToken);
    Task SaveIdempotencyAsync(string requestSha256, StoredCommandResult result, CancellationToken cancellationToken);
    Task AppendAuditAsync(AuditEntryInput entry, CancellationToken cancellationToken);
    Task AppendOutboxAsync(OutboxMessage message, CancellationToken cancellationToken);
    Task<bool> TryRecordInboxAsync(string consumerKey, Guid eventId, CancellationToken cancellationToken);
    Task<long> CompareAndSwapAsync(CasUpdate update, CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

public interface ICommandTransactionFactory
{
    Task<ICommandTransaction> BeginAsync(CommandContext context, CancellationToken cancellationToken);
}

public interface ICommandAuthorizer
{
    Task AuthorizeAsync(CommandContext context, object command, CancellationToken cancellationToken);
}

/// <summary>Safe composition default until the access module installs its policy adapter.</summary>
public sealed class DenyAllCommandAuthorizer : ICommandAuthorizer
{
    public Task AuthorizeAsync(CommandContext context, object command, CancellationToken cancellationToken) =>
        throw new AtlasCommandException("FORBIDDEN", "The current principal cannot execute this command.", 403);
}

public interface ICommandHandler<in TCommand>
{
    Task<CommandResult> HandleAsync(
        TCommand command,
        ICommandTransaction transaction,
        CancellationToken cancellationToken);
}

public sealed record CommandExecutionResult(JsonElement Data, int StatusCode, bool Replayed);

public sealed class AtlasCommandException(string code, string message, int statusCode)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;

    public static AtlasCommandException Conflict(string message = "The idempotency key was already used for a different request.") =>
        new("IDEMPOTENCY_CONFLICT", message, StatusCodes.Conflict);

    public static AtlasCommandException CasMismatch() =>
        new("CAS_MISMATCH", "The resource changed; reload it and retry with its current version.", StatusCodes.PreconditionFailed);

    private static class StatusCodes
    {
        internal const int Conflict = 409;
        internal const int PreconditionFailed = 412;
    }
}

/// <summary>Internal control flow used when a racing transaction wins the idempotency insert.</summary>
public sealed class IdempotencyRaceException(IdempotencyMatch match) : Exception
{
    public IdempotencyMatch Match { get; } = match;
}
