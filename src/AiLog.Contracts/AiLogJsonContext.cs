using System.Text.Json.Serialization;

namespace AiLog.Contracts;

/// <summary>Indented JSON for the exchange log files on disk.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ExchangeLog))]
[JsonSerializable(typeof(string))]
public sealed partial class AiLogJsonContext : JsonSerializerContext;
