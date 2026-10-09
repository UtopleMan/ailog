namespace AiLog.Host;

/// <summary>A model API that --provider selects: its route name, its public endpoint and the harnesses that use it.</summary>
internal sealed record ModelProvider(
    string Name,
    string? DefaultUpstream,
    Func<string, IReadOnlyList<HarnessCommand>> Harnesses)
{
    public const string AnthropicApi = "https://api.anthropic.com";
    public const string OpenAiApi = "https://api.openai.com";
    private const string GitHubCopilotApi = "https://api.githubcopilot.com";

    public static ModelProvider Anthropic { get; } = new("anthropic", AnthropicApi, HarnessCommands.ForAnthropic);

    public static ModelProvider OpenAi { get; } = new("openai", OpenAiApi, HarnessCommands.ForOpenAi);

    /// <summary>Every Foundry resource has a host of its own, so there is no default upstream.</summary>
    public static ModelProvider Foundry { get; } = new("foundry", DefaultUpstream: null, HarnessCommands.ForFoundry);

    public static ModelProvider Copilot { get; } = new("copilot", GitHubCopilotApi, HarnessCommands.ForCopilot);

    public static IReadOnlyList<ModelProvider> All { get; } = [Anthropic, OpenAi, Foundry, Copilot];

    public static string Names => string.Join(", ", All.Select(provider => provider.Name));

    public static ModelProvider? Find(string name) =>
        All.FirstOrDefault(provider => provider.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The provider whose public API the route points at, failing that the one the route is named after.</summary>
    public static ModelProvider? ForRoute(string name, string upstream) =>
        All.FirstOrDefault(provider => provider.IsPublicApi(upstream)) ?? Find(name);

    private bool IsPublicApi(string upstream) =>
        DefaultUpstream is not null
        && HostOf(upstream) is { } host
        && host.Equals(HostOf(DefaultUpstream), StringComparison.OrdinalIgnoreCase);

    private static string? HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : null;
}
