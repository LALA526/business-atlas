using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeCore.Flows;
using ProjectIdentity.Application;

namespace Atlas.GoldenSemantic.Tests;

public sealed class FlowConservationTests
{
    private static readonly Guid DecisionRule = Guid.Parse("30000000-0000-4000-8000-000000000001");
    private static readonly Guid GuardRule = Guid.Parse("30000000-0000-4000-8000-000000000002");

    [Fact]
    public void AT_T41_Seven_stage_flow_conserves_all_business_semantics_in_text_projection()
    {
        var original = GoldenFlow();
        var before = original.ToJsonString();
        var flow = FlowSemantics.Project(Json(original));

        Assert.Equal(7, flow.Stages.Count);
        Assert.Equal(12, flow.Nodes.Count);
        Assert.Equal(14, flow.Edges.Count);
        Assert.Equal(Enumerable.Range(1, 7).Select(number => $"s{number}"), flow.Stages.Select(stage => stage.StageId));
        Assert.All(flow.Stages, stage => Assert.NotEmpty(stage.NodeIds));
        Assert.Contains(flow.Nodes, node => node.Kind == "decision" && node.RuleRefs.Single().GetProperty("unitId").GetGuid() == DecisionRule);
        Assert.Contains(flow.Edges, edge => edge.Kind == "conditional" && edge.ConditionText == "Approved" && edge.GuardRuleRefs.Single().GetProperty("unitId").GetGuid() == GuardRule);
        Assert.Contains(flow.Edges, edge => edge.Kind == "exception" && edge.ConditionText == "Inspection failed");
        Assert.Equal(2, flow.Edges.Count(edge => edge.Kind == "return"));
        Assert.Contains(flow.Edges, edge => edge.FromStageId == "s2" && edge.ToStageId == "s1");
        Assert.Equal(before, original.ToJsonString()); // Reading/projection has no fact side effects (INV-10).
    }

    [Fact]
    public void AT_T41_Dropping_a_guard_stage_or_return_edge_is_rejected_or_detectable()
    {
        var guardless = GoldenFlow();
        guardless["edges"]![2]!["guardRuleRefs"] = new JsonArray();
        guardless["edges"]![2]!["conditionText"] = "";
        Assert.Throws<ProjectIdentityException>(() => FlowSemantics.Project(Json(guardless)));

        var missingStage = GoldenFlow();
        missingStage["stageOrder"]!.AsArray().RemoveAt(6);
        Assert.Throws<ProjectIdentityException>(() => FlowSemantics.Project(Json(missingStage)));

        var missingReturn = GoldenFlow();
        var edges = missingReturn["edges"]!.AsArray();
        var index = Enumerable.Range(0, edges.Count).Single(i => edges[i]!["edgeId"]!.GetValue<string>() == "e7");
        edges.RemoveAt(index);
        Assert.Equal(13, FlowSemantics.Project(Json(missingReturn)).Edges.Count);
    }

    private static JsonObject GoldenFlow()
    {
        var stages = new JsonArray(Enumerable.Range(1, 7).Select(number => (JsonNode)new JsonObject { ["stageId"] = $"s{number}", ["title"] = $"Stage {number}" }).ToArray());
        var nodes = new JsonArray(
            Node("n1", "s1", "start"), Node("n2", "s1", "action"), Node("n3", "s2", "decision", DecisionRule),
            Node("n4", "s2", "exception"), Node("n5", "s3", "action"), Node("n6", "s4", "action"),
            Node("n7", "s4", "event"), Node("n8", "s5", "action"), Node("n9", "s5", "action"),
            Node("n10", "s6", "action"), Node("n11", "s7", "action"), Node("n12", "s7", "end"));
        return new JsonObject
        {
            ["purpose"] = "Independent seven-stage semantic acceptance fixture", ["stageOrder"] = new JsonArray("s1", "s2", "s3", "s4", "s5", "s6", "s7"),
            ["stages"] = stages, ["nodes"] = nodes, ["entryNodeId"] = "n1", ["endNodeIds"] = new JsonArray("n12"),
            ["edges"] = new JsonArray(
                Edge("e1", "n1", "n2"), Edge("e2", "n2", "n3"), Edge("e3", "n3", "n5", "conditional", "Approved", GuardRule),
                Edge("e4", "n3", "n4", "conditional", "Changes required"), Edge("e5", "n4", "n2", "return"),
                Edge("e6", "n5", "n6"), Edge("e7", "n4", "n2", "return"), Edge("e8", "n6", "n7"),
                Edge("e9", "n7", "n8"), Edge("e10", "n8", "n9"), Edge("e11", "n9", "n10"),
                Edge("e12", "n10", "n11"), Edge("e13", "n11", "n12"), Edge("e14", "n10", "n8", "exception", "Inspection failed"))
        };
    }

    private static JsonObject Node(string id, string stage, string kind, Guid? rule = null) => new()
    {
        ["nodeId"] = id, ["stageId"] = stage, ["kind"] = kind, ["title"] = id,
        ["ruleRefs"] = rule.HasValue ? new JsonArray(new JsonObject { ["unitId"] = rule.Value.ToString() }) : new JsonArray()
    };
    private static JsonObject Edge(string id, string from, string to, string kind = "sequence", string? condition = null, Guid? guard = null)
    {
        var edge = new JsonObject
        {
            ["edgeId"] = id, ["fromNodeId"] = from, ["toNodeId"] = to, ["kind"] = kind,
            ["guardRuleRefs"] = guard.HasValue ? new JsonArray(new JsonObject { ["unitId"] = guard.Value.ToString() }) : new JsonArray()
        };
        if (condition is not null) edge["conditionText"] = condition;
        return edge;
    }
    private static JsonElement Json(JsonNode value) => JsonDocument.Parse(value.ToJsonString()).RootElement.Clone();
}
