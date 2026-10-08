using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiLog.Contracts;

/// <summary>One request/response exchange between the harness and an upstream provider.</summary>
public sealed class ExchangeLog
{
    /// <summary>File name without extension; sortable by start time.</summary>
    public required string Id { get; init; }
    public required long Sequence { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required double DurationMs { get; init; }
    public required string Route { get; init; }
    public required string UpstreamUrl { get; init; }
    public required ExchangeOutcome Outcome { get; init; }
    public string? Error { get; init; }
    public required LoggedRequest Request { get; init; }

    /// <summary>Null when the upstream never returned response headers.</summary>
    public LoggedResponse? Response { get; init; }
}

public sealed class LoggedRequest
{
    public required string Method { get; init; }

    /// <summary>Raw request target as received from the harness, including the route prefix and query.</summary>
    public required string Target { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public LoggedBody? Body { get; init; }
}

public sealed class LoggedResponse
{
    public required int StatusCode { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public LoggedBody? Body { get; init; }
}

public sealed class LoggedBody
{
    public required BodyFormat Format { get; init; }

    /// <summary>Number of bytes on the wire (before any decompression).</summary>
    public required long SizeBytes { get; init; }
    public string? ContentEncoding { get; init; }

    /// <summary>Set when the body could not be decoded as expected, e.g. a truncated gzip stream.</summary>
    public string? Note { get; init; }

    /// <summary>A JSON value for <see cref="BodyFormat.Json"/>, otherwise a string (text or base64).</summary>
    public required JsonElement Content { get; init; }
}

public enum BodyFormat
{
    [JsonStringEnumMemberName("json")] Json,
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("base64")] Base64,
}

public enum ExchangeOutcome
{
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("client_aborted")] ClientAborted,
    [JsonStringEnumMemberName("upstream_error")] UpstreamError,
}
