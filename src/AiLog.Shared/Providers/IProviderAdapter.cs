using System.Text.Json;
using AiLog.Contracts;
using AiLog.Shared.Context;

namespace AiLog.Shared.Providers;

/// <summary>
/// Understands one wire format (not one company: many providers speak OpenAI Chat Completions).
/// Adding a provider means implementing this and registering it in <see cref="ProviderRegistry"/>.
/// </summary>
public interface IProviderAdapter
{
    /// <summary>Display name, e.g. "Anthropic Messages".</summary>
    string Name { get; }

    /// <summary>Recognises the exchange by request path and body shape.</summary>
    bool Matches(ExchangeLog log);

    /// <summary>Reads token usage from a JSON or SSE response body; null when it carries none in this format.</summary>
    TokenUsage? ExtractUsage(LoggedResponse response);

    /// <summary>Folds a streamed response into the message a non-streaming call would have returned.</summary>
    JsonElement? Reassemble(IReadOnlyList<SseEvent> events);

    /// <summary>A tree with the top-level groups System, Tools and Messages.</summary>
    ContextSegment ParseRequest(JsonElement body);

    /// <summary>The model's output, from a JSON response body or a reassembled stream.</summary>
    ContextSegment ParseResponse(JsonElement message);

    /// <summary>The top-level request groups in the order the provider builds its prompt cache prefix.</summary>
    IEnumerable<ContextSegment> CacheOrder(ContextSegment request);
}
