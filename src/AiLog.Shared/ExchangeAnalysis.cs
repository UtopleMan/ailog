using System.Text.Json;
using AiLog.Contracts;
using AiLog.Shared.Context;
using AiLog.Shared.Harness;
using AiLog.Shared.Providers;

namespace AiLog.Shared;

/// <summary>Everything the detail page shows beyond the raw log: provider view, reassembly and token breakdown.</summary>
public sealed class ExchangeAnalysis
{
    /// <summary>Id prefix of segments in the request tree.</summary>
    public const string RequestIdPrefix = "q";

    /// <summary>Id prefix of segments in the response tree.</summary>
    public const string ResponseIdPrefix = "r";

    /// <summary>The analysed exchange.</summary>
    public required ExchangeLog Log { get; init; }

    /// <summary>The wire format the exchange speaks; null when no adapter recognises it.</summary>
    public IProviderAdapter? Provider { get; private init; }

    /// <summary>The harness that sent the request; null when unknown.</summary>
    public IHarnessClassifier? Harness { get; private init; }

    /// <summary>The model named in the request body.</summary>
    public string? Model { get; private init; }

    /// <summary>Token counts reported by the provider.</summary>
    public TokenUsage? Usage { get; private init; }

    /// <summary>Parsed events when the response body is an event stream.</summary>
    public IReadOnlyList<SseEvent>? Events { get; private init; }

    /// <summary>The final message folded from <see cref="Events"/>.</summary>
    public JsonElement? Reassembled { get; private init; }

    /// <summary>The request as a segment tree with token estimates.</summary>
    public ContextSegment? Request { get; private set; }

    /// <summary>The model's output as a segment tree with token estimates.</summary>
    public ContextSegment? Response { get; private set; }

    /// <summary>True when the request estimate adds up to the provider's real input count.</summary>
    public bool InputCalibrated { get; private set; }

    /// <summary>True when the response estimate adds up to the provider's real output count.</summary>
    public bool OutputCalibrated { get; private set; }

    /// <summary>Request tokens grouped by category and item, largest first.</summary>
    public IReadOnlyList<BreakdownCategory> InputBreakdown { get; private set; } = [];

    /// <summary>Response tokens grouped by category and item, largest first.</summary>
    public IReadOnlyList<BreakdownCategory> OutputBreakdown { get; private set; } = [];

    /// <summary>Set when the provider view could not be built; the raw views still work.</summary>
    public string? Error { get; private set; }

    /// <summary>The response message for the provider view: the reassembled stream or the JSON body.</summary>
    public JsonElement? ResponseMessage =>
        Reassembled ?? (Log.Response?.Body is { Format: BodyFormat.Json } body ? body.Content : null);

    /// <summary>Builds every view of the exchange that its provider and harness allow.</summary>
    public static ExchangeAnalysis Analyze(ExchangeLog log)
    {
        IProviderAdapter? provider = ProviderRegistry.Match(log);
        JsonElement? requestBody = RequestBody(log);
        IReadOnlyList<SseEvent>? events = ResponseEvents(log);

        var analysis = new ExchangeAnalysis
        {
            Log = log,
            Provider = provider,
            Harness = provider is null ? null : HarnessRegistry.Match(log),
            Model = requestBody is { } body ? Json.String(body, "model") : null,
            Usage = ProviderRegistry.ExtractUsage(log),
            Events = events,
            Reassembled = events is null ? null : provider?.Reassemble(events),
        };

        if (provider is not null)
        {
            analysis.TryBuildViews(provider, requestBody);
        }

        return analysis;
    }

    private static JsonElement? RequestBody(ExchangeLog log) =>
        log.Request.Body is { Format: BodyFormat.Json } body && body.Content.ValueKind == JsonValueKind.Object
            ? body.Content
            : null;

    private static IReadOnlyList<SseEvent>? ResponseEvents(ExchangeLog log) =>
        log.Response is { Body: { Format: BodyFormat.Text } body } response && Sse.IsEventStream(response.Headers)
            ? Sse.Parse(body.Content.GetString() ?? "")
            : null;

    private void TryBuildViews(IProviderAdapter provider, JsonElement? requestBody)
    {
        try
        {
            BuildViews(provider, requestBody);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException or KeyNotFoundException)
        {
            Error = $"Could not interpret the exchange as {provider.Name}: {ex.Message}";
        }
    }

    private void BuildViews(IProviderAdapter provider, JsonElement? requestBody)
    {
        if (requestBody is { } body)
        {
            BuildRequestView(provider, body);
        }

        if (ResponseMessage is { ValueKind: JsonValueKind.Object } message)
        {
            BuildResponseView(provider, message);
        }
    }

    private void BuildRequestView(IProviderAdapter provider, JsonElement body)
    {
        ContextSegment request = provider.ParseRequest(body);
        Categories.Apply(request, isOutput: false);
        Harness?.Classify(request);
        ContextBreakdown.MarkCachedPrefix(provider.CacheOrder(request));
        InputCalibrated = TokenEstimator.Estimate(request, Usage?.InputTokens);
        ContextBreakdown.AssignIds(request, RequestIdPrefix);
        InputBreakdown = ContextBreakdown.Build(request);
        Request = request;
    }

    private void BuildResponseView(IProviderAdapter provider, JsonElement message)
    {
        ContextSegment output = provider.ParseResponse(message);
        Categories.Apply(output, isOutput: true);
        OutputCalibrated = TokenEstimator.Estimate(output, Usage?.OutputTokens);
        ContextBreakdown.AssignIds(output, ResponseIdPrefix);
        OutputBreakdown = ContextBreakdown.Build(output);
        Response = output;
    }

    /// <summary>Finds a segment by id in the request or response tree.</summary>
    public ContextSegment? Find(string id)
    {
        ContextSegment? root = id.StartsWith(RequestIdPrefix, StringComparison.Ordinal) ? Request : Response;
        if (root is null)
        {
            return null;
        }

        return root.Id == id ? root : root.Descendants().FirstOrDefault(s => s.Id == id);
    }
}
