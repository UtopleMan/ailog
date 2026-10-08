namespace AiLog.Host;

public sealed class AiLogOptions
{
    public int Port { get; set; } = 5100;
    public string LogsPath { get; set; } = "logs";

    /// <summary>Route prefix (first path segment) to upstream base URL.</summary>
    public Dictionary<string, string> Routes { get; set; } = [];
    public List<string> RedactHeaders { get; set; } = [];

    internal void ApplyDefaults()
    {
        if (Routes.Count == 0)
        {
            Routes["anthropic"] = "https://api.anthropic.com";
            Routes["openai"] = "https://api.openai.com";
        }

        if (RedactHeaders.Count == 0)
        {
            RedactHeaders.AddRange(["authorization", "proxy-authorization", "x-api-key", "api-key", "x-goog-api-key", "cookie", "set-cookie"]);
        }

        // Environment variable keys arrive upper-cased (AILOG_ROUTES__GEMINI), so normalise for display.
        Routes = Routes.ToDictionary(r => r.Key.ToLowerInvariant(), r => r.Value, StringComparer.OrdinalIgnoreCase);
        if (Routes.ContainsKey(UiEndpoints.Prefix))
        {
            throw new InvalidOperationException($"The route name '{UiEndpoints.Prefix}' is reserved for the ailog UI.");
        }

        LogsPath = Path.GetFullPath(LogsPath);
    }
}
