using System.Text.Json;
using AiLog.Contracts;

namespace AiLog.Host.Tests;

/// <summary>Builds logged bodies the way the proxy records them.</summary>
public static class LoggedBodies
{
    public static LoggedBody Json(string json) => new()
    {
        Format = BodyFormat.Json,
        SizeBytes = json.Length,
        Content = JsonDocument.Parse(json).RootElement.Clone(),
    };

    public static LoggedBody Text(string text) => new()
    {
        Format = BodyFormat.Text,
        SizeBytes = text.Length,
        Content = JsonSerializer.SerializeToElement(text, AiLogJsonContext.Default.String),
    };
}
