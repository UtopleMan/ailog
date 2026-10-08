using System.Net.ServerSentEvents;
using System.Text;

namespace AiLog.Shared;

/// <summary>One dispatched event of a logged <c>text/event-stream</c> body.</summary>
public sealed record SseEvent(string EventType, string Data);

public static class Sse
{
    public static bool IsEventStream(Dictionary<string, string> headers) =>
        Headers.Get(headers, "content-type")?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;

    public static List<SseEvent> Parse(string text)
    {
        // A stream cut short (client abort) can end without the blank line that dispatches its last event.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text + "\n\n"));
        var events = new List<SseEvent>();
        foreach (var item in SseParser.Create(stream).Enumerate())
        {
            events.Add(new SseEvent(item.EventType, item.Data));
        }

        return events;
    }
}

public static class Headers
{
    /// <summary>Deserialised header dictionaries are case-sensitive, so match the name by hand.</summary>
    public static string? Get(Dictionary<string, string> headers, string name)
    {
        foreach (var (key, value) in headers)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}
