using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiLog.Contracts;
using AiLog.Shared.Context;

namespace AiLog.Shared.Providers;

/// <summary>Anthropic Messages API (<c>POST /v1/messages</c>), JSON or streamed.</summary>
public sealed class AnthropicMessagesAdapter : ProviderAdapter
{
    private const string EndpointPath = "/v1/messages";

    /// <inheritdoc/>
    public override string Name => "Anthropic Messages";

    /// <inheritdoc/>
    public override bool Matches(ExchangeLog log) =>
        PathOf(log).EndsWith(EndpointPath, StringComparison.OrdinalIgnoreCase) && BodyHas(log, "messages");

    /// <inheritdoc/>
    protected override void ApplyUsage(JsonElement document, UsageBuilder usage)
    {
        if (document.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // SSE message_start carries the input counts inside "message"; message_delta and JSON bodies at the top.
        if (Json.String(document, "type") == "message_start"
            && Json.TryObject(document, "message", out JsonElement message)
            && Json.TryObject(message, "usage", out JsonElement startUsage))
        {
            ApplyUsageFields(startUsage, usage);
        }
        else if (Json.TryObject(document, "usage", out JsonElement bodyUsage))
        {
            ApplyUsageFields(bodyUsage, usage);
        }
    }

    private static void ApplyUsageFields(JsonElement usage, UsageBuilder builder)
    {
        // OpenAI usage shapes are left to their own adapters. (Anthropic has output_tokens_details too.)
        if (usage.TryGetProperty("prompt_tokens", out _) || usage.TryGetProperty("input_tokens_details", out _))
        {
            return;
        }

        builder.Input(usage, "input_tokens");
        builder.Output(usage, "output_tokens");
        builder.CacheRead(usage, "cache_read_input_tokens");
        builder.CacheWrite(usage, "cache_creation_input_tokens");
    }

    /// <inheritdoc/>
    public override JsonElement? Reassemble(IReadOnlyList<SseEvent> events)
    {
        var stream = new MessageStream();
        foreach (SseEvent item in events)
        {
            if (Json.TryParseNode(item.Data) is JsonObject data)
            {
                stream.Apply(data);
            }
        }

        return stream.Finish();
    }

    /// <inheritdoc/>
    public override ContextSegment ParseRequest(JsonElement body)
    {
        var blocks = new BlockParser();
        ContextSegment system = GroupOf(SystemGroup, blocks.ParseSystem(body));
        ContextSegment tools = GroupOf(ToolsGroup, Json.Items(body, "tools").Select(ParseToolDefinition));
        ContextSegment messages = GroupOf(MessagesGroup, Json.Items(body, "messages").Select((message, i) => blocks.ParseMessage(message, i + 1)));
        return GroupOf(RequestRoot, [system, tools, messages]);
    }

    private static ContextSegment ParseToolDefinition(JsonElement tool)
    {
        ContextSegment segment = ToolDefinition(tool, Json.String(tool, "name"));
        segment.CacheBreakpoint = HasCacheBreakpoint(tool);
        return segment;
    }

    private static bool HasCacheBreakpoint(JsonElement block) => block.TryGetProperty("cache_control", out _);

    /// <summary>Anthropic's cache prefix is tools, then system, then messages.</summary>
    public override IEnumerable<ContextSegment> CacheOrder(ContextSegment request) =>
        [request.Children[1], request.Children[0], request.Children[2]];

    /// <inheritdoc/>
    public override ContextSegment ParseResponse(JsonElement message)
    {
        var blocks = new BlockParser();
        ContextSegment output = GroupOf(OutputRoot, Json.Items(message, "content").Select(blocks.Parse));
        AddError(output, message);
        SetRole(output, AssistantRole);
        output.Note = Json.String(message, "stop_reason") is { } stopReason ? $"stop reason: {stopReason}" : null;
        return output;
    }

    /// <summary>Parses content blocks, labelling tool results with the name of the tool_use they answer.</summary>
    private sealed class BlockParser
    {
        private const string SystemLabel = "system";
        private const string PdfMediaType = "application/pdf";

        private readonly ToolCallNames toolNames = new();

        public List<ContextSegment> ParseSystem(JsonElement body)
        {
            if (!body.TryGetProperty("system", out JsonElement system))
            {
                return [];
            }

            List<ContextSegment> blocks = system.ValueKind == JsonValueKind.String
                ? [ContextSegment.FromText(SegmentKind.Text, SystemLabel, system.GetString())]
                : ParseContent(system, Parse).ToList();
            foreach (ContextSegment block in blocks)
            {
                block.Role = SystemRole;
            }

            return blocks;
        }

        public ContextSegment ParseMessage(JsonElement message, int number)
        {
            string role = Json.String(message, "role") ?? UnknownRole;
            ContextSegment node = Message($"#{number} {role}", role);
            if (message.TryGetProperty("content", out JsonElement content))
            {
                node.Children.AddRange(ParseContent(content, Parse));
            }

            SetRole(node, role);
            return node;
        }

        public ContextSegment Parse(JsonElement block)
        {
            string? type = Json.String(block, "type");
            ContextSegment segment = type switch
            {
                "text" => ContextSegment.FromText(SegmentKind.Text, TextLabel, Json.String(block, "text")),
                "thinking" => ContextSegment.FromText(SegmentKind.Thinking, "thinking", Json.String(block, "thinking")),
                "redacted_thinking" => RedactedThinking(block),
                "tool_use" or "server_tool_use" or "mcp_tool_use" => ToolUse(block),
                "image" => Image(block),
                "document" => Document(block),
                _ when type?.EndsWith("tool_result", StringComparison.Ordinal) == true => ToolResult(block),
                _ => Unknown(block, type),
            };

            segment.CacheBreakpoint = HasCacheBreakpoint(block);
            return segment;
        }

        private static ContextSegment RedactedThinking(JsonElement block) =>
            new()
            {
                Kind = SegmentKind.Thinking,
                Label = "redacted thinking",
                Text = "",
                Weight = Json.String(block, "data")?.Length ?? 0,
                Note = "encrypted; size from the encrypted payload",
            };

        private ContextSegment ToolUse(JsonElement block)
        {
            string name = Json.String(block, "name") ?? UnnamedTool;
            toolNames.Remember(Json.String(block, "id"), name);
            return block.TryGetProperty("input", out JsonElement input)
                ? ContextSegment.FromJson(SegmentKind.ToolCall, name, input)
                : ContextSegment.FromText(SegmentKind.ToolCall, name, "");
        }

        private ContextSegment ToolResult(JsonElement block)
        {
            string name = toolNames.Find(Json.String(block, "tool_use_id")) ?? Json.String(block, "type") ?? "tool_result";
            var result = new ContextSegment { Kind = SegmentKind.ToolResult, Label = name };
            if (block.TryGetProperty("is_error", out JsonElement isError) && isError.ValueKind == JsonValueKind.True)
            {
                result.Note = "error";
            }

            if (block.TryGetProperty("content", out JsonElement content))
            {
                SetResultContent(result, content);
            }
            else
            {
                result.Text = "";
            }

            return result;
        }

        private void SetResultContent(ContextSegment result, JsonElement content)
        {
            switch (content.ValueKind)
            {
                case JsonValueKind.String:
                    result.Text = content.GetString();
                    result.Weight = result.Text?.Length ?? 0;
                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement part in content.EnumerateArray())
                    {
                        ContextSegment child = Parse(part);
                        child.Label = $"{result.Label} · {child.Label}";
                        result.Children.Add(child);
                    }

                    break;
                default:
                    // Server tool results (web search etc.) carry structured content.
                    result.Json = Json.Format(content);
                    result.Weight = Json.Format(content, indented: false).Length;
                    break;
            }
        }

        private static ContextSegment Image(JsonElement block)
        {
            if (!Json.TryObject(block, "source", out JsonElement source))
            {
                return ImageFromBase64(ImageLabel, null);
            }

            return Json.String(source, "type") switch
            {
                "base64" => ImageFromBase64($"image ({Json.String(source, "media_type")})", Json.String(source, "data")),
                "url" => ImageFromUrl(ImageLabel, Json.String(source, "url")),
                _ => ImageFromBase64(ImageLabel, null),
            };
        }

        private ContextSegment Document(JsonElement block)
        {
            string title = Json.String(block, "title") is { } documentTitle ? $"document: {documentTitle}" : "document";
            if (!Json.TryObject(block, "source", out JsonElement source))
            {
                return Unknown(block, "document");
            }

            return Json.String(source, "type") switch
            {
                "base64" when Json.String(source, "media_type") == PdfMediaType => PdfFromBase64(title, Json.String(source, "data")),
                "text" => ContextSegment.FromText(SegmentKind.Document, title, Json.String(source, "data")),
                "content" when Json.TryArray(source, "content", out JsonElement parts) => DocumentOf(title, parts),
                _ => UnsizedDocument(title, source),
            };
        }

        private ContextSegment DocumentOf(string title, JsonElement parts)
        {
            var document = new ContextSegment { Kind = SegmentKind.Document, Label = title };
            document.Children.AddRange(parts.EnumerateArray().Select(Parse));
            return document;
        }
    }

