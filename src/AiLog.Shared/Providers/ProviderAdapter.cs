using System.Text.Json;
using AiLog.Contracts;
using AiLog.Shared.Context;

namespace AiLog.Shared.Providers;

/// <summary>Shared plumbing: walking response bodies for usage and common segment shapes.</summary>
public abstract class ProviderAdapter : IProviderAdapter
{
    /// <summary>Label of the top-level request group holding system instructions.</summary>
    public const string SystemGroup = "System";

    /// <summary>Label of the top-level request group holding tool definitions.</summary>
    public const string ToolsGroup = "Tools";

    /// <summary>Label of the top-level request group holding the conversation.</summary>
    public const string MessagesGroup = "Messages";

    /// <summary>Label of the request tree's root.</summary>
    protected const string RequestRoot = "Request";

    /// <summary>Label of the response tree's root.</summary>
    protected const string OutputRoot = "Output";

    /// <summary>Label of plain text blocks.</summary>
    protected const string TextLabel = "text";

    /// <summary>Label of reasoning blocks.</summary>
    protected const string ReasoningLabel = "reasoning";

    /// <summary>Label of refusal blocks.</summary>
    protected const string RefusalLabel = "refusal";

    /// <summary>Label of image blocks without further detail.</summary>
    protected const string ImageLabel = "image";

    /// <summary>Tool name used when the wire format does not give one.</summary>
    protected const string UnnamedTool = "tool";

    /// <summary>Role the model's output is attributed to.</summary>
    protected const string AssistantRole = "assistant";

    /// <summary>Role of system instructions.</summary>
    protected const string SystemRole = "system";

    /// <summary>Role shown when a message does not state one.</summary>
    protected const string UnknownRole = "?";

    private const int UrlNoteLength = 80;

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract bool Matches(ExchangeLog log);

    /// <inheritdoc/>
    public abstract JsonElement? Reassemble(IReadOnlyList<SseEvent> events);

    /// <inheritdoc/>
    public abstract ContextSegment ParseRequest(JsonElement body);

    /// <inheritdoc/>
    public abstract ContextSegment ParseResponse(JsonElement message);

    /// <inheritdoc/>
    public virtual IEnumerable<ContextSegment> CacheOrder(ContextSegment request) => request.Children;

    /// <inheritdoc/>
    public TokenUsage? ExtractUsage(LoggedResponse response)
    {
        LoggedBody? body = response.Body;
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
            ApplyStreamUsage(body.Content.GetString() ?? "", usage);
        }

        return usage.Build();
    }

    private void ApplyStreamUsage(string stream, UsageBuilder usage)
    {
        foreach (SseEvent item in Sse.Parse(stream))
        {
            // Skips non-JSON lines such as OpenAI's "[DONE]" sentinel.
            if (Json.TryParse(item.Data) is { } document)
            {
                ApplyUsage(document, usage);
            }
        }
    }

    /// <summary>Applies one JSON document (a whole body or one SSE data line) to the running usage.</summary>
    protected abstract void ApplyUsage(JsonElement document, UsageBuilder usage);

    /// <summary>The request target without query string, for matching endpoint suffixes.</summary>
    protected static string PathOf(ExchangeLog log)
    {
        string target = log.Request.Target;
        int query = target.IndexOf('?');
        return (query < 0 ? target : target[..query]).TrimEnd('/');
    }

    /// <summary>True when the request body is a JSON object with the given property.</summary>
    protected static bool BodyHas(ExchangeLog log, string property) =>
        log.Request.Body is { Format: BodyFormat.Json } body
        && body.Content.ValueKind == JsonValueKind.Object
        && body.Content.TryGetProperty(property, out _);

    /// <summary>A group segment holding the given children.</summary>
    protected static ContextSegment GroupOf(string label, IEnumerable<ContextSegment> children)
    {
        ContextSegment group = ContextSegment.Group(label);
        group.Children.AddRange(children);
        return group;
    }

    /// <summary>An empty conversation turn.</summary>
    protected static ContextSegment Message(string label, string role) =>
        new() { Kind = SegmentKind.Message, Label = label, Role = role };

    /// <summary>A tool definition, named by <paramref name="name"/> or else by its type.</summary>
    protected static ContextSegment ToolDefinition(JsonElement tool, string? name) =>
        ContextSegment.FromJson(SegmentKind.ToolDefinition, name ?? Json.String(tool, "type") ?? UnnamedTool, tool);

    /// <summary>Message content that is either a plain string or an array of blocks; empty for anything else.</summary>
    protected static IEnumerable<ContextSegment> ParseContent(JsonElement content, Func<JsonElement, ContextSegment> parseBlock)
    {
        return content.ValueKind switch
        {
            JsonValueKind.String => [ContextSegment.FromText(SegmentKind.Text, TextLabel, content.GetString())],
            JsonValueKind.Array => content.EnumerateArray().Select(parseBlock).ToList(),
            _ => [],
        };
    }

    /// <summary>Sets the role on a message and every block inside it.</summary>
    protected static void SetRole(ContextSegment node, string role)
    {
        node.Role = role;
        foreach (ContextSegment descendant in node.Descendants())
        {
            descendant.Role = role;
        }
    }

    /// <summary>Appends the response's <c>error</c> object, if any, to the output.</summary>
    protected static void AddError(ContextSegment output, JsonElement message)
    {
        if (Json.TryObject(message, "error", out JsonElement error))
        {
            output.Children.Add(ContextSegment.FromJson(SegmentKind.Other, "error", error));
        }
    }

    /// <summary>An image estimated from its base64 header; a flat estimate when unreadable.</summary>
    protected static ContextSegment ImageFromBase64(string label, string? base64)
    {
        (long tokens, string note) = MediaEstimator.Image(base64);
        return new ContextSegment { Kind = SegmentKind.Image, Label = label, FixedTokens = tokens, Note = note };
    }

    /// <summary>An image given by URL: estimated when it is a data URL, flat otherwise.</summary>
    protected static ContextSegment ImageFromUrl(string label, string? url) =>
        MediaEstimator.DataUrlPayload(url) is { } payload
            ? ImageFromBase64(label, payload)
            : new ContextSegment
            {
                Kind = SegmentKind.Image,
                Label = label,
                FixedTokens = MediaEstimator.UnknownImageTokens,
                Note = url is null ? "flat estimate" : $"{url.Shorten(UrlNoteLength)} (flat estimate)",
            };

    /// <summary>A PDF estimated by its page count.</summary>
    protected static ContextSegment PdfFromBase64(string label, string? base64)
    {
        (long tokens, string note) = MediaEstimator.Pdf(base64);
        return new ContextSegment { Kind = SegmentKind.Document, Label = label, FixedTokens = tokens, Note = note };
    }

    /// <summary>An attached file given inline as base64 or a data URL, estimated as a PDF.</summary>
    protected static ContextSegment FileFromData(string? fileName, string data) =>
        PdfFromBase64($"file: {fileName ?? "pdf"}", MediaEstimator.DataUrlPayload(data) ?? data);

    /// <summary>A document whose size cannot be estimated; shows its source and counts as zero.</summary>
    protected static ContextSegment UnsizedDocument(string label, JsonElement source) =>
        new()
        {
            Kind = SegmentKind.Document,
            Label = label,
            FixedTokens = 0,
            Note = "size unknown",
            Json = Json.Format(source),
        };

    /// <summary>A block of an unknown type, kept as JSON.</summary>
    protected static ContextSegment Unknown(JsonElement block, string? type) =>
        ContextSegment.FromJson(SegmentKind.Other, type ?? "unknown", block);
}
