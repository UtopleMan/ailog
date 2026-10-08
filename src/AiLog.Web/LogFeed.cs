using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using AiLog.Contracts;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace AiLog.Web;

public enum FeedState
{
    Connecting,
    Live,
    Reconnecting,
}

/// <summary>
/// Keeps the list of exchanges current: subscribes to the server's SSE stream, then (re)loads the full list
/// on every connect and merges by id, so nothing is missed or duplicated across reconnects.
/// </summary>
public sealed class LogFeed(HttpClient http) : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, ExchangeSummary> _byId = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <summary>Newest first. Replaced (never mutated) on change so the grid sees a new reference.</summary>
    public IReadOnlyList<ExchangeSummary> Items { get; private set; } = [];
    public FeedState State { get; private set; } = FeedState.Connecting;
    public event Action? Changed;

    public void Start() => _loop ??= RunAsync(_stop.Token);

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "api/logs/events");
                request.SetBrowserResponseStreamingEnabled(true);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(stop);

                var parser = SseParser.Create(stream, static (type, data) => type == "exchange"
                    ? JsonSerializer.Deserialize(data, AiLogApiJsonContext.Default.ExchangeSummary)
                    : null);

                await foreach (var item in parser.EnumerateAsync(stop))
                {
                    if (item.EventType == "ready")
                    {
                        // Subscribed: anything written from here on arrives as an event, so the list cannot miss a file.
                        var all = await http.GetFromJsonAsync("api/logs", AiLogApiJsonContext.Default.ListExchangeSummary, stop);
                        Merge(all ?? [], FeedState.Live);
                    }
                    else if (item.Data is { } summary)
                    {
                        Merge([summary], FeedState.Live);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ailog: event stream failed: {ex.Message}");
            }

            SetState(FeedState.Reconnecting);
            try
            {
                await Task.Delay(ReconnectDelay, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Merge(IEnumerable<ExchangeSummary> summaries, FeedState state)
    {
        var added = false;
        foreach (var summary in summaries)
        {
            added |= _byId.TryAdd(summary.Id, summary);
        }

        if (added)
        {
            Items = _byId.Values
                .OrderByDescending(s => s.StartedAt)
                .ThenByDescending(s => s.Sequence)
                .ThenByDescending(s => s.Id, StringComparer.Ordinal)
                .ToList();
        }

        if (added || State != state)
        {
            State = state;
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

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            await _loop;
        }

        _stop.Dispose();
    }
}
