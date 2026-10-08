using AiLog.Shared;

namespace AiLog.Web.Components;

/// <summary>The chosen view of an event-stream response body, kept across tab switches; texts are formatted once.</summary>
public sealed class StreamBodyState(ExchangeAnalysis analysis)
{
    /// <summary>The final message rebuilt from the stream.</summary>
    public const string Reassembled = "reassembled";

    /// <summary>Each parsed event, formatted.</summary>
    public const string Events = "events";

    /// <summary>The body as logged.</summary>
    public const string Raw = "raw";

    /// <summary>The selected view: <see cref="Reassembled"/> when available, else <see cref="Raw"/>.</summary>
    public string? View { get; set; } = analysis.Reassembled is not null ? Reassembled : Raw;

    /// <summary>The reassembled message as indented JSON, or null when the stream could not be reassembled.</summary>
    public string? ReassembledText { get; } = analysis.Reassembled is { } message ? Json.Format(message) : null;

    /// <summary>All events as text; formatted on first use since long streams have thousands of events.</summary>
    public string EventsText => field ??= string.Join("\n\n", (analysis.Events ?? []).Select(FormatEvent));

    private static string FormatEvent(SseEvent sseEvent) =>
        $"event: {sseEvent.EventType}\n{(Json.TryParse(sseEvent.Data) is { } data ? Json.Format(data) : sseEvent.Data)}";
}
