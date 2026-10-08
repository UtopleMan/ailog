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

    // Keep SSE text and prompts readable instead of \u-escaping quotes, '<', '+' and non-ASCII characters.
    private static readonly AiLogJsonContext Json = new(new JsonSerializerOptions(AiLogJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private readonly string _directory;
    private long _sequence;

    public ExchangeLogWriter(AiLogOptions options)
    {
        _directory = options.LogsPath;
        Directory.CreateDirectory(_directory);
    }

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>e.g. 2026-10-08T14-23-05.123Z_0001_POST_v1-messages</summary>
    public static string BuildId(DateTimeOffset startedAt, long sequence, string method, string upstreamPath)
    {
        var timestamp = startedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH-mm-ss.fff'Z'", CultureInfo.InvariantCulture);
        return $"{timestamp}_{sequence:D4}_{method.ToUpperInvariant()}_{Slug(upstreamPath)}";
    }

    public async Task WriteAsync(ExchangeLog entry)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, Json.ExchangeLog);
        var path = Path.Combine(_directory, entry.Id + ".json");
        var temp = path + ".tmp";

        // Write-then-rename so readers (e.g. the future UI) never see a half-written file.
        await File.WriteAllBytesAsync(temp, bytes);
        File.Move(temp, path, overwrite: true);

        var status = entry.Response?.StatusCode.ToString(CultureInfo.InvariantCulture) ?? "ERR";
        var outcome = entry.Outcome == ExchangeOutcome.Completed ? "" : $" [{OutcomeName(entry.Outcome)}]";
        var target = entry.Request.Target.Split('?', 2)[0];
        Console.WriteLine(
            $"{entry.StartedAt.ToLocalTime():HH:mm:ss} {entry.Request.Method} {target} → {status} ({entry.DurationMs / 1000:0.0}s) {entry.Id}.json{outcome}");
    }

    private static string OutcomeName(ExchangeOutcome outcome) => outcome switch
    {
        ExchangeOutcome.ClientAborted => "client_aborted",
        ExchangeOutcome.UpstreamError => "upstream_error",
        _ => "completed",
    };

    private static string Slug(string upstreamPath)
    {
        var path = upstreamPath.Split('?', 2)[0].Trim('/');
        var slug = new StringBuilder(path.Length);
        foreach (var c in path)
        {
            var safe = char.IsAsciiLetterOrDigit(c) || c is '.' or '_' ? c : '-';
            if (safe != '-' || (slug.Length > 0 && slug[^1] != '-'))
            {
                slug.Append(safe);
            }
        }

        var result = slug.ToString().Trim('-');
        if (result.Length > MaxPathSlugLength)
        {
            result = result[..MaxPathSlugLength].TrimEnd('-');
        }

        return result.Length == 0 ? "root" : result;
    }
}
