using System.Text.Json;
using ProjectIdentity.Application;

namespace KnowledgeCore.Relations;

/// <summary>Frozen predicate meanings and endpoint types used by the relation store.</summary>
public static class RelationSemantics
{
    private sealed record Definition(string[] From, string[] To, string Meaning);

    private static readonly Dictionary<string, Definition> Definitions = new(StringComparer.Ordinal)
    {
        ["contains"] = new(["Domain", "Capability", "System", "Module", "Feature", "ProjectProfile"], ["*"], "归属，不是执行"),
        ["defines"] = new(["DomainTerm", "DomainConcept"], ["DomainConcept", "BusinessAttribute", "BusinessObject"], "定义"),
        ["specializes"] = new(["DomainConcept", "BusinessRule"], ["DomainConcept", "BusinessRule"], "细化"),
        ["refines"] = new(["Requirement", "BusinessRule", "QualityRequirement"], ["Requirement", "QualityRequirement"], "细化需求"),
        ["usesFact"] = new(["BusinessRule"], ["BusinessAttribute", "DomainConcept", "Assertion", "BusinessObject"], "业务判断依据"),
        ["constrains"] = new(["BusinessRule", "Constraint", "QualityRequirement"], ["Behavior", "Action", "BusinessObject", "State", "Flow", "Component", "Api"], "约束，不是执行"),
        ["performs"] = new(["Actor", "Component", "System"], ["Action", "Operation", "Behavior"], "履责"),
        ["triggers"] = new(["Event", "Action"], ["Action", "Event", "Behavior"], "明确触发"),
        ["followsWhen"] = new(["Action", "Event", "Behavior"], ["Action", "Event", "Behavior"], "明确条件执行"),
        ["changesState"] = new(["Action", "Transition", "Behavior"], ["State", "BusinessObject"], "状态变化"),
        ["satisfies"] = new(["Behavior", "Action", "Capability", "Feature"], ["Requirement", "QualityRequirement"], "目标满足，非验收"),
        ["implements"] = new(["Api", "Component", "Implementation", "Operation", "Module"], ["BusinessRule", "Behavior", "Action", "Design", "Requirement"], "实现映射"),
        ["mapsTo"] = new(["BusinessAttribute", "DomainConcept", "BusinessObject"], ["DataField", "DataEntity", "Api", "Operation", "Implementation"], "业务到实现语义映射"),
        ["calls"] = new(["Api", "Component", "Operation", "Implementation"], ["Api", "Component", "Operation", "Implementation"], "真实技术调用"),
        ["reads"] = new(["Action", "Operation", "Api", "Implementation"], ["BusinessAttribute", "DataField", "DataEntity"], "数据读取"),
        ["writes"] = new(["Action", "Operation", "Api", "Implementation"], ["BusinessAttribute", "DataField", "DataEntity"], "数据写入"),
        ["dependsOn"] = new(["*"], ["*"], "一般依赖，须调查"),
        ["governedBy"] = new(["*"], ["Constraint", "Decision", "Design", "BusinessRule"], "既定职责约束"),
        ["validates"] = new(["BusinessCase", "Test"], ["BusinessRule", "Requirement", "QualityRequirement", "Constraint"], "验证目标，不代表通过"),
        ["observedAt"] = new(["Observation"], ["Implementation", "Deployment", "Environment", "Source"], "观察版本环境"),
        ["evidencedBy"] = new(["*"], ["Source", "Observation"], "证据支持"),
        ["supersedes"] = new(["*"], ["*"], "替代，禁止循环"),
        ["conflictsWith"] = new(["*"], ["*"], "冲突待决")
    };

    public static bool IsRelation(string kind) => kind is "Relationship" or "Relation";

    public static string Meaning(string predicate) => Definitions.TryGetValue(predicate, out var definition)
        ? definition.Meaning : throw ProjectIdentityException.Invalid($"The relation predicate '{predicate}' is not registered.");

    public static void ValidateShape(JsonElement body)
    {
        if (!body.TryGetProperty("predicate", out var predicate) || predicate.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(predicate.GetString()))
            throw ProjectIdentityException.Invalid("Relation requires a registered predicate.");
        _ = Meaning(predicate.GetString()!);
        if (!body.TryGetProperty("from", out var from) || !ValidReference(from) || !body.TryGetProperty("to", out var to) || !ValidReference(to))
            throw ProjectIdentityException.Invalid("Relation from and to require stable unitId references.");
        if (body.TryGetProperty("meaning", out var meaning) && (meaning.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(meaning.GetString())))
            throw ProjectIdentityException.Invalid("Relation meaning must be a non-empty explanation when supplied.");
        if (body.TryGetProperty("mappingExpression", out var mapping) && mapping.ValueKind != JsonValueKind.String)
            throw ProjectIdentityException.Invalid("Relation mappingExpression must be text, not executable code.");
    }

    public static void ValidateEndpoints(string predicate, string fromKind, string toKind)
    {
        var definition = Definitions.TryGetValue(predicate, out var registered)
            ? registered : throw ProjectIdentityException.Invalid($"The relation predicate '{predicate}' is not registered.");
        fromKind = CanonicalKind(fromKind); toKind = CanonicalKind(toKind);
        if (!Accepts(definition.From, fromKind) || !Accepts(definition.To, toKind))
            throw ProjectIdentityException.Invalid($"Predicate '{predicate}' does not allow {fromKind} → {toKind}: {definition.Meaning}.");
    }

    private static bool ValidReference(JsonElement reference) =>
        reference.ValueKind == JsonValueKind.Object && reference.TryGetProperty("unitId", out var id) &&
        id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var unitId) && unitId != Guid.Empty;

    private static bool Accepts(IEnumerable<string> allowed, string actual) => allowed.Contains("*") || allowed.Contains(actual);
    private static string CanonicalKind(string kind) => kind switch { "Rule" => "BusinessRule", "Relation" => "Relationship", _ => kind };
}
