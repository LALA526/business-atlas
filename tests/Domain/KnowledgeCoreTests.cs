using System.Text.Json;
using KnowledgeCore.Application;
using KnowledgeCore.Contracts;
using KnowledgeCore.Relations;
using KnowledgeCore.Vocabulary;

namespace Atlas.Domain.Tests;

public sealed class KnowledgeCoreTests
{
    private static readonly Guid Amount = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Region = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void AT_T08_Canonical_hash_ignores_object_order_but_preserves_array_order()
    {
        var first = Json("""{"body":{"b":2,"a":1},"rows":[1,2]}""");
        var reordered = Json("""{"rows":[1,2],"body":{"a":1,"b":2}}""");
        var changed = Json("""{"body":{"a":1,"b":2},"rows":[2,1]}""");
        Assert.Equal(CanonicalJson.Sha256(first), CanonicalJson.Sha256(reordered));
        Assert.NotEqual(CanonicalJson.Sha256(first), CanonicalJson.Sha256(changed));
    }

    [Fact]
    public void AT_T08_Structured_rule_matches_without_executing_an_outcome()
    {
        var result = RuleEvaluator.Evaluate(Rule("unique"), Facts("1", "EU"));
        Assert.Equal("matched", result.Status);
        Assert.Equal("deny", result.Outcomes.Single().GetProperty("kind").GetString());
        Assert.Contains(result.Conditions, condition => condition.Value == "true");
    }

    [Fact]
    public void AT_T08_Missing_fact_never_becomes_false_or_default()
    {
        var result = RuleEvaluator.Evaluate(Rule("unique"), Json($"{{\"{Amount}\":10}}"));
        Assert.Equal("unknown", result.Status);
        Assert.Empty(result.Outcomes);
        Assert.Contains(result.Conditions, condition => condition.Value == "unknown");
    }

    [Fact]
    public void AT_T08_Unique_multiple_matches_conflict_and_human_rules_do_not_execute()
    {
        var rule = Rule("unique");
        Assert.Equal("matched", RuleEvaluator.Evaluate(rule, Facts("1", "EU")).Status);
        var body = JsonNode(rule);
        body["rows"]!.AsArray().Add(JsonNode(AdditionalRow()));
        Assert.Equal("conflict", RuleEvaluator.Evaluate(Json(body), Facts("10", "EU")).Status);
        Assert.Equal("not_executable", RuleEvaluator.Evaluate(Json("""{"evaluationMode":"human_only","rows":[]}"""), Json("{}")).Status);
    }

    [Fact]
    public void AT_T08_Ordered_unknown_higher_priority_blocks_lower_match()
    {
        var result = RuleEvaluator.Evaluate(Rule("ordered"), Json($"{{\"{Region}\":\"EU\"}}"));
        Assert.Equal("unknown", result.Status);
    }

    [Fact]
    public void AT_T09_Renaming_vocabulary_retains_identity_semantics_and_former_alias()
    {
        var unitId = Guid.NewGuid();
        var previous = new RevisionView(unitId, Guid.NewGuid(), 1, "旧字段名", "BusinessAttribute", "1.0", "normative", "unverified",
            Json("{}"), Json("""{"definition":"Stable business meaning","contextKey":"sales","aliases":["Amount"]}"""), Json("[]"), Json("[]"), Json("[]"), "hash", DateTimeOffset.UnixEpoch, Guid.Empty, ["business"]);
        var input = new RevisionInput(unitId, "新字段名", "BusinessAttribute", "1.0", null, "normative", "unverified", Json("{}"),
            Json("""{"definition":"Stable business meaning","contextKey":"sales","aliases":["Amount"]}"""), Json("[]"), Json("[]"), Json("[]"), Json("[\"business\"]"));

        var revised = VocabularySemantics.RetainFormerNameAsAlias(input, previous);

        Assert.Equal(unitId, revised.UnitId);
        Assert.Equal("Stable business meaning", revised.Body.GetProperty("definition").GetString());
        Assert.Contains("旧字段名", revised.Body.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString()));
        Assert.Equal(VocabularySemantics.NormalizeForLookup(" \u00c9tat "), VocabularySemantics.NormalizeForLookup("e\u0301tat"));
    }

    [Fact]
    public void AT_T09_Typed_predicates_do_not_turn_dependencies_into_execution()
    {
        RelationSemantics.ValidateEndpoints("mapsTo", "BusinessAttribute", "DataField");
        RelationSemantics.ValidateEndpoints("usesFact", "BusinessRule", "BusinessAttribute");
        Assert.Equal("业务到实现语义映射", RelationSemantics.Meaning("mapsTo"));
        Assert.NotEqual(RelationSemantics.Meaning("implements"), RelationSemantics.Meaning("triggers"));
        Assert.Throws<ProjectIdentity.Application.ProjectIdentityException>(() => RelationSemantics.ValidateEndpoints("usesFact", "DataField", "BusinessRule"));
    }

    private static JsonElement Facts(string amount, string region) => Json(JsonSerializer.Serialize(new Dictionary<string, string> { [Amount.ToString()] = amount, [Region.ToString()] = region }));
    private static JsonElement Rule(string policy) => Json("""
        {"purpose":"test","evaluationMode":"structured","priorityPolicy":"POLICY","inputs":[],"caseRefs":[],"exceptionExplanation":"",
         "rows":[
           {"rowId":"numeric","priority":0,"when":{"op":"gte","comparison":"typed","left":{"kind":"field","attributeId":"AMOUNT"},"right":{"kind":"literal","valueType":"decimal","value":"10"}},"outcome":{"kind":"deny","description":"matched"},"evidenceRefs":[]},
           {"rowId":"region","priority":1,"when":{"op":"eq","comparison":"ordinal","left":{"kind":"field","attributeId":"REGION"},"right":{"kind":"literal","valueType":"string","value":"EU"}},"outcome":{"kind":"deny","description":"matched"},"evidenceRefs":[]}
         ]}
        """.Replace("POLICY", policy).Replace("AMOUNT", Amount.ToString()).Replace("REGION", Region.ToString()));
    private static JsonElement AdditionalRow() => Json("""{"rowId":"numeric-low","priority":0,"when":{"op":"gte","comparison":"typed","left":{"kind":"field","attributeId":"AMOUNT"},"right":{"kind":"literal","valueType":"decimal","value":0}},"outcome":{"kind":"deny","description":"also matched"},"evidenceRefs":[]}""".Replace("AMOUNT", Amount.ToString()));
    private static System.Text.Json.Nodes.JsonNode JsonNode(JsonElement value) => System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText())!;
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static JsonElement Json(System.Text.Json.Nodes.JsonNode value) => JsonDocument.Parse(value.ToJsonString()).RootElement.Clone();
}
