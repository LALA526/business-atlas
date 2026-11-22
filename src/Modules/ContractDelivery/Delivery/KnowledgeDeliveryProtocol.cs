using System.Text.Json;
using System.Text.RegularExpressions;
using ProjectIdentity.Application;

namespace ContractDelivery.Delivery;

/// <summary>Fail-closed validation for the two wire versions shared with the Git CI scripts.</summary>
public static partial class KnowledgeDeliveryProtocol
{
    public static void ValidateProposal(JsonElement proposal)
    {
        UniqueKeys(proposal, "proposal");
        var version = RequiredString(proposal, "schemaVersion");
        if (version is not ("1.2.0" or "1.3.0")) throw Invalid("Only proposal schema 1.2.0 or 1.3.0 is supported.");
        Object(proposal, "proposal", ["schemaVersion", "proposalId", "projectId", "taskId", "requirementIds", "changeKind", "repo", "changedFiles", "entityChanges", "assertionChanges", "apiChanges", "dataChanges", "testEvidence", "sourceAnchors", "knowledgeStatus", "unknowns", "errorsObserved"], ["contractDigests"]);
        RequiredString(proposal, "proposalId"); RequiredString(proposal, "projectId"); RequiredString(proposal, "taskId");
        Enum(proposal, "changeKind", "feature", "fix", "refactor", "migration", "security", "documentation", "operations");
        if (RequiredString(proposal, "knowledgeStatus") != "proposed") throw Invalid("A knowledge proposal cannot claim approval or publication.");
        var requirements = RequiredArray(proposal, "requirementIds");
        if (requirements.GetArrayLength() == 0) throw Invalid("At least one requirement ID is required.");
        var requirementIds = requirements.EnumerateArray().Select(item => String(item, "requirementId")).ToArray();
        if (requirementIds.Distinct().Count() != requirementIds.Length || requirementIds.Any(item => !RequirementId().IsMatch(item) || version == "1.2.0" && item.StartsWith("PD-", StringComparison.Ordinal))) throw Invalid("Requirement IDs are invalid or duplicated.");
        var repo = RequiredObject(proposal, "repo");
        Object(repo, "repo", ["url", "branch", "pullRequestId"], version == "1.2.0" ? ["baseSha", "headSha"] : ["baseSha"]);
        RequiredString(repo, "url"); RequiredString(repo, "branch"); RequiredString(repo, "pullRequestId");
        if (repo.TryGetProperty("baseSha", out var baseSha)) Match(String(baseSha, "baseSha"), GitSha(), "baseSha");
        if (version == "1.2.0" && repo.TryGetProperty("headSha", out var headSha)) Match(String(headSha, "headSha"), GitSha(), "headSha");
        var changes = RequiredArray(proposal, "changedFiles");
        if (changes.GetArrayLength() == 0) throw Invalid("changedFiles must describe the actual candidate diff.");
        StringArray(proposal, "unknowns"); StringArray(proposal, "contractDigests", false);
        foreach (var change in changes.EnumerateArray())
        {
            Object(change, "changedFile", ["path", "changeType"], ["blobSha", "contentSha256"]);
            RequiredString(change, "path"); Enum(change, "changeType", "added", "modified", "removed", "renamed");
            OptionalDigest(change, "blobSha", GitSha()); OptionalDigest(change, "contentSha256", Sha256());
        }
        foreach (var name in new[] { "entityChanges", "assertionChanges" })
            foreach (var change in RequiredArray(proposal, name).EnumerateArray())
            {
                Object(change, name, ["stableId", "kind", "factLevel", "summary"], ["sourceAnchorIds"]);
                RequiredString(change, "stableId"); RequiredString(change, "summary"); Enum(change, "kind", "added", "modified", "removed");
                Enum(change, "factLevel", "observed", "documented_intent", "inferred", "unknown", "conflicting"); StringArray(change, "sourceAnchorIds", false);
            }
        foreach (var change in RequiredArray(proposal, "apiChanges").EnumerateArray())
        {
            Object(change, "apiChange", ["operationId", "kind", "contractPath"], ["authorizationPolicy"]);
            RequiredString(change, "operationId"); RequiredString(change, "contractPath"); Enum(change, "kind", "added", "modified", "removed"); OptionalString(change, "authorizationPolicy");
        }
        foreach (var change in RequiredArray(proposal, "dataChanges").EnumerateArray())
        {
            Object(change, "dataChange", ["migrationId", "description"], []); RequiredString(change, "migrationId"); RequiredString(change, "description");
        }
        TestEvidence(RequiredArray(proposal, "testEvidence"), true);
        var anchors = RequiredArray(proposal, "sourceAnchors");
        if (anchors.GetArrayLength() == 0) throw Invalid("At least one source anchor is required.");
        foreach (var anchor in anchors.EnumerateArray())
        {
            Object(anchor, "sourceAnchor", ["path", "locator"], ["blobSha", "contentSha256", "sourceRevision"]);
            RequiredString(anchor, "path"); RequiredString(anchor, "locator"); OptionalString(anchor, "sourceRevision"); OptionalDigest(anchor, "blobSha", GitSha()); OptionalDigest(anchor, "contentSha256", Sha256());
        }
        foreach (var error in RequiredArray(proposal, "errorsObserved").EnumerateArray())
        {
            Object(error, "errorObserved", ["patternId", "symptom", "preventionTestId"], []); RequiredString(error, "patternId"); RequiredString(error, "symptom"); RequiredString(error, "preventionTestId");
        }
    }

