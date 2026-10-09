using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace AiLog.Host.Tests;

public sealed class ProviderOptionsTests
{
    private const string FoundryUrl = "https://my-resource.services.ai.azure.com";

    [Fact]
    public async Task The_provider_flag_proxies_only_that_providers_route()
    {
        string logsPath = ProxyFixture.NewLogsPath();
        await using (WebApplication app = AiLogApp.Build(["--port", "0", "--logs", logsPath, "--provider", "copilot"]))
        {
            AiLogOptions options = app.Services.GetRequiredService<AiLogOptions>();

            Assert.Equal(new Dictionary<string, string> { ["copilot"] = "https://api.githubcopilot.com" }, options.Routes);
        }

        if (Directory.Exists(logsPath))
        {
            Directory.Delete(logsPath, recursive: true);
        }
    }

    [Fact]
    public void Without_a_provider_the_default_routes_are_proxied()
    {
        AiLogOptions options = new();

        options.ApplyDefaults();

        Assert.Equal(["anthropic", "openai"], options.Routes.Keys.Order());
    }

    [Fact]
    public void The_provider_replaces_configured_routes()
    {
        AiLogOptions options = new()
        {
            Provider = "anthropic",
            Routes = new() { ["openai"] = "https://api.openai.com" },
        };

        options.ApplyDefaults();

        Assert.Equal(new Dictionary<string, string> { ["anthropic"] = "https://api.anthropic.com" }, options.Routes);
    }

    [Fact]
    public void The_provider_name_is_case_insensitive()
    {
        AiLogOptions options = new() { Provider = "OpenAI" };

        options.ApplyDefaults();

        Assert.Equal(["openai"], options.Routes.Keys);
    }

    [Fact]
    public void An_upstream_replaces_the_providers_public_api()
    {
        AiLogOptions options = new() { Provider = "foundry", Upstream = FoundryUrl };

        options.ApplyDefaults();

        Assert.Equal(new Dictionary<string, string> { ["foundry"] = FoundryUrl }, options.Routes);
    }

    [Fact]
    public void An_unknown_provider_is_refused_with_the_valid_names()
    {
        AiLogOptions options = new() { Provider = "gemini" };

        ConfigurationException error = Assert.Throws<ConfigurationException>(options.ApplyDefaults);

        Assert.Contains("anthropic, openai, foundry, copilot", error.Message);
    }

    [Fact]
    public void Foundry_without_an_upstream_is_refused()
    {
        AiLogOptions options = new() { Provider = "foundry" };

        ConfigurationException error = Assert.Throws<ConfigurationException>(options.ApplyDefaults);

        Assert.Contains("--upstream", error.Message);
    }

    [Fact]
    public void An_upstream_without_a_provider_is_refused()
    {
        AiLogOptions options = new() { Upstream = FoundryUrl };

        ConfigurationException error = Assert.Throws<ConfigurationException>(options.ApplyDefaults);

        Assert.Contains("--provider", error.Message);
    }

    [Theory]
    [InlineData("my-resource.services.ai.azure.com")]
    [InlineData("ftp://my-resource.services.ai.azure.com")]
    public void An_upstream_that_is_not_an_http_url_is_refused(string upstream)
    {
        AiLogOptions options = new() { Provider = "foundry", Upstream = upstream };

        Assert.Throws<ConfigurationException>(options.ApplyDefaults);
    }
}
