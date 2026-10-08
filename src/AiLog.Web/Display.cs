using System.Globalization;
using AiLog.Contracts;
using BlazorBlueprint.Components;

namespace AiLog.Web;

/// <summary>Formatting for grid cells and the detail page.</summary>
public static class Display
{
    private const double MillisecondsPerSecond = 1000;
    private const double MillisecondsPerMinute = 60_000;
    private const long ExactTokenLimit = 10_000;
    private const long Thousand = 1000;
    private const long Million = 1_000_000;
    private const long Kibibyte = 1024;
    private const long Mebibyte = Kibibyte * Kibibyte;

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>Local time of day with milliseconds.</summary>
    public static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss.fff", Culture);

    /// <summary>Local date and time with milliseconds and offset.</summary>
    public static string FullTime(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", Culture);

    /// <summary>"350 ms", "4.2 s" or "2m 05s".</summary>
    public static string Duration(double ms) => ms switch
    {
        < MillisecondsPerSecond => $"{ms.ToString("0", Culture)} ms",
        < MillisecondsPerMinute => $"{(ms / MillisecondsPerSecond).ToString("0.0", Culture)} s",
        _ => $"{(int)(ms / MillisecondsPerMinute)}m {(ms % MillisecondsPerMinute / MillisecondsPerSecond).ToString("00", Culture)}s",
    };

    /// <summary>"1,204" below 10k, then "48.2k" and "1.3M".</summary>
    public static string Tokens(long? tokens) => tokens switch
    {
        null => "—",
        < ExactTokenLimit => tokens.Value.ToString("N0", Culture),
        < Million => ((double)tokens.Value / Thousand).ToString("0.#", Culture) + "k",
        _ => ((double)tokens.Value / Million).ToString("0.#", Culture) + "M",
    };

    /// <summary>Token count with thousands separators, or "unknown".</summary>
    public static string ExactTokens(long? tokens) => tokens?.ToString("N0", Culture) ?? "unknown";

    /// <summary>e.g. "1,204 new · 48,000 cache read · 0 cache write".</summary>
    public static string InputBreakdown(TokenUsage? usage)
    {
        if (usage?.InputTokens is not { } input)
        {
            return "No input token count in the response";
        }

        if (usage.CacheReadTokens is null && usage.CacheWriteTokens is null)
        {
            return $"{ExactTokens(input)} input tokens";
        }

        long fresh = input - (usage.CacheReadTokens ?? 0) - (usage.CacheWriteTokens ?? 0);
        List<string> parts = [$"{ExactTokens(fresh)} new"];
        if (usage.CacheReadTokens is { } read)
        {
            parts.Add($"{ExactTokens(read)} cache read");
        }

        if (usage.CacheWriteTokens is { } write)
        {
            parts.Add($"{ExactTokens(write)} cache write");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"512 B", "3.4 KB" or "1.2 MB".</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        < Kibibyte => $"{bytes} B",
        < Mebibyte => $"{((double)bytes / Kibibyte).ToString("0.#", Culture)} KB",
        _ => $"{((double)bytes / Mebibyte).ToString("0.#", Culture)} MB",
    };

    /// <summary>The status code, or "ERR" when no response arrived.</summary>
    public static string Status(int? statusCode) => statusCode?.ToString(Culture) ?? "ERR";

    /// <summary>Badge colour for a summary's status code.</summary>
    public static BadgeVariant StatusVariant(ExchangeSummary summary) => StatusVariant(summary.StatusCode);

    /// <summary>Badge colour for a status code: red for errors, amber for client errors, green for success.</summary>
    public static BadgeVariant StatusVariant(int? statusCode) => statusCode switch
    {
        null => BadgeVariant.SoftDestructive,
        >= 500 => BadgeVariant.SoftDestructive,
        >= 400 => BadgeVariant.SoftWarning,
        >= 200 and < 300 => BadgeVariant.SoftSuccess,
        _ => BadgeVariant.Secondary,
    };

    /// <summary>A label for unusual outcomes; null for a normal completion.</summary>
    public static string? Outcome(ExchangeOutcome outcome) => outcome switch
    {
        ExchangeOutcome.ClientAborted => "client aborted",
        ExchangeOutcome.UpstreamError => "upstream error",
        _ => null,
    };
}
