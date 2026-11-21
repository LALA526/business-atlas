using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using Atlas.Api.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
