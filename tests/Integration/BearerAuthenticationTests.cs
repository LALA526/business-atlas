using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Atlas.Api.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Atlas.Integration.Tests;

public sealed class BearerAuthenticationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Issuer = "https://identity.test";
    private const string Audience = "business-atlas-api";
    private static readonly SymmetricSecurityKey SigningKey = new(
        "atlas-integration-test-signing-key-0123456789"u8.ToArray());
    private readonly WebApplicationFactory<Program> _factory;

    public BearerAuthenticationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Theory]
    [InlineData(Audience, true)]
    [InlineData("another-api", false)]
    public async Task Expired_or_wrong_audience_bearer_tokens_are_rejected(string audience, bool expired)
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Bearer:Authority"] = Issuer,
                ["Authentication:Bearer:Audience"] = Audience,
                ["Authentication:Oidc:Authority"] = Issuer,
                ["Authentication:Oidc:ClientId"] = "atlas-test"
            }))
            .ConfigureLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Debug))
            .ConfigureServices(services => services.PostConfigure<JwtBearerOptions>(AtlasAuthentication.BearerScheme, options =>
            {
                options.Configuration = new OpenIdConnectConfiguration();
                options.TokenValidationParameters.ValidIssuer = Issuer;
                options.TokenValidationParameters.ValidAudience = Audience;
                options.TokenValidationParameters.IssuerSigningKey = SigningKey;
            })));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(audience, expired));

        using var response = await client.GetAsync("/api/v1/projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, value => value.Scheme == "Bearer");
    }

    [Fact]
    public async Task Private_oidc_backchannel_retrieves_public_jwks_without_a_hairpin_connection()
    {
        const string issuer = "https://identity.test/auth/realms/atlas";
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var requests = new List<string>();
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                var context = await listener.GetContextAsync();
                requests.Add(context.Request.Url!.AbsolutePath);
                var document = context.Request.Url.AbsolutePath.EndsWith("/certs", StringComparison.Ordinal)
                    ? "{\"keys\":[]}"
                    : JsonSerializer.Serialize(new
                    {
                        issuer,
                        authorization_endpoint = issuer + "/protocol/openid-connect/auth",
                        token_endpoint = issuer + "/protocol/openid-connect/token",
                        userinfo_endpoint = issuer + "/protocol/openid-connect/userinfo",
                        jwks_uri = issuer + "/protocol/openid-connect/certs"
                    });
                var bytes = Encoding.UTF8.GetBytes(document);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        });
        using var factory = _factory.WithWebHostBuilder(builder => builder
            .UseSetting("Authentication:AllowInsecureInternalMetadata", "true")
            .UseSetting("Authentication:Oidc:Authority", "https://identity.test/auth")
            .UseSetting("Authentication:Oidc:MetadataAddress", $"http://127.0.0.1:{port}/realms/atlas/.well-known/openid-configuration")
            .UseSetting("Authentication:Oidc:ClientId", "atlas-test")
            .UseSetting("Authentication:Oidc:CallbackOrigin", "https://public.atlas.test")
            .UseSetting("Authentication:Oidc:UseUserInfo", "false")
            .UseSetting("Authentication:Bearer:Authority", "https://identity.test/auth")
            .UseSetting("Authentication:Bearer:MetadataAddress", $"http://127.0.0.1:{port}/realms/atlas/.well-known/openid-configuration")
            .UseSetting("Authentication:DataProtectionKeyPath", ""));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/auth/login?returnUrl=/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(issuer + "/protocol/openid-connect/auth", response.Headers.Location?.ToString(), StringComparison.Ordinal);
        var parameters = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal("https://public.atlas.test/signin-oidc", parameters["redirect_uri"]);
        Assert.Contains("/realms/atlas/protocol/openid-connect/certs", requests);
        listener.Stop();
        try { await serving; } catch (Exception) when (!listener.IsListening) { }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string CreateToken(string audience, bool expired)
    {
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: Issuer,
            audience: audience,
            claims: [new Claim("sub", "test-user"), new Claim("scope", "atlas.read")],
            notBefore: now.AddMinutes(-10),
            expires: expired ? now.AddMinutes(-2) : now.AddMinutes(10),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));
    }
}
