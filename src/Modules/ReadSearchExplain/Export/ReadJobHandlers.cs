using System.Text.Json;
using AccessApplicability.Infrastructure;
using KnowledgeCore.Contracts;
using PlatformOps.Jobs;
using ReadSearchExplain.Search;

namespace ReadSearchExplain.Export;

public sealed class ExportJobHandler(PostgresExportService exports) : IJobHandler
{
    public string Kind => "knowledge.export";
    public async Task<JobCompletion> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var artifact = await exports.GenerateAsync(context.Job.Job.ProjectId, context.Job.Job.RequesterId, context.Job.Job.Input, cancellationToken);
        return new(JsonSerializer.SerializeToElement(new { uploadId = artifact.UploadId, fileName = artifact.FileName, contentType = artifact.ContentType, byteSize = artifact.Content.LongLength, sha256 = artifact.Sha256 }));
    }
}

public sealed class ContentReviewJobHandler(PostgresAccessApplicabilityStore access) : IJobHandler
{
    public string Kind => "knowledge.content_review";
    public async Task<JobCompletion> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var revision = context.Job.Job.Input.GetProperty("revision").Deserialize<RevisionInput>() ?? throw new JobExecutionException("INVALID_REVIEW", "A revision is required.");
        var read = context.Job.Job.Input.GetProperty("readContext").Deserialize<AccessApplicability.Contracts.ReadContextRequest>()!;
        var resolved = await access.CreateReadContextAsync(context.Job.Job.RequesterId, context.Job.Job.ProjectId, read, cancellationToken);
        if (revision.UnitId.HasValue) await access.AuthorizeReadAsync(resolved, revision.UnitId.Value, "read", null, cancellationToken);
        try
        {
            var result = ContentReviewService.Review(revision, context.Job.Job.Input.GetProperty("useModel").GetBoolean());
            return new(JsonSerializer.SerializeToElement(result));
        }
        catch (JobExecutionUnavailableException error) { throw new JobExecutionException(error.Code, error.Message); }
    }
}
