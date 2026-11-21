using System.Text.Json;
using KnowledgeCore.Contracts;
using ReadSearchExplain.ContentCheck;

namespace Atlas.Domain.Tests;

public sealed class ContentReviewTests
{
    [Fact]
    public void AT_T20_Consistency_without_comparison_basis_is_blocking()
    {
        var result = new DeterministicContentChecker().Review(Revision("BusinessRule", "normative", "unverified",
            """
            {"purpose":"Apply matching parameter","evaluationMode":"human_only","inputs":[],"priorityPolicy":"unique",
             "rows":[{"rowId":"r1","situation":"一致则应用","comparisonExplanation":"","outcome":{"kind":"perform","description":"应用"},"evidenceRefs":[]}],
             "defaultOutcome":{"kind":"skip","description":"不应用"},"exceptionExplanation":"missing is unknown","caseRefs":[]}
            """));
        var check = result.Checks.Single(item => item.Key == "semantic.rule.rows[0].comparisonBasis");
        Assert.Equal("fail", check.Status);
        Assert.Contains(result.Unknowns, item => item.Key == check.Key && item.Reason == "missing");
    }

    [Fact]
    public void AT_T20_Missing_evidence_is_unknown_and_does_not_invent_a_negative()
    {
        var result = new DeterministicContentChecker().Review(Revision("Component", "observed", "unverified", "{}"));
        var evidence = result.Checks.Single(item => item.Key == "content.evidence");
        Assert.Equal("unknown", evidence.Status);
        Assert.False(evidence.Critical);
        Assert.DoesNotContain(result.Checks, item => item.Key.StartsWith("profile.rule."));
    }

    [Fact]
    public void AT_T20_Refuting_evidence_and_unsupported_verification_remain_explicit()
    {
        var revision = Revision("BusinessRule", "normative", "verified", "{}", evidence: "refutes");
        var result = new DeterministicContentChecker().Review(revision);
        Assert.Contains(result.Checks, item => item.Key == "semantic.evidence.refuting" && item.Status == "contradictory");
        Assert.DoesNotContain(result.Checks, item => item.Key == "semantic.verification.unsupported");
    }

    private static RevisionInput Revision(string kind, string factKind, string verification, string body, string? evidence = null) => new(
        Guid.NewGuid(), "Content under review", kind, "1.0", null, factKind, verification,
        Json("{\"unrestricted\":true}"), Json(body), Json("[{\"blockId\":\"summary\",\"kind\":\"paragraph\",\"text\":\"Readable summary\"}]"),
        Json(evidence is null ? "[]" : $"[{{\"support\":\"{evidence}\",\"source\":{{\"unitId\":\"{Guid.NewGuid()}\",\"revisionId\":\"{Guid.NewGuid()}\"}}}}]"),
        Json("[]"), Json("[]"));
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}
