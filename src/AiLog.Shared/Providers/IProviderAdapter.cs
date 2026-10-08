using System.Text.Json;
using AiLog.Contracts;
using AiLog.Shared.Context;

namespace AiLog.Shared.Providers;

/// <summary>
/// Understands one wire format (not one company: many providers speak OpenAI Chat Completions).
/// Adding a provider means implementing this and registering it in <see cref="ProviderRegistry"/>.
/// </summary>
public interface IProviderAdapter
{
    /// <summary>Display name, e.g. "Anthropic Messages".</summary>
    string Name { get; }

    /// <summary>Recognises the exchange by request path and body shape.</summary>
    bool Matches(ExchangeLog log);

    /// <summary>Reads token usage from a JSON or SSE response body; null when it carries none in this format.</summary>
    TokenUsage? ExtractUsage(LoggedResponse response);

    /// <summary>Folds a streamed response into the message a non-streaming call would have returned.</summary>
    JsonElement? Reassemble(IReadOnlyList<SseEvent> events);

    /// <summary>A tree with the top-level groups System, Tools and Messages.</summary>
    ContextSegment ParseRequest(JsonElement body);

    /// <summary>The model's output, from a JSON response body or a reassembled stream.</summary>
    ContextSegment ParseResponse(JsonElement message);

    /// <summary>The top-level request groups in the order the provider builds its prompt cache prefix.</summary>
    IEnumerable<ContextSegment> CacheOrder(ContextSegment request);
}

/// <summary>Shared plumbing: walking response bodies for usage and common segment shapes.</summary>
public abstract class ProviderAdapter : IProviderAdapter
{
    public const string SystemGroup = "System";
    public const string ToolsGroup = "Tools";
    public const string MessagesGroup = "Messages";

    public abstract string Name { get; }

    public abstract bool Matches(ExchangeLog log);

    public abstract JsonElement? Reassemble(IReadOnlyList<SseEvent> events);

    public abstract ContextSegment ParseRequest(JsonElement body);

    public abstract ContextSegment ParseResponse(JsonElement message);

    public virtual IEnumerable<ContextSegment> CacheOrder(ContextSegment request) => request.Children;

    /// <summary>Applies one JSON document (a whole body or one SSE data line) to the running usage.</summary>
    protected abstract void ApplyUsage(JsonElement document, UsageBuilder usage);

    public TokenUsage? ExtractUsage(LoggedResponse response)
    {
        var body = response.Body;
        if (body is null)
        {
            return null;
        }

        var usage = new UsageBuilder();
        if (body.Format == BodyFormat.Json)
        {
            ApplyUsage(body.Content, usage);
        }
        else if (body.Format == BodyFormat.Text && Sse.IsEventStream(response.Headers))
        {
            foreach (var item in Sse.Parse(body.Content.GetString() ?? ""))
            {
                // Skips non-JSON lines such as OpenAI's "[DONE]" sentinel.
                if (Json.TryParse(item.Data) is { } document)
                {
                    ApplyUsage(document, usage);
                }
            }
        }

        return usage.Build();
    }

    /// <summary>The request target without query string, for matching endpoint suffixes.</summary>
    protected static string PathOf(ExchangeLog log)
    {
        var target = log.Request.Target;
        var query = target.IndexOf('?');
        return (query < 0 ? target : target[..query]).TrimEnd('/');
    }

    protected static bool BodyHas(ExchangeLog log, string property) =>
        log.Request.Body is { Format: BodyFormat.Json } body
        && body.Content.ValueKind == JsonValueKind.Object
        && body.Content.TryGetProperty(property, out _);

    /// <summary>Sets the role on a message and every block inside it.</summary>
    protected static void SetRole(ContextSegment node, string role)
    {
        node.Role = role;
        foreach (var descendant in node.Descendants())
        {
            descendant.Role = role;
        }
    }

    protected static ContextSegment ImageFromBase64(string label, string? base64)
    {
        var (tokens, note) = MediaEstimator.Image(base64);
        return new ContextSegment { Kind = SegmentKind.Image, Label = label, FixedTokens = tokens, Note = note };
    }

    protected static ContextSegment ImageFromUrl(string label, string? url) =>
        MediaEstimator.DataUrlPayload(url) is { } payload
            ? ImageFromBase64(label, payload)
            : new ContextSegment
            {
                Kind = SegmentKind.Image, Label = label, FixedTokens = MediaEstimator.UnknownImageTokens,
                Note = url is null ? "flat estimate" : $"{Shorten(url, 80)} (flat estimate)",
            };

    protected static ContextSegment PdfFromBase64(string label, string? base64)
    {
        var (tokens, note) = MediaEstimator.Pdf(base64);
        return new ContextSegment { Kind = SegmentKind.Document, Label = label, FixedTokens = tokens, Note = note };
    }

    protected static ContextSegment Unknown(JsonElement block, string? type) =>
        ContextSegment.FromJson(SegmentKind.Other, type ?? "unknown", block);

    protected static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>Collects usage fields across documents; later values win (e.g. Anthropic message_delta).</summary>
public sealed class UsageBuilder
{
    private long? _input;
    private long? _output;
    private long? _cacheRead;
    private long? _cacheWrite;
    private bool _found;

    /// <summary>OpenAI counts cached tokens inside the input total; Anthropic does not.</summary>
    public bool InputIncludesCache { get; set; }

    public void Input(JsonElement obj, string name) => Set(ref _input, obj, name);

    public void Output(JsonElement obj, string name) => Set(ref _output, obj, name);

    public void CacheRead(JsonElement obj, string name) => Set(ref _cacheRead, obj, name);

    public void CacheWrite(JsonElement obj, string name) => Set(ref _cacheWrite, obj, name);

    private void Set(ref long? field, JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            field = number;
            _found = true;
        }
    }

    public TokenUsage? Build()
    {
        if (!_found)
        {
            return null;
        }

        var input = InputIncludesCache || _input is null
            ? _input
            : _input + (_cacheRead ?? 0) + (_cacheWrite ?? 0);

        return new TokenUsage
        {
            InputTokens = input,
            OutputTokens = _output,
            CacheReadTokens = _cacheRead,
            CacheWriteTokens = _cacheWrite,
        };
    }
}
