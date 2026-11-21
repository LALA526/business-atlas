using BuildingBlocks.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlatformOps.Jobs;

namespace PlatformOps;

/// <summary>Consumers must commit their idempotent business effects through the supplied transaction.</summary>
public interface IEventConsumer
{
    string EventType { get; }
    string ConsumerKey { get; }
    Task ConsumeAsync(ClaimedOutboxMessage message, ICommandTransaction transaction, CancellationToken cancellationToken);
}

public sealed class PostgresEventDeliveryWorker(
    IServiceScopeFactory scopes,
    IEnumerable<IEventConsumer> consumers,
    IOptions<JobWorkerOptions> options,
    ILogger<PostgresEventDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var registry = consumers.GroupBy(c => c.EventType, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                using var scope = scopes.CreateScope();
                var jobs = scope.ServiceProvider.GetRequiredService<PostgresJobStore>();
                var store = scope.ServiceProvider.GetRequiredService<PostgresOutboxStore>();
                foreach (var project in await jobs.ListWorkerProjectsAsync(stoppingToken))
                {
                    var claimed = await store.ClaimAsync(project, Guid.NewGuid(), options.Value.LeaseDuration, stoppingToken);
                    if (claimed is null) continue;
                    worked = true;
                    if (!registry.TryGetValue(claimed.EventType, out var eventConsumers))
                    {
                        await store.FailAsync(project, claimed.EventId, claimed.Fence, stoppingToken);
                        logger.LogError("No consumer registered for outbox event {EventType}", claimed.EventType);
                        break;
                    }
                    try
                    {
                        foreach (var consumer in eventConsumers)
                        {
                            using var consumerScope = scopes.CreateScope();
                            var transactions = consumerScope.ServiceProvider.GetRequiredService<ICommandTransactionFactory>();
                            await using var transaction = await transactions.BeginAsync(new CommandContext(project, Guid.NewGuid(), "event.consume",
                                $"{claimed.EventId}:{consumer.ConsumerKey}", $"event-{claimed.EventId:N}"), stoppingToken);
                            if (await transaction.TryRecordInboxAsync(consumer.ConsumerKey, claimed.EventId, stoppingToken))
                            {
                                await consumer.ConsumeAsync(claimed, transaction, stoppingToken);
                            }
                            await transaction.CommitAsync(stoppingToken);
                        }
                        await store.CompleteAsync(project, Guid.NewGuid(), claimed.EventId, claimed.Fence, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        logger.LogError(error, "Outbox delivery failed for {EventId}; attempt {Attempt}", claimed.EventId, claimed.Attempt);
                        if (claimed.Attempt >= 5) await store.FailAsync(project, claimed.EventId, claimed.Fence, CancellationToken.None);
                        else await store.RetryAsync(project, Guid.NewGuid(), claimed.EventId, claimed.Fence,
                            TimeSpan.FromSeconds(Math.Pow(2, Math.Clamp(claimed.Attempt, 1, 6))), CancellationToken.None);
                    }
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Outbox polling failed"); }
            if (!worked) await Task.Delay(options.Value.PollInterval, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }
}
