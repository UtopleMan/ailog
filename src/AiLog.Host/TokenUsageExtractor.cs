using System.Text;
using System.Text.Json;
using System.Net.ServerSentEvents;
using AiLog.Contracts;

namespace AiLog.Host;

/// <summary>
/// Reads token counts out of a logged response, recognising the provider by body shape:
/// Anthropic Messages, OpenAI Chat Completions and OpenAI Responses, each as JSON or SSE.
/// </summary>
internal static class TokenUsageExtractor
{
    public static TokenUsage? Extract(LoggedResponse? response)
    {
        var body = response?.Body;
        if (body is null)
        {
            return null;
        }

        var usage = new Accumulator();
        if (body.Format == BodyFormat.Json)
        {
            usage.ApplyDocument(body.Content);
        }
        else if (body.Format == BodyFormat.Text && IsEventStream(response!))
        {
            foreach (var data in ReadSseData(body.Content.GetString() ?? ""))
            {
                try
                {
                    using var document = JsonDocument.Parse(data);
                    usage.ApplyDocument(document.RootElement);
                }
                catch (JsonException)
                {
                    // e.g. OpenAI's "[DONE]" sentinel.
                }
            }
        }

        return usage.ToTokenUsage();
    }

    // Deserialised header dictionaries are case-sensitive, so match the name by hand.
    private static bool IsEventStream(LoggedResponse response) =>
        response.Headers.Any(h => h.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)
            && h.Value.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<byte[]> ReadSseData(string text)
    {
        // A stream cut short (client abort) can end without the blank line that dispatches its last event.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text + "\n\n"));
        foreach (var item in SseParser.Create(stream, (_, data) => data.ToArray()).Enumerate())
        {
            yield return item.Data;
        }
    }

    private sealed class Accumulator
    {
        private long? _input;
        private long? _output;
        private long? _cacheRead;
        private long? _cacheWrite;
        private bool _inputIncludesCache;
        private bool _found;

        public void ApplyDocument(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            // Anthropic SSE: message_start carries the input counts inside "message".
            if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.ValueEquals("message_start")
                && root.TryGetProperty("message", out var message) && TryGetObject(message, "usage", out var startUsage))
            {
                ApplyUsage(startUsage);
            }
            // OpenAI Responses SSE: response.completed / response.incomplete carry "response.usage".
            else if (TryGetObject(root, "response", out var inner) && TryGetObject(inner, "usage", out var responseUsage))
            {
                ApplyUsage(responseUsage);
            }
            // Plain JSON bodies of all three APIs, Anthropic message_delta and the final Chat Completions chunk.
            else if (TryGetObject(root, "usage", out var usage))
            {
                ApplyUsage(usage);
            }
        }

        private void ApplyUsage(JsonElement usage)
        {
            if (usage.TryGetProperty("prompt_tokens", out _))
            {
                // OpenAI Chat Completions: prompt_tokens already includes cached tokens.
                _inputIncludesCache = true;
                Set(ref _input, usage, "prompt_tokens");
                Set(ref _output, usage, "completion_tokens");
                if (TryGetObject(usage, "prompt_tokens_details", out var details))
                {
                    Set(ref _cacheRead, details, "cached_tokens");
                }
            }
            else if (usage.TryGetProperty("input_tokens_details", out _) || usage.TryGetProperty("output_tokens_details", out _))
            {
                // OpenAI Responses: input_tokens already includes cached tokens.
                _inputIncludesCache = true;
                Set(ref _input, usage, "input_tokens");
                Set(ref _output, usage, "output_tokens");
                if (TryGetObject(usage, "input_tokens_details", out var details))
                {
                    Set(ref _cacheRead, details, "cached_tokens");
                }
            }
            else
            {
                // Anthropic: input_tokens excludes cache reads and writes. message_delta repeats or updates fields.
                Set(ref _input, usage, "input_tokens");
                Set(ref _output, usage, "output_tokens");
                Set(ref _cacheRead, usage, "cache_read_input_tokens");
                Set(ref _cacheWrite, usage, "cache_creation_input_tokens");
            }
        }

        private void Set(ref long? field, JsonElement obj, string name)
        {
            if (obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                field = number;
                _found = true;
            }
        }

        public TokenUsage? ToTokenUsage()
        {
            if (!_found)
            {
                return null;
            }

            var input = _inputIncludesCache || _input is null
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

        private static bool TryGetObject(JsonElement element, string name, out JsonElement value) =>
            element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }
}
