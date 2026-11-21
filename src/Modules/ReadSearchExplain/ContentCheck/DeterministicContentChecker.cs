using System.Text.Json;
using KnowledgeCore.Contracts;
using ReadSearchExplain.Contracts;

namespace ReadSearchExplain.ContentCheck;

/// <summary>Deterministic, reviewable content checks. These checks never approve business content.</summary>
public sealed class DeterministicContentChecker
{
    public ContentReviewResult Review(RevisionInput revision)
    {
        var checks = new List<Check>
        {
            Check("schema.title", !string.IsNullOrWhiteSpace(revision.Title), true,
                "A bounded human-readable title is required.", "schema.title"),
            Check("schema.blocks", revision.Blocks.ValueKind == JsonValueKind.Array && ValidBlocks(revision.Blocks), true,
                "Content blocks require stable IDs and registered block shapes.", "schema.blocks"),
            new("schema.applicability", HasProperties(revision.Applicability) ? "pass" : "unknown", true,
                "Applicability must be explicit, including an explicit unrestricted declaration.", [],
                "Declare applicability or explicitly record unrestricted scope."),
            new("content.evidence", HasEvidence(revision) ? "pass" : "unknown", false,
                "Key conclusions require traceable sources or evidence; missing evidence is unknown, not a verified negative.", [],
                "Add precise source revisions and locators for each key conclusion."),
            new("governance.humanApproval", "not_applicable", true,
                "Automated content checks never constitute human approval or verified business truth.", [])
        };

        if (revision.Kind is "BusinessRule" or "Rule") CheckRule(revision, checks);
        if (revision.Kind is "Feature" or "BusinessRequirement" or "Requirement") CheckRequirement(revision, checks);
        if (revision.Kind is "Component" or "API") CheckTechnicalProfile(revision, checks);
        CheckFactSeparation(revision, checks);
        CheckDuplicateBlocks(revision, checks);
        CheckEvidenceConflict(revision, checks);

        var unknowns = checks.Where(check => check.Status is "fail" or "unknown" or "contradictory")
            .Select(check => new Unknown(check.Key, check.Reason,
                check.Status == "fail" ? "blocking" : "warning",
                check.Status switch { "fail" => "missing", "contradictory" => "conflicting", _ => "unverified" },
                check.NextAction)).ToArray();
        return new(checks, unknowns, false);
    }

    private static void CheckRule(RevisionInput revision, List<Check> checks)
    {
        var body = revision.Body;
        foreach (var field in new[] { "purpose", "evaluationMode", "inputs", "priorityPolicy", "rows", "exceptionExplanation", "caseRefs" })
            checks.Add(Check($"profile.rule.{field}", Has(body, field), true,
                $"Rule profile requires '{field}'.", $"body.{field}"));
        if (!body.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return;

        var rowIds = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var location = $"body.rows[{index}]";
            var rowId = Text(row, "rowId");
            checks.Add(Check($"profile.rule.rows[{index}].identity", rowId is not null && rowIds.Add(rowId), true,
                "Every decision row requires a non-empty, unique stable rowId.", $"{location}.rowId"));
            foreach (var field in new[] { "situation", "comparisonExplanation", "outcome", "evidenceRefs" })
                checks.Add(Check($"profile.rule.rows[{index}].{field}", Has(row, field), true,
                    $"Decision row requires '{field}'; non-empty prose alone does not supply its semantics.", $"{location}.{field}"));

            var comparison = Text(row, "comparisonExplanation");
            var situation = Text(row, "situation");
            var outcome = Text(row, "outcome.description");
            var text = string.Join(' ', new[] { situation, comparison, outcome }.Where(value => value is not null));
            var lacksBasis = (text.Contains("一致则应用", StringComparison.Ordinal) || text.Contains("相同则应用", StringComparison.Ordinal) ||
                              text.Contains("if consistent", StringComparison.OrdinalIgnoreCase) || text.Contains("if same", StringComparison.OrdinalIgnoreCase)) &&
                             string.IsNullOrWhiteSpace(comparison) || IsVagueComparison(comparison);
            checks.Add(lacksBasis
                ? Issue($"semantic.rule.rows[{index}].comparisonBasis", "fail",
                    "The outcome depends on consistency or equality, but the compared inputs, operator and normalization are not defined.",
                    $"{location}.comparisonExplanation", "Name both operands and define the comparison, including case, null and missing semantics.")
                : Check($"semantic.rule.rows[{index}].comparisonBasis", !string.IsNullOrWhiteSpace(comparison), true,
                    "A decision must explain its comparison basis rather than only saying 'apply when consistent'.", $"{location}.comparisonExplanation"));

            if (Text(body, "evaluationMode") == "structured")
                checks.Add(Check($"profile.rule.rows[{index}].when", Has(row, "when"), true,
                    "Structured rules require an executable condition AST for every row.", $"{location}.when"));
            if (Text(body, "priorityPolicy") == "ordered")
                checks.Add(Check($"profile.rule.rows[{index}].priority", Has(row, "priority"), true,
                    "Ordered rules require an explicit priority on every row.", $"{location}.priority"));
            index++;
        }
        if (Text(body, "priorityPolicy") == "collect")
            checks.Add(Check("profile.rule.collectOperator", Has(body, "collectOperator"), true,
                "Collect priority requires an approved merge operator.", "body.collectOperator"));
        checks.Add(Check("profile.rule.defaultOutcome", Has(body, "defaultOutcome"), true,
            "Rules require an explicit default outcome; missing input must not be treated as false.", "body.defaultOutcome"));
    }

