using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Atlas.Api.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Npgsql;

namespace Atlas.Integration.Tests;

public sealed class McpProtocolTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Issuer = "https://identity.test";
    private const string Audience = "business-atlas-api";
    private static readonly SymmetricSecurityKey SigningKey = new("atlas-mcp-integration-test-signing-key-0123456789"u8.ToArray());
    private readonly WebApplicationFactory<Program> factory;
    public McpProtocolTests(WebApplicationFactory<Program> factory) => this.factory = factory;

    [Fact]
    public async Task AT_T30_Real_client_handshake_lists_contract_and_rejects_tool_scope()
    {
        using var host = Host(null);
        await using var client = await Client(host, "atlas.read");
        var tools = await client.ListToolsAsync();
        Assert.Equal(35, tools.Count);
        Assert.DoesNotContain(tools, tool => tool.Name.Contains("approve", StringComparison.OrdinalIgnoreCase) || tool.Name.Contains("publish", StringComparison.OrdinalIgnoreCase));
        var templates = await client.ListResourceTemplatesAsync();
        Assert.Contains(templates, resource => resource.UriTemplate.StartsWith("atlas://projects/", StringComparison.Ordinal));
        var invalid = await client.CallToolAsync("knowledge.search", new Dictionary<string, object?> { ["projectId"] = Guid.NewGuid() });
        Assert.True(invalid.IsError);
        Assert.Equal("VALIDATION_FAILED", invalid.StructuredContent?.GetProperty("code").GetString());
        var denied = await client.CallToolAsync("work.list", new Dictionary<string, object?> { ["projectId"] = Guid.NewGuid() });
        Assert.True(denied.IsError);
        Assert.Contains("atlas.agent", denied.StructuredContent?.GetProperty("message").GetString());
    }

    [PostgreSqlFact]
    public async Task AT_T30_Real_client_projects_list_uses_application_service()
    {
        var administrator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ATLAS_TEST_CONNECTIONSTRING"));
        var database = $"atlas_t30_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(administrator.ConnectionString))
        { await connection.OpenAsync(); await Execute(connection, $"CREATE DATABASE {database}"); }
        administrator.Database = database;
        try
        {
            var options = new DbContextOptionsBuilder<BuildingBlocks.Persistence.AtlasDbContext>().UseNpgsql(administrator.ConnectionString).Options;
            await using (var context = new BuildingBlocks.Persistence.AtlasDbContext(options)) await context.Database.MigrateAsync();
            var runtime = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Options = "-c role=atlas_app" }.ConnectionString;
            using var host = Host(runtime);
            await using var client = await Client(host, "atlas.read");
            var result = await client.CallToolAsync("projects.list", new Dictionary<string, object?> { ["limit"] = 10 });
            Assert.True(result.IsError != true, result.StructuredContent?.GetRawText() ?? string.Join("\n", result.Content.OfType<TextContentBlock>().Select(item => item.Text)));
            Assert.Equal("OK", result.StructuredContent?.GetProperty("code").GetString());
            Assert.Empty(result.StructuredContent!.Value.GetProperty("data").GetProperty("items").EnumerateArray());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools(); administrator.Database = "postgres";
            await using var connection = new NpgsqlConnection(administrator.ConnectionString); await connection.OpenAsync(); await Execute(connection, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
    }

    private WebApplicationFactory<Program> Host(string? connection) => factory.WithWebHostBuilder(builder => builder
        .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Bearer:Authority"] = Issuer, ["Authentication:Bearer:Audience"] = Audience,
            ["Authentication:Oidc:Authority"] = Issuer, ["Authentication:Oidc:ClientId"] = "atlas-test",
            ["ConnectionStrings:Atlas"] = connection
        }))
        .ConfigureLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Debug))
        .ConfigureServices(services =>
        {
            if (connection is not null) services.AddSingleton(new ProjectIdentity.Infrastructure.PostgresProjectIdentityStore(connection));
            services.PostConfigure<JwtBearerOptions>(AtlasAuthentication.BearerScheme, options =>
            {
                options.Configuration = new OpenIdConnectConfiguration();
                options.TokenValidationParameters.ValidIssuer = Issuer;
                options.TokenValidationParameters.ValidAudience = Audience;
                options.TokenValidationParameters.IssuerSigningKey = SigningKey;
            });
        }));

    private static async Task<McpClient> Client(WebApplicationFactory<Program> host, string scope)
    {
        var http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", Token(scope));
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp
        }, http);
        return await McpClient.CreateAsync(transport);
    }

    private static string Token(string scope)
    {
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience,
            [new Claim("sub", $"mcp-test-{Guid.NewGuid():N}"), new Claim("scope", scope)], now.AddMinutes(-2), now.AddMinutes(10),
            new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));
    }
    private static async Task Execute(NpgsqlConnection db, string sql)
    { await using var command = new NpgsqlCommand(sql, db); await command.ExecuteNonQueryAsync(); }
}
