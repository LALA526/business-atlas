using BuildingBlocks.Commands;
using BuildingBlocks.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace PlatformOps;

public sealed class PostgresCommandTransactionFactory(IServiceScopeFactory scopes) : ICommandTransactionFactory
{
    public async Task<ICommandTransaction> BeginAsync(
        CommandContext context,
        CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        try
        {
            var database = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
            var transaction = await PostgresCommandTransaction.BeginAsync(database, context, cancellationToken);
            return new ScopedCommandTransaction(scope, transaction);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    private sealed class ScopedCommandTransaction(AsyncServiceScope scope, ICommandTransaction inner) : ICommandTransaction
    {
        public CommandContext CommandContext => inner.CommandContext;
        public Task<IdempotencyMatch?> FindIdempotencyAsync(CancellationToken cancellationToken) => inner.FindIdempotencyAsync(cancellationToken);
        public Task SaveIdempotencyAsync(string requestSha256, StoredCommandResult result, CancellationToken cancellationToken) => inner.SaveIdempotencyAsync(requestSha256, result, cancellationToken);
        public Task AppendAuditAsync(AuditEntryInput entry, CancellationToken cancellationToken) => inner.AppendAuditAsync(entry, cancellationToken);
        public Task AppendOutboxAsync(OutboxMessage message, CancellationToken cancellationToken) => inner.AppendOutboxAsync(message, cancellationToken);
        public Task<bool> TryRecordInboxAsync(string consumerKey, Guid eventId, CancellationToken cancellationToken) => inner.TryRecordInboxAsync(consumerKey, eventId, cancellationToken);
        public Task<long> CompareAndSwapAsync(CasUpdate update, CancellationToken cancellationToken) => inner.CompareAndSwapAsync(update, cancellationToken);
        public Task CommitAsync(CancellationToken cancellationToken) => inner.CommitAsync(cancellationToken);
        public Task RollbackAsync(CancellationToken cancellationToken) => inner.RollbackAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await scope.DisposeAsync();
        }
    }
}
