using System.Net;
using System.Text.Json;
using BuildingBlocks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Integration.Tests;

public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthMatchesFrozenEnvelope(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("OK", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("requestId").GetString()));
        Assert.Equal("ok", body.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReadinessBecomesUnavailableDuringShutdown()
    {
        using var factory = _factory.WithWebHostBuilder(_ => { });
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<HealthState>().BeginShutdown();
        using var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("DEPENDENCY_UNAVAILABLE", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("requestId").GetString()));
        Assert.False(body.RootElement.TryGetProperty("data", out _));
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task ConcurrentProbesDoNotShareMutableRequestState()
    {
        using var client = _factory.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.GetAsync("/health/ready")));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var ids = await Task.WhenAll(responses.Select(async response =>
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("requestId").GetString()));
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }
}
