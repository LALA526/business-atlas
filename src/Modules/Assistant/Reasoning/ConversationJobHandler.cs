using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AccessApplicability.Application;
using Assistant.Infrastructure;
using Assistant.Providers;
using PlatformOps.Jobs;

namespace Assistant.Reasoning;

public sealed class ConversationJobHandler(PostgresAssistantStore store, AssistantReasoningService reasoning, IProviderPort provider, PolicyService policies) : IJobHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Kind => "assistant.conversation";

    public async Task<JobCompletion> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var job = context.Job.Job;
        var input = job.Input;
        var runId = input.GetProperty("runId").GetGuid();
        var conversationId = input.GetProperty("conversationId").GetGuid();
        var completed = await store.GetCompletedResultAsync(job.ProjectId, job.RequesterId, runId, cancellationToken);
        if (completed is not null) return new(completed.Value, completed);
        if (await context.IsCancellationRequestedAsync(cancellationToken)) throw new OperationCanceledException(cancellationToken);
        var readRequest = input.GetProperty("readContext").Deserialize<AccessApplicability.Contracts.ReadContextRequest>(Json) ?? throw new JobExecutionException("VALIDATION_FAILED", "The conversation read context is invalid.");
        var read = await policies.CreateReadContextAsync(job.RequesterId, job.ProjectId, readRequest, cancellationToken);
        var binding = await store.GetRunBindingAsync(job.ProjectId, job.RequesterId, runId, cancellationToken);
        if (binding.ConversationId != conversationId || binding.BaselineId != read.BaselineId) throw new JobExecutionException("CONTEXT_CONFLICT", "The conversation and read context no longer match.");
        await policies.EnsureCurrentAsync(read, cancellationToken);
        var config = await store.GetActiveProviderAsync(job.ProjectId, job.RequesterId, cancellationToken);
        var maxOutput = input.GetProperty("budget").TryGetProperty("maxOutputTokens", out var output) ? Math.Clamp(output.GetInt32(), 1, 16384) : 4096;
        var question = input.GetProperty("text").GetString()!;
        var history = await store.ListMessagesAsync(job.RequesterId, job.ProjectId, conversationId, null, 100, cancellationToken);
        var bundle = await reasoning.RetrieveAsync(read, binding.RootUnitId, question, cancellationToken);
        await context.CheckpointAsync(JsonSerializer.SerializeToElement(new { phase = "retrieved", citationRevisionIds = bundle.Sources.Select(s => s.RevisionId) }, Json), 0.25, cancellationToken);
        if (!await store.StartRunAsync(job.ProjectId, job.RequesterId, runId, config, cancellationToken)) throw new OperationCanceledException(cancellationToken);
        var watch = Stopwatch.StartNew(); var text = new StringBuilder(); Assistant.Contracts.ProviderUsage? usage = null; string? requestId = null; var outputTruncated = false;
        try
        {
            await foreach (var delta in provider.StreamAsync(config, AssistantReasoningService.BuildRequest(bundle.Sources, question, maxOutput, history.Items), cancellationToken))
            {
                if (delta.TextDelta is not null) { text.Append(delta.TextDelta); await store.AppendDeltaAsync(job.ProjectId, job.RequesterId, runId, delta.TextDelta, cancellationToken); }
                usage = delta.Usage ?? usage; requestId = delta.RequestId ?? requestId; outputTruncated |= delta.OutputTruncated;
            }
            var validated = AssistantReasoningService.Validate(text.ToString(), bundle.Sources, bundle.Truncated, outputTruncated);
            await policies.EnsureCurrentAsync(read, cancellationToken);
            if (await context.IsCancellationRequestedAsync(cancellationToken)) throw new OperationCanceledException(cancellationToken);
            var message = await store.CompleteRunAsync(job.ProjectId, job.RequesterId, runId, validated.Answer, validated.Citations, usage, requestId, watch.ElapsedMilliseconds, cancellationToken);
            var result = JsonSerializer.SerializeToElement(new { resultRef = $"conversation:{conversationId}:message:{message.MessageId}", messageId = message.MessageId, status = validated.Answer.GetProperty("status").GetString(), providerRequestId = requestId }, Json);
            return new(result, result);
        }
        catch (ProviderException error)
        {
            await store.FailRunAsync(job.ProjectId, job.RequesterId, runId, error.Code, error.Message, CancellationToken.None);
            throw new JobExecutionException(error.Code, error.Message, error.Transient);
        }
        catch (JsonException)
        {
            await store.FailRunAsync(job.ProjectId, job.RequesterId, runId, "PROVIDER_RESPONSE_INVALID", "The model returned an invalid structured answer; no unsupported content was saved.", CancellationToken.None);
            throw new JobExecutionException("PROVIDER_RESPONSE_INVALID", "The model returned an invalid structured answer.");
        }
    }
}
