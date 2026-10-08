using System.Text.Json;
using System.Text.Json.Nodes;
using AiLog.Contracts;
using AiLog.Shared.Context;

namespace AiLog.Shared.Providers;

/// <summary>OpenAI Chat Completions (<c>POST /chat/completions</c>), also spoken by many other providers.</summary>
public sealed class OpenAiChatAdapter : ProviderAdapter
{
    private const string EndpointPath = "/chat/completions";

    /// <inheritdoc/>
    public override string Name => "OpenAI Chat Completions";

    /// <inheritdoc/>
    public override bool Matches(ExchangeLog log) =>
        PathOf(log).EndsWith(EndpointPath, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    protected override void ApplyUsage(JsonElement document, UsageBuilder usage)
    {
        // Streams carry usage only in the final chunk (with stream_options.include_usage), "usage": null before it.
        if (Json.TryObject(document, "usage", out JsonElement body) && body.TryGetProperty("prompt_tokens", out _))
        {
            OpenAiUsageFields.ChatCompletions.Apply(body, usage);
        }
    }

    /// <inheritdoc/>
    public override JsonElement? Reassemble(IReadOnlyList<SseEvent> events)
    {
        var stream = new CompletionStream();
        foreach (SseEvent item in events)
        {
            // Skips the "[DONE]" sentinel.
            if (Json.TryParseNode(item.Data) is JsonObject chunk)
            {
                stream.Apply(chunk);
            }
        }

        return stream.Finish();
    }

    /// <inheritdoc/>
    public override ContextSegment ParseRequest(JsonElement body)
    {
        var messages = new MessageParser();
        ContextSegment tools = GroupOf(ToolsGroup, Json.Items(body, "tools").Select(ParseToolDefinition));

        // Chat Completions has no separate system field: system and developer messages stay in place.
        ContextSegment conversation = GroupOf(MessagesGroup, Json.Items(body, "messages").Select((message, i) => messages.Parse(message, i + 1)));
        return GroupOf(RequestRoot, [ContextSegment.Group(SystemGroup), tools, conversation]);
    }

    private static ContextSegment ParseToolDefinition(JsonElement tool)
    {
        string? name = Json.TryObject(tool, "function", out JsonElement function) ? Json.String(function, "name") : null;
        return ToolDefinition(tool, name);
    }

    /// <inheritdoc/>
    public override ContextSegment ParseResponse(JsonElement message)
    {
        ContextSegment output = ContextSegment.Group(OutputRoot);
        foreach (JsonElement choice in Json.Items(message, "choices"))
        {
            AddChoice(output, choice);
        }

        AddError(output, message);
        return output;
    }

    private static void AddChoice(ContextSegment output, JsonElement choice)
    {
        if (!Json.TryObject(choice, "message", out JsonElement choiceMessage))
        {
            return;
        }

        ContextSegment parsed = new MessageParser().Parse(choiceMessage, 0);
        output.Children.AddRange(parsed.Children);
        output.Note ??= Json.String(choice, "finish_reason") is { } finishReason ? $"finish reason: {finishReason}" : null;
    }

    /// <summary>Parses messages, labelling tool messages with the name of the call they answer.</summary>
    private sealed class MessageParser
    {
        private const string ToolRole = "tool";

        private readonly ToolCallNames toolNames = new();

        public ContextSegment Parse(JsonElement message, int number)
        {
            string role = Json.String(message, "role") ?? UnknownRole;
            ContextSegment node = Message($"#{number} {role}", role);
            if (Json.String(message, "reasoning_content") is { Length: > 0 } reasoning)
            {
                node.Children.Add(ContextSegment.FromText(SegmentKind.Thinking, ReasoningLabel, reasoning));
            }

            if (message.TryGetProperty("content", out JsonElement content))
            {
                node.Children.AddRange(role == ToolRole ? ToolResult(message, content) : ParseContent(content, ParsePart));
            }

            if (Json.String(message, "refusal") is { Length: > 0 } refusal)
            {
                node.Children.Add(ContextSegment.FromText(SegmentKind.Text, RefusalLabel, refusal));
            }

            node.Children.AddRange(Json.Items(message, "tool_calls").Select(ToolCall));
            SetRole(node, role);
            return node;
        }

        /// <summary>A tool message's content as one result: text, or a parent of its parts.</summary>
        private IEnumerable<ContextSegment> ToolResult(JsonElement message, JsonElement content)
        {
            string toolName = toolNames.Find(Json.String(message, "tool_call_id")) ?? UnnamedTool;
            switch (content.ValueKind)
            {
                case JsonValueKind.String:
                    return [ContextSegment.FromText(SegmentKind.ToolResult, toolName, content.GetString())];
                case JsonValueKind.Array:
                    var result = new ContextSegment { Kind = SegmentKind.ToolResult, Label = toolName };
                    result.Children.AddRange(content.EnumerateArray().Select(ParsePart));
                    return [result];
                default:
                    return [];
            }
        }

        private ContextSegment ToolCall(JsonElement call)
        {
            if (!Json.TryObject(call, "function", out JsonElement function))
            {
                return Unknown(call, Json.String(call, "type") ?? "tool call");
            }

            string name = Json.String(function, "name") ?? UnnamedTool;
            toolNames.Remember(Json.String(call, "id"), name);

            // Arguments are a JSON-encoded string; show them parsed when they are valid JSON.
            string arguments = Json.String(function, "arguments") ?? "";
            return Json.TryParse(arguments) is { } parsed
                ? ContextSegment.FromJson(SegmentKind.ToolCall, name, parsed)
                : ContextSegment.FromText(SegmentKind.ToolCall, name, arguments);
        }

        private static ContextSegment ParsePart(JsonElement part)
        {
            string? type = Json.String(part, "type");
            return type switch
            {
                "text" => ContextSegment.FromText(SegmentKind.Text, TextLabel, Json.String(part, "text")),
                "refusal" => ContextSegment.FromText(SegmentKind.Text, RefusalLabel, Json.String(part, "refusal")),
                "image_url" => ImageFromUrl(ImageLabel, Json.TryObject(part, "image_url", out JsonElement image) ? Json.String(image, "url") : null),
                "file" when Json.TryObject(part, "file", out JsonElement file) => File(file),
                _ => Unknown(part, type),
            };
        }

        private static ContextSegment File(JsonElement file) =>
            Json.String(file, "file_data") is { } data
                ? FileFromData(Json.String(file, "filename"), data)
                : UnsizedDocument("file", file);
    }

    /// <summary>Folds streamed chunks back into one chat completion.</summary>
    private sealed class CompletionStream
    {
        private readonly SortedDictionary<int, Choice> choices = new();
        private JsonObject? completion;

        public void Apply(JsonObject chunk)
        {
            completion ??= new JsonObject
            {
                ["id"] = chunk["id"]?.DeepClone(),
                ["object"] = "chat.completion",
                ["created"] = chunk["created"]?.DeepClone(),
                ["model"] = chunk["model"]?.DeepClone(),
            };

            if (chunk["usage"] is JsonObject usage)
            {
                completion["usage"] = usage.DeepClone();
            }

            if (chunk["choices"] is JsonArray chunkChoices)
            {
                foreach (JsonObject choiceDelta in chunkChoices.OfType<JsonObject>())
                {
                    ChoiceAt(Json.Int(choiceDelta, "index") ?? 0).Apply(choiceDelta);
                }
            }
        }

        /// <summary>The folded completion; null when the stream held no chunks.</summary>
        public JsonElement? Finish()
        {
            if (completion is null)
            {
                return null;
            }

            var choiceArray = new JsonArray();
            foreach ((int index, Choice choice) in choices)
            {
                choiceArray.Add(choice.ToJson(index));
            }

            completion["choices"] = choiceArray;
            return Json.ToElement(completion);
        }

        private Choice ChoiceAt(int index)
        {
            if (!choices.TryGetValue(index, out Choice? choice))
            {
                choices[index] = choice = new Choice();
            }

            return choice;
        }
    }

    /// <summary>One choice being assembled from its deltas.</summary>
    private sealed class Choice
    {
        private readonly JsonObject message = new();
        private readonly SortedDictionary<int, JsonObject> toolCalls = new();
        private string? finishReason;

        public void Apply(JsonObject choiceDelta)
        {
            if (Json.String(choiceDelta, "finish_reason") is { } finish)
            {
                finishReason = finish;
            }

            if (choiceDelta["delta"] is JsonObject delta)
            {
                ApplyDelta(delta);
            }
        }

        public JsonNode ToJson(int index)
        {
            message["role"] ??= AssistantRole;
            if (toolCalls.Count > 0)
            {
                message["tool_calls"] = new JsonArray(toolCalls.Values.Select(c => (JsonNode)c).ToArray());
            }

            return new JsonObject
            {
                ["index"] = index,
                ["message"] = message,
                ["finish_reason"] = finishReason,
            };
        }

        private void ApplyDelta(JsonObject delta)
        {
            if (Json.String(delta, "role") is { } role)
            {
                message["role"] = role;
            }

            Json.Append(message, "content", Json.String(delta, "content"));
            Json.Append(message, "reasoning_content", Json.String(delta, "reasoning_content"));
            Json.Append(message, "refusal", Json.String(delta, "refusal"));
            if (delta["tool_calls"] is JsonArray toolCallDeltas)
            {
                foreach (JsonObject callDelta in toolCallDeltas.OfType<JsonObject>())
                {
                    ApplyToolCallDelta(callDelta);
                }
            }
        }

        private void ApplyToolCallDelta(JsonObject callDelta)
        {
            JsonObject call = ToolCallAt(Json.Int(callDelta, "index") ?? 0);
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

        private JsonObject ToolCallAt(int index)
        {
            if (!toolCalls.TryGetValue(index, out JsonObject? call))
            {
                toolCalls[index] = call = new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject(),
                };
            }

            return call;
        }
    }
}
