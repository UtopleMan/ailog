namespace AiLog.Host;

/// <summary>The "AiLog" configuration section: where to listen, what to proxy and where to write logs.</summary>
public sealed class AiLogOptions
{
    private const int DefaultPort = 5100;

    private static readonly Dictionary<string, string> DefaultRoutes = new()
    {
        ["anthropic"] = "https://api.anthropic.com",
        ["openai"] = "https://api.openai.com",
    };

    private static readonly string[] DefaultRedactedHeaders =
    [
        "authorization", "proxy-authorization", "x-api-key", "api-key", "x-goog-api-key", "cookie", "set-cookie",
    ];

    /// <summary>Port on localhost; 0 picks a free port.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>Folder for the exchange log files; made absolute by <see cref="ApplyDefaults"/>.</summary>
    public string LogsPath { get; set; } = "logs";

    /// <summary>Route prefix (first path segment) to upstream base URL.</summary>
    public Dictionary<string, string> Routes { get; set; } = [];

    /// <summary>Header names whose values are masked in log files.</summary>
    public List<string> RedactHeaders { get; set; } = [];

    internal void ApplyDefaults()
    {
        if (Routes.Count == 0)
        {
            Routes = new Dictionary<string, string>(DefaultRoutes);
        }

        if (RedactHeaders.Count == 0)
        {
            RedactHeaders.AddRange(DefaultRedactedHeaders);
        }

        Routes = NormaliseRouteNames(Routes);
        LogsPath = Path.GetFullPath(LogsPath);
    }

    /// <summary>Environment variable keys arrive upper-cased (AILOG_ROUTES__GEMINI), so lower-case them for display.</summary>
    private static Dictionary<string, string> NormaliseRouteNames(Dictionary<string, string> routes)
    {
        Dictionary<string, string> normalised = routes.ToDictionary(
            route => route.Key.ToLowerInvariant(),
            route => route.Value,
            StringComparer.OrdinalIgnoreCase);

        if (normalised.ContainsKey(UiEndpoints.Prefix))
        {
            throw new InvalidOperationException($"The route name '{UiEndpoints.Prefix}' is reserved for the ailog UI.");
        }

        return normalised;
    }
}
