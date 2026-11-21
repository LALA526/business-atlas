using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SchemaCatalog.Contracts;

namespace SchemaCatalog.Application;

public sealed record BuiltInType(string Kind, string Dimension, string Profile, string StrictBodySchema, string[] Required);
public sealed record BuiltInPredicate(string Key, string[] FromKinds, string[] ToKinds, string Meaning, string[] ImpactDirections);

/// <summary>The frozen atlas-registry/1.0 built-ins. Project drafts can only extend these definitions.</summary>
public static class BuiltInRegistry
{
    public const string Version = "atlas-registry/1.0";
    public static readonly IReadOnlyList<RegistryDimension> Dimensions =
    [
        new("project", "项目认知"), new("business", "业务认知"), new("system", "系统认知"),
        new("knowledge", "知识资产"), new("governance", "协作治理")
    ];

    private static BuiltInType T(string kind, string dimension, string profile, string schema, params string[] required) =>
        new(kind, dimension, profile, schema, required);

    public static readonly IReadOnlyList<BuiltInType> Types =
    [
        T("ProjectProfile","project","project","ProjectBody","purpose","scope","nonScope","stakeholders","capabilities","systems"),
        T("DomainTerm","business","vocabulary","TermBody","definition","contextKey","aliases"),
        T("DomainConcept","business","vocabulary","TermBody","definition","contextKey","aliases"),
        T("BusinessObject","business","vocabulary","TermBody","definition","contextKey","aliases"),
        T("BusinessAttribute","business","vocabulary","TermBody","definition","contextKey","aliases"),
        T("Requirement","business","requirement","RequirementBody","problem","goal","beneficiaries","scope","successCriteria"),
        T("BusinessRule","business","rule","RuleBody","purpose","evaluationMode","inputs","priorityPolicy","rows","exceptionExplanation","caseRefs"),
        T("Flow","business","flow","FlowBody","purpose","stageOrder","stages","nodes","edges","entryNodeId","endNodeIds"),
        T("Transition","business","state","TransitionBody","object","from","to","trigger","guardRuleRefs"),
        T("Relationship","knowledge","relation","RelationBody","predicate","from","to","meaning"),
        T("Assertion","knowledge","assertion","AssertionBody","subject","predicate","qualifiers"),
        T("Design","system","design","DesignBody","purpose","responsibility","nonResponsibility","inputs","outputs","processing","dataEffects","errors","constraintRefs"),
        T("Component","system","design","DesignBody","purpose","responsibility","nonResponsibility","inputs","outputs","processing","dataEffects","errors","constraintRefs"),
        T("Api","system","design","DesignBody","purpose","responsibility","nonResponsibility","inputs","outputs","processing","dataEffects","errors","constraintRefs"),
        T("Implementation","system","design","DesignBody","purpose","responsibility","nonResponsibility","inputs","outputs","processing","dataEffects","errors","constraintRefs"),
        T("Observation","business","observation","ObservationBody","subject","buildDigest","environment","method","input","actual","observedAt","evidenceRefs","limitations"),
        T("BusinessCase","business","case","CaseBody","target","input","expected","caseKind","context","expectedExplanation"),
        T("Source","knowledge","source","SourceBody","sourceKind","locator","provenance"),
        T("DataField","system","data","DataBody","businessMeaning","valueType","missingNullSemantics","origin","constraints","mappingRefs"),
        T("TypeDefinition","knowledge","registry","SchemaDefinitionBody","typeKey","version","profile","schema","category"),
        T("PredicateDefinition","knowledge","registry","PredicateDefinitionBody","predicate","fromKinds","toKinds","meaning","impactDirections"),
        T("ContentProfile","knowledge","registry","SchemaDefinitionBody","typeKey","version","profile","schema","category"),
        T("Domain","project","standard","GenericBody"), T("Capability","project","standard","GenericBody"),
        T("Feature","business","feature","FeatureBody","purpose","actors","requirementRefs","ruleRefs","behaviorRefs","designRefs","nonScope"),
        T("Actor","business","standard","GenericBody"),
        T("Action","business","behavior","ActionBody","purpose","actorRefs","objectRefs","preconditions","ruleRefs","effects","exceptions"),
        T("Event","business","event","EventBody","meaning","trigger","subjectRefs","effects","ruleRefs"),
        T("Behavior","business","behavior","ActionBody","purpose","actorRefs","objectRefs","preconditions","ruleRefs","effects","exceptions"),
        T("State","business","state","StateBody","objectRef","meaning","invariants","terminal"),
        T("Constraint","business","constraint","ConstraintBody","statement","scope","violationEffect","verificationMethod","subjectRefs"),
        T("Decision","governance","decision","DecisionBody","question","alternatives","recommendation","rationale","effects","preserveRefs"),
        T("System","system","design","DesignBody","purpose","responsibility","nonResponsibility","inputs","outputs","processing","dataEffects","errors","constraintRefs"),
        T("Module","system","design","DesignBody","purpose","responsibility","nonResponsibility","inputs","outputs","processing","dataEffects","errors","constraintRefs"),
        T("DataEntity","system","data","DataEntityBody","meaning","identitySemantics","attributeRefs","fieldRefs","constraints","lifecycle"),
        T("Operation","system","design","DesignBody","purpose","responsibility","nonResponsibility","inputs","outputs","processing","dataEffects","errors","constraintRefs"),
        T("Environment","system","standard","GenericBody"), T("Deployment","system","standard","GenericBody"),
        T("Test","system","test","TestBody","purpose","targetRefs","preconditions","steps","expected"),
        T("OperationalKnowledge","system","standard","GenericBody"),
        T("EngineeringStandard","system","constraint","ConstraintBody","statement","scope","violationEffect","verificationMethod","subjectRefs"),
        T("QualityRequirement","system","constraint","ConstraintBody","statement","scope","violationEffect","verificationMethod","subjectRefs"),
        T("Document","knowledge","standard","GenericBody"), T("Metric","project","standard","GenericBody"),
        T("Milestone","project","standard","GenericBody"), T("Permission","governance","standard","GenericBody"),
        T("Risk","project","standard","GenericBody"), T("TechnicalDebt","governance","standard","GenericBody"),
        T("Configuration","system","standard","GenericBody"), T("Exception","business","standard","GenericBody"),
        T("Compensation","business","standard","GenericBody"),
        T("Scenario","business","scenario","ScenarioBody","purpose","context","trigger","participants","preconditions","steps","ruleRefs","flowRefs","outcomes","exceptions")
    ];

