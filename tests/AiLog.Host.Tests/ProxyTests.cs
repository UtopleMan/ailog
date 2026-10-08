using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AiLog.Contracts;

namespace AiLog.Host.Tests;

public sealed class ProxyTests
{
    private const string ApiKey = "sk-ant-api03-SECRETSECRETSECRET-wxyz";
    private const string MessagesTarget = "/fake/v1/messages?beta=true";
    private const string MessageJson = """{"model":"m","messages":[]}""";
    private static readonly TimeSpan FirstLineTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UpstreamCancelTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Json_request_is_forwarded_unchanged()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();

        using HttpResponseMessage response = await SendMessageWithApiKeyAsync(fixture);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($$"""{"echo":{{MessageJson}}}""", await response.Content.ReadAsStringAsync(fixture.CancellationToken));
        Assert.Equal(ProxyFixture.Sha256(ApiKey), response.Headers.GetValues(ProxyFixture.SeenKeyHashHeader).Single());
        Assert.Equal("?beta=true", response.Headers.GetValues(ProxyFixture.SeenQueryHeader).Single());

        // The upstream's own Server header, not a second one added by ailog.
        Assert.Equal("Kestrel", response.Headers.Server.Single().Product!.Name);
    }

    [Fact]
    public async Task Json_exchange_is_logged_to_a_sortable_file()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();
        using HttpResponseMessage response = await SendMessageWithApiKeyAsync(fixture);

        (string fileName, ExchangeLog log) = (await fixture.WaitForLogsAsync(1))[0];

        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}\.\d{3}Z_0001_POST_v1-messages\.json$"), fileName);
        Assert.Equal(ExchangeOutcome.Completed, log.Outcome);
        Assert.Equal("fake", log.Route);
        Assert.Equal(MessagesTarget, log.Request.Target);
        Assert.Equal(BodyFormat.Json, log.Request.Body!.Format);
        Assert.Equal("m", log.Request.Body.Content.GetProperty("model").GetString());
        Assert.Equal(200, log.Response!.StatusCode);
        Assert.Equal(BodyFormat.Json, log.Response.Body!.Format);
    }

    [Fact]
    public async Task Api_key_is_redacted_in_the_log()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();
        using HttpResponseMessage response = await SendMessageWithApiKeyAsync(fixture);

        (string fileName, ExchangeLog log) = (await fixture.WaitForLogsAsync(1))[0];

        Assert.Equal("sk-ant…wxyz", log.Request.Headers["x-api-key"]);
        string written = await File.ReadAllTextAsync(Path.Combine(fixture.LogsPath, fileName), fixture.CancellationToken);
        Assert.DoesNotContain(ApiKey, written);
    }

    [Fact]
    public async Task Sse_chunks_reach_the_client_before_the_upstream_finishes()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();
        CancellationToken cancellationToken = fixture.CancellationToken;

        using HttpRequestMessage request = new(HttpMethod.Post, "/fake/v1/stream") { Content = new StringContent("{}") };
        using HttpResponseMessage response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        using StreamReader reader = new(await response.Content.ReadAsStreamAsync(cancellationToken));

        // The upstream is blocked on the gate, so this line can only arrive if the proxy is not buffering.
        string? first = await reader.ReadLineAsync(cancellationToken).AsTask().WaitAsync(FirstLineTimeout, cancellationToken);
        Assert.Equal("event: message_start", first);

        fixture.StreamGate.SetResult();
        string rest = await reader.ReadToEndAsync(cancellationToken);
        Assert.Contains("event: message_stop", rest);

        ExchangeLog log = await fixture.WaitForFirstLogAsync();
        Assert.Equal(BodyFormat.Text, log.Response!.Body!.Format);
        Assert.Equal(ProxyFixture.FirstStreamEvent + ProxyFixture.LastStreamEvent, log.Response.Body.Content.GetString());
    }

    [Fact]
    public async Task Compressed_response_is_forwarded_compressed_and_logged_decompressed()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();

        using HttpResponseMessage response = await fixture.Client.GetAsync("/fake/v1/gzip", fixture.CancellationToken);

        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.Single());
        Assert.Equal(ProxyFixture.Gzip(ProxyFixture.CompressedJson), await response.Content.ReadAsByteArrayAsync(fixture.CancellationToken));

        ExchangeLog log = await fixture.WaitForFirstLogAsync();
        Assert.Equal(BodyFormat.Json, log.Response!.Body!.Format);
        Assert.Equal("gzip", log.Response.Body.ContentEncoding);
        Assert.True(log.Response.Body.Content.GetProperty("compressed").GetBoolean());
    }

    [Fact]
    public async Task Client_disconnect_cancels_upstream_and_logs_partial_response()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();
        CancellationToken cancellationToken = fixture.CancellationToken;

        HttpResponseMessage response = await fixture.Client.GetAsync("/fake/v1/hang", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        StreamReader reader = new(await response.Content.ReadAsStreamAsync(cancellationToken));
        Assert.Equal("data: partial", await reader.ReadLineAsync(cancellationToken));
        response.Dispose();

        await fixture.UpstreamCancelled.Task.WaitAsync(UpstreamCancelTimeout, cancellationToken);
        ExchangeLog log = await fixture.WaitForFirstLogAsync();
        Assert.Equal(ExchangeOutcome.ClientAborted, log.Outcome);
        Assert.Equal(ProxyFixture.PartialStreamEvent, log.Response!.Body!.Content.GetString());
    }

    [Fact]
    public async Task Unreachable_upstream_returns_502_and_logs_upstream_error()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();

        using StringContent content = new("{}");
        using HttpResponseMessage response = await fixture.Client.PostAsync("/dead/v1/messages", content, fixture.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        ExchangeLog log = await fixture.WaitForFirstLogAsync();
        Assert.Equal(ExchangeOutcome.UpstreamError, log.Outcome);
        Assert.Null(log.Response);
        Assert.NotNull(log.Error);
    }

    [Fact]
    public async Task Unknown_route_returns_404_without_logging()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();

        using HttpResponseMessage response = await fixture.Client.GetAsync("/nope/v1/models", fixture.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(Directory.GetFiles(fixture.LogsPath));
    }

    [Fact]
    public async Task Log_files_sort_in_request_order()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();

        for (int i = 0; i < 3; i++)
        {
            await fixture.PostMessageAsync($$"""{"i":{{i}}}""");
        }

        LogFile[] logs = await fixture.WaitForLogsAsync(3);
        Assert.Equal([1L, 2L, 3L], logs.Select(file => file.Log.Sequence));
        Assert.Equal([0, 1, 2], logs.Select(file => file.Log.Request.Body!.Content.GetProperty("i").GetInt32()));
    }

    private static async Task<HttpResponseMessage> SendMessageWithApiKeyAsync(ProxyFixture fixture)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, MessagesTarget)
        {
            Content = new StringContent(MessageJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", ApiKey);
        return await fixture.Client.SendAsync(request, fixture.CancellationToken);
    }
}
