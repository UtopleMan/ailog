using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AiLog.Contracts;
using AiLog.Shared.Providers;

namespace AiLog.Host;

/// <summary>
/// In-memory summaries of every log file in the logs folder. A FileSystemWatcher picks up files as they
/// appear (including ones written by other ailog instances) and pushes them to subscribers.
/// </summary>
internal sealed class LogIndex : IDisposable
{
    private const string LogFilePattern = "*.json";
    private const string LogFileExtension = ".json";
    private const int MaxLoadAttempts = 5;
    private const int RetryDelayStepMs = 50;

    private readonly string directory;
    private readonly ConcurrentDictionary<string, ExchangeSummary> items = new(StringComparer.Ordinal);
    private readonly Lock subscribersLock = new();
    private readonly List<Channel<ExchangeSummary>> subscribers = [];
    private readonly FileSystemWatcher watcher;
    private readonly Task initialScan;

    public LogIndex(AiLogOptions options)
    {
        directory = options.LogsPath;
        Directory.CreateDirectory(directory);

        // Start watching before scanning so a file written during the scan is not missed; the dictionary de-duplicates.
        watcher = CreateWatcher();
        initialScan = Task.Run(Scan);
    }

    /// <summary>All known exchanges, newest first.</summary>
    public async Task<List<ExchangeSummary>> GetAllAsync(CancellationToken cancellationToken)
    {
        await initialScan.WaitAsync(cancellationToken);
        return Newest(items.Values);
    }

    public static List<ExchangeSummary> Newest(IEnumerable<ExchangeSummary> summaries) =>
        summaries
            .OrderByDescending(summary => summary.StartedAt)
            .ThenByDescending(summary => summary.Sequence)
            .ThenByDescending(summary => summary.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>Receives every exchange added to the index from now on until disposed.</summary>
    public Subscription Subscribe()
    {
        Channel<ExchangeSummary> channel = Channel.CreateUnbounded<ExchangeSummary>(new UnboundedChannelOptions { SingleReader = true });
        lock (subscribersLock)
        {
            subscribers.Add(channel);
        }

        return new Subscription(channel.Reader, () => Unsubscribe(channel));
    }

    public static ExchangeSummary Summarize(ExchangeLog log)
    {
        return new ExchangeSummary
        {
            Id = log.Id,
            Sequence = log.Sequence,
            StartedAt = log.StartedAt,
            Method = log.Request.Method,
            Route = log.Route,
            Path = PathWithoutRoute(log),
            StatusCode = log.Response?.StatusCode,
            DurationMs = log.DurationMs,
            Outcome = log.Outcome,
            RequestBytes = log.Request.Body?.SizeBytes ?? 0,
            ResponseBytes = log.Response?.Body?.SizeBytes ?? 0,
            Usage = ProviderRegistry.ExtractUsage(log),
        };
    }

    public void Dispose() => watcher.Dispose();

    private FileSystemWatcher CreateWatcher()
    {
        FileSystemWatcher created = new(directory)
        {
            NotifyFilter = NotifyFilters.FileName,
            IncludeSubdirectories = false,
        };
        created.Created += (_, e) => OnFileAppeared(e.FullPath);

        // The writer renames *.json.tmp to *.json.
        created.Renamed += (_, e) => OnFileAppeared(e.FullPath);
        created.Deleted += (_, e) => items.TryRemove(Path.GetFileNameWithoutExtension(e.FullPath), out ExchangeSummary? _);

        // Buffer overflow: catch up from disk.
        created.Error += (_, _) => Task.Run(Scan);
        created.EnableRaisingEvents = true;
        return created;
    }

    private void Unsubscribe(Channel<ExchangeSummary> channel)
    {
        lock (subscribersLock)
        {
            subscribers.Remove(channel);
        }

        channel.Writer.TryComplete();
    }

    private static string PathWithoutRoute(ExchangeLog log)
    {
        string target = log.Request.Target;
        string prefix = "/" + log.Route;
        string path = target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? target[prefix.Length..] : target;
        return path.StartsWith('/') ? path : "/" + path;
    }

    private void Scan()
    {
        foreach (string path in Directory.EnumerateFiles(directory, LogFilePattern))
        {
            OnFileAppeared(path);
        }
    }

    private void OnFileAppeared(string path)
    {
        if (!path.EndsWith(LogFileExtension, StringComparison.OrdinalIgnoreCase) || items.ContainsKey(Path.GetFileNameWithoutExtension(path)))
        {
            return;
        }

        ExchangeSummary? summary = TryLoad(path);
        if (summary is null || !items.TryAdd(summary.Id, summary))
        {
            return;
        }

        Publish(summary);
    }

    private static ExchangeSummary? TryLoad(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                ExchangeLog? log = JsonSerializer.Deserialize(File.ReadAllBytes(path), AiLogJsonContext.Default.ExchangeLog);
                return log is null ? null : Summarize(log);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < MaxLoadAttempts)
            {
                // Another process may still hold the file (e.g. copied in by hand); give it a moment.
                Thread.Sleep(RetryDelayStepMs * attempt);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"ailog: skipping {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }
    }

    private void Publish(ExchangeSummary summary)
    {
        lock (subscribersLock)
        {
            foreach (Channel<ExchangeSummary> subscriber in subscribers)
            {
                subscriber.Writer.TryWrite(summary);
            }
        }
    }

    /// <summary>A live feed of newly indexed exchanges; dispose to stop receiving.</summary>
    public sealed class Subscription(ChannelReader<ExchangeSummary> reader, Action unsubscribe) : IDisposable
    {
        public ChannelReader<ExchangeSummary> Reader { get; } = reader;

        public void Dispose() => unsubscribe();
    }
}
