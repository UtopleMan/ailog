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
}
