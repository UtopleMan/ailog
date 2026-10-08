using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiLog.Contracts;

/// <summary>One request/response exchange between the harness and an upstream provider.</summary>
public sealed class ExchangeLog
{
    /// <summary>File name without extension; sortable by start time.</summary>
    public required string Id { get; init; }

    /// <summary>Per-process counter that orders exchanges started in the same millisecond.</summary>
    public required long Sequence { get; init; }

    /// <summary>When ailog received the request.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the response finished, failed or was abandoned.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>Wall-clock time from receiving the request to completion.</summary>
    public required double DurationMs { get; init; }

    /// <summary>Route prefix (first path segment) that selected the upstream.</summary>
    public required string Route { get; init; }

    /// <summary>Absolute URL the request was forwarded to.</summary>
    public required string UpstreamUrl { get; init; }

    /// <summary>How the exchange ended.</summary>
    public required ExchangeOutcome Outcome { get; init; }

    /// <summary>Failure description; null when the exchange completed.</summary>
    public string? Error { get; init; }

    /// <summary>The request as received from the harness.</summary>
    public required LoggedRequest Request { get; init; }

    /// <summary>Null when the upstream never returned response headers.</summary>
    public LoggedResponse? Response { get; init; }
}

/// <summary>The harness request as logged, with secrets masked.</summary>
public sealed class LoggedRequest
{
    /// <summary>HTTP method, e.g. "POST".</summary>
    public required string Method { get; init; }

    /// <summary>Raw request target as received from the harness, including the route prefix and query.</summary>
    public required string Target { get; init; }

    /// <summary>Request headers with redacted values masked; repeated headers are comma-joined.</summary>
    public required Dictionary<string, string> Headers { get; init; }

    /// <summary>Null when the request had no body.</summary>
    public LoggedBody? Body { get; init; }
}

/// <summary>The upstream response as logged, with secrets masked.</summary>
public sealed class LoggedResponse
{
    /// <summary>HTTP status code returned by the upstream.</summary>
    public required int StatusCode { get; init; }

    /// <summary>Response and content headers with redacted values masked.</summary>
    public required Dictionary<string, string> Headers { get; init; }

    /// <summary>Null when the response had no body.</summary>
    public LoggedBody? Body { get; init; }
}

/// <summary>A captured body, decompressed and decoded into its most readable form.</summary>
public sealed class LoggedBody
{
    /// <summary>How <see cref="Content"/> is encoded.</summary>
    public required BodyFormat Format { get; init; }

    /// <summary>Number of bytes on the wire (before any decompression).</summary>
    public required long SizeBytes { get; init; }

    /// <summary>The Content-Encoding header as sent, e.g. "gzip".</summary>
    public string? ContentEncoding { get; init; }

    /// <summary>Set when the body could not be decoded as expected, e.g. a truncated gzip stream.</summary>
    public string? Note { get; init; }

    /// <summary>A JSON value for <see cref="BodyFormat.Json"/>, otherwise a string (text or base64).</summary>
    public required JsonElement Content { get; init; }
}

/// <summary>Encoding of <see cref="LoggedBody.Content"/>.</summary>
public enum BodyFormat
{
    /// <summary>Parsed JSON value.</summary>
    [JsonStringEnumMemberName("json")]
    Json,

    /// <summary>UTF-8 text stored as a JSON string.</summary>
    [JsonStringEnumMemberName("text")]
    Text,

    /// <summary>Binary or undecodable bytes stored as a base64 string.</summary>
    [JsonStringEnumMemberName("base64")]
    Base64,
}

/// <summary>How an exchange ended.</summary>
public enum ExchangeOutcome
{
    /// <summary>The full response was relayed to the harness.</summary>
    [JsonStringEnumMemberName("completed")]
    Completed,

    /// <summary>The harness disconnected before the response completed.</summary>
    [JsonStringEnumMemberName("client_aborted")]
    ClientAborted,

    /// <summary>The upstream request failed or broke off.</summary>
    [JsonStringEnumMemberName("upstream_error")]
    UpstreamError,
}
