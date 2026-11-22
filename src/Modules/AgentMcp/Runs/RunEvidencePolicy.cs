using System.Text.Json;
using AgentMcp.Contracts;
using ProjectIdentity.Application;
using ProjectIdentity.Contracts;

namespace AgentMcp.Runs;

/// <summary>Separates observed tool output from model narrative and prevents privileged outcomes.</summary>
public static class RunEvidencePolicy
{
    private static readonly HashSet<string> CaptureKinds = ["rawCaptured", "verifiedScript", "reconstructed", "proposed", "humanAttestation"];

    public static void ValidateEvent(AtlasPrincipal actor, RunEventView request)
    {
        if (string.IsNullOrWhiteSpace(request.EventId) || request.EventId.Length > 200 || request.Seq < 0 ||
            string.IsNullOrWhiteSpace(request.EventType) || request.EventType.Length > 200 || !CaptureKinds.Contains(request.CaptureKind) ||
            request.Data.ValueKind != JsonValueKind.Object || request.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(5))
            throw ProjectIdentityException.Invalid("Run event identity, sequence, type, capture kind, object data and timestamp are required.");
        RejectSecrets(request.Data);
        if (request.ArtifactSha256 is not null && (request.ArtifactSha256.Length != 64 || request.ArtifactSha256.Any(c => !Uri.IsHexDigit(c) || char.IsUpper(c))))
            throw ProjectIdentityException.Invalid("artifactSha256 must be 64 lowercase hexadecimal characters.");
        if (request.CaptureKind is "rawCaptured" or "verifiedScript")
        {
            if (actor.Kind != PrincipalKind.Service)
                throw ProjectIdentityException.Forbidden("Raw tool evidence must be captured by an authenticated controlled runner or CI identity, not asserted by a model.");
            if (request.ArtifactSha256 is null || StringProperty(request.Data, "tool") is not { Length: > 0 } ||
                !request.Data.TryGetProperty("exitCode", out var exitCode) || exitCode.ValueKind != JsonValueKind.Number)
                throw ProjectIdentityException.Invalid("Raw tool evidence requires a tool, numeric exitCode and artifact hash.");
        }
        if (request.CaptureKind == "humanAttestation" && actor.Kind != PrincipalKind.Human)
            throw ProjectIdentityException.Forbidden("Only its human author can submit a human attestation.");
    }

    public static void ValidateResult(JsonElement resultRef)
    {
        if (resultRef.ValueKind != JsonValueKind.Object) throw ProjectIdentityException.Invalid("resultRef must be an object.");
        RejectSecrets(resultRef);
        RejectPrivilegedOutcome(resultRef);
    }

    private static void RejectSecrets(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name.Equals("environment", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Contains("connectionstring", StringComparison.OrdinalIgnoreCase))
                    throw ProjectIdentityException.Invalid("Run evidence and results must not contain credentials, cookies, connection strings or process environment.");
                RejectSecrets(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectSecrets(item);
    }

    private static void RejectPrivilegedOutcome(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "approved" or "published" or "approvalGranted" or "autoApproved" &&
                    property.Value.ValueKind == JsonValueKind.True)
                    throw ProjectIdentityException.Forbidden("Agent work results cannot approve or publish knowledge.");
                if (property.Name is "action" or "operation" && property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is "approve" or "publish" or "approveAndPublish")
                    throw ProjectIdentityException.Forbidden("Agent work results cannot invoke approval or publication.");
                RejectPrivilegedOutcome(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectPrivilegedOutcome(item);
    }

    private static string? StringProperty(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
