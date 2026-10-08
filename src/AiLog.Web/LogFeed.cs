using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using AiLog.Contracts;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace AiLog.Web;

/// <summary>Connection state of the live exchange feed.</summary>
public enum FeedState
{
    /// <summary>First connection attempt in progress.</summary>
    Connecting,

    /// <summary>Subscribed; new log files arrive as events.</summary>
    Live,

    /// <summary>The stream dropped; waiting to retry.</summary>
    Reconnecting,
}

/// <summary>
/// Keeps the list of exchanges current: subscribes to the server's SSE stream, then (re)loads the full list
/// on every connect and merges by id, so nothing is missed or duplicated across reconnects.
/// </summary>
public sealed class LogFeed(HttpClient http) : IAsyncDisposable
{
    private const string ExchangeEvent = "exchange";
    private const string ReadyEvent = "ready";

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, ExchangeSummary> byId = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stop = new();
    private Task? loop;

    /// <summary>Newest first. Replaced (never mutated) on change so the grid sees a new reference.</summary>
    public IReadOnlyList<ExchangeSummary> Items { get; private set; } = [];

    /// <summary>Current connection state.</summary>
    public FeedState State { get; private set; } = FeedState.Connecting;

    /// <summary>Raised when <see cref="Items"/> or <see cref="State"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Starts the feed loop once; later calls do nothing.</summary>
    public void Start() => loop ??= RunAsync(stop.Token);

    /// <summary>Stops the feed loop and waits for it to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        if (loop is not null)
        {
            await loop;
        }

        stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await TryListenAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            SetState(FeedState.Reconnecting);
            await WaitBeforeReconnectAsync(cancellationToken);
        }
    }

    private async Task TryListenAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ListenAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposed: stopping is not a failure.
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            Console.WriteLine($"ailog: event stream failed: {ex.Message}");
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "api/logs/events");
        request.SetBrowserResponseStreamingEnabled(true);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        SseParser<ExchangeSummary?> parser = SseParser.Create(stream, static (type, data) => type == ExchangeEvent
            ? JsonSerializer.Deserialize(data, AiLogApiJsonContext.Default.ExchangeSummary)
            : null);

        await foreach (SseItem<ExchangeSummary?> item in parser.EnumerateAsync(cancellationToken))
        {
            await HandleAsync(item, cancellationToken);
        }
    }

    private async Task HandleAsync(SseItem<ExchangeSummary?> item, CancellationToken cancellationToken)
    {
        if (item.EventType == ReadyEvent)
        {
            // Subscribed: anything written from here on arrives as an event, so the list cannot miss a file.
            List<ExchangeSummary>? all = await http.GetFromJsonAsync("api/logs", AiLogApiJsonContext.Default.ListExchangeSummary, cancellationToken);
            Merge(all ?? []);
        }
        else if (item.Data is { } summary)
        {
            Merge([summary]);
        }
    }

    private static async Task WaitBeforeReconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ReconnectDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Disposed while waiting; the loop sees the cancellation and ends.
        }
    }

    private void Merge(IEnumerable<ExchangeSummary> summaries)
    {
        bool added = false;
        foreach (ExchangeSummary summary in summaries)
        {
            added |= byId.TryAdd(summary.Id, summary);
        }

        if (added)
        {
            Items = byId.Values
                .OrderByDescending(s => s.StartedAt)
                .ThenByDescending(s => s.Sequence)
                .ThenByDescending(s => s.Id, StringComparer.Ordinal)
                .ToList();
        }

        if (added || State != FeedState.Live)
        {
            State = FeedState.Live;
            Changed?.Invoke();
        }
    }

    private void SetState(FeedState state)
    {
        if (State != state)
        {
            State = state;
            Changed?.Invoke();
        }
    }
}
