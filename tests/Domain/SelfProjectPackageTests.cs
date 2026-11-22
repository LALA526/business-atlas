using ImportEvidence.Sources;

namespace Atlas.Domain.Tests;

public sealed class SelfProjectPackageTests
{
    [Theory]
    [InlineData("knowledge/baseline/self-bootstrap.akp.json", "AKP-SELF-95783495c5f7")]
    [InlineData("knowledge/baseline/self-change-T36.akp.json", "AKP-SELF-3400a48f291b-95783495c5f7")]
    public async Task AT_T38_Real_git_packages_pass_the_product_AKP_reader_as_unverified_candidates(string path, string packageId)
    {
        var file = RepositoryFile(path);
        await using var stream = File.OpenRead(file);
        var normalized = await AkpPackageReader.ReadAsync(stream, file, CancellationToken.None);
        Assert.Equal(packageId, normalized.PackageId);
        Assert.Equal("akp:BusinessAtlas-Self", normalized.Namespace);
        Assert.Contains(normalized.Entries, item => item.ExternalId == "T38" && item.Kind == "Milestone" && item.FactKind == "documented_intent");
        Assert.Contains(normalized.Entries, item => item.ExternalId == "PD-33" && item.Kind == "Requirement");
        Assert.Contains(normalized.Entries, item => item.Kind == "Implementation" && item.FactKind == "observed");
        Assert.All(normalized.Entries.Where(item => item.Kind is "Requirement" or "Implementation" or "Observation"), item => Assert.Equal("unverified", item.Verification));
        Assert.All(normalized.Entries.SelectMany(item => item.SourceIds), reference => Assert.Contains(normalized.Entries, item => item.Kind == "Source" && item.ExternalId == reference));
    }

    [Fact]
    public async Task AT_T38_Followup_candidate_contains_the_real_T36_commit_interval()
    {
        var file = RepositoryFile("knowledge/baseline/self-change-T36.akp.json");
        await using var stream = File.OpenRead(file);
        var normalized = await AkpPackageReader.ReadAsync(stream, file, CancellationToken.None);
        var observation = Assert.Single(normalized.Entries, item => item.ExternalId == "SELF-CHANGE-3400a48f291b-95783495c5f7");
        Assert.Equal("observed", observation.FactKind);
        Assert.Equal("3400a48f291b", observation.Original.GetProperty("properties").GetProperty("baseCommitSha").GetString()![..12]);
        Assert.Equal("95783495c5f7", observation.Original.GetProperty("properties").GetProperty("headCommitSha").GetString()![..12]);
    }

    private static string RepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BusinessAtlas.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, relative);
    }
}
