using System.Text.Json;
using ImportEvidence.Sources;

namespace Atlas.Domain.Tests;

public sealed class SelfProjectCasePackageTests
{
    [Fact]
    public async Task Expanded_self_project_package_parses_as_unapproved_traceable_entries()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BusinessAtlas.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var packagePath = Path.Combine(root!.FullName, "knowledge/candidates/AKP-BUSINESS-ATLAS-SELF-2026-11.json");
        await using var stream = File.OpenRead(packagePath);
        var package = await AkpPackageReader.ReadAsync(stream, Path.GetFileName(packagePath), CancellationToken.None);

        Assert.Equal("AKP-BUSINESS-ATLAS-SELF-2026-11", package.PackageId);
        Assert.Equal("akp:BusinessAtlas-Self", package.Namespace);
        Assert.Equal(242, package.Entries.Count);
        Assert.Equal(154, package.Entries.Count(entry => entry.ExternalType == "Requirement"));
        Assert.Equal(44, package.Entries.Count(entry => entry.ExternalType == "Task"));
        Assert.Equal(34, package.Entries.Count(entry => entry.ExternalType == "Source"));
        Assert.All(package.Entries.Where(entry => entry.ExternalType != "Issue"), entry => Assert.NotEqual("verified", entry.Verification));
        Assert.Single(package.Unknowns);
    }
}
