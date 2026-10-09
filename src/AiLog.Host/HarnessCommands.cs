namespace AiLog.Host;

/// <summary>Startup-banner commands that launch a harness with its traffic routed through ailog.</summary>
internal static class HarnessCommands
{
    private const string AnthropicApiHost = "api.anthropic.com";
    private const string OpenAiApiHost = "api.openai.com";
    private const string OpenAiApiVersionPath = "/v1";
    private const string OpenAiKeyPlaceholder = "YOUR-OPENAI-API-KEY";
    private const string ModelPlaceholder = "YOUR-MODEL";

    /// <summary>
    /// Claude Code (on the Anthropic route) and Copilot CLI (on the OpenAI route) commands for macOS and Windows.
    /// A harness is left out when no route points at its API.
    /// </summary>
    public static IReadOnlyList<string> Describe(string proxyAddress, IReadOnlyDictionary<string, string> routes)
    {
        string proxy = proxyAddress.TrimEnd('/');
        List<string> lines = [];
        if (FindRoute(routes, AnthropicApiHost) is { } anthropicRoute)
        {
            lines.AddRange(Describe(Claude($"{proxy}/{anthropicRoute}")));
        }

        if (FindRoute(routes, OpenAiApiHost) is { } openAiRoute)
        {
            lines.AddRange(Describe(Copilot($"{proxy}/{openAiRoute}{OpenAiApiVersionPath}")));
        }

        return lines;
    }

    private static string? FindRoute(IReadOnlyDictionary<string, string> routes, string apiHost) =>
        routes.FirstOrDefault(route => HasHost(route.Value, apiHost)).Key;

    private static bool HasHost(string upstream, string host) =>
        Uri.TryCreate(upstream, UriKind.Absolute, out Uri? uri)
        && uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase);

    private static HarnessCommand Claude(string baseUrl) =>
        new("claude", [new("ANTHROPIC_BASE_URL", baseUrl)]);

    /// <summary>
    /// Copilot CLI only talks to a custom endpoint in bring-your-own-key mode, which needs a key and a model.
    /// The provider type is set explicitly so a value left over in the shell cannot override it.
    /// </summary>
    private static HarnessCommand Copilot(string baseUrl) =>
        new("copilot",
        [
            new("COPILOT_PROVIDER_TYPE", "openai"),
            new("COPILOT_PROVIDER_BASE_URL", baseUrl),
            new("COPILOT_PROVIDER_API_KEY", OpenAiKeyPlaceholder),
            new("COPILOT_MODEL", ModelPlaceholder),
        ]);

    private static IEnumerable<string> Describe(HarnessCommand command) =>
    [
        $"{command.Program} (macOS): {command.ForPosixShell()}",
        $"{command.Program} (Windows): {command.ForPowerShell()}",
    ];

    private sealed record EnvironmentVariable(string Name, string Value);

    private sealed record HarnessCommand(string Program, IReadOnlyList<EnvironmentVariable> Environment)
    {
        public string ForPosixShell() =>
            string.Join(' ', [..Environment.Select(variable => $"{variable.Name}={variable.Value}"), Program]);

        public string ForPowerShell() =>
            string.Join("; ", [..Environment.Select(variable => $"$env:{variable.Name}='{variable.Value}'"), Program]);
    }
}
