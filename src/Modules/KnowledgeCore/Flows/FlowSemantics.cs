using System.Text.Json;
using ProjectIdentity.Application;

namespace KnowledgeCore.Flows;

/// <summary>
/// Validates and projects authored flow semantics. Projection preserves every stage, node,
/// edge and rule reference; it never infers execution order from semantic relations.
/// </summary>
public static class FlowSemantics
{
    private static readonly HashSet<string> NodeKinds = ["start", "action", "decision", "event", "end", "exception"];
    private static readonly HashSet<string> EdgeKinds = ["sequence", "conditional", "exception", "return"];

    public static void Validate(JsonElement body)
    {
        var flow = Read(body);
        if (flow.Stages.Count == 0) throw Invalid("A flow requires at least one stage.");
        if (flow.Nodes.Count == 0) throw Invalid("A flow requires at least one node.");
        if (flow.Nodes.All(node => node.NodeId != flow.EntryNodeId))
            throw Invalid("entryNodeId must reference a declared node.");
        if (flow.Nodes.Single(node => node.NodeId == flow.EntryNodeId).Kind != "start")
            throw Invalid("The flow entry node must have kind 'start'.");
        if (flow.EndNodeIds.Count == 0) throw Invalid("A flow requires at least one endNodeId.");
        foreach (var endNodeId in flow.EndNodeIds)
        {
            var end = flow.Nodes.SingleOrDefault(node => node.NodeId == endNodeId)
                ?? throw Invalid($"endNodeId '{endNodeId}' must reference a declared node.");
            if (end.Kind != "end") throw Invalid($"End node '{endNodeId}' must have kind 'end'.");
        }
        foreach (var stage in flow.Stages)
            if (flow.Nodes.All(node => node.StageId != stage.StageId))
                throw Invalid($"Stage '{stage.StageId}' has no declared nodes; it cannot be omitted from a textual projection.");
        foreach (var edge in flow.Edges)
            if (edge.Kind == "conditional" && string.IsNullOrWhiteSpace(edge.ConditionText) && edge.GuardRuleRefs.Count == 0)
                throw Invalid($"Conditional edge '{edge.EdgeId}' requires conditionText or at least one guardRuleRef.");
    }

    public static FlowProjection Project(JsonElement body)
    {
        Validate(body);
        return Read(body);
    }

    public static IEnumerable<JsonElement> RuleReferences(JsonElement body)
    {
        var flow = Read(body);
        return flow.Nodes.SelectMany(node => node.RuleRefs)
            .Concat(flow.Edges.SelectMany(edge => edge.GuardRuleRefs));
    }

    public static void ValidateTransition(JsonElement body)
    {
        RequireObject(body, "Transition body");
        RequirePropertyReference(body, "object");
        RequirePropertyReference(body, "from");
        RequirePropertyReference(body, "to");
        RequirePropertyReference(body, "trigger");
        ReadReferences(body, "guardRuleRefs");
        if (OptionalString(body, "exception") is { Length: > 2000 })
            throw Invalid("Transition exception text cannot exceed 2000 characters.");
    }

    public static IEnumerable<JsonElement> TransitionRuleReferences(JsonElement body) => ReadReferences(body, "guardRuleRefs");

    private static FlowProjection Read(JsonElement body)
    {
        RequireObject(body, "Flow body");
        var purpose = RequiredString(body, "purpose");
        var entryNodeId = RequiredString(body, "entryNodeId");
        var stageOrder = RequiredStrings(body, "stageOrder");
        EnsureUnique(stageOrder, "stageOrder");

        var stageDefinitions = RequiredArray(body, "stages").EnumerateArray().Select(stage =>
        {
            RequireObject(stage, "Flow stage");
            return new StageDefinition(RequiredString(stage, "stageId"), RequiredString(stage, "title"),
                OptionalString(stage, "purpose"), OptionalString(stage, "entryCondition"), OptionalString(stage, "summary"));
        }).ToList();
        EnsureUnique(stageDefinitions.Select(stage => stage.StageId), "stageId");
        var stagesById = stageDefinitions.ToDictionary(stage => stage.StageId, StringComparer.Ordinal);
        if (stageOrder.Count != stageDefinitions.Count || stageOrder.Any(id => !stagesById.ContainsKey(id)))
            throw Invalid("stageOrder must contain every declared stage exactly once and no undeclared stage.");

        var nodes = RequiredArray(body, "nodes").EnumerateArray().Select(node =>
        {
            RequireObject(node, "Flow node");
            var nodeId = RequiredString(node, "nodeId");
            var stageId = RequiredString(node, "stageId");
            if (!stagesById.ContainsKey(stageId)) throw Invalid($"Node '{nodeId}' references undeclared stage '{stageId}'.");
            var kind = RequiredString(node, "kind");
            if (!NodeKinds.Contains(kind)) throw Invalid($"Node '{nodeId}' has unsupported kind '{kind}'.");
            OptionalReference(node, "action");
            OptionalReference(node, "actor");
            return new FlowNodeProjection(nodeId, stageId, kind, RequiredString(node, "title"),
                OptionalReference(node, "action"), OptionalReference(node, "actor"), ReadReferences(node, "ruleRefs"), OptionalString(node, "result"));
        }).ToList();
        EnsureUnique(nodes.Select(node => node.NodeId), "nodeId");
        var nodesById = nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);

