namespace AiLog.Host;

/// <summary>Startup-banner commands that launch a harness with its traffic routed through ailog.</summary>
internal static class HarnessCommands
{
    private const string OpenAiApiVersionPath = "/v1";
    private const string FoundryAnthropicPath = "/anthropic";
    private const string OpenAiKeyPlaceholder = "YOUR-OPENAI-API-KEY";
    private const string FoundryKeyPlaceholder = "YOUR-FOUNDRY-API-KEY";
    private const string ModelPlaceholder = "YOUR-MODEL";
    private const string GitHubTokenCommand = "gh auth token";
    private const string CopilotIntegrationHeader = "Copilot-Integration-Id: copilot-developer-cli";

    /// <summary>
    /// Commands for macOS and Windows for every route a known provider serves.
    /// A route no provider recognises gets no commands.
    /// </summary>
    public static IReadOnlyList<string> Describe(string proxyAddress, IReadOnlyDictionary<string, string> routes)
    {
        string proxy = proxyAddress.TrimEnd('/');
        List<string> lines = [];
        foreach (var (name, upstream) in routes)
        {
            if (ModelProvider.ForRoute(name, upstream) is { } provider)
            {
                lines.AddRange(provider.Harnesses($"{proxy}/{name}").SelectMany(Describe));
            }
        }

        return lines;
    }

    public static IReadOnlyList<HarnessCommand> ForAnthropic(string routeUrl) => [Claude(routeUrl)];

    public static IReadOnlyList<HarnessCommand> ForOpenAi(string routeUrl) =>
        [CopilotWithOpenAiKey(routeUrl + OpenAiApiVersionPath)];

    /// <summary>Foundry serves Claude models under /anthropic and OpenAI models under /openai.</summary>
    public static IReadOnlyList<HarnessCommand> ForFoundry(string routeUrl) =>
        [ClaudeOnFoundry(routeUrl + FoundryAnthropicPath), CopilotOnFoundry(routeUrl)];

    public static IReadOnlyList<HarnessCommand> ForCopilot(string routeUrl) => [CopilotWithGitHubToken(routeUrl)];

    private static HarnessCommand Claude(string baseUrl) =>
        new("claude", [new LiteralVariable("ANTHROPIC_BASE_URL", baseUrl)]);

    private static HarnessCommand ClaudeOnFoundry(string baseUrl) =>
        new("claude",
        [
            new LiteralVariable("CLAUDE_CODE_USE_FOUNDRY", "1"),
            new LiteralVariable("ANTHROPIC_FOUNDRY_BASE_URL", baseUrl),
            new LiteralVariable("ANTHROPIC_FOUNDRY_API_KEY", FoundryKeyPlaceholder),
        ]);

    /// <summary>
    /// Copilot CLI only talks to a custom endpoint in bring-your-own-key mode, which needs a key and a model.
    /// The provider type is set explicitly so a value left over in the shell cannot override it.
    /// </summary>
    private static HarnessCommand CopilotWithOpenAiKey(string baseUrl) =>
        new("copilot",
        [
            new LiteralVariable("COPILOT_PROVIDER_TYPE", "openai"),
            new LiteralVariable("COPILOT_PROVIDER_BASE_URL", baseUrl),
            new LiteralVariable("COPILOT_PROVIDER_API_KEY", OpenAiKeyPlaceholder),
            new LiteralVariable("COPILOT_MODEL", ModelPlaceholder),
        ]);

    private static HarnessCommand CopilotOnFoundry(string baseUrl) =>
        new("copilot",
        [
            new LiteralVariable("COPILOT_PROVIDER_TYPE", "azure"),
            new LiteralVariable("COPILOT_PROVIDER_BASE_URL", baseUrl),
            new LiteralVariable("COPILOT_PROVIDER_API_KEY", FoundryKeyPlaceholder),
            new LiteralVariable("COPILOT_MODEL", ModelPlaceholder),
        ]);

    /// <summary>
    /// Bring-your-own-key mode never sends the GitHub login, so the token is passed as the provider's bearer token,
    /// read from the gh CLI when the command runs.
    /// </summary>
    private static HarnessCommand CopilotWithGitHubToken(string baseUrl) =>
        new("copilot",
        [
            new LiteralVariable("COPILOT_PROVIDER_TYPE", "openai"),
            new LiteralVariable("COPILOT_PROVIDER_BASE_URL", baseUrl),
            new CommandOutputVariable("COPILOT_PROVIDER_BEARER_TOKEN", GitHubTokenCommand),
            new LiteralVariable("COPILOT_PROVIDER_HEADERS", CopilotIntegrationHeader),
            new LiteralVariable("COPILOT_MODEL", ModelPlaceholder),
        ]);

    private static IEnumerable<string> Describe(HarnessCommand command) =>
    [
        $"{command.Program} (macOS): {command.ForPosixShell()}",
        $"{command.Program} (Windows): {command.ForPowerShell()}",
    ];
}
