using System.Text.Json;

namespace BuildingBlocks.Commands;

/// <summary>
/// Runs the business write, immutable idempotency result, audit and outbox appends
/// on one transaction. Any exception before commit rolls back every write.
/// </summary>
public sealed class CommandPipeline(ICommandTransactionFactory transactions, ICommandAuthorizer authorizer)
{
    public async Task<CommandExecutionResult> ExecuteAsync<TCommand>(
        CommandContext context,
        TCommand command,
        ICommandHandler<TCommand> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(handler);
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(command);
        await authorizer.AuthorizeAsync(context, command, cancellationToken);
        var requestSha256 = RequestDigest.Compute(command);

        await using var transaction = await transactions.BeginAsync(context, cancellationToken);
        try
        {
            var existing = await transaction.FindIdempotencyAsync(cancellationToken);
            if (existing is not null)
            {
                return Replay(existing, requestSha256);
            }

            var result = await handler.HandleAsync(command, transaction, cancellationToken);
            if (result.StatusCode is < 100 or > 599)
            {
                throw new ArgumentOutOfRangeException(nameof(result), "The command returned an invalid HTTP status code.");
            }

            var stored = new StoredCommandResult(JsonSerializer.SerializeToElement(result.Data), result.StatusCode);
            await transaction.SaveIdempotencyAsync(requestSha256, stored, cancellationToken);
            await transaction.AppendAuditAsync(new AuditEntryInput(
                context.OperationId,
                result.ResourceRef,
                "OK",
                result.SafeMetadata), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CommandExecutionResult(stored.Data, stored.StatusCode, false);
        }
        catch (IdempotencyRaceException race)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Replay(race.Match, requestSha256);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static CommandExecutionResult Replay(IdempotencyMatch match, string requestSha256)
    {
        if (!string.Equals(match.RequestSha256, requestSha256, StringComparison.Ordinal))
        {
            throw AtlasCommandException.Conflict();
        }

        return new CommandExecutionResult(match.Result.Data, match.Result.StatusCode, true);
    }

    private static void ValidateContext(CommandContext context)
    {
        if (context.ProjectId == Guid.Empty || context.PrincipalId == Guid.Empty)
        {
            throw new ArgumentException("A verified project and principal are required.", nameof(context));
        }

        if (string.IsNullOrWhiteSpace(context.OperationId) || string.IsNullOrWhiteSpace(context.RequestId))
        {
            throw new ArgumentException("An operation and request ID are required.", nameof(context));
        }

        if (context.IdempotencyKey.Length is < 8 or > 128 || string.IsNullOrWhiteSpace(context.IdempotencyKey))
        {
            throw new ArgumentException("The idempotency key must contain 8 to 128 characters.", nameof(context));
        }
    }
}