        var edges = RequiredArray(body, "edges").EnumerateArray().Select(edge =>
        {
            RequireObject(edge, "Flow edge");
            var edgeId = RequiredString(edge, "edgeId");
            var fromNodeId = RequiredString(edge, "fromNodeId");
            var toNodeId = RequiredString(edge, "toNodeId");
            if (!nodesById.TryGetValue(fromNodeId, out var from)) throw Invalid($"Edge '{edgeId}' references undeclared fromNodeId '{fromNodeId}'.");
            if (!nodesById.TryGetValue(toNodeId, out var to)) throw Invalid($"Edge '{edgeId}' references undeclared toNodeId '{toNodeId}'.");
            var kind = RequiredString(edge, "kind");
            if (!EdgeKinds.Contains(kind)) throw Invalid($"Edge '{edgeId}' has unsupported kind '{kind}'.");
            return new FlowEdgeProjection(edgeId, fromNodeId, toNodeId, kind, OptionalString(edge, "conditionText"),
                ReadReferences(edge, "guardRuleRefs"), from.StageId, to.StageId);
        }).ToList();
        EnsureUnique(edges.Select(edge => edge.EdgeId), "edgeId");

        var endNodeIds = RequiredStrings(body, "endNodeIds");
        EnsureUnique(endNodeIds, "endNodeIds");
        var stages = stageOrder.Select(id =>
        {
            var stage = stagesById[id];
            return new FlowStageProjection(stage.StageId, stage.Title, stage.Purpose, stage.EntryCondition, stage.Summary,
                nodes.Where(node => node.StageId == id).Select(node => node.NodeId).ToArray());
        }).ToArray();
        return new(purpose, entryNodeId, endNodeIds, stages, nodes, edges);
    }

    private static IReadOnlyList<JsonElement> ReadReferences(JsonElement value, string property)
    {
        var references = RequiredArray(value, property).EnumerateArray().Select(item =>
        {
            RequireReference(item, property);
            return item.Clone();
        }).ToArray();
        var identities = references.Select(item => item.GetProperty("unitId").GetGuid()).ToArray();
        EnsureUnique(identities.Select(id => id.ToString()), property + ".unitId");
        return references;
    }

    private static JsonElement? OptionalReference(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var reference)) return null;
        RequireReference(reference, property);
        return reference.Clone();
    }

    private static void RequirePropertyReference(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var reference)) throw Invalid($"{property} is required.");
        RequireReference(reference, property);
    }

    private static void RequireReference(JsonElement reference, string path)
    {
        if (reference.ValueKind != JsonValueKind.Object ||
            !reference.TryGetProperty("unitId", out var unitId) || unitId.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(unitId.GetString(), out var parsedUnitId) || parsedUnitId == Guid.Empty)
            throw Invalid($"{path} requires a non-empty stable unitId.");
        if (reference.TryGetProperty("revisionId", out var revisionId) &&
            (revisionId.ValueKind != JsonValueKind.String || !Guid.TryParse(revisionId.GetString(), out var parsedRevisionId) || parsedRevisionId == Guid.Empty))
            throw Invalid($"{path}.revisionId must be a non-empty exact revision identifier when supplied.");
    }

    private static IReadOnlyList<string> RequiredStrings(JsonElement value, string property) =>
        RequiredArray(value, property).EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())
            ? item.GetString()! : throw Invalid($"{property} entries must be non-empty strings.")).ToArray();

    private static JsonElement RequiredArray(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.Array
            ? result : throw Invalid($"{property} must be an array.");

    private static string RequiredString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(result.GetString())
            ? result.GetString()! : throw Invalid($"{property} must be a non-empty string.");

    private static string? OptionalString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var result)) return null;
        return result.ValueKind == JsonValueKind.String ? result.GetString() : throw Invalid($"{property} must be a string.");
    }

    private static void RequireObject(JsonElement value, string description)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid($"{description} must be an object.");
    }

    private static void EnsureUnique(IEnumerable<string> values, string description)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            if (!seen.Add(value)) throw Invalid($"{description} contains duplicate identifier '{value}'.");
    }

    private static ProjectIdentityException Invalid(string message) => ProjectIdentityException.Invalid(message);

    private sealed record StageDefinition(string StageId, string Title, string? Purpose, string? EntryCondition, string? Summary);
}

public sealed record FlowProjection(
    string Purpose,
    string EntryNodeId,
    IReadOnlyList<string> EndNodeIds,
    IReadOnlyList<FlowStageProjection> Stages,
    IReadOnlyList<FlowNodeProjection> Nodes,
    IReadOnlyList<FlowEdgeProjection> Edges);

public sealed record FlowStageProjection(
    string StageId,
    string Title,
    string? Purpose,
    string? EntryCondition,
    string? Summary,
    IReadOnlyList<string> NodeIds);

public sealed record FlowNodeProjection(
    string NodeId,
    string StageId,
    string Kind,
    string Title,
    JsonElement? Action,
    JsonElement? Actor,
    IReadOnlyList<JsonElement> RuleRefs,
    string? Result);

public sealed record FlowEdgeProjection(
    string EdgeId,
    string FromNodeId,
    string ToNodeId,
    string Kind,
    string? ConditionText,
    IReadOnlyList<JsonElement> GuardRuleRefs,
    string FromStageId,
    string ToStageId);
