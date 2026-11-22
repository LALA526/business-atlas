using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Assistant.Contracts;

namespace Assistant.Providers;

/// <summary>Real OpenAI-compatible chat-completions transport. No fallback or synthesized response path.</summary>
public sealed class OpenAiCompatibleProvider(IHttpClientFactory clients, ISecretRefResolver secrets) : IProviderPort
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ProviderCapabilities ModelCapabilities { get; } = new(true, true, true);

    public async Task<ProviderGenerationResult> GenerateAsync(ProviderConfig provider, ProviderGenerationRequest request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var content = new StringBuilder();
        ProviderUsage? usage = null;
        string? requestId = null;
        await foreach (var item in StreamAsync(provider, request, cancellationToken))
        {
            if (item.TextDelta is not null) content.Append(item.TextDelta);
            usage = item.Usage ?? usage;
            requestId = item.RequestId ?? requestId;
        }
        return new(content.ToString(), usage, requestId, watch.ElapsedMilliseconds);
    }

    public async IAsyncEnumerable<ProviderStreamEvent> StreamAsync(ProviderConfig provider, ProviderGenerationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!provider.Allowed) throw new ProviderException("PROVIDER_NOT_ALLOWED", "The provider has not been enabled by a project administrator.", false);
        if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ProviderException("PROVIDER_ENDPOINT_INVALID", "Only an absolute HTTPS provider endpoint without query or fragment is supported.", false);
        var key = secrets.Resolve(provider.SecretRef);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = provider.Model,
            ["messages"] = request.Messages.Select(m => new { role = m.Role, content = m.Content }),
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
            ["max_tokens"] = request.Options.MaxOutputTokens,
            ["temperature"] = request.Options.Temperature,
            ["response_format"] = new { type = "json_schema", json_schema = new { name = "agent_result", strict = true, schema = request.ResponseSchema } }
        };
        if (request.Tools.Count > 0) payload["tools"] = request.Tools.Select(t => new { type = "function", function = new { name = t.Name, description = t.Description, parameters = t.InputSchema } });
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");
        using var client = clients.CreateClient("AtlasProvider");
        client.Timeout = Timeout.InfiniteTimeSpan;
        HttpResponseMessage response;
        try { response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ProviderException("PROVIDER_TIMEOUT", "The model provider timed out.", true); }
        catch (HttpRequestException) { throw new ProviderException("DEPENDENCY_UNAVAILABLE", "The model provider is unreachable.", true); }
        using (response)
        {
            var requestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
            if (!response.IsSuccessStatusCode)
            {
                // Do not log or return the response body: compatible providers may echo request content.
                var transient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500;
                throw new ProviderException(transient ? "DEPENDENCY_UNAVAILABLE" : "PROVIDER_REQUEST_REJECTED", $"The model provider rejected the request with HTTP {(int)response.StatusCode}.", transient);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrEmpty(line) || line.StartsWith(':')) continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") yield break;
                JsonDocument document;
                try { document = JsonDocument.Parse(data); }
                catch (JsonException) { throw new ProviderException("PROVIDER_RESPONSE_INVALID", "The provider returned malformed streaming data.", false); }
                using (document)
                {
                    ProviderUsage? usage = null;
                    if (document.RootElement.TryGetProperty("usage", out var usageValue) && usageValue.ValueKind == JsonValueKind.Object)
                        usage = new ProviderUsage(Int(usageValue, "prompt_tokens"), Int(usageValue, "completion_tokens"), Int(usageValue, "total_tokens"));
                    string? delta = null;
                    var outputTruncated = false;
                    if (document.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("delta", out var deltaValue) && deltaValue.ValueKind == JsonValueKind.Object && deltaValue.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String)
                            delta = text.GetString();
                        if (choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() == "length")
                            outputTruncated = true;
                    }
                    if (delta is not null || usage is not null || outputTruncated) yield return new(delta, usage, requestId, outputTruncated);
                }
            }
        }
    }

    private static int? Int(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.TryGetInt32(out var parsed) ? parsed : null;
}
