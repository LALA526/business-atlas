using System.Text.Json;
using AccessApplicability.Contracts;

namespace Assistant.Contracts;

public sealed record ConversationView(Guid ConversationId, Guid? RootUnitId, Guid BaselineId, string Title, long Version);
public sealed record ConversationPage(IReadOnlyList<ConversationView> Items, string? NextCursor);
public sealed record MessageView(Guid MessageId, long Seq, string Kind, string? Text, JsonElement? Answer, DateTimeOffset CreatedAt, Guid? RunId);
public sealed record MessagePage(IReadOnlyList<MessageView> Items, string? NextCursor);
public sealed record ProviderConfig(Guid? ProviderId, string ProviderKey, string Endpoint, string Model, string SecretRef, bool Allowed, JsonElement Options, long? Version);
public sealed record ProviderConfigPage(IReadOnlyList<ProviderConfig> Items, string? NextCursor);
public sealed record CreateConversationRequest(Guid? RootUnitId, ReadContextRequest ReadContext, string? Title);
public sealed record SendMessageRequest(string Text, ReadContextRequest ReadContext);
public sealed record CancelConversationRequest(Guid JobId);
public sealed record ConversationEvent(long Seq, Guid EventId, string Kind, JsonElement Payload, DateTimeOffset CreatedAt);

public sealed record CitationSource(string RefId, Guid UnitId, Guid RevisionId, string Title, string Summary, string FactKind, string Verification);
public sealed record ProviderGenerationOptions(int MaxOutputTokens = 4096, int MaxInputTokens = 24000, int MaxToolCalls = 20, double Temperature = 0);
public sealed record ProviderCapabilities(bool Streaming, bool StructuredOutput, bool Usage);
public sealed record ProviderUsage(int? InputTokens, int? OutputTokens, int? TotalTokens);
public sealed record ProviderStreamEvent(string? TextDelta, ProviderUsage? Usage, string? RequestId, bool OutputTruncated = false);
