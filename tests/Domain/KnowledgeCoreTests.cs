using System.Text.Json;
using KnowledgeCore.Application;
using KnowledgeCore.Contracts;
using KnowledgeCore.Flows;
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

    [Fact]
    public void AT_T11_Manufacturing_flow_projection_preserves_seven_stages_twelve_nodes_and_fourteen_edges()
    {
        var flow = FlowSemantics.Project(GoldenFlow());

        Assert.Equal(7, flow.Stages.Count);
        Assert.Equal(12, flow.Nodes.Count);
        Assert.Equal(14, flow.Edges.Count);
        Assert.All(flow.Stages, stage => Assert.NotEmpty(stage.NodeIds));
        Assert.Equal(2, flow.Edges.Count(edge => edge.Kind == "return"));
        Assert.Contains(flow.Edges, edge => edge.EdgeId == "e4" && edge.GuardRuleRefs.Single().GetProperty("unitId").GetGuid() == Region);
        Assert.Equal("s1", flow.Edges.Single(edge => edge.EdgeId == "e7").ToStageId);
    }

    [Fact]
    public void AT_T11_Flow_validation_rejects_dropped_stages_edges_and_guard_references()
    {
        var missingStage = JsonNode(GoldenFlow());
        missingStage["stageOrder"]!.AsArray().RemoveAt(6);
        Assert.Throws<ProjectIdentity.Application.ProjectIdentityException>(() => FlowSemantics.Validate(Json(missingStage)));

        var missingGuard = JsonNode(GoldenFlow());
        missingGuard["edges"]![3]!["guardRuleRefs"] = new System.Text.Json.Nodes.JsonArray();
        missingGuard["edges"]![3]!["conditionText"] = "";
        Assert.Throws<ProjectIdentity.Application.ProjectIdentityException>(() => FlowSemantics.Validate(Json(missingGuard)));

        var missingNode = JsonNode(GoldenFlow());
        missingNode["edges"]![0]!["toNodeId"] = "invented";
        Assert.Throws<ProjectIdentity.Application.ProjectIdentityException>(() => FlowSemantics.Validate(Json(missingNode)));
    }

    [Fact]
    public void AT_T11_Transition_guards_are_explicit_and_non_execution_relations_do_not_create_order()
    {
        var transition = Json($$"""
            {"object":{"unitId":"{{Amount}}"},"from":{"unitId":"{{Region}}"},"to":{"unitId":"{{Region}}"},
             "trigger":{"unitId":"{{Amount}}"},"guardRuleRefs":[{"unitId":"{{Region}}"}]}
            """);
        FlowSemantics.ValidateTransition(transition);
        Assert.Equal(Region, FlowSemantics.TransitionRuleReferences(transition).Single().GetProperty("unitId").GetGuid());
        Assert.True(RelationSemantics.IsExecutionRelation("triggers"));
        Assert.True(RelationSemantics.IsExecutionRelation("followsWhen"));
        Assert.False(RelationSemantics.IsExecutionRelation("contains"));
        Assert.False(RelationSemantics.IsExecutionRelation("calls"));
        Assert.False(RelationSemantics.IsExecutionRelation("implements"));
        Assert.False(RelationSemantics.IsExecutionRelation("constrains"));
    }

    private static JsonElement GoldenFlow() => Json($$"""
        {"purpose":"Synthetic manufacturing semantic-conservation fixture","stageOrder":["s1","s2","s3","s4","s5","s6","s7"],
         "stages":[
           {"stageId":"s1","title":"Intake"},{"stageId":"s2","title":"Review"},{"stageId":"s3","title":"Plan"},
           {"stageId":"s4","title":"Prepare"},{"stageId":"s5","title":"Produce"},{"stageId":"s6","title":"Inspect"},{"stageId":"s7","title":"Complete"}],
         "entryNodeId":"n1","endNodeIds":["n12"],
         "nodes":[
           {"nodeId":"n1","stageId":"s1","kind":"start","title":"Start","ruleRefs":[]},
           {"nodeId":"n2","stageId":"s1","kind":"action","title":"Capture","ruleRefs":[]},
           {"nodeId":"n3","stageId":"s2","kind":"decision","title":"Review","ruleRefs":[{"unitId":"{{Amount}}"}]},
           {"nodeId":"n4","stageId":"s3","kind":"action","title":"Plan","ruleRefs":[]},
           {"nodeId":"n5","stageId":"s3","kind":"event","title":"Ready","ruleRefs":[]},
           {"nodeId":"n6","stageId":"s4","kind":"action","title":"Prepare","ruleRefs":[]},
           {"nodeId":"n7","stageId":"s2","kind":"exception","title":"Correct","ruleRefs":[]},
           {"nodeId":"n8","stageId":"s5","kind":"action","title":"Produce","ruleRefs":[]},
           {"nodeId":"n9","stageId":"s5","kind":"action","title":"Record","ruleRefs":[]},
           {"nodeId":"n10","stageId":"s6","kind":"action","title":"Inspect","ruleRefs":[]},
           {"nodeId":"n11","stageId":"s7","kind":"action","title":"Release","ruleRefs":[]},
           {"nodeId":"n12","stageId":"s7","kind":"end","title":"End","ruleRefs":[]}],
         "edges":[
           {"edgeId":"e1","fromNodeId":"n1","toNodeId":"n2","kind":"sequence","guardRuleRefs":[]},
           {"edgeId":"e2","fromNodeId":"n2","toNodeId":"n3","kind":"sequence","guardRuleRefs":[]},
           {"edgeId":"e3","fromNodeId":"n3","toNodeId":"n4","kind":"conditional","conditionText":"Accepted","guardRuleRefs":[{"unitId":"{{Amount}}"}]},
           {"edgeId":"e4","fromNodeId":"n3","toNodeId":"n7","kind":"conditional","conditionText":"Correction required","guardRuleRefs":[{"unitId":"{{Region}}"}]},
           {"edgeId":"e5","fromNodeId":"n4","toNodeId":"n5","kind":"sequence","guardRuleRefs":[]},
           {"edgeId":"e6","fromNodeId":"n5","toNodeId":"n6","kind":"sequence","guardRuleRefs":[]},
           {"edgeId":"e7","fromNodeId":"n7","toNodeId":"n2","kind":"return","guardRuleRefs":[]},
           {"edgeId":"e8","fromNodeId":"n6","toNodeId":"n8","kind":"sequence","guardRuleRefs":[]},
           {"edgeId":"e9","fromNodeId":"n8","toNodeId":"n9","kind":"sequence","guardRuleRefs":[]},
           {"edgeId":"e10","fromNodeId":"n9","toNodeId":"n10","kind":"sequence","guardRuleRefs":[]},
           {"edgeId":"e11","fromNodeId":"n10","toNodeId":"n11","kind":"sequence","guardRuleRefs":[]},
           {"edgeId":"e12","fromNodeId":"n11","toNodeId":"n12","kind":"sequence","guardRuleRefs":[]},
           {"edgeId":"e13","fromNodeId":"n6","toNodeId":"n3","kind":"return","conditionText":"Re-review","guardRuleRefs":[]},
           {"edgeId":"e14","fromNodeId":"n10","toNodeId":"n8","kind":"exception","conditionText":"Rework","guardRuleRefs":[]}]}
        """);

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