    /// <summary>Folds message_start, content_block_* and message_delta events back into one message.</summary>
    private sealed class MessageStream
    {
        private readonly SortedDictionary<int, JsonObject> blocks = new();
        private readonly Dictionary<int, StringBuilder> partialInputs = new();
        private JsonObject? message;
        private bool isRecognised;

        public void Apply(JsonObject data)
        {
            int index = Json.Int(data, "index") ?? 0;
            switch (Json.String(data, "type"))
            {
                case "message_start":
                    message = data["message"]?.DeepClone() as JsonObject;
                    isRecognised = true;
                    break;
                case "content_block_start" when data["content_block"] is JsonObject block:
                    blocks[index] = (JsonObject)block.DeepClone();
                    isRecognised = true;
                    break;
                case "content_block_delta":
                    ApplyBlockDelta(data, index);
                    break;
                case "content_block_stop":
                    FinishInput(index);
                    break;
                case "message_delta":
                    ApplyMessageDelta(data);
                    isRecognised = true;
                    break;
                case "error":
                    message ??= new JsonObject();
                    message["error"] = data["error"]?.DeepClone();
                    isRecognised = true;
                    break;
            }
        }

        /// <summary>The folded message; null when the stream held no Anthropic events.</summary>
        public JsonElement? Finish()
        {
            if (!isRecognised)
            {
                return null;
            }

            // An aborted stream may stop mid tool input.
            foreach (int index in partialInputs.Keys.ToList())
            {
                FinishInput(index);
            }

            JsonObject result = message ?? new JsonObject { ["type"] = "message", ["role"] = AssistantRole };
            result["content"] = new JsonArray(blocks.Values.Select(b => (JsonNode)b).ToArray());
            return Json.ToElement(result);
        }

