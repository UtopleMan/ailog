using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AiLog.Contracts;

namespace AiLog.Host.Tests;

public sealed class ProxyTests
{
    [Fact]
    public async Task Json_request_is_forwarded_unchanged_and_logged_with_redacted_key()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        const string key = "sk-ant-api03-SECRETSECRETSECRET-wxyz";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/fake/v1/messages?beta=true")
        {
            Content = new StringContent("""{"model":"m","messages":[]}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", key);
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"echo":{"model":"m","messages":[]}}""", await response.Content.ReadAsStringAsync());
        Assert.Equal(ProxyFixture.Sha256(key), response.Headers.GetValues("x-upstream-saw-key-sha256").Single());
        Assert.Equal("?beta=true", response.Headers.GetValues("x-upstream-saw-query").Single());
        Assert.Equal("Kestrel", response.Headers.Server.Single().Product!.Name); // upstream's own header, not a second one from ailog

        var (fileName, log) = (await fixture.WaitForLogsAsync(1))[0];
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}\.\d{3}Z_0001_POST_v1-messages\.json$"), fileName);
        Assert.Equal(ExchangeOutcome.Completed, log.Outcome);
        Assert.Equal("fake", log.Route);
        Assert.Equal("/fake/v1/messages?beta=true", log.Request.Target);
        Assert.Equal("sk-ant…wxyz", log.Request.Headers["x-api-key"]);
        Assert.Equal(BodyFormat.Json, log.Request.Body!.Format);
        Assert.Equal("m", log.Request.Body.Content.GetProperty("model").GetString());
        Assert.Equal(200, log.Response!.StatusCode);
        Assert.Equal(BodyFormat.Json, log.Response.Body!.Format);
        Assert.DoesNotContain(key, await File.ReadAllTextAsync(Path.Combine(fixture.LogsPath, fileName)));
    }

    [Fact]
    public async Task Sse_chunks_reach_the_client_before_the_upstream_finishes()
    {
        await using var fixture = await ProxyFixture.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/fake/v1/stream") { Content = new StringContent("{}") };
        using var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        // The upstream is blocked on the gate, so this line can only arrive if the proxy is not buffering.
        var first = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("event: message_start", first);

        fixture.StreamGate.SetResult();
        var rest = await reader.ReadToEndAsync();
        Assert.Contains("event: message_stop", rest);

        var (_, log) = (await fixture.WaitForLogsAsync(1))[0];
        Assert.Equal(BodyFormat.Text, log.Response!.Body!.Format);
        Assert.Equal(
            "event: message_start\ndata: {\"n\":1}\n\nevent: message_stop\ndata: {\"n\":2}\n\n",
            log.Response.Body.Content.GetString());
    }

    [Fact]
    public async Task Compressed_response_is_forwarded_compressed_and_logged_decompressed()
    {
        await using var fixture = await ProxyFixture.StartAsync();

        using var response = await fixture.Client.GetAsync("/fake/v1/gzip");

        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.Single());
        Assert.Equal(ProxyFixture.Gzip("""{"compressed":true}"""), await response.Content.ReadAsByteArrayAsync());

        var (_, log) = (await fixture.WaitForLogsAsync(1))[0];
        Assert.Equal(BodyFormat.Json, log.Response!.Body!.Format);
        Assert.Equal("gzip", log.Response.Body.ContentEncoding);
        Assert.True(log.Response.Body.Content.GetProperty("compressed").GetBoolean());
    }

    [Fact]
    public async Task Client_disconnect_cancels_upstream_and_logs_partial_response()
    {
        await using var fixture = await ProxyFixture.StartAsync();

        var response = await fixture.Client.GetAsync("/fake/v1/hang", HttpCompletionOption.ResponseHeadersRead);
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal("data: partial", await reader.ReadLineAsync());
        response.Dispose();

        await fixture.UpstreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var (_, log) = (await fixture.WaitForLogsAsync(1))[0];
        Assert.Equal(ExchangeOutcome.ClientAborted, log.Outcome);
        Assert.Equal("data: partial\n\n", log.Response!.Body!.Content.GetString());
    }

    [Fact]
    public async Task Unreachable_upstream_returns_502_and_logs_upstream_error()
    {
        await using var fixture = await ProxyFixture.StartAsync();

        using var response = await fixture.Client.PostAsync("/dead/v1/messages", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var (_, log) = (await fixture.WaitForLogsAsync(1))[0];
        Assert.Equal(ExchangeOutcome.UpstreamError, log.Outcome);
        Assert.Null(log.Response);
        Assert.NotNull(log.Error);
    }

    [Fact]
    public async Task Unknown_route_returns_404_without_logging()
    {
        await using var fixture = await ProxyFixture.StartAsync();

        using var response = await fixture.Client.GetAsync("/nope/v1/models");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(Directory.GetFiles(fixture.LogsPath));
    }

    [Fact]
    public async Task Log_files_sort_in_request_order()
    {
        await using var fixture = await ProxyFixture.StartAsync();

        for (var i = 0; i < 3; i++)
        {
            using var _ = await fixture.Client.PostAsync("/fake/v1/messages", new StringContent($$"""{"i":{{i}}}""", Encoding.UTF8, "application/json"));
        }

        var logs = await fixture.WaitForLogsAsync(3);
        Assert.Equal([1L, 2L, 3L], logs.Select(l => l.Log.Sequence));
        Assert.Equal([0, 1, 2], logs.Select(l => l.Log.Request.Body!.Content.GetProperty("i").GetInt32()));
    }
}
