using System.Text.Json;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;
using TaskCollab.Contracts;

namespace TaskCollab.Investigation;

/// <summary>
/// Validates the provenance that distinguishes an executed inspection from a model's
/// reconstruction. Persistence and project authorization remain the store's responsibility.
/// </summary>
public static class InvestigationEvidence
{
    private static readonly HashSet<string> CaptureKinds = ["rawCaptured", "verifiedScript", "reconstructed", "proposed", "humanAttestation"];
    private static readonly HashSet<string> LocatorKinds = ["file", "git", "url", "test", "human", "document"];
    private static readonly HashSet<string> Support = ["supports", "refutes", "contextual"];

    public static bool IsExecuted(string captureKind) => captureKind is "rawCaptured" or "verifiedScript" or "humanAttestation";

    public static void Validate(AtlasPrincipal actor, InvestigationInput request)
    {
        if (request.TaskId == Guid.Empty || request.BaselineId == Guid.Empty || string.IsNullOrWhiteSpace(request.Coverage) ||
            request.Methods.Count == 0 || request.Methods.Any(string.IsNullOrWhiteSpace) || !CaptureKinds.Contains(request.CaptureKind))
            throw ProjectIdentityException.Invalid("Investigation task, baseline, methods, coverage and captureKind are required.");
        if (request.CommitSha is not null && (request.CommitSha.Length is < 40 or > 64 || request.CommitSha.Any(c => !Uri.IsHexDigit(c) || char.IsUpper(c))))
            throw ProjectIdentityException.Invalid("commitSha must be lowercase hexadecimal.");
        if (request.SearchedPaths.Any(string.IsNullOrWhiteSpace) || request.Exclusions.Any(string.IsNullOrWhiteSpace))
            throw ProjectIdentityException.Invalid("Investigation paths and exclusions cannot be blank.");
        if (!string.IsNullOrWhiteSpace(request.CommitSha) && string.IsNullOrWhiteSpace(request.Repo))
            throw ProjectIdentityException.Invalid("A repository is required when commitSha is supplied.");
        if (request.CaptureKind == "humanAttestation" && actor.Kind != PrincipalKind.Human)
            throw ProjectIdentityException.Invalid("A human attestation must be submitted by its human author.");
        if (request.CaptureKind is "rawCaptured" or "verifiedScript" && !request.RunId.HasValue)
            throw ProjectIdentityException.Invalid("Executed tool evidence must identify its controlled agent run.");

        foreach (var evidence in request.Evidence) ValidateEvidence(request, evidence);
    }

    public static IReadOnlyList<Guid> SourceRevisionIds(IEnumerable<JsonElement> evidence) => evidence
        .Select(item => item.GetProperty("source").GetProperty("revisionId").GetGuid()).Distinct().ToArray();

    public static bool ContainsEvidence(IEnumerable<JsonElement> actual, IEnumerable<JsonElement> claimed) =>
        claimed.All(claim => actual.Any(item => JsonElement.DeepEquals(item, claim)));

    public static bool HasRepositoryScope(string? repo, string? commitSha, IReadOnlyList<string> paths) =>
        !string.IsNullOrWhiteSpace(repo) && !string.IsNullOrWhiteSpace(commitSha) && paths.Count > 0;

    private static void ValidateEvidence(InvestigationInput request, JsonElement evidence)
    {
        if (evidence.ValueKind != JsonValueKind.Object || !evidence.TryGetProperty("source", out var source) ||
            !evidence.TryGetProperty("locator", out var locator) || !evidence.TryGetProperty("support", out var support) ||
            !evidence.TryGetProperty("captureKind", out var capture))
            throw ProjectIdentityException.Invalid("Every evidence item requires source, locator, support and captureKind.");
        if (source.ValueKind != JsonValueKind.Object || locator.ValueKind != JsonValueKind.Object ||
            support.ValueKind != JsonValueKind.String || capture.ValueKind != JsonValueKind.String)
            throw ProjectIdentityException.Invalid("Evidence source and locator must be objects; support and captureKind must be strings.");
        if (GuidProperty(source, "unitId") == Guid.Empty || GuidProperty(source, "revisionId") == Guid.Empty)
            throw ProjectIdentityException.Invalid("Evidence must identify an exact source unit and revision.");
        if (support.GetString() is not { } supportKind || !Support.Contains(supportKind) || capture.GetString() != request.CaptureKind)
            throw ProjectIdentityException.Invalid("Evidence support is invalid and its captureKind must match the investigation.");
        if (!locator.TryGetProperty("kind", out var kindElement) || kindElement.GetString() is not { } locatorKind || !LocatorKinds.Contains(locatorKind))
            throw ProjectIdentityException.Invalid("Evidence locator kind is invalid.");
        var kind = kindElement.GetString();
        var path = StringProperty(locator, "path");
        var description = StringProperty(locator, "description");
        if (kind is "file" or "git" or "document" && string.IsNullOrWhiteSpace(path) ||
            kind == "test" && string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(description) ||
            kind == "url" && string.IsNullOrWhiteSpace(StringProperty(locator, "uri")))
            throw ProjectIdentityException.Invalid("Evidence locator does not identify the inspected artifact.");
        if (kind is "file" or "git" or "test")
        {
            if (!HasRepositoryScope(request.Repo, request.CommitSha, request.SearchedPaths))
                throw ProjectIdentityException.Invalid("Source and test evidence require repository, commit and searched path scope.");
            if (StringProperty(locator, "repo") != request.Repo || StringProperty(locator, "commitSha") != request.CommitSha)
                throw ProjectIdentityException.Invalid("Source evidence must match the investigation repository and commit.");
            if (!string.IsNullOrWhiteSpace(path) && !request.SearchedPaths.Any(scope => PathWithinScope(path, scope)))
                throw ProjectIdentityException.Invalid("Evidence path must be inside the declared searched paths.");
        }
        if (kind == "human" && string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(StringProperty(locator, "quote")))
            throw ProjectIdentityException.Invalid("Human evidence must identify the attested observation.");
    }

    private static bool PathWithinScope(string path, string scope)
    {
        var normalizedPath = path.Replace('\\', '/').TrimStart('/');
        var normalizedScope = scope.Replace('\\', '/').TrimStart('/').TrimEnd('/');
        return normalizedPath.Equals(normalizedScope, StringComparison.Ordinal) ||
               normalizedPath.StartsWith(normalizedScope + "/", StringComparison.Ordinal) ||
               normalizedScope.Contains('*') || normalizedScope.Contains('?');
    }

    private static Guid GuidProperty(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && Guid.TryParse(property.GetString(), out var id) ? id : Guid.Empty;

    private static string? StringProperty(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
