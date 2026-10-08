using System.Text.Json;
using System.Text.Json.Nodes;
using AiLog.Contracts;
using AiLog.Shared.Context;

namespace AiLog.Shared.Providers;

/// <summary>OpenAI Responses API (<c>POST /responses</c>), used by e.g. Codex.</summary>
public sealed class OpenAiResponsesAdapter : ProviderAdapter
{
    private const string EndpointPath = "/responses";

    /// <inheritdoc/>
    public override string Name => "OpenAI Responses";

    /// <inheritdoc/>
    public override bool Matches(ExchangeLog log) =>
        PathOf(log).EndsWith(EndpointPath, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    protected override void ApplyUsage(JsonElement document, UsageBuilder usage)
    {
        if (UsageObject(document) is { } body)
        {
            OpenAiUsageFields.Responses.Apply(body, usage);
        }
    }

    private static JsonElement? UsageObject(JsonElement document)
    {
        // SSE: response.completed / response.incomplete carry "response.usage"; JSON bodies have it at the top.
        if (Json.TryObject(document, "response", out JsonElement response) && Json.TryObject(response, "usage", out JsonElement inner))
        {
            return inner;
        }

        // Anthropic usage can carry output_tokens_details as well; input_tokens_details is OpenAI's alone.
        if (Json.TryObject(document, "usage", out JsonElement top) && top.TryGetProperty("input_tokens_details", out _))
        {
            return top;
        }

        return null;
    }

    /// <inheritdoc/>
    public override JsonElement? Reassemble(IReadOnlyList<SseEvent> events)
    {
        var stream = new ResponseStream();
        foreach (SseEvent item in events)
        {
            if (Json.TryParseNode(item.Data) is not JsonObject data)
            {
                continue;
            }

            string type = Json.String(data, "type") ?? item.EventType;

            // The terminal events carry the complete response; nothing else is needed.
            if (IsTerminal(type) && data["response"] is JsonObject final)
            {
                return Json.ToElement(final);
            }

            stream.Apply(type, data);
        }

        return stream.Finish();
    }

    private static bool IsTerminal(string eventType) =>
        eventType is "response.completed" or "response.incomplete" or "response.failed";

    /// <inheritdoc/>
    public override ContextSegment ParseRequest(JsonElement body)
    {
        var items = new ItemParser();
        ContextSegment system = GroupOf(SystemGroup, Instructions(body));
        ContextSegment tools = GroupOf(ToolsGroup, Json.Items(body, "tools").Select(tool => ToolDefinition(tool, Json.String(tool, "name"))));
        ContextSegment messages = GroupOf(MessagesGroup, items.ParseInput(body));
        return GroupOf(RequestRoot, [system, tools, messages]);
    }

    private static IEnumerable<ContextSegment> Instructions(JsonElement body)
    {
        if (Json.String(body, "instructions") is not { } instructions)
        {
            return [];
        }

        ContextSegment segment = ContextSegment.FromText(SegmentKind.Text, "instructions", instructions);
        segment.Role = SystemRole;
        return [segment];
    }

    /// <inheritdoc/>
    public override ContextSegment ParseResponse(JsonElement message)
    {
        var items = new ItemParser();
        ContextSegment output = GroupOf(OutputRoot, Json.Items(message, "output").SelectMany(item => items.Parse(item, 0).Children));
        AddError(output, message);
        SetRole(output, AssistantRole);
        output.Note = Json.String(message, "status") is { } status ? $"status: {status}" : null;
        return output;
    }

    /// <summary>Parses input and output items, labelling call outputs with the name of the call they answer.</summary>
    private sealed class ItemParser
    {
        private const string UserRole = "user";
        private const string ToolRole = "tool";
        private const string MessageType = "message";

        private readonly ToolCallNames toolNames = new();

        /// <summary>The request input: a bare string is one user message, an array holds items.</summary>
        public IEnumerable<ContextSegment> ParseInput(JsonElement body)
        {
            if (!body.TryGetProperty("input", out JsonElement input))
            {
                return [];
            }

            return input.ValueKind switch
            {
                JsonValueKind.String => [UserMessage(input.GetString())],
                JsonValueKind.Array => input.EnumerateArray().Select((item, i) => Parse(item, i + 1)).ToList(),
                _ => [],
            };
        }

        private static ContextSegment UserMessage(string? text)
        {
            ContextSegment message = Message($"#1 {UserRole}", UserRole);
            message.Children.Add(ContextSegment.FromText(SegmentKind.Text, TextLabel, text));
            SetRole(message, UserRole);
            return message;
        }

        /// <summary>Each input/output item becomes a message node holding its blocks.</summary>
        public ContextSegment Parse(JsonElement item, int number)
        {
            string? type = Json.String(item, "type") ?? (item.TryGetProperty("role", out _) ? MessageType : null);
            string role = Json.String(item, "role") ?? (IsCallOutput(type) ? ToolRole : AssistantRole);
            ContextSegment node = Message($"#{number} {(type == MessageType ? role : type)}", role);
            node.Children.AddRange(type switch
            {
                MessageType => MessageContent(item),
                "function_call" or "custom_tool_call" => [ToolCall(item)],
                _ when IsCallOutput(type) => [ToolResult(item)],
                "reasoning" => [Reasoning(item)],
                _ => [Unknown(item, type)],
            });
            SetRole(node, role);
            return node;
        }

        private static bool IsCallOutput(string? type) => type is "function_call_output" or "custom_tool_call_output";

        private static IEnumerable<ContextSegment> MessageContent(JsonElement item) =>
            item.TryGetProperty("content", out JsonElement content) ? ParseContent(content, ParsePart) : [];

        private ContextSegment ToolCall(JsonElement item)
        {
            string name = Json.String(item, "name") ?? UnnamedTool;
            toolNames.Remember(Json.String(item, "call_id"), name);
            string arguments = Json.String(item, "arguments") ?? Json.String(item, "input") ?? "";
            return Json.TryParse(arguments) is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } parsed
                ? ContextSegment.FromJson(SegmentKind.ToolCall, name, parsed)
                : ContextSegment.FromText(SegmentKind.ToolCall, name, arguments);
        }

        private ContextSegment ToolResult(JsonElement item)
        {
            string name = toolNames.Find(Json.String(item, "call_id")) ?? UnnamedTool;
            if (!Json.TryArray(item, "output", out JsonElement parts))
            {
                return ContextSegment.FromText(SegmentKind.ToolResult, name, Json.String(item, "output") ?? "");
            }

            var result = new ContextSegment { Kind = SegmentKind.ToolResult, Label = name };
            result.Children.AddRange(parts.EnumerateArray().Select(ParsePart));
            return result;
        }

        private static ContextSegment Reasoning(JsonElement item)
        {
            IEnumerable<string?> summaries = Json.Items(item, "summary").Select(part => Json.String(part, "text")).Where(text => text is not null);
            ContextSegment segment = ContextSegment.FromText(SegmentKind.Thinking, ReasoningLabel, string.Join("\n\n", summaries));
            if (Json.String(item, "encrypted_content") is { } encrypted)
            {
                // The summary is not what the model reads back; the encrypted payload is.
                segment.Weight = Math.Max(segment.Weight, encrypted.Length);
                segment.Note = "encrypted content; size from the encrypted payload";
            }

            return segment;
        }

        private static ContextSegment ParsePart(JsonElement part)
        {
            string? type = Json.String(part, "type");
            return type switch
            {
                "input_text" or "output_text" or "text" => ContextSegment.FromText(SegmentKind.Text, TextLabel, Json.String(part, "text")),
                "refusal" => ContextSegment.FromText(SegmentKind.Text, RefusalLabel, Json.String(part, "refusal")),
                "input_image" => ImageFromUrl(ImageLabel, Json.String(part, "image_url")),
                "input_file" => File(part),
                _ => Unknown(part, type),
            };
        }

        private static ContextSegment File(JsonElement part) =>
            Json.String(part, "file_data") is { } data
                ? FileFromData(Json.String(part, "filename"), data)
                : UnsizedDocument("file", part);
    }

