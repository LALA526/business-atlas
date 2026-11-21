using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeCore.Application;

namespace Atlas.GoldenSemantic.Tests;

public sealed class RuleSemanticsTests
{
    private static readonly Guid Config = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid Path = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private static readonly Guid RequestName = Guid.Parse("10000000-0000-4000-8000-000000000003");
    private static readonly Guid RequestDescription = Guid.Parse("10000000-0000-4000-8000-000000000004");
    private static readonly Guid CandidateConfig = Guid.Parse("10000000-0000-4000-8000-000000000005");
    private static readonly Guid CandidatePath = Guid.Parse("10000000-0000-4000-8000-000000000006");
    private static readonly Guid CandidateName = Guid.Parse("10000000-0000-4000-8000-000000000007");
    private static readonly Guid CandidateDescription = Guid.Parse("10000000-0000-4000-8000-000000000008");
    private static readonly Guid CandidateCount = Guid.Parse("10000000-0000-4000-8000-000000000009");

    [Theory]
    [InlineData("EX-A01", "C1", "P1", "N1", "D1", "C1", "P1", "N1", "D2", 1, "matched", "perform")]
    [InlineData("EX-A02", "C1", "P1", "N1", "D1", "C2", "P1", "N1", "D1", 0, "defaulted", "skip")]
    [InlineData("EX-A03", "C1", "P1", null, "D1", "C1", "P1", "", "D1", 1, "matched", "perform")]
    [InlineData("EX-A04", "C1", "P1", null, "D1", "C1", "P1", "N1", "D1", 0, "defaulted", "skip")]
    [InlineData("EX-A05", "C1", "P1", "N1", "D1", "C1", "P1", "N1", "D1", 2, "matched", "deny")]
    [InlineData("EX-A06", "C1", "P1", null, "D1", "C1", "P1", null, "D1", 2, "matched", "deny")]
    [InlineData("EX-A07", "C1", "P1", "<missing>", "D1", "C1", "P1", null, "D1", 1, "unknown", null)]
    [InlineData("EX-A08", "C1", "P1", null, null, "C1", "P1", null, "D1", 0, "matched", "deny")]
    [InlineData("EX-A09", "C1", "P1", "n1", "D1", "C1", "P1", "N1", "D1", 0, "defaulted", "skip")]
    [InlineData("EX-A10", "C1", "P1", "N1", "D1", "C1", "P1", "<missing>", "D1", 1, "unknown", null)]
    public void AT_T10_Golden_A_preserves_branch_unknown_duplicate_and_scope_semantics(
        string caseId, string config, string path, string? requestName, string? requestDescription,
        string candidateConfig, string candidatePath, string? candidateName, string? candidateDescription,
        int candidateCount, string expectedStatus, string? expectedKind)
    {
        var facts = new JsonObject
        {
            [Config.ToString()] = config,
            [Path.ToString()] = path,
            [RequestDescription.ToString()] = requestDescription,
            [CandidateConfig.ToString()] = candidateConfig,
            [CandidatePath.ToString()] = candidatePath,
            [CandidateDescription.ToString()] = candidateDescription,
            [CandidateCount.ToString()] = candidateCount
        };
        if (requestName != "<missing>") facts[RequestName.ToString()] = requestName;
        if (candidateName != "<missing>") facts[CandidateName.ToString()] = candidateName;

        var result = RuleEvaluator.Evaluate(Json(GoldenRule()), Json(facts));

        Assert.True(result.Status == expectedStatus, $"{caseId}: expected {expectedStatus}, got {result.Status}: {string.Join("; ", result.Conditions.Select(c => $"{c.RowId}={c.Value} ({c.Reason})"))}");
        if (expectedKind is null) Assert.Empty(result.Outcomes);
        else Assert.Equal(expectedKind, result.Outcomes.Single().GetProperty("kind").GetString());
    }

    [Fact]
    public void AT_T10_Invalid_ast_and_ambiguous_unique_matches_never_default()
    {
        var malformed = Json("""{"evaluationMode":"structured","rows":[{"rowId":"bad","when":{"op":"script","arg":"1=1"},"outcome":{"kind":"perform","description":"not allowed"}}]}""");
        Assert.Equal("not_executable", RuleEvaluator.Evaluate(malformed, Json("{}")).Status);

        var unique = GoldenRule();
        unique["priorityPolicy"] = "unique";
        var conflictFacts = Json(new JsonObject
        {
            [Config.ToString()] = "C1", [Path.ToString()] = "P1", [RequestName.ToString()] = "N1",
            [RequestDescription.ToString()] = "D1", [CandidateConfig.ToString()] = "C1", [CandidatePath.ToString()] = "P1",
            [CandidateName.ToString()] = "N1", [CandidateDescription.ToString()] = "D1", [CandidateCount.ToString()] = 2
        });
        Assert.Equal("conflict", RuleEvaluator.Evaluate(Json(unique), conflictFacts).Status);
    }