    private static BuiltInPredicate P(string key, string[] from, string[] to, string meaning, params string[] directions) =>
        new(key, from, to, meaning, directions);
    private static string[] K(params string[] kinds) => kinds;

    public static readonly IReadOnlyList<BuiltInPredicate> Predicates =
    [
        P("contains",K("Domain","Capability","System","Module","Feature","ProjectProfile"),K("*"),"归属，不是执行","scope_only"),
        P("defines",K("DomainTerm","DomainConcept"),K("DomainConcept","BusinessAttribute","BusinessObject"),"定义","forward","reverse"),
        P("specializes",K("DomainConcept","BusinessRule"),K("DomainConcept","BusinessRule"),"细化","forward","reverse"),
        P("refines",K("Requirement","BusinessRule","QualityRequirement"),K("Requirement","QualityRequirement"),"细化需求","forward","reverse"),
        P("usesFact",K("BusinessRule"),K("BusinessAttribute","DomainConcept","Assertion","BusinessObject"),"业务判断依据","reverse","forward"),
        P("constrains",K("BusinessRule","Constraint","QualityRequirement"),K("Behavior","Action","BusinessObject","State","Flow","Component","Api"),"约束，不是执行","forward","reverse"),
        P("performs",K("Actor","Component","System"),K("Action","Operation","Behavior"),"履责","forward","reverse"),
        P("triggers",K("Event","Action"),K("Action","Event","Behavior"),"明确触发","forward","reverse"),
        P("followsWhen",K("Action","Event","Behavior"),K("Action","Event","Behavior"),"明确条件执行","forward","reverse"),
        P("changesState",K("Action","Transition","Behavior"),K("State","BusinessObject"),"状态变化","forward","reverse"),
        P("satisfies",K("Behavior","Action","Capability","Feature"),K("Requirement","QualityRequirement"),"目标满足，非验收","forward","reverse"),
        P("implements",K("Api","Component","Implementation","Operation","Module"),K("BusinessRule","Behavior","Action","Design","Requirement"),"实现映射","forward","reverse"),
        P("mapsTo",K("BusinessAttribute","DomainConcept","BusinessObject"),K("DataField","DataEntity","Api","Operation","Implementation"),"业务到实现语义映射","reverse","forward"),
        P("calls",K("Api","Component","Operation","Implementation"),K("Api","Component","Operation","Implementation"),"真实技术调用","forward","reverse"),
        P("reads",K("Action","Operation","Api","Implementation"),K("BusinessAttribute","DataField","DataEntity"),"数据读取","reverse","forward"),
        P("writes",K("Action","Operation","Api","Implementation"),K("BusinessAttribute","DataField","DataEntity"),"数据写入","reverse","forward"),
        P("dependsOn",K("*"),K("*"),"一般依赖，须调查","scope_only"),
        P("governedBy",K("*"),K("Constraint","Decision","Design","BusinessRule"),"既定职责约束","reverse","forward"),
        P("validates",K("BusinessCase","Test"),K("BusinessRule","Requirement","QualityRequirement","Constraint"),"验证目标，不代表通过","reverse","forward"),
        P("observedAt",K("Observation"),K("Implementation","Deployment","Environment","Source"),"观察版本环境","forward","reverse"),
        P("evidencedBy",K("*"),K("Source","Observation"),"证据支持","reverse","forward"),
        P("supersedes",K("*"),K("*"),"替代，禁止循环","forward","reverse"),
        P("conflictsWith",K("*"),K("*"),"冲突待决","forward","reverse")
    ];