    public static void ValidateManifest(JsonElement manifest)
    {
        UniqueKeys(manifest, "manifest");
        var version = RequiredString(manifest, "schemaVersion");
        if (version is not ("1.2.0" or "1.3.0")) throw Invalid("Only manifest schema 1.2.0 or 1.3.0 is supported.");
        Object(manifest, "manifest", ["schemaVersion", "deliveryId", "proposalId", "proposalSha256", "projectId", "taskId", "repo", "finalCommitSha", "mergeStrategy", "buildId", "buildDigest", "verifiedAt", "testEvidence", "anchorResolutions", "status"], ["artifactUri", "ciRunId", "signerId"]);
        foreach (var name in new[] { "deliveryId", "proposalId", "projectId", "taskId", "buildId" }) RequiredString(manifest, name);
        Match(RequiredString(manifest, "proposalSha256"), Sha256(), "proposalSha256"); Match(RequiredString(manifest, "finalCommitSha"), GitSha(), "finalCommitSha");
        Match(RequiredString(manifest, "buildDigest"), BuildDigest(), "buildDigest"); Enum(manifest, "mergeStrategy", "merge", "squash", "rebase"); Enum(manifest, "status", "verified", "evidence_missing", "rejected");
        if (!DateTimeOffset.TryParse(RequiredString(manifest, "verifiedAt"), out _)) throw Invalid("verifiedAt must be an RFC3339 timestamp.");
        foreach (var name in new[] { "artifactUri", "ciRunId", "signerId" }) OptionalString(manifest, name);
        var repo = RequiredObject(manifest, "repo"); Object(repo, "repo", ["url", "pullRequestId"], []); RequiredString(repo, "url"); RequiredString(repo, "pullRequestId");
        var tests = TestEvidence(RequiredArray(manifest, "testEvidence"), true);
        var status = RequiredString(manifest, "status");
        if (status == "verified")
        {
            if (tests.Any(item => RequiredString(item, "result") != "passed")) throw Invalid("A verified manifest cannot contain failed or skipped evidence.");
            if (tests.Any(item => !item.TryGetProperty("artifactSha256", out _))) throw Invalid("Verified test evidence must include the actual artifact SHA-256.");
        }
        var anchors = RequiredArray(manifest, "anchorResolutions");
        foreach (var anchor in anchors.EnumerateArray())
        {
            Object(anchor, "anchorResolution", ["path", "locator", "resolvedBlobSha", "verification"], ["explanation"]);
            RequiredString(anchor, "path"); RequiredString(anchor, "locator"); Match(RequiredString(anchor, "resolvedBlobSha"), GitSha(), "resolvedBlobSha");
            Enum(anchor, "verification", "matched", "remapped", "missing", "mismatch"); OptionalString(anchor, "explanation");
            if (status == "verified" && RequiredString(anchor, "verification") is "missing" or "mismatch") throw Invalid("Missing or mismatched source anchors cannot be verified.");
        }
    }

