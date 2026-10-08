namespace AiLog.Contracts;

/// <summary>The grid-sized view of an <see cref="ExchangeLog"/>, served by the UI API and pushed over SSE.</summary>
public sealed class ExchangeSummary
{
    /// <summary>Log file name without extension.</summary>
    public required string Id { get; init; }
    public required long Sequence { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required string Method { get; init; }
    public required string Route { get; init; }

    /// <summary>Request target with the route prefix stripped, e.g. "/v1/messages?beta=true".</summary>
    public required string Path { get; init; }

    /// <summary>Null when the upstream never answered.</summary>
    public int? StatusCode { get; init; }
    public required double DurationMs { get; init; }
    public required ExchangeOutcome Outcome { get; init; }

    /// <summary>Wire sizes (as sent, possibly compressed); 0 when there was no body.</summary>
    public required long RequestBytes { get; init; }
    public required long ResponseBytes { get; init; }

    /// <summary>Null when the response carried no recognisable usage block.</summary>
    public TokenUsage? Usage { get; init; }
}

/// <summary>Token counts normalised across providers.</summary>
public sealed class TokenUsage
{
    /// <summary>Total prompt tokens, including cache reads and cache writes.</summary>
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? CacheReadTokens { get; init; }
    public long? CacheWriteTokens { get; init; }
}
