using AiLog.Contracts;

namespace AiLog.Shared.Providers;

public static class ProviderRegistry
{
    public static IReadOnlyList<IProviderAdapter> Adapters { get; } =
    [
        new AnthropicMessagesAdapter(),
        new OpenAiChatAdapter(),
        new OpenAiResponsesAdapter(),
    ];

    public static IProviderAdapter? Match(ExchangeLog log) => Adapters.FirstOrDefault(a => a.Matches(log));

    /// <summary>
    /// Usage from the adapter matching the request; failing that (unusual paths, no request body), from whichever
    /// adapter recognises the response's usage shape.
    /// </summary>
    public static TokenUsage? ExtractUsage(ExchangeLog log) =>
        log.Response is null ? null : Match(log)?.ExtractUsage(log.Response) ?? ExtractUsage(log.Response);

    public static TokenUsage? ExtractUsage(LoggedResponse? response) =>
        response is null ? null : Adapters.Select(a => a.ExtractUsage(response)).FirstOrDefault(u => u is not null);
}