    private static void CheckRequirement(RevisionInput revision, List<Check> checks)
    {
        foreach (var field in new[] { "purpose", "scope", "successCriteria" })
            checks.Add(Check($"profile.requirement.{field}", Has(revision.Body, field), true,
                $"Business requirements require '{field}' separate from implementation mechanics.", $"body.{field}"));
        var allText = revision.Body.GetRawText();
        var mixesMechanism = ContainsAny(allText, "SELECT ", "INSERT INTO", "public class ", "async Task<", "CREATE TABLE");
        checks.Add(mixesMechanism
            ? Issue("semantic.requirement.mechanismMixed", "unknown",
                "The requirement appears to contain implementation code or SQL; business outcome and mechanism need separate review.",
                "body", "Move implementation detail to a design or component while preserving the business rule.")
            : new("semantic.requirement.mechanismMixed", "pass", false,
                "No obvious source code or SQL was found in the business requirement profile.", []));
    }

    private static void CheckTechnicalProfile(RevisionInput revision, List<Check> checks)
    {
        checks.Add(Check("profile.technical.responsibility", HasAny(revision.Body, "responsibility", "purpose"), true,
            "Components and APIs require a clear responsibility.", "body.responsibility"));
        if (revision.Kind == "API")
        {
            checks.Add(Check("profile.api.contract", HasAny(revision.Body, "operationId", "contract", "request"), true,
                "APIs require an invocable contract and inputs.", "body.contract"));
            checks.Add(Check("profile.api.errors", HasAny(revision.Body, "errors", "errorResponses", "exceptionExplanation"), true,
                "APIs require explicit error behavior.", "body.errors"));
        }
        else
            checks.Add(Check("profile.component.dependencies", HasAny(revision.Body, "dependencies", "dependsOn"), false,
                "Component dependencies should be explicit when present; absence remains reviewable.", "body.dependencies"));
    }

    private static void CheckFactSeparation(RevisionInput revision, List<Check> checks)
    {
        if (revision.FactKind == "observed" && revision.Kind is "BusinessRule" or "Rule")
            checks.Add(Issue("semantic.factKind.observedRule", "fail",
                "An actual observation cannot be published as a normative business rule.", "factKind",
                "Keep the observation separate and obtain human approval for any proposed rule."));
        if (revision.FactKind is "inferred" or "assumption")
            checks.Add(Issue("semantic.factKind.unapprovedInference", "unknown",
                "Inferences and assumptions are candidates, not verified or approved business facts.", "factKind",
                "Resolve the candidate with evidence and the required human decision."));
        if (revision.Verification is "verified" && !HasEvidence(revision))
            checks.Add(Issue("semantic.verification.unsupported", "contradictory",
                "The revision claims verified status without supporting evidence.", "verification",
                "Attach evidence or change verification to unverified/insufficient."));
    }

    private static void CheckDuplicateBlocks(RevisionInput revision, List<Check> checks)
    {
        if (revision.Blocks.ValueKind != JsonValueKind.Array) return;
        var duplicates = revision.Blocks.EnumerateArray().Select(block => Text(block, "text"))
            .Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text!.Trim())
            .GroupBy(text => text, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToArray();
        if (duplicates.Length > 0)
            checks.Add(Issue("semantic.blocks.duplicate", "unknown",
                "Repeated block text may duplicate a condition, judgment or summary instead of adding information.", "blocks",
                "Keep one semantic statement and link repeated uses rather than stacking repeated content."));
    }

    private static void CheckEvidenceConflict(RevisionInput revision, List<Check> checks)
    {
        if (revision.Verification == "contradicted")
            checks.Add(Issue("semantic.evidence.conflict", "contradictory",
                "The revision is marked contradicted; conflicting evidence must not be collapsed into a verified conclusion.", "verification",
                "Resolve the evidence conflict or retain an explicit unknown."));
        if (revision.Evidence.ValueKind == JsonValueKind.Array && revision.Evidence.EnumerateArray().Any(item => Text(item, "support") == "refutes"))
            checks.Add(Issue("semantic.evidence.refuting", "contradictory",
                "Refuting evidence is attached and requires an explicit conclusion, not silent agreement.", "evidence",
                "Reconcile supporting and refuting evidence and retain both in the explanation."));
    }

    private static bool IsVagueComparison(string? comparison) => string.IsNullOrWhiteSpace(comparison) ||
        comparison.Trim() is "一致" or "相同" or "consistent" or "same" or "一致则应用" or "按一致性判断";

    private static bool HasEvidence(RevisionInput revision) => revision.Evidence.ValueKind == JsonValueKind.Array && revision.Evidence.GetArrayLength() > 0;
    private static bool HasProperties(JsonElement value) => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any();
    private static bool HasAny(JsonElement value, params string[] paths) => paths.Any(path => Has(value, path));
    private static bool Has(JsonElement value, string path)
    {
        foreach (var part in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) return false;
        }
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => value.GetArrayLength() > 0,
            _ => true
        };
    }
    private static string? Text(JsonElement value, string path)
    {
        foreach (var part in path.Split('.'))
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private static bool ContainsAny(string text, params string[] candidates) => candidates.Any(candidate => text.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    private static bool ValidBlocks(JsonElement blocks) => blocks.EnumerateArray().All(block => block.ValueKind == JsonValueKind.Object &&
        block.TryGetProperty("blockId", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()) &&
        block.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() is "paragraph" or "table" or "list" or "notice");
    private static Check Check(string key, bool valid, bool critical, string reason, string location) => valid
        ? new(key, "pass", critical, reason, [])
        : Issue(key, "fail", reason, location, "Supply the required semantic content or record an explicit unknown before approval.");
    private static Check Issue(string key, string status, string reason, string location, string nextAction) =>
        new(key, status, true, reason, [JsonSerializer.SerializeToElement(new { location })], nextAction);
}
