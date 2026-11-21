using System.Text.Json;
using KnowledgeCore.Contracts;
using ProjectIdentity.Application;
using ReadSearchExplain.Contracts;

namespace ReadSearchExplain.Search;

public static class ContentReviewService
{
    public static ContentReviewResult Review(RevisionInput revision, bool useModel)
    {
        if (useModel) throw new JobExecutionUnavailableException("MODEL_NOT_CONFIGURED", "No governed semantic-review model adapter is configured; structural checks remain available with useModel=false.");
        var checks = new List<Check>
        {
            new("schema.title", string.IsNullOrWhiteSpace(revision.Title) ? "fail" : "pass", true, "A bounded human-readable title is required.", []),
            new("schema.blocks", revision.Blocks.ValueKind == JsonValueKind.Array && ValidBlocks(revision.Blocks) ? "pass" : "fail", true, "Content blocks require stable IDs and registered block shapes.", []),
            new("schema.applicability", revision.Applicability.ValueKind == JsonValueKind.Object && revision.Applicability.EnumerateObject().Any() ? "pass" : "unknown", true, "Applicability must be explicit, including an explicit unrestricted declaration.", []),
            new("governance.humanApproval", "not_applicable", true, "Automated content checks never constitute human approval or verified business truth.", [])
        };
        var unknowns = checks.Where(check => check.Status is "fail" or "unknown").Select(check => new Unknown(check.Key, check.Reason, check.Status == "fail" ? "blocking" : "warning", check.Status == "fail" ? "missing" : "unverified", check.NextAction)).ToArray();
        return new(checks, unknowns, false);
    }
    private static bool ValidBlocks(JsonElement blocks) => blocks.EnumerateArray().All(block => block.ValueKind == JsonValueKind.Object && block.TryGetProperty("blockId", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()) && block.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() is "paragraph" or "table" or "list" or "notice");
}
public sealed class JobExecutionUnavailableException(string code, string message) : Exception(message) { public string Code { get; } = code; }
