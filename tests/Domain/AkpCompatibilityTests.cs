using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ImportEvidence.Sources;
using ProjectIdentity.Application;

namespace Atlas.Domain.Tests;

public sealed class AkpCompatibilityTests
{
    [Theory]
    [InlineData("./fixtures/import-smoke.akp.json", "AKP-SMOKE-V94", 2, 0)]
    [InlineData("./packages/business-atlas-self.akp.json", "AKP-BUSINESS-ATLAS-SELF-001", 166, 6)]
    public async Task AT_T16_Real_V94_packages_preserve_sources_relations_and_unknowns(string archivePath, string packageId, int entries, int unknowns)
    {
        await using var stream = OpenArchivedPackage(archivePath);
        var result = await AkpPackageReader.ReadAsync(stream, "legacy.akp.json", CancellationToken.None);
        Assert.Equal(packageId, result.PackageId);
        Assert.Equal(entries, result.Entries.Count);
        Assert.Equal(unknowns, result.Unknowns.Count);
        Assert.All(result.Entries.Where(e => e.FactKind == "observed"), e => Assert.Equal("unverified", e.Verification));
        Assert.DoesNotContain(result.Entries, e => e.ExternalId.StartsWith("businessStructures:constraints:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AT_T16_Native_proposal_stays_candidate_and_validates_source_references()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = "AtlasProposal/1.0", packageId = "NATIVE-T16", sourceNamespace = "self:test",
            entries = new object[]
            {
                new { externalId = "SOURCE-1", externalType = "Source", title = "Actual contract", kind = "Source", factKind = "observed", verification = "unverified", applicability = new { }, body = new { locator = new { path = "contracts/akp/atlas-proposal-v1.0.schema.json" } }, categoryKeys = Array.Empty<string>() },
                new { externalId = "RULE-1", externalType = "BusinessRule", title = "Candidate only", kind = "BusinessRule", factKind = "documented_intent", verification = "unverified", applicability = new { }, body = new { }, categoryKeys = new[] { "business.rules" }, sourceIds = new[] { "SOURCE-1" } }
            }
        });
        await using var stream = new MemoryStream(bytes);
        var result = await AkpPackageReader.ReadAsync(stream, "native.json", CancellationToken.None);
        Assert.Equal("AtlasProposal/1.0", result.FormatVersion);
        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.Equal("unverified", e.Verification));
    }

    [Fact]
    public async Task AT_T16_Damaged_or_unresolved_packages_are_rejected()
    {
        await using var damaged = new MemoryStream(Encoding.UTF8.GetBytes("{not-json"));
        await Assert.ThrowsAsync<ProjectIdentityException>(() => AkpPackageReader.ReadAsync(damaged, "broken.json", CancellationToken.None));

        await using var native = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = "AtlasProposal/1.0", packageId = "NATIVE-BAD", sourceNamespace = "test", entries = new[] { new { externalId = "A", externalType = "Assertion", title = "A", kind = "Assertion", factKind = "observed", verification = "verified", applicability = new { }, body = new { }, categoryKeys = Array.Empty<string>() } } }));
        await Assert.ThrowsAsync<ProjectIdentityException>(() => AkpPackageReader.ReadAsync(native, "bad.json", CancellationToken.None));
    }

    private static MemoryStream OpenArchivedPackage(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BusinessAtlas.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        using var archive = File.OpenRead(Path.Combine(directory!.FullName, "docs/ui-reference/working-v9.4.tar.gz"));
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.Name != path) continue;
            var output = new MemoryStream(); entry.DataStream!.CopyTo(output); output.Position = 0; return output;
        }
        throw new FileNotFoundException(path);
    }
}
