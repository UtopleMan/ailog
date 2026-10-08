using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AiLog.Contracts;

namespace AiLog.Host;

/// <summary>
/// In-memory summaries of every log file in the logs folder. A FileSystemWatcher picks up files as they
/// appear (including ones written by other ailog instances) and pushes them to subscribers.
/// </summary>
internal sealed class LogIndex : IDisposable
{
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, ExchangeSummary> _items = new(StringComparer.Ordinal);
    private readonly Lock _subscribersLock = new();
    private readonly List<Channel<ExchangeSummary>> _subscribers = [];
    private readonly FileSystemWatcher _watcher;
    private readonly Task _initialScan;

    public LogIndex(AiLogOptions options)
    {
        _directory = options.LogsPath;
        Directory.CreateDirectory(_directory);

        // Start watching before scanning so a file written during the scan is not missed; the dictionary de-duplicates.
        _watcher = new FileSystemWatcher(_directory)
        {
            NotifyFilter = NotifyFilters.FileName,
            IncludeSubdirectories = false,
        };
        _watcher.Created += (_, e) => OnFileAppeared(e.FullPath);
        _watcher.Renamed += (_, e) => OnFileAppeared(e.FullPath); // the writer renames *.json.tmp to *.json
        _watcher.Deleted += (_, e) => _items.TryRemove(Path.GetFileNameWithoutExtension(e.FullPath), out ExchangeSummary? _);
        _watcher.Error += (_, _) => Task.Run(Scan); // buffer overflow: catch up from disk
        _watcher.EnableRaisingEvents = true;

        _initialScan = Task.Run(Scan);
    }

    /// <summary>All known exchanges, newest first.</summary>
    public async Task<List<ExchangeSummary>> GetAllAsync()
    {
        await _initialScan;
        return Newest(_items.Values);
    }

    public static List<ExchangeSummary> Newest(IEnumerable<ExchangeSummary> items) =>
        items.OrderByDescending(s => s.StartedAt).ThenByDescending(s => s.Sequence).ThenByDescending(s => s.Id, StringComparer.Ordinal).ToList();

    /// <summary>Receives every exchange added to the index from now on until disposed.</summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateUnbounded<ExchangeSummary>(new UnboundedChannelOptions { SingleReader = true });
        lock (_subscribersLock)
        {
            _subscribers.Add(channel);
        }

        return new Subscription(channel.Reader, () =>
        {
            lock (_subscribersLock)
            {
                _subscribers.Remove(channel);
            }

            channel.Writer.TryComplete();
        });
    }

    public static ExchangeSummary Summarize(ExchangeLog log)
    {
        var prefix = "/" + log.Route;
        var path = log.Request.Target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? log.Request.Target[prefix.Length..]
            : log.Request.Target;
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return new ExchangeSummary
        {
            Id = log.Id,
            Sequence = log.Sequence,
            StartedAt = log.StartedAt,
            Method = log.Request.Method,
            Route = log.Route,
            Path = path,
            StatusCode = log.Response?.StatusCode,
            DurationMs = log.DurationMs,
            Outcome = log.Outcome,
            RequestBytes = log.Request.Body?.SizeBytes ?? 0,
            ResponseBytes = log.Response?.Body?.SizeBytes ?? 0,
            Usage = TokenUsageExtractor.Extract(log.Response),
        };
    }

    private void Scan()
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            OnFileAppeared(path);
        }
    }

    private void OnFileAppeared(string path)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || _items.ContainsKey(Path.GetFileNameWithoutExtension(path)))
        {
            return;
        }

        var summary = TryLoad(path);
        if (summary is null || !_items.TryAdd(summary.Id, summary))
        {
            return;
        }

        lock (_subscribersLock)
        {
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(summary);
            }
        }
    }

    private static ExchangeSummary? TryLoad(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var log = JsonSerializer.Deserialize(File.ReadAllBytes(path), AiLogJsonContext.Default.ExchangeLog);
                return log is null ? null : Summarize(log);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < 5)
            {
                // Another process may still hold the file (e.g. copied in by hand); give it a moment.
                Thread.Sleep(50 * attempt);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"ailog: skipping {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }
    }

    public void Dispose() => _watcher.Dispose();

    public sealed class Subscription(ChannelReader<ExchangeSummary> reader, Action unsubscribe) : IDisposable
    {
        public ChannelReader<ExchangeSummary> Reader { get; } = reader;

        public void Dispose() => unsubscribe();
    }
}
