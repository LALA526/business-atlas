using System.Text.Json;
using AccessApplicability.Contracts;
using KnowledgeCore.Contracts;
using KnowledgeCore.Flows;
using ReadSearchExplain.Contracts;

namespace ReadSearchExplain.Reading;

/// <summary>Builds a read-only, purpose-oriented projection from already authorized revisions.</summary>
public static class ReadingComposer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ReadingSection> Compose(RevisionView root, IReadOnlyList<RevisionView> related, ReadPurpose purpose, string? profile)
    {
        var revisions = new[] { root }.Concat(related).GroupBy(item => item.UnitId).Select(group => group.First()).ToArray();
        if (IsKind(root, profile, "Feature")) return FeatureTabs(root, revisions);
        if (IsKind(root, profile, "Flow")) return Flow(root, revisions);
        var title = root.Kind switch
        {
            "BusinessRule" or "Rule" => "Conditions, outcomes and exceptions",
            "Api" => "Contract, inputs, outputs and errors",
            "Component" => "Responsibilities, dependencies and runtime",
            "DomainTerm" or "DomainConcept" or "BusinessAttribute" => "Definition, context and mappings",
            "ProjectProfile" => "Positioning, scope and capabilities",
            _ => "Business purpose and content"
        };
        return Group(title, root, revisions, purpose);
    }

    private static IReadOnlyList<ReadingSection> FeatureTabs(RevisionView root, IReadOnlyList<RevisionView> revisions)
    {
        var requirements = revisions.Where(item => item.UnitId == root.UnitId || item.Kind is "Requirement" or "QualityRequirement" or "Behavior" or "Action").ToArray();
        var rules = revisions.Where(item => item.Kind is "BusinessRule" or "Rule" or "Constraint" or "BusinessCase" or "Test").ToArray();
        var designs = revisions.Where(item => item.Kind is "Design" or "Component" or "Api" or "Implementation" or "Operation" or "DataEntity" or "DataField" or "Observation").ToArray();
        return
        [
            new("requirements", "Business requirements", requirements.SelectMany(ContentBlocks).ToArray(), requirements.Where(item => item.UnitId != root.UnitId).Select(Link).ToArray()),
            new("rules", "Business rules", rules.SelectMany(ContentBlocks).ToArray(), rules.Select(Link).ToArray()),
            new("design", "Business design and actual observations", designs.SelectMany(ContentBlocks).ToArray(), designs.Select(Link).ToArray())
        ];
    }

    private static IReadOnlyList<ReadingSection> Group(string title, RevisionView root, IReadOnlyList<RevisionView> revisions, ReadPurpose purpose)
    {
        var ordered = purpose switch
        {
            ReadPurpose.Implement => revisions.OrderBy(item => item.Kind is "Design" or "Component" or "Api" or "Implementation" ? 0 : item.UnitId == root.UnitId ? 1 : 2),
            ReadPurpose.Change => revisions.OrderBy(item => item.Kind is "BusinessRule" or "Rule" or "Constraint" ? 0 : item.UnitId == root.UnitId ? 1 : 2),
            ReadPurpose.Verify => revisions.OrderBy(item => item.Kind is "BusinessCase" or "Test" or "Observation" ? 0 : 1),
            _ => revisions.OrderBy(item => item.UnitId == root.UnitId ? 0 : 1)
        };
        return [new("content", title, ordered.SelectMany(ContentBlocks).ToArray(), ordered.Where(item => item.UnitId != root.UnitId).Select(Link).ToArray())];
    }

    private static IReadOnlyList<ReadingSection> Flow(RevisionView root, IReadOnlyList<RevisionView> revisions)
    {
        var projection = FlowSemantics.Project(root.Body);
        var byUnit = revisions.ToDictionary(item => item.UnitId);
        var children = new List<ReadingSection>();
        foreach (var stage in projection.Stages)
        {
            var blocks = new List<JsonElement>();
            if (!string.IsNullOrWhiteSpace(stage.Purpose)) blocks.Add(Block($"stage-purpose-{stage.StageId}", "paragraph", stage.Purpose!, root));
            if (!string.IsNullOrWhiteSpace(stage.EntryCondition)) blocks.Add(Block($"stage-entry-{stage.StageId}", "notice", $"Entry condition: {stage.EntryCondition}", root));
            foreach (var nodeId in stage.NodeIds)
            {
                var node = projection.Nodes.Single(item => item.NodeId == nodeId);
                var refs = node.RuleRefs.Select(RefId).Where(byUnit.ContainsKey).Select(id => byUnit[id]).Append(root).ToArray();
                blocks.Add(Block($"node-{node.NodeId}", "paragraph", $"{node.Title} ({node.Kind})" + (node.Result is null ? string.Empty : $" — result: {node.Result}"), refs));
            }
            children.Add(new($"stage-{stage.StageId}", stage.Title, blocks));
        }
        var edges = projection.Edges.Select(edge =>
        {
            var from = projection.Nodes.Single(node => node.NodeId == edge.FromNodeId);
            var to = projection.Nodes.Single(node => node.NodeId == edge.ToNodeId);
            var condition = edge.ConditionText is null ? string.Empty : $"; condition: {edge.ConditionText}";
            var returned = edge.Kind == "return" ? $"; returns to stage {to.StageId}" : string.Empty;
            var refs = edge.GuardRuleRefs.Select(RefId).Where(byUnit.ContainsKey).Select(id => byUnit[id]).Append(root).ToArray();
            return Block($"edge-{edge.EdgeId}", "paragraph", $"{edge.EdgeId}: {from.Title} → {to.Title} ({edge.Kind}{condition}{returned})", refs);
        }).ToArray();
        var sections = new List<ReadingSection>
        {
            new("stages", "Stages and steps", [Block("flow-purpose", "paragraph", projection.Purpose, root)], children),
            new("transitions", "Branches, guards and returns", edges)
        };
        var rules = revisions.Where(item => item.UnitId != root.UnitId && item.Kind is "BusinessRule" or "Rule" or "Constraint").ToArray();
        if (rules.Length > 0) sections.Add(new("rules", "Referenced decision rules", rules.SelectMany(ContentBlocks).ToArray(), rules.Select(Link).ToArray()));
        return sections;
    }

    private static IEnumerable<JsonElement> ContentBlocks(RevisionView revision)
    {
        if (revision.Blocks.ValueKind == JsonValueKind.Array)
            foreach (var block in revision.Blocks.EnumerateArray()) yield return WithCitations(block, revision);
        var evidence = EvidenceBlock(revision);
        if (evidence.HasValue) yield return evidence.Value;
    }

    private static JsonElement? EvidenceBlock(RevisionView revision)
    {
        if (revision.Evidence.ValueKind != JsonValueKind.Array || revision.Evidence.GetArrayLength() == 0) return null;
        return JsonSerializer.SerializeToElement(new
        {
            blockId = $"evidence-{revision.UnitId}", kind = "list", semanticRef = $"evidence:{revision.UnitId}",
            text = revision.FactKind == "observed" ? "Actual observation evidence (not target behavior)" : "Sources and evidence",
            revisionRefs = new[] { Ref(revision) }, evidence = revision.Evidence, expandRef = $"/api/v1/units/{revision.UnitId}/revisions/{revision.RevisionId}#evidence"
        }, Json);
    }

    private static ReadingSection Link(RevisionView revision) => new($"unit-{revision.UnitId}", revision.Title, [], ExpandRef: $"/api/v1/units/{revision.UnitId}/revisions/{revision.RevisionId}");
    private static JsonElement WithCitations(JsonElement block, RevisionView revision)
    {
        if (block.ValueKind != JsonValueKind.Object) return block.Clone();
        var value = JsonSerializer.SerializeToNode(block)!.AsObject();
        if (value["revisionRefs"] is null) value["revisionRefs"] = JsonSerializer.SerializeToNode(new[] { Ref(revision) }, Json);
        return JsonSerializer.SerializeToElement(value, Json);
    }
    private static JsonElement Block(string id, string kind, string text, params RevisionView[] revisions) => Block(id, kind, text, (IReadOnlyList<RevisionView>)revisions);
    private static JsonElement Block(string id, string kind, string text, IReadOnlyList<RevisionView> revisions) => JsonSerializer.SerializeToElement(new
    {
        blockId = id, kind, semanticRef = id, text, revisionRefs = revisions.DistinctBy(item => item.RevisionId).Select(Ref).ToArray()
    }, Json);
    private static object Ref(RevisionView revision) => new { unitId = revision.UnitId, revisionId = revision.RevisionId };
    private static Guid RefId(JsonElement reference) => reference.GetProperty("unitId").GetGuid();
    private static bool IsKind(RevisionView root, string? profile, string kind) => string.Equals(profile, kind, StringComparison.OrdinalIgnoreCase) || string.Equals(root.Kind, kind, StringComparison.OrdinalIgnoreCase);
}
