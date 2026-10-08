using System.Text.Json.Serialization;

namespace AiLog.Contracts;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ExchangeLog))]
[JsonSerializable(typeof(string))]
public sealed partial class AiLogJsonContext : JsonSerializerContext;
