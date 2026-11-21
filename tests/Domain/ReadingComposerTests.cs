using System.Text.Json;
using AccessApplicability.Contracts;
using KnowledgeCore.Contracts;
using ReadSearchExplain.Reading;

namespace Atlas.Domain.Tests;

public sealed class ReadingComposerTests
{
    [Fact]
    public void AT_T19_Feature_has_three_tabs_and_separates_observations()
    {
        var feature = Revision("Feature", "Register quality issue", "normative", "{\"purpose\":\"Track quality\"}");
        var rule = Revision("BusinessRule", "Severity", "normative", "{}");
        var observation = Revision("Observation", "Observed save", "observed", "{}");
        var sections = ReadingComposer.Compose(feature, [rule, observation], ReadPurpose.Understand, null);
        Assert.Equal(["requirements", "rules", "design"], sections.Select(section => section.Key));
        Assert.Contains(sections[2].Blocks, block => block.GetProperty("text").GetString() == "Actual observation evidence (not target behavior)");
        Assert.All(sections.SelectMany(section => section.Blocks), block => Assert.True(block.TryGetProperty("revisionRefs", out _)));
    }

    [Fact]
    public void AT_T19_Flow_preserves_stages_edges_guards_and_return_without_duplicate_rule_text()
    {
        var rootId = Guid.NewGuid(); var ruleId = Guid.NewGuid();
        var body = Json($$"""
        {"purpose":"Handle quality","entryNodeId":"start","stageOrder":["intake","review"],
         "stages":[{"stageId":"intake","title":"Intake","entryCondition":"issue received"},{"stageId":"review","title":"Review"}],
         "nodes":[{"nodeId":"start","stageId":"intake","kind":"start","title":"Start","ruleRefs":[]},
                  {"nodeId":"decide","stageId":"review","kind":"decision","title":"Decide","result":"route","ruleRefs":[{"unitId":"{{ruleId}}"}]},
                  {"nodeId":"end","stageId":"review","kind":"end","title":"Finish","ruleRefs":[]}],
         "edges":[{"edgeId":"e1","fromNodeId":"start","toNodeId":"decide","kind":"sequence","guardRuleRefs":[]},
                  {"edgeId":"e2","fromNodeId":"decide","toNodeId":"end","kind":"conditional","conditionText":"severity is high","guardRuleRefs":[{"unitId":"{{ruleId}}"}]},
                  {"edgeId":"e3","fromNodeId":"decide","toNodeId":"decide","kind":"return","conditionText":"information missing","guardRuleRefs":[]}],
         "endNodeIds":["end"]}
        """);
        var flow = Revision("Flow", "Quality flow", "normative", body.GetRawText(), rootId);
        var rule = Revision("BusinessRule", "Severity", "normative", "{}", ruleId);
        var sections = ReadingComposer.Compose(flow, [rule], ReadPurpose.Understand, null);
        var blocks = sections.SelectMany(Flatten).ToArray();
        Assert.Equal(2, sections[0].Children!.Count);
        Assert.Contains(blocks, block => block.GetProperty("text").GetString()!.Contains("condition: severity is high"));
        Assert.Contains(blocks, block => block.GetProperty("text").GetString()!.Contains("returns to stage review"));
        Assert.Equal(3, blocks.Count(block => block.GetProperty("blockId").GetString()!.StartsWith("edge-")));
        Assert.Single(sections.Single(section => section.Key == "rules").Children!);
    }

    private static IEnumerable<JsonElement> Flatten(ReadSearchExplain.Contracts.ReadingSection section) =>
        section.Blocks.Concat(section.Children?.SelectMany(Flatten) ?? []);
    private static RevisionView Revision(string kind, string title, string factKind, string body, Guid? id = null) => new(
        id ?? Guid.NewGuid(), Guid.NewGuid(), 1, title, kind, "1.0", factKind, "verified",
        Json("{\"unrestricted\":true}"), Json(body), Json("[{\"blockId\":\"summary\",\"kind\":\"paragraph\",\"text\":\"Authored content\"}]"),
        Json("[{\"source\":\"approved fixture\"}]"), Json("[]"), "sha", DateTimeOffset.UtcNow, Guid.NewGuid(), []);
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}
