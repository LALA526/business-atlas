using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PlatformOps.Jobs;

/// <summary>Runs claimed work outside database locks and leaves interrupted leases recoverable after process termination.</summary>
public sealed class PostgresJobWorker(
    IServiceScopeFactory scopes,
    IEnumerable<IJobHandler> handlers,
    IOptions<JobWorkerOptions> options,
    ILogger<PostgresJobWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, IJobHandler> handlers = handlers.ToDictionary(h => h.Kind, StringComparer.Ordinal);
    private readonly JobWorkerOptions options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<PostgresJobStore>();
                var projects = await store.ListWorkerProjectsAsync(stoppingToken);
                foreach (var project in projects)
                {
                    var claimed = await store.ClaimAsync(project, Guid.NewGuid(), options.WorkerId, options.LeaseDuration, stoppingToken);
                    if (claimed is null) continue;
                    worked = true;
                    await ExecuteClaim(claimed, stoppingToken);
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "Persistent job polling failed; existing leases remain recoverable");
            }
            if (!worked) await Task.Delay(options.PollInterval, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private async Task ExecuteClaim(ClaimedJob claimed, CancellationToken stoppingToken)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PostgresJobStore>();
        if (!handlers.TryGetValue(claimed.Job.Kind, out var handler))
        {
            await store.FailAsync(claimed, "JOB_HANDLER_NOT_REGISTERED", false, TimeSpan.Zero, CancellationToken.None);
            logger.LogError("No handler is registered for persistent job kind {Kind}", claimed.Job.Kind);
            return;
        }

        var budget = ExecutionBudget(claimed.Job);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        execution.CancelAfter(budget);
        using var monitor = CancellationTokenSource.CreateLinkedTokenSource(execution.Token);
        var heartbeat = MonitorLease(store, claimed, monitor, monitor.Token);
        try
        {
            var completion = await handler.ExecuteAsync(new JobExecutionContext(claimed, store, budget), monitor.Token);
            if (execution.IsCancellationRequested)
            {
                if (stoppingToken.IsCancellationRequested) return;
                if (await store.IsCancelRequestedAsync(claimed, CancellationToken.None))
                    await store.AcknowledgeCancelAsync(claimed, CancellationToken.None);
                else
                    await store.FailAsync(claimed, "BUDGET_EXHAUSTED", false, TimeSpan.Zero, CancellationToken.None);
                return;
            }
            if (await store.IsCancelRequestedAsync(claimed, execution.Token))
            {
                await store.AcknowledgeCancelAsync(claimed, CancellationToken.None);
                logger.LogInformation("Canceled job {JobId} did not commit its result", claimed.Job.JobId);
                return;
            }
            var committed = await store.CompleteAsync(claimed, completion, execution.Token);
            if (!committed) logger.LogWarning("Stale or canceled worker fence {Fence} could not complete job {JobId}", claimed.Fence, claimed.Job.JobId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Worker stopped; job {JobId} will resume from its durable checkpoint after lease expiry", claimed.Job.JobId);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested || monitor.IsCancellationRequested)
        {
            // Cancellation and fence loss both surface through the linked execution token.
            // Inspect durable state so operator cancellation is never mislabeled as budget exhaustion.
            if (await store.IsCancelRequestedAsync(claimed, CancellationToken.None))
            {
                await store.AcknowledgeCancelAsync(claimed, CancellationToken.None);
                logger.LogInformation("Execution stopped without committing canceled job {JobId}", claimed.Job.JobId);
            }
            else
            {
                await store.FailAsync(claimed, "BUDGET_EXHAUSTED", false, TimeSpan.Zero, CancellationToken.None);
            }
        }
        catch (JobExecutionException error)
        {
            await store.FailAsync(claimed, error.Code, error.Transient, RetryDelay(claimed.Attempt), CancellationToken.None);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Transient failure executing job {JobId}", claimed.Job.JobId);
            await store.FailAsync(claimed, "TRANSIENT_EXECUTION_FAILURE", true, RetryDelay(claimed.Attempt), CancellationToken.None);
        }
        finally
        {
            await monitor.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }
    }

    private async Task MonitorLease(PostgresJobStore store, ClaimedJob claimed, CancellationTokenSource execution, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(options.HeartbeatInterval, cancellationToken);
            if (!await store.HeartbeatAsync(claimed, options.LeaseDuration, cancellationToken))
            {
                logger.LogWarning("Lost job lease or cancellation requested for {JobId}", claimed.Job.JobId);
                await execution.CancelAsync();
                return;
            }
            if (await store.IsCancelRequestedAsync(claimed, cancellationToken))
            {
                logger.LogInformation("Cancellation observed for job {JobId}", claimed.Job.JobId);
                await execution.CancelAsync();
                return;
            }
        }
    }

    private TimeSpan ExecutionBudget(JobInfo job)
    {
        if (job.Input.TryGetProperty("budget", out var value) && value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("maxDurationSeconds", out var seconds) && seconds.TryGetDouble(out var requested) && requested > 0)
            return TimeSpan.FromSeconds(Math.Min(requested, options.DefaultExecutionBudget.TotalSeconds));
        return options.DefaultExecutionBudget;
    }

    private static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Pow(2, Math.Clamp(attempt, 1, 6))) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
}
