using System.Net.ServerSentEvents;
using System.Text;

namespace AiLog.Shared;

/// <summary>One dispatched event of a logged <c>text/event-stream</c> body.</summary>
public sealed record SseEvent(string EventType, string Data);

/// <summary>Reads logged server-sent event streams.</summary>
public static class Sse
{
    private const string EventStreamMediaType = "text/event-stream";
    private const string DispatchingBlankLine = "\n\n";

    /// <summary>True when the headers declare a <c>text/event-stream</c> body.</summary>
    public static bool IsEventStream(Dictionary<string, string> headers) =>
        Headers.Get(headers, "content-type")?.Contains(EventStreamMediaType, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Splits a logged stream body into its dispatched events.</summary>
    public static List<SseEvent> Parse(string text)
    {
        // A stream cut short (client abort) can end without the blank line that dispatches its last event.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text + DispatchingBlankLine));
        var events = new List<SseEvent>();
        foreach (SseItem<string> item in SseParser.Create(stream).Enumerate())
        {
            events.Add(new SseEvent(item.EventType, item.Data));
        }

        return events;
    }
}
