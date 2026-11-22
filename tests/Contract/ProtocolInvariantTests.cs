using System.Text.Json;

namespace Atlas.Contract.Tests;

public sealed class ProtocolInvariantTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void AT_T41_Mcp_contract_keeps_human_governance_and_reauthorization()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "contracts/mcp-tools.json")));
        var root = document.RootElement;
        Assert.Equal("streamable-http", root.GetProperty("transport").GetString());
        Assert.Contains("No direct approval, publish, or arbitrary execution", root.GetProperty("policy").GetString());
        Assert.Contains("reauthorize", root.GetProperty("policy").GetString());
        var names = root.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(names, name => name is "approve" or "publish" or "shell" or "execute");
        Assert.All(root.GetProperty("tools").EnumerateArray(), tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("operationId").GetString()));
            Assert.True(tool.TryGetProperty("inputSchema", out _));
            Assert.True(tool.TryGetProperty("outputSchema", out _));
        });
    }

    [Fact]
    public void AT_T41_Development_pipeline_has_only_build_then_mac8_deploy()
    {
        var pipeline = File.ReadAllText(Path.Combine(Root, "azure-pipelines.yml"));
        Assert.Contains("- stage: Build", pipeline, StringComparison.Ordinal);
        Assert.Contains("- stage: DeployMac8", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("- stage: Knowledge", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("manifest-generation", pipeline, StringComparison.OrdinalIgnoreCase);
        Assert.True(pipeline.IndexOf("- stage: Build", StringComparison.Ordinal) < pipeline.IndexOf("- stage: DeployMac8", StringComparison.Ordinal));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BusinessAtlas.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}
