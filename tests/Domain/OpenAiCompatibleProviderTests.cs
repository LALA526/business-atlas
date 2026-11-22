using System.Net;
using System.Text;
using System.Text.Json;
using Assistant.Contracts;
using Assistant.Providers;

namespace Atlas.Domain.Tests;

public sealed class OpenAiCompatibleProviderTests
{
    [Fact]
    public async Task GenerateReadsActualSseContentUsageAndRequestId()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Headers = { { "x-request-id", "provider-request-1" } },
            Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"status\\\":\\\"ok\\\"}\"}}]}\n\ndata: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3,\"total_tokens\":15}}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
        });
        var provider = new OpenAiCompatibleProvider(new Factory(handler), new FixedSecrets());
        var result = await provider.GenerateAsync(Config(), Request(), CancellationToken.None);
        Assert.Equal("{\"status\":\"ok\"}", result.Content);
        Assert.Equal(15, result.Usage?.TotalTokens);
        Assert.Equal("provider-request-1", result.RequestId);
        Assert.NotNull(handler.Request);
        Assert.Equal("Bearer server-secret", handler.Request!.Headers.Authorization?.ToString());
        var payload = handler.Payload;
        Assert.DoesNotContain("server-secret", payload);
        Assert.True(JsonDocument.Parse(payload).RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task ProviderRejectsHttpEndpointBeforeSendingCredential()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Must not send"));
        var provider = new OpenAiCompatibleProvider(new Factory(handler), new FixedSecrets());
        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.GenerateAsync(Config() with { Endpoint = "http://localhost/v1/chat/completions" }, Request(), CancellationToken.None));
        Assert.Equal("PROVIDER_ENDPOINT_INVALID", error.Code);
        Assert.Null(handler.Request);
    }

    private static ProviderConfig Config() => new(Guid.NewGuid(), "test", "https://models.example/v1/chat/completions", "test-model", "env:TEST", true, JsonSerializer.SerializeToElement(new { }), 1);
    private static ProviderGenerationRequest Request() => new([new("user", "synthetic adapter test")], JsonSerializer.SerializeToElement(new { type = "object" }), [], new());
    private sealed class FixedSecrets : ISecretRefResolver { public string Resolve(string secretRef) => "server-secret"; }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Payload { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Request = request; Payload = await request.Content!.ReadAsStringAsync(cancellationToken); return response(request); }
    }
}
