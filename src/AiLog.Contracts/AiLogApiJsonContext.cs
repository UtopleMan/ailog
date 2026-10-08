using System.Text.Json.Serialization;

namespace AiLog.Contracts;

/// <summary>Compact JSON for the UI API and SSE data lines (one line per event).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ExchangeSummary))]
[JsonSerializable(typeof(List<ExchangeSummary>))]
public sealed partial class AiLogApiJsonContext : JsonSerializerContext;
