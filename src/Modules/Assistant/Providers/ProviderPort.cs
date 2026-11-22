using System.Runtime.CompilerServices;
using System.Text.Json;
using Assistant.Contracts;
using Microsoft.Extensions.Configuration;

namespace Assistant.Providers;

public sealed record ProviderMessage(string Role, string Content);
public sealed record ProviderToolDescriptor(string Name, string Description, JsonElement InputSchema);
public sealed record ProviderGenerationRequest(IReadOnlyList<ProviderMessage> Messages, JsonElement ResponseSchema, IReadOnlyList<ProviderToolDescriptor> Tools, ProviderGenerationOptions Options);
public sealed record ProviderGenerationResult(string Content, ProviderUsage? Usage, string? RequestId, long LatencyMs);

public interface IProviderPort
{
    ProviderCapabilities ModelCapabilities { get; }
    Task<ProviderGenerationResult> GenerateAsync(ProviderConfig provider, ProviderGenerationRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<ProviderStreamEvent> StreamAsync(ProviderConfig provider, ProviderGenerationRequest request, CancellationToken cancellationToken);
}

public interface ISecretRefResolver
{
    string Resolve(string secretRef);
}

public sealed class ConfigurationSecretRefResolver(IConfiguration configuration) : ISecretRefResolver
{
    public string Resolve(string secretRef)
    {
        if (string.IsNullOrWhiteSpace(secretRef) || secretRef.Length > 300) throw new ProviderException("PROVIDER_SECRET_UNAVAILABLE", "A provider secret reference is invalid.", false);
        string? value = secretRef.StartsWith("env:", StringComparison.Ordinal)
            ? Environment.GetEnvironmentVariable(secretRef[4..])
            : secretRef.StartsWith("config:", StringComparison.Ordinal) ? configuration[secretRef[7..]] : null;
        if (string.IsNullOrWhiteSpace(value)) throw new ProviderException("PROVIDER_SECRET_UNAVAILABLE", "The configured provider credential is unavailable.", false);
        return value;
    }
}

public sealed class ProviderException(string code, string message, bool transient) : Exception(message)
{
    public string Code { get; } = code;
    public bool Transient { get; } = transient;
}
