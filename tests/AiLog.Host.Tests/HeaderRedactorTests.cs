namespace AiLog.Host.Tests;

public sealed class HeaderRedactorTests
{
    [Theory]
    [InlineData("sk-ant-api03-abcdefghijklmnop-wxyz", "sk-ant…wxyz")]
    [InlineData("Bearer sk-proj-abcdefghijklmnop1234", "Bearer sk-pro…1234")]
    [InlineData("short", "…")]
    public void Masks_secret_values(string value, string expected) =>
        Assert.Equal(expected, HeaderRedactor.Mask(value));

    [Fact]
    public void Only_configured_headers_are_redacted()
    {
        var redactor = new HeaderRedactor(["x-api-key"]);
        var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        redactor.Add(captured, "X-Api-Key", ["sk-ant-api03-abcdefghijklmnop-wxyz"]);
        redactor.Add(captured, "anthropic-version", ["2023-06-01"]);

        Assert.Equal("sk-ant…wxyz", captured["x-api-key"]);
        Assert.Equal("2023-06-01", captured["anthropic-version"]);
    }
}
