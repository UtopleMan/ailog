using System.Globalization;
using AiLog.Contracts;
using BlazorBlueprint.Components;

namespace AiLog.Web;

/// <summary>Formatting for grid cells and the detail page.</summary>
public static class Display
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss.fff", Culture);

    public static string FullTime(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", Culture);

    public static string Duration(double ms) => ms switch
    {
        < 1000 => $"{ms.ToString("0", Culture)} ms",
        < 60_000 => $"{(ms / 1000).ToString("0.0", Culture)} s",
        _ => $"{(int)(ms / 60_000)}m {(ms % 60_000 / 1000).ToString("00", Culture)}s",
    };

    /// <summary>"1,204" below 10k, then "48.2k" and "1.3M".</summary>
    public static string Tokens(long? tokens) => tokens switch
    {
        null => "—",
        < 10_000 => tokens.Value.ToString("N0", Culture),
        < 1_000_000 => (tokens.Value / 1000d).ToString("0.#", Culture) + "k",
        _ => (tokens.Value / 1_000_000d).ToString("0.#", Culture) + "M",
    };

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

        var fresh = input - (usage.CacheReadTokens ?? 0) - (usage.CacheWriteTokens ?? 0);
        var parts = new List<string> { $"{ExactTokens(fresh)} new" };
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

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{(bytes / 1024d).ToString("0.#", Culture)} KB",
        _ => $"{(bytes / (1024d * 1024)).ToString("0.#", Culture)} MB",
    };

    public static string Status(int? statusCode) => statusCode?.ToString(Culture) ?? "ERR";

    public static BadgeVariant StatusVariant(ExchangeSummary s) => StatusVariant(s.StatusCode);

    public static BadgeVariant StatusVariant(int? statusCode) => statusCode switch
    {
        null => BadgeVariant.SoftDestructive,
        >= 500 => BadgeVariant.SoftDestructive,
        >= 400 => BadgeVariant.SoftWarning,
        >= 200 and < 300 => BadgeVariant.SoftSuccess,
        _ => BadgeVariant.Secondary,
    };

    public static string? Outcome(ExchangeOutcome outcome) => outcome switch
    {
        ExchangeOutcome.ClientAborted => "client aborted",
        ExchangeOutcome.UpstreamError => "upstream error",
        _ => null,
    };
}
