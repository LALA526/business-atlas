using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Atlas.Api.Mcp;

public sealed record AtlasToolDefinition(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Description,
    [property: JsonRequired] string OperationId,
    [property: JsonRequired] string RequiredScope,
    bool ReadOnly,
    bool RequiresIdempotencyKey,
    bool RequiresIfMatch,
    [property: JsonRequired] JsonElement InputSchema,
    [property: JsonRequired] JsonElement OutputSchema);

public sealed class AtlasToolCatalog
{
    private readonly IReadOnlyList<AtlasToolDefinition> definitions;
    public AtlasToolCatalog(IHostEnvironment environment)
    {
        var path = Path.Combine(environment.ContentRootPath, "contracts", "mcp-tools.json");
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "contracts", "mcp-tools.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        definitions = document.RootElement.GetProperty("tools").Deserialize<List<AtlasToolDefinition>>(JsonOptions)
            ?? throw new InvalidDataException("The MCP tool contract is empty.");
        if (definitions.Count != 35 || definitions.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != 35)
            throw new InvalidDataException("The deployed MCP contract must contain exactly 35 uniquely named tools.");
        if (definitions.Any(tool => tool.Name.Contains("approve", StringComparison.OrdinalIgnoreCase) ||
                                    tool.Name.Contains("publish", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Approval and publication are human-controlled API operations, not MCP tools.");
        Tools = definitions.Select(definition => new AtlasMcpTool(definition)).ToArray();
    }

    public IReadOnlyList<AtlasMcpTool> Tools { get; }
    public AtlasToolDefinition Get(string name) => definitions.SingleOrDefault(tool => tool.Name == name)
        ?? throw new InvalidDataException("The requested tool is not registered.");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed class AtlasMcpTool(AtlasToolDefinition definition) : McpServerTool
{
    public AtlasToolDefinition Definition => definition;
    public override Tool ProtocolTool { get; } = new()
    {
        Name = definition.Name,
        Description = definition.Description,
        InputSchema = definition.InputSchema.Clone(),
        OutputSchema = definition.OutputSchema.Clone(),
        Annotations = new ToolAnnotations { ReadOnlyHint = definition.ReadOnly }
    };
    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var dispatcher = (request.Services ?? throw new InvalidOperationException("MCP request services are unavailable.")).GetRequiredService<AtlasMcpDispatcher>();
        return await dispatcher.InvokeAsync(definition, request, cancellationToken);
    }
}
