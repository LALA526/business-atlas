using System.Text.Json;
using AccessApplicability.Application;
using AccessApplicability.Contracts;
using ProjectIdentity.Application;

namespace Atlas.Domain.Tests;

public sealed class ApplicabilityEvaluatorTests
{
    [Fact]
    public void ExplicitUnrestrictedIsApplicable()
    {
        var result = ApplicabilityEvaluator.Evaluate(Json("""{"unrestricted":true}"""), null);
        Assert.Equal(ApplicabilityStatus.Applicable, result.Status);
    }

    [Fact]
    public void MissingMetadataOrRequiredEnvironmentNeverGuessesTrue()
    {
        Assert.Equal(ApplicabilityStatus.Unknown, ApplicabilityEvaluator.Evaluate(Json("{}"), null).Status);
        var result = ApplicabilityEvaluator.Evaluate(Json("""{"unrestricted":false,"environments":["production"]}"""), null);
        Assert.Equal(ApplicabilityStatus.Unknown, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("environment", StringComparison.Ordinal));
    }

    [Fact]
    public void NonMatchingDimensionIsNotApplicable()
    {
        var context = new ApplicableContext(null, null, null, "test", null, null);
        var result = ApplicabilityEvaluator.Evaluate(Json("""{"unrestricted":false,"environments":["production"]}"""), context);
        Assert.Equal(ApplicabilityStatus.NotApplicable, result.Status);
    }

    [Fact]
    public void AllDimensionsMustMatchAndListsUseOr()
    {
        var context = new ApplicableContext("quote", "acme", "cpq", "production", null, null);
        var applicability = Json("""{"unrestricted":false,"contexts":["quote","order"],"organizations":["acme"],"products":["cpq"],"environments":["production"]}""");
        Assert.Equal(ApplicabilityStatus.Applicable, ApplicabilityEvaluator.Evaluate(applicability, context).Status);
    }

    [Fact]
    public void MissingExpressionFactIsUnknownNotFalse()
    {
        var id = Guid.NewGuid();
        var applicability = Json("""{"unrestricted":false,"expression":{"op":"eq","left":{"kind":"field","attributeId":"__ID__"},"right":{"kind":"literal","valueType":"string","value":"A"},"comparison":"ordinal"}}""".Replace("__ID__", id.ToString(), StringComparison.Ordinal));
        Assert.Equal(ApplicabilityStatus.Unknown, ApplicabilityEvaluator.Evaluate(applicability, new ApplicableContext(null,null,null,null,null,null)).Status);
    }

    [Fact]
    public void ExplicitNullIsDistinctFromMissingFact()
    {
        var id = Guid.NewGuid();
        var facts = new Dictionary<string, JsonElement> { [id.ToString()] = Json("null") };
        var applicability = Json("""{"unrestricted":false,"expression":{"op":"isNull","operand":{"kind":"field","attributeId":"__ID__"}}}""".Replace("__ID__", id.ToString(), StringComparison.Ordinal));
        Assert.Equal(ApplicabilityStatus.Applicable, ApplicabilityEvaluator.Evaluate(applicability, Context(facts)).Status);
    }

    [Fact]
    public void AnyRetainsUnknownWhenNoBranchIsTrue()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var facts = new Dictionary<string, JsonElement> { [first.ToString()] = Json("\"A\"") };
        var expression = """{"unrestricted":false,"expression":{"op":"any","args":[{"op":"eq","left":{"kind":"field","attributeId":"__FIRST__"},"right":{"kind":"literal","valueType":"string","value":"B"},"comparison":"ordinal"},{"op":"eq","left":{"kind":"field","attributeId":"__SECOND__"},"right":{"kind":"literal","valueType":"string","value":"C"},"comparison":"ordinal"}]}}"""
            .Replace("__FIRST__", first.ToString(), StringComparison.Ordinal).Replace("__SECOND__", second.ToString(), StringComparison.Ordinal);
        Assert.Equal(ApplicabilityStatus.Unknown, ApplicabilityEvaluator.Evaluate(Json(expression), Context(facts)).Status);
    }

    [Fact]
    public void ValidityIntervalUsesInclusiveFromExclusiveTo()
    {
        var applicability = Json("""{"unrestricted":false,"validFrom":"2026-01-01T00:00:00Z","validTo":"2026-02-01T00:00:00Z"}""");
        Assert.Equal(ApplicabilityStatus.Applicable, ApplicabilityEvaluator.Evaluate(applicability, At("2026-01-01T00:00:00Z")).Status);
        Assert.Equal(ApplicabilityStatus.NotApplicable, ApplicabilityEvaluator.Evaluate(applicability, At("2026-02-01T00:00:00Z")).Status);
    }

    [Fact]
    public void MultipleMatchingExclusiveSpecificationsConflict()
    {
        var yes = new ApplicabilityResult(ApplicabilityStatus.Applicable, []);
        Assert.Equal(ApplicabilityStatus.Conflict, ApplicabilityEvaluator.Combine([yes, yes]).Status);
    }

    [Fact]
    public void ReaderCannotWriteButContributorCanPropose()
    {
        var policy = new PolicyService(null!);
        var reader = AuthorizationContext("ProjectReader");
        Assert.Throws<ProjectIdentityException>(() => policy.AuthorizeCommand(reader, "create_task"));
        Assert.Throws<ProjectIdentityException>(() => policy.AuthorizeCommand(reader, "propose"));
        policy.AuthorizeCommand(AuthorizationContext("Contributor"), "create_task");
        policy.AuthorizeCommand(AuthorizationContext("AgentProposer"), "propose");
    }

    private static ResolvedReadContext AuthorizationContext(string role) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        ReadMode.Published, null, 1, [role], 0, new string('0', 64), DateTimeOffset.UtcNow, ReadPurpose.Understand,
        new ApplicableContext(null,null,null,null,null,null));
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static ApplicableContext Context(IReadOnlyDictionary<string, JsonElement> facts) => new(null,null,null,null,null,facts);
    private static ApplicableContext At(string value) => new(null,null,null,null,DateTimeOffset.Parse(value),null);
}
