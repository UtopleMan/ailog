using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using AiLog.Contracts;

namespace AiLog.Host.Tests;

public sealed class UiApiTests
{
    private const string LogsApi = "/_ailog/api/logs";
    private const string ForeignLogId = "2026-01-01T00-00-00.000Z_0042_POST_v1-messages";
    private const string AppShellBaseTag = """
        <base href="/_ailog/"
        """;

    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ListPollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan LogWriteGrace = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task List_returns_summaries_newest_first()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();
        foreach (string model in new[] { "a", "b", "c" })
        {
            await fixture.PostMessageAsync($$"""{"model":"{{model}}"}""");
        }

        LogFile[] files = await fixture.WaitForLogsAsync(3);
        List<ExchangeSummary> list = await WaitForListAsync(fixture, 3);

        Assert.Equal(files.Select(file => file.Log.Id).Reverse(), list.Select(summary => summary.Id));
    }

    [Fact]
    public async Task Summary_describes_the_exchange()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();
        const string requestJson = """{"model":"a"}""";
        using StringContent content = new(requestJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await fixture.Client.PostAsync("/fake/v1/messages?beta=true", content, fixture.CancellationToken);

        ExchangeSummary summary = Assert.Single(await WaitForListAsync(fixture, 1));

        Assert.Equal(1, summary.Sequence);
        Assert.Equal("POST", summary.Method);
        Assert.Equal("fake", summary.Route);
        Assert.Equal("/v1/messages?beta=true", summary.Path);
        Assert.Equal(200, summary.StatusCode);
        Assert.Equal(ExchangeOutcome.Completed, summary.Outcome);
        Assert.Equal(Encoding.UTF8.GetByteCount(requestJson), summary.RequestBytes);
        Assert.True(summary.ResponseBytes > 0);
        Assert.Null(summary.Usage);
    }

    [Fact]
    public async Task Event_stream_announces_ready_then_each_new_exchange()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();
        await using EventStream events = await EventStream.OpenAsync(fixture);

        Assert.Equal("ready", (await events.NextAsync()).EventType);

        await fixture.PostMessageAsync();
        SseItem<string> item = await events.NextAsync();
        ExchangeLog log = await fixture.WaitForFirstLogAsync();

        Assert.Equal("exchange", item.EventType);
        Assert.Equal(log.Id, item.EventId);
        ExchangeSummary summary = ReadSummary(item);
        Assert.Equal(log.Id, summary.Id);
        Assert.Equal("/v1/messages", summary.Path);
    }

    [Fact]
    public async Task Files_written_by_someone_else_are_announced_with_their_usage()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();
        await using EventStream events = await EventStream.OpenPastReadyAsync(fixture);

        ExchangeLog foreign = ForeignLog(ForeignLogId);
        await WriteLikeAnotherInstanceAsync(fixture.LogsPath, foreign, fixture.CancellationToken);

        SseItem<string> item = await events.NextAsync();
        Assert.Equal(foreign.Id, item.EventId);
        ExchangeSummary summary = ReadSummary(item);
        Assert.Equal(49304, summary.Usage!.InputTokens);
        Assert.Equal(356, summary.Usage.OutputTokens);
    }

    [Fact]
    public async Task Files_written_by_someone_else_are_announced_only_once()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();
        await using EventStream events = await EventStream.OpenPastReadyAsync(fixture);
        ExchangeLog foreign = ForeignLog(ForeignLogId);
        await WriteLikeAnotherInstanceAsync(fixture.LogsPath, foreign, fixture.CancellationToken);
        await events.NextAsync();

        // Watcher events for the same file (create, rename) must not produce a second announcement.
        await fixture.PostMessageAsync();
        SseItem<string> next = await events.NextAsync();

        Assert.NotEqual(foreign.Id, next.EventId);
    }

    [Fact]
    public async Task Files_already_in_the_folder_at_startup_are_listed()
    {
        using CancellationTokenSource timeout = new(ProxyFixture.TestTimeout);
        string logsPath = ProxyFixture.NewLogsPath();
        Directory.CreateDirectory(logsPath);
        ExchangeLog existing = ForeignLog("2026-01-01T00-00-00.000Z_0001_POST_v1-messages");
        await WriteLikeAnotherInstanceAsync(logsPath, existing, timeout.Token);
        await File.WriteAllTextAsync(Path.Combine(logsPath, "not-a-log.json"), """{"hello":1}""", timeout.Token);

        await using ProxyFixture fixture = await ProxyFixture.StartAsync(logsPath);
        List<ExchangeSummary>? list = await fixture.Client.GetFromJsonAsync(LogsApi, AiLogApiJsonContext.Default.ListExchangeSummary, fixture.CancellationToken);

        Assert.Equal(existing.Id, Assert.Single(list!).Id);
    }

    [Fact]
    public async Task Reconnecting_client_finds_exchanges_from_while_it_was_away_in_the_list()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();
        EventStream first = await EventStream.OpenPastReadyAsync(fixture);
        await first.DisposeAsync();

        await fixture.PostMessageAsync();
        ExchangeLog missed = await fixture.WaitForFirstLogAsync();

        await using EventStream second = await EventStream.OpenAsync(fixture);
        Assert.Equal("ready", (await second.NextAsync()).EventType);
        List<ExchangeSummary> list = await WaitForListAsync(fixture, 1);
        Assert.Equal(missed.Id, Assert.Single(list).Id);
    }

    [Fact]
    public async Task Ui_paths_are_not_proxied_or_logged()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();
        CancellationToken cancellationToken = fixture.CancellationToken;

        using HttpClient noRedirects = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = fixture.Client.BaseAddress };
        using HttpResponseMessage redirect = await noRedirects.GetAsync("/_ailog", cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/_ailog/", redirect.Headers.Location!.OriginalString);

        using HttpResponseMessage shell = await fixture.Client.GetAsync("/_ailog/", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Contains(AppShellBaseTag, await shell.Content.ReadAsStringAsync(cancellationToken));

        using HttpResponseMessage css = await fixture.Client.GetAsync("/_ailog/_content/BlazorBlueprint.Components/blazorblueprint.css", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);

        using HttpResponseMessage unknownApi = await fixture.Client.GetAsync("/_ailog/api/nope", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);

        await Task.Delay(LogWriteGrace, cancellationToken);
        Assert.Empty(Directory.GetFiles(fixture.LogsPath, "*.json"));
    }

    [Fact]
    public async Task Detail_returns_the_log_file_as_written()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();
        CancellationToken cancellationToken = fixture.CancellationToken;
        ExchangeLog log = ForeignLog("2026-01-01T00-00-00.000Z_0001_POST_v1-messages");
        await WriteLikeAnotherInstanceAsync(fixture.LogsPath, log, cancellationToken);

        using HttpResponseMessage response = await fixture.Client.GetAsync($"{LogsApi}/{log.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        byte[] written = await File.ReadAllBytesAsync(LogFilePath(fixture.LogsPath, log), cancellationToken);
        Assert.Equal(written, await response.Content.ReadAsByteArrayAsync(cancellationToken));
        ExchangeLog? roundTripped = await JsonSerializer.DeserializeAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            AiLogJsonContext.Default.ExchangeLog,
            cancellationToken);
        Assert.Equal(log.Id, roundTripped!.Id);
    }

    [Fact]
    public async Task Detail_of_a_missing_log_is_not_found()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();

        using HttpResponseMessage response = await fixture.Client.GetAsync($"{LogsApi}/2026-01-01T00-00-00.000Z_0099_POST_nope", fixture.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    [InlineData("..%5Csecret")]
    [InlineData(".hidden")]
    [InlineData("a%20b")]
    public async Task Detail_rejects_ids_that_are_not_plain_file_names(string id)
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();

        using HttpResponseMessage response = await fixture.Client.GetAsync($"{LogsApi}/{id}", fixture.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("""a\b""")]
    [InlineData("")]
    public void Log_ids_cannot_leave_the_logs_folder(string id) =>
        Assert.Null(UiEndpoints.ResolveLogFile(Path.GetTempPath(), id));

    [Fact]
    public async Task Exchange_pages_serve_the_app_shell_even_though_ids_contain_a_dot()
    {
        await using ProxyFixture fixture = await ProxyFixture.StartAsync();

        using HttpResponseMessage page = await fixture.Client.GetAsync("/_ailog/exchanges/2026-10-08T14-23-05.123Z_0001_POST_v1-messages", fixture.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains(AppShellBaseTag, await page.Content.ReadAsStringAsync(fixture.CancellationToken));
    }

    [Fact]
    public void A_proxy_route_named_after_the_ui_prefix_is_refused()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            AiLogApp.Build(["--port", "0", "--logs", Path.GetTempPath(), "--AiLog:Routes:_AILOG=http://localhost:1"]));

        Assert.Contains("reserved", error.Message);
    }

    private static ExchangeSummary ReadSummary(SseItem<string> item) =>
        JsonSerializer.Deserialize(item.Data, AiLogApiJsonContext.Default.ExchangeSummary)!;

    private static async Task<List<ExchangeSummary>> WaitForListAsync(ProxyFixture fixture, int count)
    {
        DateTime deadline = DateTime.UtcNow + EventTimeout;
        while (true)
        {
            List<ExchangeSummary> list = (await fixture.Client.GetFromJsonAsync(LogsApi, AiLogApiJsonContext.Default.ListExchangeSummary, fixture.CancellationToken))!;
            if (list.Count >= count || DateTime.UtcNow > deadline)
            {
                return list;
            }

            await Task.Delay(ListPollInterval, fixture.CancellationToken);
        }
    }

    private static ExchangeLog ForeignLog(string id) => new()
    {
        Id = id,
        Sequence = 42,
        StartedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        CompletedAt = DateTimeOffset.Parse("2026-01-01T00:00:01Z"),
        DurationMs = 1000,
        Route = "anthropic",
        UpstreamUrl = "https://api.anthropic.com/v1/messages",
        Outcome = ExchangeOutcome.Completed,
        Request = new LoggedRequest { Method = "POST", Target = "/anthropic/v1/messages", Headers = [] },
        Response = new LoggedResponse
        {
            StatusCode = 200,
            Headers = new() { ["Content-Type"] = "application/json" },
            Body = LoggedBodies.Json("""{"usage":{"input_tokens":1204,"cache_creation_input_tokens":100,"cache_read_input_tokens":48000,"output_tokens":356}}"""),
        },
    };

    /// <summary>Same write-then-rename as ExchangeLogWriter, as another ailog instance would do.</summary>
    private static async Task WriteLikeAnotherInstanceAsync(string directory, ExchangeLog log, CancellationToken cancellationToken)
    {
        string path = LogFilePath(directory, log);
        string temporaryPath = path + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(log, AiLogJsonContext.Default.ExchangeLog), cancellationToken);
        File.Move(temporaryPath, path);
    }

    private static string LogFilePath(string directory, ExchangeLog log) => Path.Combine(directory, log.Id + ".json");

    private sealed class EventStream(HttpResponseMessage response, IAsyncEnumerator<SseItem<string>> items) : IAsyncDisposable
    {
        public static async Task<EventStream> OpenAsync(ProxyFixture fixture)
        {
            CancellationToken cancellationToken = fixture.CancellationToken;
            HttpResponseMessage response = await fixture.Client.GetAsync($"{LogsApi}/events", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
            Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new EventStream(response, SseParser.Create(stream).EnumerateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken));
        }

        public static async Task<EventStream> OpenPastReadyAsync(ProxyFixture fixture)
        {
            EventStream events = await OpenAsync(fixture);
            Assert.Equal("ready", (await events.NextAsync()).EventType);
            return events;
        }

        public async Task<SseItem<string>> NextAsync()
        {
            Assert.True(await items.MoveNextAsync().AsTask().WaitAsync(EventTimeout), "event stream ended");
            return items.Current;
        }

        public async ValueTask DisposeAsync()
        {
            response.Dispose();
            try
            {
                await items.DisposeAsync();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The stream was torn down underneath the parser.
            }
        }
    }
}
