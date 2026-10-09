namespace AiLog.Host;

/// <summary>The "AiLog" configuration section: where to listen, what to proxy and where to write logs.</summary>
public sealed class AiLogOptions
{
    private const int DefaultPort = 5100;

    private static readonly Dictionary<string, string> DefaultRoutes = new()
    {
        [ModelProvider.Anthropic.Name] = ModelProvider.AnthropicApi,
        [ModelProvider.OpenAi.Name] = ModelProvider.OpenAiApi,
    };

    private static readonly string[] DefaultRedactedHeaders =
    [
        "authorization", "proxy-authorization", "x-api-key", "api-key", "x-goog-api-key", "cookie", "set-cookie",
    ];

    /// <summary>Port on localhost; 0 picks a free port.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>Folder for the exchange log files; made absolute by <see cref="ApplyDefaults"/>.</summary>
    public string LogsPath { get; set; } = "logs";

    /// <summary>Route prefix (first path segment) to upstream base URL; replaced by the route of <see cref="Provider"/>.</summary>
    public Dictionary<string, string> Routes { get; set; } = [];

    /// <summary>The one model provider to proxy (--provider); unset proxies every route in <see cref="Routes"/>.</summary>
    public string? Provider { get; set; }

    /// <summary>Upstream base URL for <see cref="Provider"/> (--upstream); unset uses the provider's public API.</summary>
    public string? Upstream { get; set; }

    /// <summary>Header names whose values are masked in log files.</summary>
    public List<string> RedactHeaders { get; set; } = [];

    /// <exception cref="ConfigurationException">The provider or upstream settings cannot be used.</exception>
    internal void ApplyDefaults()
    {
        Routes = NormaliseRouteNames(string.IsNullOrWhiteSpace(Provider) ? ConfiguredRoutes() : ProviderRoute(Provider));
        if (RedactHeaders.Count == 0)
        {
            RedactHeaders.AddRange(DefaultRedactedHeaders);
        }

        LogsPath = Path.GetFullPath(LogsPath);
    }

    private Dictionary<string, string> ConfiguredRoutes()
    {
        if (!string.IsNullOrWhiteSpace(Upstream))
        {
            throw new ConfigurationException("--upstream requires --provider.");
        }

        return Routes.Count == 0 ? new Dictionary<string, string>(DefaultRoutes) : Routes;
    }

    private Dictionary<string, string> ProviderRoute(string name)
    {
        ModelProvider provider = ModelProvider.Find(name)
            ?? throw new ConfigurationException($"unknown provider '{name}'. Valid providers: {ModelProvider.Names}.");
        string upstream = string.IsNullOrWhiteSpace(Upstream) ? provider.DefaultUpstream ?? throw MissingUpstream(provider) : Upstream;
        if (!IsHttpUrl(upstream))
        {
            throw new ConfigurationException($"--upstream must be an absolute http(s) URL, not '{upstream}'.");
        }

        return new Dictionary<string, string> { [provider.Name] = upstream };
    }

    private static ConfigurationException MissingUpstream(ModelProvider provider) =>
        new($"the {provider.Name} provider has no public API; pass --upstream <url> with your endpoint.");

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Environment variable keys arrive upper-cased (AILOG_ROUTES__GEMINI), so lower-case them for display.</summary>
    private static Dictionary<string, string> NormaliseRouteNames(Dictionary<string, string> routes)
    {
        Dictionary<string, string> normalised = routes.ToDictionary(
            route => route.Key.ToLowerInvariant(),
            route => route.Value,
            StringComparer.OrdinalIgnoreCase);

        if (normalised.ContainsKey(UiEndpoints.Prefix))
        {
            throw new ConfigurationException($"the route name '{UiEndpoints.Prefix}' is reserved for the ailog UI.");
        }

        return normalised;
    }
}
