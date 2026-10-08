using System.Text.Json;

namespace AiLog.Shared.Providers;

/// <summary>
/// Where an OpenAI usage object keeps its counts. Both OpenAI formats share the shape (input already includes
/// cached tokens, which sit in a details object) but name the fields differently.
/// </summary>
internal sealed record OpenAiUsageFields(string Input, string Output, string Details)
{
    private const string CachedTokens = "cached_tokens";

    public static OpenAiUsageFields ChatCompletions { get; } = new("prompt_tokens", "completion_tokens", "prompt_tokens_details");

    public static OpenAiUsageFields Responses { get; } = new("input_tokens", "output_tokens", "input_tokens_details");

    public void Apply(JsonElement usage, UsageBuilder builder)
    {
        builder.InputIncludesCache = true;
        builder.Input(usage, Input);
        builder.Output(usage, Output);
        if (Json.TryObject(usage, Details, out JsonElement details))
        {
            builder.CacheRead(details, CachedTokens);
        }
    }
}
