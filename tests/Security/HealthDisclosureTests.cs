namespace Atlas.Security.Tests;

public sealed class HealthDisclosureTests
{
    [Fact]
    public void HealthResponsesContainNoEnvironmentOrDependencySecrets()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "apps/Atlas.Api/Program.cs"));
        Assert.DoesNotContain("GetEnvironmentVariable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStrings", source, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BusinessAtlas.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}
