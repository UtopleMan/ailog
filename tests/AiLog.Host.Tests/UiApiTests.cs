using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using AiLog.Contracts;

namespace AiLog.Host.Tests;

public sealed class UiApiTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task List_returns_summaries_newest_first()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        foreach (var model in new[] { "a", "b", "c" })
        {
            using var response = await fixture.Client.PostAsync("/fake/v1/messages?beta=true", Json($$"""{"model":"{{model}}"}"""));
        }

        var files = await fixture.WaitForLogsAsync(3);
        var list = await WaitForListAsync(fixture, 3);

        Assert.Equal(files.Select(f => f.Log.Id).Reverse(), list.Select(s => s.Id));
        var newest = list[0];
        Assert.Equal(3, newest.Sequence);
        Assert.Equal("POST", newest.Method);
        Assert.Equal("fake", newest.Route);
        Assert.Equal("/v1/messages?beta=true", newest.Path);
        Assert.Equal(200, newest.StatusCode);
        Assert.Equal(ExchangeOutcome.Completed, newest.Outcome);
        Assert.Equal(13, newest.RequestBytes);
        Assert.True(newest.ResponseBytes > 0);
        Assert.Null(newest.Usage);
    }

    [Fact]
    public async Task Event_stream_announces_ready_then_each_new_exchange()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        await using var events = await EventStream.OpenAsync(fixture.Client);

        Assert.Equal("ready", (await events.NextAsync()).EventType);

        using var response = await fixture.Client.PostAsync("/fake/v1/messages", Json("{}"));
        var item = await events.NextAsync();
        var (_, log) = (await fixture.WaitForLogsAsync(1))[0];

        Assert.Equal("exchange", item.EventType);
        Assert.Equal(log.Id, item.EventId);
        var summary = JsonSerializer.Deserialize(item.Data, AiLogApiJsonContext.Default.ExchangeSummary)!;
        Assert.Equal(log.Id, summary.Id);
        Assert.Equal("/v1/messages", summary.Path);
    }

    [Fact]
    public async Task Files_written_by_someone_else_are_picked_up_and_announced_once()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        await using var events = await EventStream.OpenAsync(fixture.Client);
        await events.NextAsync(); // ready

        var foreign = ForeignLog("2026-01-01T00-00-00.000Z_0042_POST_v1-messages");
        await WriteLikeAnotherInstanceAsync(fixture.LogsPath, foreign);

        var item = await events.NextAsync();
        Assert.Equal(foreign.Id, item.EventId);
        var summary = JsonSerializer.Deserialize(item.Data, AiLogApiJsonContext.Default.ExchangeSummary)!;
        Assert.Equal(49304, summary.Usage!.InputTokens);
        Assert.Equal(356, summary.Usage.OutputTokens);

        // Watcher events for the same file (create, rename) must not produce a second announcement.
        using var response = await fixture.Client.PostAsync("/fake/v1/messages", Json("{}"));
        var next = await events.NextAsync();
        Assert.NotEqual(foreign.Id, next.EventId);
    }

    [Fact]
    public async Task Files_already_in_the_folder_at_startup_are_listed()
    {
        var logsPath = Path.Combine(Path.GetTempPath(), "ailog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logsPath);
        var existing = ForeignLog("2026-01-01T00-00-00.000Z_0001_POST_v1-messages");
        await WriteLikeAnotherInstanceAsync(logsPath, existing);
        await File.WriteAllTextAsync(Path.Combine(logsPath, "not-a-log.json"), "{\"hello\":1}");

        await using var fixture = await ProxyFixture.StartAsync(logsPath);
        var list = await fixture.Client.GetFromJsonAsync("/_ailog/api/logs", AiLogApiJsonContext.Default.ListExchangeSummary);

        Assert.Equal(existing.Id, Assert.Single(list!).Id);
    }

    [Fact]
    public async Task Reconnecting_client_finds_exchanges_from_while_it_was_away_in_the_list()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        await using (var first = await EventStream.OpenAsync(fixture.Client))
        {
            await first.NextAsync();
        }

        using (await fixture.Client.PostAsync("/fake/v1/messages", Json("{}")))
        {
        }

        var (_, missed) = (await fixture.WaitForLogsAsync(1))[0];

        await using var second = await EventStream.OpenAsync(fixture.Client);
        Assert.Equal("ready", (await second.NextAsync()).EventType);
        var list = await WaitForListAsync(fixture, 1);
        Assert.Equal(missed.Id, Assert.Single(list).Id);
    }

    [Fact]
    public async Task Ui_paths_are_not_proxied_or_logged()
    {
        await using var fixture = await ProxyFixture.StartAsync();

        using var noRedirects = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = fixture.Client.BaseAddress };
        using var redirect = await noRedirects.GetAsync("/_ailog");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/_ailog/", redirect.Headers.Location!.OriginalString);

        using var shell = await fixture.Client.GetAsync("/_ailog/");
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Contains("<base href=\"/_ailog/\"", await shell.Content.ReadAsStringAsync());

        using var css = await fixture.Client.GetAsync("/_ailog/_content/BlazorBlueprint.Components/blazorblueprint.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);

        using var unknownApi = await fixture.Client.GetAsync("/_ailog/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);

        await Task.Delay(200);
        Assert.Empty(Directory.GetFiles(fixture.LogsPath, "*.json"));
    }

    [Fact]
    public async Task Detail_returns_the_log_file_as_written()
    {
        await using var fixture = await ProxyFixture.StartAsync();
        var log = ForeignLog("2026-01-01T00-00-00.000Z_0001_POST_v1-messages");
        await WriteLikeAnotherInstanceAsync(fixture.LogsPath, log);

        using var response = await fixture.Client.GetAsync($"/_ailog/api/logs/{log.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(fixture.LogsPath, log.Id + ".json")), await response.Content.ReadAsByteArrayAsync());
        var roundTripped = JsonSerializer.Deserialize(await response.Content.ReadAsStreamAsync(), AiLogJsonContext.Default.ExchangeLog);
        Assert.Equal(log.Id, roundTripped!.Id);
    }

    [Fact]
    public async Task Detail_of_a_missing_log_is_not_found()
    {
        await using var fixture = await ProxyFixture.StartAsync();

        using var response = await fixture.Client.GetAsync("/_ailog/api/logs/2026-01-01T00-00-00.000Z_0099_POST_nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    [InlineData("..%5Csecret")]
    [InlineData(".hidden")]
    [InlineData("a%20b")]
    public async Task Detail_rejects_ids_that_are_not_plain_file_names(string id)
    {
        await using var fixture = await ProxyFixture.StartAsync();

        using var response = await fixture.Client.GetAsync($"/_ailog/api/logs/{id}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void Log_ids_cannot_leave_the_logs_folder(string id) =>
        Assert.Null(UiEndpoints.ResolveLogFile(Path.GetTempPath(), id));

    [Fact]
    public async Task Exchange_pages_serve_the_app_shell_even_though_ids_contain_a_dot()
    {
        await using var fixture = await ProxyFixture.StartAsync();

        using var page = await fixture.Client.GetAsync("/_ailog/exchanges/2026-10-08T14-23-05.123Z_0001_POST_v1-messages");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<base href=\"/_ailog/\"", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public void A_proxy_route_named_after_the_ui_prefix_is_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            AiLogApp.Build(["--port", "0", "--logs", Path.GetTempPath(), "--AiLog:Routes:_AILOG=http://localhost:1"]));

        Assert.Contains("reserved", error.Message);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<List<ExchangeSummary>> WaitForListAsync(ProxyFixture fixture, int count)
    {
        var deadline = DateTime.UtcNow + EventTimeout;
        while (true)
        {
            var list = await fixture.Client.GetFromJsonAsync("/_ailog/api/logs", AiLogApiJsonContext.Default.ListExchangeSummary);
            if (list!.Count >= count || DateTime.UtcNow > deadline)
            {
                return list;
            }

            await Task.Delay(25);
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
            Body = new LoggedBody
            {
                Format = BodyFormat.Json,
                SizeBytes = 100,
                Content = JsonDocument.Parse("""{"usage":{"input_tokens":1204,"cache_creation_input_tokens":100,"cache_read_input_tokens":48000,"output_tokens":356}}""").RootElement.Clone(),
            },
        },
    };

    /// <summary>Same write-then-rename as ExchangeLogWriter, as another ailog instance would do.</summary>
    private static async Task WriteLikeAnotherInstanceAsync(string directory, ExchangeLog log)
    {
        var path = Path.Combine(directory, log.Id + ".json");
        await File.WriteAllBytesAsync(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(log, AiLogJsonContext.Default.ExchangeLog));
        File.Move(path + ".tmp", path);
    }

    private sealed class EventStream : IAsyncDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly IAsyncEnumerator<SseItem<string>> _items;

        private EventStream(HttpResponseMessage response, IAsyncEnumerator<SseItem<string>> items)
        {
            _response = response;
            _items = items;
        }

        public static async Task<EventStream> OpenAsync(HttpClient client)
        {
            var response = await client.GetAsync("/_ailog/api/logs/events", HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
            var stream = await response.Content.ReadAsStreamAsync();
            return new EventStream(response, SseParser.Create(stream).EnumerateAsync().GetAsyncEnumerator());
        }

        public async Task<SseItem<string>> NextAsync()
        {
            Assert.True(await _items.MoveNextAsync().AsTask().WaitAsync(EventTimeout), "event stream ended");
            return _items.Current;
        }

        public async ValueTask DisposeAsync()
        {
            _response.Dispose();
            try
            {
                await _items.DisposeAsync();
            }
            catch (Exception)
            {
                // The stream was torn down underneath the parser.
            }
        }
    }
}
