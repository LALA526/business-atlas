using KnowledgeCore.Contracts;
using ProjectIdentity.Application;
using ReadSearchExplain.ContentCheck;
using ReadSearchExplain.Contracts;

namespace ReadSearchExplain.Search;

public static class ContentReviewService
{
    public static ContentReviewResult Review(RevisionInput revision, bool useModel)
    {
        if (useModel) throw new JobExecutionUnavailableException("MODEL_NOT_CONFIGURED", "No governed semantic-review model adapter is configured; structural checks remain available with useModel=false.");
        return new DeterministicContentChecker().Review(revision);
    }
}
public sealed class JobExecutionUnavailableException(string code, string message) : Exception(message) { public string Code { get; } = code; }
