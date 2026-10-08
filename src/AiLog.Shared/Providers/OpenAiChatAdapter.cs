using System.Text.Json;
using System.Text.Json.Nodes;
using AiLog.Contracts;
using AiLog.Shared.Context;

namespace AiLog.Shared.Providers;

/// <summary>OpenAI Chat Completions (<c>POST /chat/completions</c>), also spoken by many other providers.</summary>
public sealed class OpenAiChatAdapter : ProviderAdapter
{
    public override string Name => "OpenAI Chat Completions";

    public override bool Matches(ExchangeLog log) =>
        PathOf(log).EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase);

    protected override void ApplyUsage(JsonElement document, UsageBuilder usage)
    {
        // Streams carry usage only in the final chunk (with stream_options.include_usage), "usage": null before it.
        if (!Json.TryObject(document, "usage", out var body) || !body.TryGetProperty("prompt_tokens", out _))
        {
            return;
        }

        // prompt_tokens already includes cached tokens.
        usage.InputIncludesCache = true;
        usage.Input(body, "prompt_tokens");
        usage.Output(body, "completion_tokens");
        if (Json.TryObject(body, "prompt_tokens_details", out var details))
        {
            usage.CacheRead(details, "cached_tokens");
        }
    }

    public override JsonElement? Reassemble(IReadOnlyList<SseEvent> events)
    {
        JsonObject? result = null;
        var choices = new SortedDictionary<int, Choice>();

        foreach (var item in events)
        {
            if (Json.TryParseNode(item.Data) is not JsonObject chunk)
            {
                continue; // "[DONE]"
            }

            result ??= new JsonObject
            {
                ["id"] = chunk["id"]?.DeepClone(),
                ["object"] = "chat.completion",
                ["created"] = chunk["created"]?.DeepClone(),
                ["model"] = chunk["model"]?.DeepClone(),
            };

            if (chunk["usage"] is JsonObject usage)
            {
                result["usage"] = usage.DeepClone();
            }

            if (chunk["choices"] is not JsonArray chunkChoices)
            {
                continue;
            }

            foreach (var choiceNode in chunkChoices.OfType<JsonObject>())
            {
                var index = Json.Int(choiceNode, "index") ?? 0;
                if (!choices.TryGetValue(index, out var choice))
                {
                    choices[index] = choice = new Choice();
                }

                if (Json.String(choiceNode, "finish_reason") is { } finish)
                {
                    choice.FinishReason = finish;
                }

                if (choiceNode["delta"] is not JsonObject delta)
                {
                    continue;
                }

                if (Json.String(delta, "role") is { } role)
                {
                    choice.Message["role"] = role;
                }

                Json.Append(choice.Message, "content", Json.String(delta, "content"));
                Json.Append(choice.Message, "reasoning_content", Json.String(delta, "reasoning_content"));
                Json.Append(choice.Message, "refusal", Json.String(delta, "refusal"));

                if (delta["tool_calls"] is JsonArray toolCalls)
                {
                    foreach (var callDelta in toolCalls.OfType<JsonObject>())
                    {
                        var callIndex = Json.Int(callDelta, "index") ?? 0;
                        if (!choice.ToolCalls.TryGetValue(callIndex, out var call))
                        {
                            choice.ToolCalls[callIndex] = call = new JsonObject
                            {
                                ["type"] = "function",
                                ["function"] = new JsonObject(),
                            };
                        }

                        if (Json.String(callDelta, "id") is { } id)
                        {
                            call["id"] = id;
                        }

                        if (callDelta["function"] is JsonObject function)
                        {
                            var target = (JsonObject)call["function"]!;
                            Json.Append(target, "name", Json.String(function, "name"));
                            Json.Append(target, "arguments", Json.String(function, "arguments"));
                        }
                    }
                }
            }
        }

        if (result is null)
        {
            return null;
        }

        var choiceArray = new JsonArray();
        foreach (var (index, choice) in choices)
        {
            choice.Message["role"] ??= "assistant";
            if (choice.ToolCalls.Count > 0)
            {
                choice.Message["tool_calls"] = new JsonArray(choice.ToolCalls.Values.Select(c => (JsonNode)c).ToArray());
            }

            choiceArray.Add((JsonNode)new JsonObject
            {
                ["index"] = index,
                ["message"] = choice.Message,
                ["finish_reason"] = choice.FinishReason,
            });
        }

        result["choices"] = choiceArray;
        return Json.ToElement(result);
    }

    private sealed class Choice
    {
        public JsonObject Message { get; } = new();
        public SortedDictionary<int, JsonObject> ToolCalls { get; } = new();
        public string? FinishReason { get; set; }
    }

    public override ContextSegment ParseRequest(JsonElement body)
    {
        var root = ContextSegment.Group("Request");
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);

        var tools = ContextSegment.Group(ToolsGroup);
        if (Json.TryArray(body, "tools", out var toolArray))
        {
            foreach (var tool in toolArray.EnumerateArray())
            {
                var name = Json.TryObject(tool, "function", out var function) ? Json.String(function, "name") : null;
                tools.Children.Add(ContextSegment.FromJson(SegmentKind.ToolDefinition, name ?? Json.String(tool, "type") ?? "tool", tool));
            }
        }

        // Chat Completions has no separate system field: system and developer messages stay in place.
        var messages = ContextSegment.Group(MessagesGroup);
        if (Json.TryArray(body, "messages", out var messageArray))
        {
            var number = 0;
            foreach (var message in messageArray.EnumerateArray())
            {
                messages.Children.Add(ParseMessage(message, ++number, toolNames));
            }
        }

        root.Children.AddRange([ContextSegment.Group(SystemGroup), tools, messages]);
        return root;
    }

    public override ContextSegment ParseResponse(JsonElement message)
    {
        var root = ContextSegment.Group("Output");
        if (Json.TryArray(message, "choices", out var choices))
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (Json.TryObject(choice, "message", out var choiceMessage))
                {
                    var parsed = ParseMessage(choiceMessage, 0, new Dictionary<string, string>());
                    root.Children.AddRange(parsed.Children);
                    root.Note ??= Json.String(choice, "finish_reason") is { } finish ? $"finish reason: {finish}" : null;
                }
            }
        }

        if (Json.TryObject(message, "error", out var error))
        {
            root.Children.Add(ContextSegment.FromJson(SegmentKind.Other, "error", error));
        }

        return root;
    }

    private static ContextSegment ParseMessage(JsonElement message, int number, Dictionary<string, string> toolNames)
    {
        var role = Json.String(message, "role") ?? "?";
        var node = new ContextSegment { Kind = SegmentKind.Message, Label = $"#{number} {role}", Role = role };

        if (Json.String(message, "reasoning_content") is { Length: > 0 } reasoning)
        {
            node.Children.Add(ContextSegment.FromText(SegmentKind.Thinking, "reasoning", reasoning));
        }

        var isToolResult = role == "tool";
        var toolName = isToolResult && Json.String(message, "tool_call_id") is { } callId && toolNames.TryGetValue(callId, out var known)
            ? known
            : "tool";

        if (message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                node.Children.Add(ContextSegment.FromText(isToolResult ? SegmentKind.ToolResult : SegmentKind.Text, isToolResult ? toolName : "text", content.GetString()));
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                var parts = content.EnumerateArray().Select(ParsePart).ToList();
                if (isToolResult)
                {
                    var result = new ContextSegment { Kind = SegmentKind.ToolResult, Label = toolName };
                    result.Children.AddRange(parts);
                    node.Children.Add(result);
                }
                else
                {
                    node.Children.AddRange(parts);
                }
            }
        }

        if (Json.String(message, "refusal") is { Length: > 0 } refusal)
        {
            node.Children.Add(ContextSegment.FromText(SegmentKind.Text, "refusal", refusal));
        }

        if (Json.TryArray(message, "tool_calls", out var calls))
        {
            foreach (var call in calls.EnumerateArray())
            {
                node.Children.Add(ToolCall(call, toolNames));
            }
        }

        SetRole(node, role);
        return node;
    }

    private static ContextSegment ToolCall(JsonElement call, Dictionary<string, string> toolNames)
    {
        if (!Json.TryObject(call, "function", out var function))
        {
            return Unknown(call, Json.String(call, "type") ?? "tool call");
        }

        var name = Json.String(function, "name") ?? "tool";
        if (Json.String(call, "id") is { } id)
        {
            toolNames[id] = name;
        }

        // Arguments are a JSON-encoded string; show them parsed when they are valid JSON.
        var arguments = Json.String(function, "arguments") ?? "";
        return Json.TryParse(arguments) is { } parsed
            ? ContextSegment.FromJson(SegmentKind.ToolCall, name, parsed)
            : ContextSegment.FromText(SegmentKind.ToolCall, name, arguments);
    }

    private static ContextSegment ParsePart(JsonElement part)
    {
        var type = Json.String(part, "type");
        return type switch
        {
            "text" => ContextSegment.FromText(SegmentKind.Text, "text", Json.String(part, "text")),
            "refusal" => ContextSegment.FromText(SegmentKind.Text, "refusal", Json.String(part, "refusal")),
            "image_url" => ImageFromUrl("image", Json.TryObject(part, "image_url", out var image) ? Json.String(image, "url") : null),
            "file" when Json.TryObject(part, "file", out var file) => Json.String(file, "file_data") is { } data
                ? PdfFromBase64($"file: {Json.String(file, "filename") ?? "pdf"}", MediaEstimator.DataUrlPayload(data) ?? data)
                : new ContextSegment { Kind = SegmentKind.Document, Label = "file", FixedTokens = 0, Note = "size unknown", Json = Json.Format(file) },
            _ => Unknown(part, type),
        };
    }
}