    public static void BindManifestToProposal(JsonElement manifest, JsonElement proposal)
    {
        if (RequiredString(manifest, "proposalId") != RequiredString(proposal, "proposalId") || RequiredString(manifest, "schemaVersion") != RequiredString(proposal, "schemaVersion") ||
            RequiredString(manifest.GetProperty("repo"), "url") != RequiredString(proposal.GetProperty("repo"), "url") || RequiredString(manifest.GetProperty("repo"), "pullRequestId") != RequiredString(proposal.GetProperty("repo"), "pullRequestId"))
            throw Invalid("Manifest proposal, protocol, repository and pull request identity must match the original proposal.");
        if (RequiredString(manifest, "status") == "verified")
        {
            var expected = proposal.GetProperty("sourceAnchors").EnumerateArray().Select(item => (RequiredString(item, "path"), RequiredString(item, "locator"))).ToHashSet();
            var actual = manifest.GetProperty("anchorResolutions").EnumerateArray().Select(item => (RequiredString(item, "path"), RequiredString(item, "locator"))).ToHashSet();
            if (actual.Count != manifest.GetProperty("anchorResolutions").GetArrayLength() || !expected.SetEquals(actual)) throw Invalid("Verified anchor resolutions must exactly match proposal source anchors.");
        }
    }

    private static List<JsonElement> TestEvidence(JsonElement tests, bool required)
    {
        if (required && tests.GetArrayLength() == 0) throw Invalid("At least one test evidence item is required.");
        var result = tests.EnumerateArray().ToList();
        foreach (var test in result)
        {
            Object(test, "testEvidence", ["testId", "result", "artifactUri"], ["artifactSha256"]); RequiredString(test, "testId"); RequiredString(test, "artifactUri");
            Enum(test, "result", "passed", "failed", "skipped"); OptionalDigest(test, "artifactSha256", Sha256());
        }
        return result;
    }

    private static void UniqueKeys(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid($"Duplicate JSON property {path}/{property.Name} is not allowed.");
                UniqueKeys(property.Value, $"{path}/{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            for (var index = 0; index < element.GetArrayLength(); index++) UniqueKeys(element[index], $"{path}/{index}");
    }

    private static void Object(JsonElement value, string name, string[] required, string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid($"{name} must be an object.");
        var allowed = required.Concat(optional).ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!allowed.Contains(property.Name)) throw Invalid($"Unknown {name} property: {property.Name}.");
        foreach (var property in required) if (!value.TryGetProperty(property, out _)) throw Invalid($"{name}.{property} is required.");
    }
    private static JsonElement RequiredObject(JsonElement value, string name) { if (!value.TryGetProperty(name, out var result) || result.ValueKind != JsonValueKind.Object) throw Invalid($"{name} must be an object."); return result; }
    private static JsonElement RequiredArray(JsonElement value, string name) { if (!value.TryGetProperty(name, out var result) || result.ValueKind != JsonValueKind.Array) throw Invalid($"{name} must be an array."); return result; }
    private static string RequiredString(JsonElement value, string name) { if (!value.TryGetProperty(name, out var result)) throw Invalid($"{name} is required."); return String(result, name); }
    private static void OptionalString(JsonElement value, string name) { if (value.TryGetProperty(name, out var result)) String(result, name); }
    private static string String(JsonElement value, string name) => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw Invalid($"{name} must be a non-empty string.");
    private static void StringArray(JsonElement value, string name, bool required = true) { if (!value.TryGetProperty(name, out var result)) { if (required) throw Invalid($"{name} is required."); return; } if (result.ValueKind != JsonValueKind.Array) throw Invalid($"{name} must be an array."); foreach (var item in result.EnumerateArray()) String(item, name); }
    private static void Enum(JsonElement value, string name, params string[] allowed) { if (!allowed.Contains(RequiredString(value, name))) throw Invalid($"{name} has an unsupported value."); }
    private static void OptionalDigest(JsonElement value, string name, Regex pattern) { if (value.TryGetProperty(name, out var digest)) Match(String(digest, name), pattern, name); }
    private static void Match(string value, Regex pattern, string name) { if (!pattern.IsMatch(value)) throw Invalid($"{name} has an invalid digest or identifier."); }
    private static ProjectIdentityException Invalid(string message) => ProjectIdentityException.Invalid(message);

    [GeneratedRegex("^[0-9a-f]{40,64}$")] private static partial Regex GitSha();
    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex Sha256();
    [GeneratedRegex("^(sha256:)?[0-9a-f]{64}$")] private static partial Regex BuildDigest();
    [GeneratedRegex("^((BA|ENG)-[A-Z]+-[0-9]{3}|PD-[0-9]{2})$")] private static partial Regex RequirementId();
}
