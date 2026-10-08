using System.Text.Json;
using AiLog.Contracts;
using AiLog.Shared.Context;
using AiLog.Shared.Harness;
using AiLog.Shared.Providers;

namespace AiLog.Shared;

/// <summary>Everything the detail page shows beyond the raw log: provider view, reassembly and token breakdown.</summary>
public sealed class ExchangeAnalysis
{
    public required ExchangeLog Log { get; init; }
    public IProviderAdapter? Provider { get; private init; }
    public IHarnessClassifier? Harness { get; private init; }
    public string? Model { get; private init; }
    public TokenUsage? Usage { get; private init; }

    /// <summary>Parsed events when the response body is an event stream.</summary>
    public IReadOnlyList<SseEvent>? Events { get; private init; }

    /// <summary>The final message folded from <see cref="Events"/>.</summary>
    public JsonElement? Reassembled { get; private init; }

    public ContextSegment? Request { get; private set; }
    public ContextSegment? Response { get; private set; }

    /// <summary>True when the request estimate adds up to the provider's real input count.</summary>
    public bool InputCalibrated { get; private set; }
    public bool OutputCalibrated { get; private set; }
    public IReadOnlyList<BreakdownCategory> InputBreakdown { get; private set; } = [];
    public IReadOnlyList<BreakdownCategory> OutputBreakdown { get; private set; } = [];

    /// <summary>Set when the provider view could not be built; the raw views still work.</summary>
    public string? Error { get; private set; }

    public const string RequestIdPrefix = "q";
    public const string ResponseIdPrefix = "r";

    public static ExchangeAnalysis Analyze(ExchangeLog log)
    {
        var provider = ProviderRegistry.Match(log);
        var requestBody = log.Request.Body is { Format: BodyFormat.Json } body && body.Content.ValueKind == JsonValueKind.Object
            ? body.Content
            : (JsonElement?)null;

        IReadOnlyList<SseEvent>? events = null;
        JsonElement? reassembled = null;
        if (log.Response is { Body: { Format: BodyFormat.Text } responseBody } response && Sse.IsEventStream(response.Headers))
        {
            events = Sse.Parse(responseBody.Content.GetString() ?? "");
            reassembled = provider?.Reassemble(events);
        }

        var analysis = new ExchangeAnalysis
        {
            Log = log,
            Provider = provider,
            Harness = provider is null ? null : HarnessRegistry.Match(log),
            Model = requestBody is { } b ? Json.String(b, "model") : null,
            Usage = ProviderRegistry.ExtractUsage(log),
            Events = events,
            Reassembled = reassembled,
        };

        if (provider is not null)
        {
            try
            {
                analysis.BuildViews(provider, requestBody);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException or KeyNotFoundException)
            {
                analysis.Error = $"Could not interpret the exchange as {provider.Name}: {ex.Message}";
            }
        }

        return analysis;
    }

    /// <summary>The response message for the provider view: the reassembled stream or the JSON body.</summary>
    public JsonElement? ResponseMessage =>
        Reassembled ?? (Log.Response?.Body is { Format: BodyFormat.Json } body ? body.Content : null);

    private void BuildViews(IProviderAdapter provider, JsonElement? requestBody)
    {
        if (requestBody is { } body)
        {
            var request = provider.ParseRequest(body);
            Categories.Apply(request, isOutput: false);
            Harness?.Classify(request);
            ContextBreakdown.MarkCachedPrefix(provider.CacheOrder(request));
            InputCalibrated = TokenEstimator.Estimate(request, Usage?.InputTokens);
            ContextBreakdown.AssignIds(request, RequestIdPrefix);
            InputBreakdown = ContextBreakdown.Build(request);
            Request = request;
        }

        if (ResponseMessage is { ValueKind: JsonValueKind.Object } message)
        {
            var output = provider.ParseResponse(message);
            Categories.Apply(output, isOutput: true);
            OutputCalibrated = TokenEstimator.Estimate(output, Usage?.OutputTokens);
            ContextBreakdown.AssignIds(output, ResponseIdPrefix);
            OutputBreakdown = ContextBreakdown.Build(output);
            Response = output;
        }
    }

    /// <summary>Finds a segment by id in the request or response tree.</summary>
    public ContextSegment? Find(string id)
    {
        var root = id.StartsWith(RequestIdPrefix, StringComparison.Ordinal) ? Request : Response;
        return root is null ? null : root.Id == id ? root : root.Descendants().FirstOrDefault(s => s.Id == id);
    }
}