    [Fact]
    public void AT_T10_Datetime_comparison_uses_instants_and_requires_offsets()
    {
        var first = Guid.Parse("20000000-0000-4000-8000-000000000001");
        var second = Guid.Parse("20000000-0000-4000-8000-000000000002");
        var body = Json($$$"""
            {"evaluationMode":"structured","inputs":[{"attribute":{"unitId":"{{{first}}}"},"valueType":"datetime"},{"attribute":{"unitId":"{{{second}}}"},"valueType":"datetime"}],
             "rows":[{"rowId":"same-instant","when":{"op":"eq","left":{"kind":"field","attributeId":"{{{first}}}"},"right":{"kind":"field","attributeId":"{{{second}}}"},"comparison":"typed"},"outcome":{"kind":"skip","description":"same"}}]}
            """);
        var equal = Json($$$"""{"{{{first}}}":"2026-01-01T00:00:00Z","{{{second}}}":"2026-01-01T01:00:00+01:00"}""");
        var noOffset = Json($$$"""{"{{{first}}}":"2026-01-01T00:00:00","{{{second}}}":"2026-01-01T01:00:00+01:00"}""");
        Assert.Equal("matched", RuleEvaluator.Evaluate(body, equal).Status);
        Assert.Equal("unknown", RuleEvaluator.Evaluate(body, noOffset).Status);
    }

    private static JsonObject GoldenRule()
    {
        var noRequestName = Any(Unary("isNull", RequestName), Binary("eq", RequestName, ""));
        var noCandidateName = Any(Unary("isNull", CandidateName), Binary("eq", CandidateName, ""));
        var sameScope = new[] { Binary("eq", Config, Field(CandidateConfig)), Binary("eq", Path, Field(CandidatePath)) };
        return new JsonObject
        {
            ["evaluationMode"] = "structured",
            ["priorityPolicy"] = "ordered",
            ["inputs"] = new JsonArray(Input(Config, "string"), Input(Path, "string"), Input(RequestName, "string"), Input(RequestDescription, "string"),
                Input(CandidateConfig, "string"), Input(CandidatePath, "string"), Input(CandidateName, "string"), Input(CandidateDescription, "string"), Input(CandidateCount, "integer")),
            ["rows"] = new JsonArray(
                Row("duplicate", 0, Binary("gt", CandidateCount, 1), "deny", "Duplicate candidates block application."),
                Row("invalid-description", 1, All(noRequestName.DeepClone(), Unary("isNull", RequestDescription)), "deny", "An unnamed request requires a valid description."),
                Row("named-match", 2, All(sameScope.Concat(new[] { Unary("exists", RequestName), Not(noRequestName.DeepClone()), Not(noCandidateName.DeepClone()), Binary("eq", RequestName, Field(CandidateName)) }).ToArray()), "perform", "Apply the uniquely named match."),
                Row("unnamed-match", 3, All(sameScope.Select(node => node.DeepClone()).Concat(new[] { noRequestName, noCandidateName, Binary("eq", RequestDescription, Field(CandidateDescription)) }).ToArray()), "perform", "Apply the uniquely unnamed match.")),
            ["defaultOutcome"] = new JsonObject { ["kind"] = "skip", ["description"] = "No matching candidate; do not apply." }
        };
    }

    private static JsonObject Input(Guid id, string type) => new() { ["attribute"] = new JsonObject { ["unitId"] = id.ToString() }, ["valueType"] = type };
    private static JsonObject Field(Guid id) => new() { ["kind"] = "field", ["attributeId"] = id.ToString() };
    private static JsonObject Literal(object? value) => new() { ["kind"] = "literal", ["valueType"] = value switch { int => "integer", null => "null", _ => "string" }, ["value"] = value switch { int number => number, null => null, _ => (string)value } };
    private static JsonObject Binary(string op, Guid left, object right) => new() { ["op"] = op, ["left"] = Field(left), ["right"] = right as JsonObject ?? Literal(right), ["comparison"] = "typed" };
    private static JsonObject Unary(string op, Guid operand) => new() { ["op"] = op, ["operand"] = Field(operand) };
    private static JsonObject Not(JsonNode arg) => new() { ["op"] = "not", ["arg"] = arg };
    private static JsonObject Any(params JsonNode[] args) => new() { ["op"] = "any", ["args"] = new JsonArray(args) };
    private static JsonObject All(params JsonNode[] args) => new() { ["op"] = "all", ["args"] = new JsonArray(args) };
    private static JsonObject Row(string id, int priority, JsonNode when, string kind, string description) => new()
    {
        ["rowId"] = id, ["priority"] = priority, ["when"] = when,
        ["outcome"] = new JsonObject { ["kind"] = kind, ["description"] = description }, ["evidenceRefs"] = new JsonArray()
    };
    private static JsonElement Json(JsonNode value) => JsonDocument.Parse(value.ToJsonString()).RootElement.Clone();
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}