    /// <summary>Folds output item events back into one response when the stream ended before its terminal event.</summary>
    private sealed class ResponseStream
    {
        private static readonly PartSlot outputText = new("content", "content_index", "output_text");
        private static readonly PartSlot reasoningSummary = new("summary", "summary_index", "summary_text");

        private readonly SortedDictionary<int, JsonObject> items = new();
        private readonly HashSet<int> doneItems = [];
        private JsonObject? response;

        public void Apply(string type, JsonObject data)
        {
            int index = Json.Int(data, "output_index") ?? 0;
            switch (type)
            {
                case "response.created" or "response.in_progress" when data["response"] is JsonObject started:
                    response = (JsonObject)started.DeepClone();
                    break;
                case "response.output_item.added" when data["item"] is JsonObject added:
                    items[index] = (JsonObject)added.DeepClone();
                    break;
                case "response.output_item.done" when data["item"] is JsonObject finished:
                    items[index] = (JsonObject)finished.DeepClone();
                    doneItems.Add(index);
                    break;
                case "response.output_text.delta" when OpenItem(index) is { } message:
                    AppendDelta(outputText.PartIn(message, data), data);
                    break;
                case "response.reasoning_summary_text.delta" when OpenItem(index) is { } reasoning:
                    AppendDelta(reasoningSummary.PartIn(reasoning, data), data);
                    break;
                case "response.function_call_arguments.delta" when OpenItem(index) is { } call:
                    Json.Append(call, "arguments", Json.String(data, "delta"));
                    break;
                case "error":
                    response ??= new JsonObject();
                    response["error"] = data.DeepClone();
                    break;
            }
        }

        /// <summary>The folded response; null when the stream held neither a response nor items.</summary>
        public JsonElement? Finish()
        {
            if (response is null && items.Count == 0)
            {
                return null;
            }

            JsonObject result = response ?? new JsonObject { ["object"] = "response" };
            result["output"] = new JsonArray(items.Values.Select(i => (JsonNode)i).ToArray());
            return Json.ToElement(result);
        }

        /// <summary>An item still receiving deltas; done items are already complete.</summary>
        private JsonObject? OpenItem(int index) =>
            items.TryGetValue(index, out JsonObject? item) && !doneItems.Contains(index) ? item : null;

        private static void AppendDelta(JsonObject part, JsonObject data) =>
            Json.Append(part, "text", Json.String(data, "delta"));
    }

    /// <summary>Where text deltas of one kind land inside an item: the array, the event's index field and the part type.</summary>
    private sealed record PartSlot(string ArrayName, string IndexName, string Type)
    {
        public JsonObject PartIn(JsonObject item, JsonObject data)
        {
            int index = Json.Int(data, IndexName) ?? 0;
            JsonArray parts = Json.GetOrAddArray(item, ArrayName);
            while (parts.Count <= index)
            {
                parts.Add((JsonNode)new JsonObject { ["type"] = Type, ["text"] = "" });
            }

            return (JsonObject)parts[index]!;
        }
    }
}
