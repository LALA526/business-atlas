using System.Text.Json;
using ContractDelivery.Delivery;
using ProjectIdentity.Application;

namespace Atlas.Domain.Tests;

public sealed class KnowledgeDeliveryProtocolTests
{
    [Fact]
    public void AT_T31_Valid_v12_and_v13_protocols_are_accepted()
    {
        KnowledgeDeliveryProtocol.ValidateProposal(Proposal());
        KnowledgeDeliveryProtocol.ValidateManifest(Manifest());
        KnowledgeDeliveryProtocol.BindManifestToProposal(Manifest(), Proposal());
        var legacyProposal = Proposal().GetRawText().Replace("1.3.0", "1.2.0").Replace("PD-32", "ENG-DEL-006");
        KnowledgeDeliveryProtocol.ValidateProposal(JsonDocument.Parse(legacyProposal).RootElement);
        var legacyManifest = Manifest().GetRawText().Replace("1.3.0", "1.2.0");
        KnowledgeDeliveryProtocol.ValidateManifest(JsonDocument.Parse(legacyManifest).RootElement);
    }

    [Theory]
    [InlineData("\"finalCommitSha\":\"not-a-sha\"", "finalCommitSha")]
    [InlineData("\"status\":\"verified\",\"proposalId\":\"forged\"", "proposalId")]
    public void AT_T31_Forged_or_malformed_manifest_is_rejected(string _, string property)
    {
        var manifest = JsonDocument.Parse(Manifest().GetRawText()).RootElement;
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var item in manifest.EnumerateObject())
            {
                if (item.Name == property) writer.WriteString(property, property == "finalCommitSha" ? "not-a-sha" : "forged");
                else item.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        var forged = JsonDocument.Parse(stream.ToArray()).RootElement;
        if (property == "finalCommitSha") Assert.Throws<ProjectIdentityException>(() => KnowledgeDeliveryProtocol.ValidateManifest(forged));
        else Assert.Throws<ProjectIdentityException>(() => KnowledgeDeliveryProtocol.BindManifestToProposal(forged, Proposal()));
    }

    [Fact]
    public void AT_T31_Duplicate_JSON_fields_and_unverified_evidence_are_rejected()
    {
        var raw = Manifest().GetRawText();
        var duplicate = raw.Insert(raw.LastIndexOf('}'), ",\"buildId\":\"another-build\"");
        Assert.Throws<ProjectIdentityException>(() => KnowledgeDeliveryProtocol.ValidateManifest(JsonDocument.Parse(duplicate).RootElement));
        var failed = raw.Replace("\"passed\"", "\"failed\"");
        Assert.Throws<ProjectIdentityException>(() => KnowledgeDeliveryProtocol.ValidateManifest(JsonDocument.Parse(failed).RootElement));
    }

    private static JsonElement Proposal() => JsonSerializer.SerializeToElement(new
    {
        schemaVersion = "1.3.0", proposalId = "KCP-T31", projectId = Guid.NewGuid(), taskId = Guid.NewGuid(), requirementIds = new[] { "PD-32" }, changeKind = "feature",
        repo = new { url = "https://example.invalid/repo", branch = "task/T31", pullRequestId = "31", baseSha = new string('a', 40) },
        changedFiles = new[] { new { path = "src/a.cs", changeType = "modified", contentSha256 = new string('b', 64) } },
        entityChanges = Array.Empty<object>(), assertionChanges = Array.Empty<object>(), apiChanges = Array.Empty<object>(), dataChanges = Array.Empty<object>(),
        testEvidence = new[] { new { testId = "AT-T31", result = "passed", artifactUri = "ci://candidate" } },
        sourceAnchors = new[] { new { path = "src/a.cs", locator = "class", contentSha256 = new string('b', 64) } }, knowledgeStatus = "proposed", unknowns = Array.Empty<string>(), errorsObserved = Array.Empty<object>()
    }).Clone();

    private static JsonElement Manifest() => JsonSerializer.SerializeToElement(new
    {
        schemaVersion = "1.3.0", deliveryId = "D-T31", proposalId = "KCP-T31", proposalSha256 = new string('c', 64), projectId = Guid.NewGuid(), taskId = Guid.NewGuid(),
        repo = new { url = "https://example.invalid/repo", pullRequestId = "31" }, finalCommitSha = new string('d', 40), mergeStrategy = "squash", buildId = "CI-31", buildDigest = new string('e', 64),
        verifiedAt = DateTimeOffset.UtcNow, testEvidence = new[] { new { testId = "AT-T31", result = "passed", artifactUri = "ci://candidate", artifactSha256 = new string('e', 64) } },
        anchorResolutions = new[] { new { path = "src/a.cs", locator = "class", resolvedBlobSha = new string('b', 64), verification = "matched" } }, status = "verified"
    }).Clone();
}
