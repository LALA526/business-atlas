using System.Xml.Linq;

namespace Atlas.Contract.Tests;

public sealed class ArchitectureReferenceTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void BuildingBlocksHasNoBusinessModuleReferences()
    {
        var references = ProjectReferences(Path.Combine(Root, "src/BuildingBlocks/BuildingBlocks.csproj"));
        Assert.Empty(references);
    }

    [Fact]
    public void DomainNeverReferencesAnotherModuleInfrastructure()
    {
        var modules = Path.Combine(Root, "src/Modules");
        if (!Directory.Exists(modules)) return;
        foreach (var project in Directory.EnumerateFiles(modules, "*.csproj", SearchOption.AllDirectories))
        {
            if (!project.Contains($"{Path.DirectorySeparatorChar}Domain{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            foreach (var reference in ProjectReferences(project))
            {
                var normalized = reference.Replace('\\', '/');
                Assert.DoesNotContain("/Infrastructure/", normalized, StringComparison.Ordinal);
                Assert.False(normalized.EndsWith(".Infrastructure.csproj", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void PlatformOpsNeverReferencesDomainImplementations()
    {
        var root = Path.Combine(Root, "src/Modules/PlatformOps");
        if (!Directory.Exists(root)) return;
        foreach (var project in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        foreach (var reference in ProjectReferences(project))
            Assert.DoesNotContain("/Domain/", reference.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public void CrossModuleReferencesUseOnlyContractsOrApplicationPorts()
    {
        var modules = Path.Combine(Root, "src/Modules");
        if (!Directory.Exists(modules)) return;
        foreach (var project in Directory.EnumerateFiles(modules, "*.csproj", SearchOption.AllDirectories))
        {
            var relativeProject = Path.GetRelativePath(modules, project).Split(Path.DirectorySeparatorChar);
            if (relativeProject.Length < 2) continue;
            foreach (var reference in ProjectReferences(project))
            {
                if (!reference.StartsWith(modules + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                var target = Path.GetRelativePath(modules, reference).Split(Path.DirectorySeparatorChar);
                if (target.Length < 2 || target[0] == relativeProject[0]) continue;
                Assert.Contains(target[1], new[] { "Contracts", "Application" });
            }
        }
    }

    [Fact]
    public void EveryProjectUsesLockedCentralPackageVersions()
    {
        var projects = Directory.EnumerateFiles(Root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        foreach (var project in projects)
        {
            var document = XDocument.Load(project);
            Assert.All(document.Descendants("PackageReference"), item => Assert.Null(item.Attribute("Version")));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json")), $"Missing lock file: {project}");
        }
    }

    private static IEnumerable<string> ProjectReferences(string project)
    {
        var directory = Path.GetDirectoryName(project)!;
        return XDocument.Load(project).Descendants("ProjectReference")
            .Select(item => Path.GetFullPath(Path.Combine(directory, item.Attribute("Include")!.Value)));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BusinessAtlas.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}
