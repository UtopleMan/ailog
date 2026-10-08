using System.Text.Json;
using AiLog.Contracts;

namespace AiLog.Shared.Providers;

/// <summary>Collects usage fields across documents; later values win (e.g. Anthropic message_delta).</summary>
public sealed class UsageBuilder
{
    private long? input;
    private long? output;
    private long? cacheRead;
    private long? cacheWrite;
    private bool hasAny;

    /// <summary>OpenAI counts cached tokens inside the input total; Anthropic does not.</summary>
    public bool InputIncludesCache { get; set; }

    /// <summary>Reads the input token count from the named property.</summary>
    public void Input(JsonElement obj, string name) => Set(ref input, obj, name);

    /// <summary>Reads the output token count from the named property.</summary>
    public void Output(JsonElement obj, string name) => Set(ref output, obj, name);

    /// <summary>Reads the cache-read token count from the named property.</summary>
    public void CacheRead(JsonElement obj, string name) => Set(ref cacheRead, obj, name);

    /// <summary>Reads the cache-write token count from the named property.</summary>
    public void CacheWrite(JsonElement obj, string name) => Set(ref cacheWrite, obj, name);

    private void Set(ref long? target, JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
        {
            target = number;
            hasAny = true;
        }
    }

    /// <summary>The collected usage with input normalised to include cache tokens; null when nothing was found.</summary>
    public TokenUsage? Build()
    {
        if (!hasAny)
        {
            return null;
        }

        long? totalInput = InputIncludesCache || input is null
            ? input
            : input + (cacheRead ?? 0) + (cacheWrite ?? 0);

        return new TokenUsage
        {
            InputTokens = totalInput,
            OutputTokens = output,
            CacheReadTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
        };
    }
}
