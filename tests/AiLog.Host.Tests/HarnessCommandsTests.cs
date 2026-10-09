namespace AiLog.Host.Tests;

public sealed class HarnessCommandsTests
{
    private const string ProxyAddress = "http://localhost:5100";
    private const string AnthropicApi = "https://api.anthropic.com";
    private const string OpenAiApi = "https://api.openai.com";

    private static readonly Dictionary<string, string> DefaultRoutes = new()
    {
        ["anthropic"] = AnthropicApi,
        ["openai"] = OpenAiApi,
    };

    [Fact]
    public void Claude_is_started_with_its_base_url_on_the_anthropic_route()
    {
        IReadOnlyList<string> lines = HarnessCommands.Describe(ProxyAddress, DefaultRoutes);

        Assert.Contains("claude (macOS): ANTHROPIC_BASE_URL=http://localhost:5100/anthropic claude", lines);
        Assert.Contains("claude (Windows): $env:ANTHROPIC_BASE_URL='http://localhost:5100/anthropic'; claude", lines);
    }

    [Fact]
    public void Copilot_is_started_in_bring_your_own_key_mode_on_the_openai_route()
    {
        IReadOnlyList<string> lines = HarnessCommands.Describe(ProxyAddress, DefaultRoutes);

        Assert.Contains(
            "copilot (macOS): COPILOT_PROVIDER_TYPE=openai COPILOT_PROVIDER_BASE_URL=http://localhost:5100/openai/v1 "
            + "COPILOT_PROVIDER_API_KEY=YOUR-OPENAI-API-KEY COPILOT_MODEL=YOUR-MODEL copilot",
            lines);
        Assert.Contains(
            "copilot (Windows): $env:COPILOT_PROVIDER_TYPE='openai'; $env:COPILOT_PROVIDER_BASE_URL='http://localhost:5100/openai/v1'; "
            + "$env:COPILOT_PROVIDER_API_KEY='YOUR-OPENAI-API-KEY'; $env:COPILOT_MODEL='YOUR-MODEL'; copilot",
            lines);
    }

    [Fact]
    public void Commands_use_whichever_route_points_at_each_api()
    {
        Dictionary<string, string> routes = new()
        {
            ["claude"] = AnthropicApi + "/",
            ["gpt"] = OpenAiApi + "/",
        };

        IReadOnlyList<string> lines = HarnessCommands.Describe(ProxyAddress + "/", routes);

        Assert.All(lines.Where(line => line.StartsWith("claude")), line => Assert.Contains("http://localhost:5100/claude", line));
        Assert.All(lines.Where(line => line.StartsWith("copilot")), line => Assert.Contains("http://localhost:5100/gpt/v1", line));
    }

    [Fact]
    public void Claude_is_left_out_without_an_anthropic_route()
    {
        Dictionary<string, string> routes = new() { ["openai"] = OpenAiApi };

        IReadOnlyList<string> lines = HarnessCommands.Describe(ProxyAddress, routes);

        Assert.DoesNotContain(lines, line => line.StartsWith("claude"));
    }

    [Fact]
    public void Copilot_is_left_out_without_an_openai_route()
    {
        Dictionary<string, string> routes = new() { ["anthropic"] = AnthropicApi };

        IReadOnlyList<string> lines = HarnessCommands.Describe(ProxyAddress, routes);

        Assert.DoesNotContain(lines, line => line.StartsWith("copilot"));
    }

    [Fact]
    public void Foundry_starts_claude_in_foundry_mode_and_copilot_with_the_azure_provider()
    {
        Dictionary<string, string> routes = new() { ["foundry"] = "https://my-resource.services.ai.azure.com" };

        IReadOnlyList<string> lines = HarnessCommands.Describe(ProxyAddress, routes);

        Assert.Contains(
            "claude (macOS): CLAUDE_CODE_USE_FOUNDRY=1 ANTHROPIC_FOUNDRY_BASE_URL=http://localhost:5100/foundry/anthropic "
            + "ANTHROPIC_FOUNDRY_API_KEY=YOUR-FOUNDRY-API-KEY claude",
            lines);
        Assert.Contains(
            "copilot (Windows): $env:COPILOT_PROVIDER_TYPE='azure'; $env:COPILOT_PROVIDER_BASE_URL='http://localhost:5100/foundry'; "
            + "$env:COPILOT_PROVIDER_API_KEY='YOUR-FOUNDRY-API-KEY'; $env:COPILOT_MODEL='YOUR-MODEL'; copilot",
            lines);
    }

    [Fact]
    public void Copilot_on_the_github_route_reads_its_token_from_the_gh_cli()
    {
        Dictionary<string, string> routes = new() { ["copilot"] = "https://api.githubcopilot.com" };

        IReadOnlyList<string> lines = HarnessCommands.Describe(ProxyAddress, routes);

        Assert.Contains(
            "copilot (macOS): COPILOT_PROVIDER_TYPE=openai COPILOT_PROVIDER_BASE_URL=http://localhost:5100/copilot "
            + "COPILOT_PROVIDER_BEARER_TOKEN=$(gh auth token) COPILOT_PROVIDER_HEADERS='Copilot-Integration-Id: copilot-developer-cli' "
            + "COPILOT_MODEL=YOUR-MODEL copilot",
            lines);
        Assert.Contains(
            "copilot (Windows): $env:COPILOT_PROVIDER_TYPE='openai'; $env:COPILOT_PROVIDER_BASE_URL='http://localhost:5100/copilot'; "
            + "$env:COPILOT_PROVIDER_BEARER_TOKEN=$(gh auth token); $env:COPILOT_PROVIDER_HEADERS='Copilot-Integration-Id: copilot-developer-cli'; "
            + "$env:COPILOT_MODEL='YOUR-MODEL'; copilot",
            lines);
    }

    [Fact]
    public void A_route_named_after_a_provider_gets_its_commands_on_any_upstream()
    {
        Dictionary<string, string> routes = new() { ["copilot"] = "https://api.business.githubcopilot.com" };

        IReadOnlyList<string> lines = HarnessCommands.Describe(ProxyAddress, routes);

        Assert.Contains(lines, line => line.Contains("COPILOT_PROVIDER_BEARER_TOKEN"));
    }

    [Fact]
    public void A_route_no_provider_recognises_gets_no_commands()
    {
        Dictionary<string, string> routes = new() { ["gemini"] = "https://generativelanguage.googleapis.com" };

        IReadOnlyList<string> lines = HarnessCommands.Describe(ProxyAddress, routes);

        Assert.Empty(lines);
    }
}
