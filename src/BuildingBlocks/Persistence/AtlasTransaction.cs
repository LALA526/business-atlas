using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BuildingBlocks.Persistence;

/// <summary>
/// One connection and transaction per command. Project and subject values are set
/// only by the server after authentication/authorization and are transaction-local.
/// </summary>
public sealed class AtlasTransaction : IAsyncDisposable
{
    private readonly AtlasDbContext context;
    private readonly IDbContextTransaction transaction;
    private readonly List<Func<AtlasTransaction, CancellationToken, Task>> beforeCommitHooks = [];
    private bool completed;

    public AtlasDbContext Context => context;

    private AtlasTransaction(AtlasDbContext context, IDbContextTransaction transaction)
    {
        this.context = context;
        this.transaction = transaction;
    }

    public static async Task<AtlasTransaction> BeginAsync(
        AtlasDbContext context,
        Guid projectId,
        Guid subjectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (projectId == Guid.Empty || subjectId == Guid.Empty)
        {
            throw new ArgumentException("A verified project and subject are required.");
        }

        var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('atlas.project_id', {(string)projectId.ToString()}, true), set_config('atlas.subject_id', {(string)subjectId.ToString()}, true)",
                cancellationToken);
            return new AtlasTransaction(context, transaction);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    public DbTransaction ConnectionTransaction => transaction.GetDbTransaction();

    /// <summary>Enlists work that must complete inside this transaction before COMMIT.</summary>
    public void EnlistBeforeCommit(Func<AtlasTransaction, CancellationToken, Task> hook)
    {
        ObjectDisposedException.ThrowIf(completed, this);
        beforeCommitHooks.Add(hook ?? throw new ArgumentNullException(nameof(hook)));
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        foreach (var hook in beforeCommitHooks)
        {
            await hook(this, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        completed = true;
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        completed = true;
        return transaction.RollbackAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!completed)
        {
            await transaction.RollbackAsync();
        }

        await transaction.DisposeAsync();
        // SET LOCAL values are removed by COMMIT/ROLLBACK; no connection-global state is used.
    }
}