    public static RegistryRevision TypeRevision(BuiltInType type)
    {
        var body = Object(new { typeKey = type.Kind, version = "1.0", profile = type.Profile,
            schema = new { @ref = type.StrictBodySchema, required = type.Required }, category = type.Dimension });
        return Revision(type.Kind, "TypeDefinition", body, type.Dimension);
    }

    public static RegistryRevision PredicateRevision(BuiltInPredicate predicate)
    {
        var body = Object(new { predicate = predicate.Key, fromKinds = predicate.FromKinds, toKinds = predicate.ToKinds,
            meaning = predicate.Meaning, impactDirections = predicate.ImpactDirections });
        return Revision(predicate.Key, "PredicateDefinition", body, "knowledge");
    }

    private static RegistryRevision Revision(string title, string kind, JsonElement body, string category) =>
        new(StableId("unit:" + kind + ":" + title), StableId("revision:" + kind + ":" + title), 1, title, kind,
            "1.0", "normative", "verified", Object(new { }), body, Array, Array,
            Convert.ToHexString(SHA256.HashData(body.GetRawText().Select(c => (byte)c).ToArray())).ToLowerInvariant(),
            DateTimeOffset.UnixEpoch, Guid.Empty, [category]);

    private static Guid StableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("atlas-registry/1.0:" + value));
        return new Guid(hash[..16]);
    }

    private static JsonElement Object<T>(T value) => JsonSerializer.SerializeToDocument(value).RootElement.Clone();
    private static JsonElement Array => JsonDocument.Parse("[]").RootElement.Clone();
}