        private void ApplyBlockDelta(JsonObject data, int index)
        {
            if (data["delta"] is not JsonObject delta || !blocks.TryGetValue(index, out JsonObject? block))
            {
                return;
            }

            switch (Json.String(delta, "type"))
            {
                case "text_delta":
                    Json.Append(block, "text", Json.String(delta, "text"));
                    break;
                case "thinking_delta":
                    Json.Append(block, "thinking", Json.String(delta, "thinking"));
                    break;
                case "signature_delta":
                    block["signature"] = Json.String(delta, "signature");
                    break;
                case "input_json_delta":
                    PartialInput(index).Append(Json.String(delta, "partial_json"));
                    break;
                case "citations_delta" when delta["citation"] is { } citation:
                    Json.GetOrAddArray(block, "citations").Add(citation.DeepClone());
                    break;
            }
        }

        private StringBuilder PartialInput(int index)
        {
            if (!partialInputs.TryGetValue(index, out StringBuilder? input))
            {
                partialInputs[index] = input = new StringBuilder();
            }

            return input;
        }

        private void FinishInput(int index)
        {
            if (!partialInputs.Remove(index, out StringBuilder? input) || !blocks.TryGetValue(index, out JsonObject? block))
            {
                return;
            }

            string text = input.ToString();
            block["input"] = text.Length == 0 ? new JsonObject() : Json.TryParseNode(text) ?? JsonValue.Create(text);
        }

        private void ApplyMessageDelta(JsonObject data)
        {
            message ??= new JsonObject();
            if (data["delta"] is JsonObject messageDelta)
            {
                CopyProperties(messageDelta, message);
            }

            if (data["usage"] is JsonObject usageDelta)
            {
                CopyProperties(usageDelta, Json.GetOrAddObject(message, "usage"));
            }
        }

        private static void CopyProperties(JsonObject source, JsonObject target)
        {
            foreach ((string name, JsonNode? value) in source)
            {
                target[name] = value?.DeepClone();
            }
        }
    }
}
