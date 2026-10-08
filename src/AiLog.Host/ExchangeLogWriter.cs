using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiLog.Contracts;

namespace AiLog.Host;

/// <summary>Writes one JSON file per exchange into the logs folder and prints a console summary line.</summary>
internal sealed class ExchangeLogWriter
{
    private const int MaxPathSlugLength = 80;
    private const string IdTimestampFormat = "yyyy-MM-dd'T'HH-mm-ss.fff'Z'";
    private const string MissingStatus = "ERR";
    private const string EmptySlug = "root";
    private const char SlugSeparator = '-';
    private const double MillisecondsPerSecond = 1000;

    // Keep SSE text and prompts readable instead of \u-escaping quotes, '<', '+' and non-ASCII characters.
    private static readonly AiLogJsonContext Json = new(new JsonSerializerOptions(AiLogJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private readonly string directory;
    private long sequence;

    public ExchangeLogWriter(AiLogOptions options)
    {
        directory = options.LogsPath;
        Directory.CreateDirectory(directory);
    }

    public long NextSequence() => Interlocked.Increment(ref sequence);

    /// <summary>e.g. 2026-10-08T14-23-05.123Z_0001_POST_v1-messages</summary>
    public static string BuildId(DateTimeOffset startedAt, long sequence, string method, string upstreamPath)
    {
        string timestamp = startedAt.UtcDateTime.ToString(IdTimestampFormat, CultureInfo.InvariantCulture);
        return $"{timestamp}_{sequence:D4}_{method.ToUpperInvariant()}_{Slug(upstreamPath)}";
    }

    public async Task WriteAsync(ExchangeLog entry, CancellationToken cancellationToken)
    {
        await SaveAsync(entry, cancellationToken);
        PrintSummary(entry);
    }

    /// <summary>Write-then-rename so readers (e.g. the UI) never see a half-written file.</summary>
    private async Task SaveAsync(ExchangeLog entry, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry, Json.ExchangeLog);
        string path = Path.Combine(directory, entry.Id + ".json");
        string temp = path + ".tmp";

        await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    private static void PrintSummary(ExchangeLog entry)
    {
        string status = entry.Response?.StatusCode.ToString(CultureInfo.InvariantCulture) ?? MissingStatus;
        string outcome = entry.Outcome == ExchangeOutcome.Completed ? "" : $" [{OutcomeName(entry.Outcome)}]";
        string target = entry.Request.Target.Split('?', 2)[0];
        double seconds = entry.DurationMs / MillisecondsPerSecond;
        Console.WriteLine(
            $"{entry.StartedAt.ToLocalTime():HH:mm:ss} {entry.Request.Method} {target} → {status} ({seconds:0.0}s) {entry.Id}.json{outcome}");
    }

    private static string OutcomeName(ExchangeOutcome outcome) => outcome switch
    {
        ExchangeOutcome.ClientAborted => "client_aborted",
        ExchangeOutcome.UpstreamError => "upstream_error",
        _ => "completed",
    };

    /// <summary>File-name-safe form of the path: runs of other characters collapse to a single '-'.</summary>
    private static string Slug(string upstreamPath)
    {
        string path = upstreamPath.Split('?', 2)[0].Trim('/');
        StringBuilder slug = new(path.Length);
        foreach (char c in path)
        {
            char safe = char.IsAsciiLetterOrDigit(c) || c is '.' or '_' ? c : SlugSeparator;
            if (safe != SlugSeparator || (slug.Length > 0 && slug[^1] != SlugSeparator))
            {
                slug.Append(safe);
            }
        }

        string result = slug.ToString().Trim(SlugSeparator);
        if (result.Length > MaxPathSlugLength)
        {
            result = result[..MaxPathSlugLength].TrimEnd(SlugSeparator);
        }

        return result.Length == 0 ? EmptySlug : result;
    }
}
