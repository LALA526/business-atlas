using System.Text.Json;
using AccessApplicability.Application;
using AccessApplicability.Contracts;
using KnowledgeCore.Application;
using KnowledgeCore.Relations;
using ProjectIdentity.Application;

namespace Atlas.Domain.Tests;

/// <summary>AT-T41 expectations derived from the baseline, independently of implementation test data.</summary>
public sealed class IndependentAcceptanceTests
{
    [Theory]
    [InlineData("production", ApplicabilityStatus.Applicable)]
    [InlineData("test", ApplicabilityStatus.NotApplicable)]
    [InlineData(null, ApplicabilityStatus.Unknown)]
    public void AT_T41_Production_qualifier_never_leaks_into_test_or_unknown_context(
        string? environment, ApplicabilityStatus expected)
    {
        var applicability = Json("""{"unrestricted":false,"environments":["production"]}""");
        var context = new ApplicableContext(null, null, null, environment, null, null);
        Assert.Equal(expected, ApplicabilityEvaluator.Evaluate(applicability, context).Status);
    }

    [Fact]
    public void AT_T41_Applicability_conflict_and_rule_unknown_are_not_silently_defaulted()
    {
        var applicable = new ApplicabilityResult(ApplicabilityStatus.Applicable, []);
        Assert.Equal(ApplicabilityStatus.Conflict, ApplicabilityEvaluator.Combine([applicable, applicable]).Status);

        var attribute = Guid.NewGuid();
        var rule = Json($$$"""
            {"evaluationMode":"structured","inputs":[{"attribute":{"unitId":"{{{attribute}}}"},"valueType":"string"}],
             "priorityPolicy":"ordered","rows":[{"rowId":"only","priority":0,
               "when":{"op":"eq","comparison":"ordinal","left":{"kind":"field","attributeId":"{{{attribute}}}"},"right":{"kind":"literal","valueType":"string","value":"yes"}},
               "outcome":{"kind":"perform","description":"Only after yes"},"evidenceRefs":[]}],
             "defaultOutcome":{"kind":"skip","description":"Known no only"}}
            """);
        var result = RuleEvaluator.Evaluate(rule, Json("{}"));
        Assert.Equal("unknown", result.Status);
        Assert.Empty(result.Outcomes);
    }

    [Fact]
    public void AT_T41_Structural_dependency_and_validation_never_become_execution_or_acceptance()
    {
        foreach (var predicate in new[] { "contains", "dependsOn", "constrains", "implements", "calls", "validates" })
            Assert.False(RelationSemantics.IsExecutionRelation(predicate), predicate);
        Assert.True(RelationSemantics.IsExecutionRelation("triggers"));
        Assert.True(RelationSemantics.IsExecutionRelation("followsWhen"));
        Assert.Equal("验证目标，不代表通过", RelationSemantics.Meaning("validates"));
        Assert.Throws<ProjectIdentityException>(() => RelationSemantics.ValidateEndpoints("mapsTo", "DataField", "BusinessAttribute"));
    }

    [Fact]
    public void AT_T41_Observed_or_inferred_input_cannot_be_used_as_an_approval_credential()
    {
        var policy = new PolicyService(null!);
        var reader = Context("ProjectReader");
        var proposer = Context("AgentProposer");
        Assert.Throws<ProjectIdentityException>(() => policy.AuthorizeCommand(reader, "approve"));
        Assert.Throws<ProjectIdentityException>(() => policy.AuthorizeCommand(proposer, "approve"));
        Assert.Throws<ProjectIdentityException>(() => policy.AuthorizeCommand(proposer, "publish"));
        policy.AuthorizeCommand(proposer, "propose");
    }

    private static ResolvedReadContext Context(string role) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        ReadMode.Published, null, 1, [role], 0, new string('0', 64), DateTimeOffset.UtcNow, ReadPurpose.Understand,
        new ApplicableContext(null, null, null, null, null, null));
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}
