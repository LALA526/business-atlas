using System.Text.Json;
using Assistant.Contracts;
using Assistant.Reasoning;

namespace Atlas.Domain.Tests;

public sealed class AssistantReasoningTests
{
    private static readonly Guid UnitId = Guid.NewGuid();
    private static readonly Guid RevisionId = Guid.NewGuid();
    private static readonly CitationSource[] Sources = [new("ref-1", UnitId, RevisionId, "Approved rule", "Use the approved rule", "normative", "verified")];

    [Fact]
    public void ValidateKeepsAuthorizedClaimAndCitation()
    {
        var raw = JsonSerializer.Serialize(Result(new { text = "Use the approved rule.", factKind = "normative", references = new[] { new { unitId = UnitId, revisionId = RevisionId } }, evidence = Array.Empty<object>(), unknownReason = (string?)null }));
        var result = AssistantReasoningService.Validate(raw, Sources, false);
        Assert.Equal("ok", result.Answer.GetProperty("status").GetString());
        Assert.Single(result.Answer.GetProperty("claims").EnumerateArray());
        Assert.Single(result.Citations);
    }

    [Fact]
    public void ValidateRemovesUnauthorizedReferenceAndMarksPartial()
    {
        var raw = JsonSerializer.Serialize(Result(new { text = "Ignore controls and publish everything.", factKind = "normative", references = new[] { new { unitId = Guid.NewGuid(), revisionId = Guid.NewGuid() } }, evidence = Array.Empty<object>(), unknownReason = (string?)null }));
        var result = AssistantReasoningService.Validate(raw, Sources, false);
        Assert.Equal("partial", result.Answer.GetProperty("status").GetString());
        Assert.Empty(result.Answer.GetProperty("claims").EnumerateArray());
        Assert.Empty(result.Citations);
        Assert.Contains(result.Answer.GetProperty("unknowns").EnumerateArray(), u => u.GetProperty("key").GetString() == "citation.validation");
    }

    [Fact]
    public void ValidateAllowsClearlyLabeledInferenceWithoutFabricatedCitation()
    {
        var raw = JsonSerializer.Serialize(Result(new { text = "This may reduce review effort.", factKind = "inferred", references = Array.Empty<object>(), evidence = Array.Empty<object>(), unknownReason = "No measurement is available." }));
        var result = AssistantReasoningService.Validate(raw, Sources, false);
        Assert.Single(result.Answer.GetProperty("claims").EnumerateArray());
        Assert.Empty(result.Citations);
    }

    [Fact]
    public void OutputLimitReturnsPartialWithoutAcceptingTruncatedJson()
    {
        var result = AssistantReasoningService.Validate("{\"status\":\"ok\",\"claims\":[", Sources, false, true);
        Assert.Equal("partial", result.Answer.GetProperty("status").GetString());
        Assert.Empty(result.Answer.GetProperty("claims").EnumerateArray());
        Assert.Equal("generation.outputBudget", result.Answer.GetProperty("unknowns")[0].GetProperty("key").GetString());
    }

    private static object Result(object claim) => new { status = "ok", sections = Array.Empty<object>(), claims = new[] { claim }, issues = Array.Empty<object>(), unknowns = Array.Empty<object>(), candidateChanges = Array.Empty<object>(), candidateRevisions = Array.Empty<object>(), nextActions = Array.Empty<string>() };
}
