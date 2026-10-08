using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiLog.Contracts;
using AiLog.Shared.Context;

namespace AiLog.Shared.Providers;

/// <summary>Anthropic Messages API (<c>POST /v1/messages</c>), JSON or streamed.</summary>
public sealed class AnthropicMessagesAdapter : ProviderAdapter
{
    public override string Name => "Anthropic Messages";

    public override bool Matches(ExchangeLog log) =>
        PathOf(log).EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase) && BodyHas(log, "messages");

    protected override void ApplyUsage(JsonElement document, UsageBuilder usage)
    {
        if (document.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // SSE message_start carries the input counts inside "message"; message_delta and JSON bodies at the top.
        if (Json.String(document, "type") == "message_start" && Json.TryObject(document, "message", out var message)
            && Json.TryObject(message, "usage", out var startUsage))
        {
            Apply(startUsage, usage);
        }
        else if (Json.TryObject(document, "usage", out var bodyUsage))
        {
            Apply(bodyUsage, usage);
        }
    }

    private static void Apply(JsonElement usage, UsageBuilder builder)
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

    public override JsonElement? Reassemble(IReadOnlyList<SseEvent> events)
    {
        JsonObject? message = null;
        var blocks = new SortedDictionary<int, JsonObject>();
        var partialInputs = new Dictionary<int, StringBuilder>();
        var recognised = false;

        foreach (var item in events)
        {
            if (Json.TryParseNode(item.Data) is not JsonObject data)
            {
                continue;
            }

            var index = Json.Int(data, "index") ?? 0;
            switch (Json.String(data, "type"))
            {
                case "message_start":
                    message = data["message"]?.DeepClone() as JsonObject;
                    recognised = true;
                    break;

                case "content_block_start" when data["content_block"] is JsonObject block:
                    blocks[index] = (JsonObject)block.DeepClone();
                    recognised = true;
                    break;

                case "content_block_delta" when data["delta"] is JsonObject delta && blocks.TryGetValue(index, out var target):
                    ApplyDelta(target, delta, index, partialInputs);
                    break;

                case "content_block_stop":
                    FinishInput(blocks, partialInputs, index);
                    break;

                case "message_delta":
                    message ??= new JsonObject();
                    if (data["delta"] is JsonObject messageDelta)
                    {
                        foreach (var (name, value) in messageDelta)
                        {
                            message[name] = value?.DeepClone();
                        }
                    }

                    if (data["usage"] is JsonObject usageDelta)
                    {
                        if (message["usage"] is not JsonObject usage)
                        {
                            message["usage"] = usage = new JsonObject();
                        }

                        foreach (var (name, value) in usageDelta)
                        {
                            usage[name] = value?.DeepClone();
                        }
                    }

                    recognised = true;
                    break;

                case "error":
                    message ??= new JsonObject();
                    message["error"] = data["error"]?.DeepClone();
                    recognised = true;
                    break;
            }
        }

        if (!recognised)
        {
            return null;
        }

        // An aborted stream may stop mid tool input.
        foreach (var index in partialInputs.Keys.ToList())
        {
            FinishInput(blocks, partialInputs, index);
        }

        message ??= new JsonObject { ["type"] = "message", ["role"] = "assistant" };
        message["content"] = new JsonArray(blocks.Values.Select(b => (JsonNode)b).ToArray());
        return Json.ToElement(message);
    }

    private static void ApplyDelta(JsonObject block, JsonObject delta, int index, Dictionary<int, StringBuilder> partialInputs)
    {
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
                if (!partialInputs.TryGetValue(index, out var input))
                {
                    partialInputs[index] = input = new StringBuilder();
                }

                input.Append(Json.String(delta, "partial_json"));
                break;
            case "citations_delta" when delta["citation"] is { } citation:
                if (block["citations"] is not JsonArray citations)
                {
                    block["citations"] = citations = new JsonArray();
                }

                citations.Add(citation.DeepClone());
                break;
        }
    }

    private static void FinishInput(SortedDictionary<int, JsonObject> blocks, Dictionary<int, StringBuilder> partialInputs, int index)
    {
        if (!partialInputs.Remove(index, out var input) || !blocks.TryGetValue(index, out var block))
        {
            return;
        }

        var text = input.ToString();
        block["input"] = text.Length == 0 ? new JsonObject() : Json.TryParseNode(text) ?? JsonValue.Create(text);
    }

    public override ContextSegment ParseRequest(JsonElement body)
    {
        var root = ContextSegment.Group("Request");
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);

        var system = ContextSegment.Group(SystemGroup);
        if (body.TryGetProperty("system", out var systemValue))
        {
            if (systemValue.ValueKind == JsonValueKind.String)
            {
                system.Children.Add(ContextSegment.FromText(SegmentKind.Text, "system", systemValue.GetString()));
            }
            else if (systemValue.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in systemValue.EnumerateArray())
                {
                    system.Children.Add(ParseBlock(block, toolNames));
                }
            }
        }

        foreach (var block in system.Children)
        {
            block.Role = "system";
        }

        var tools = ContextSegment.Group(ToolsGroup);
        if (Json.TryArray(body, "tools", out var toolArray))
        {
            foreach (var tool in toolArray.EnumerateArray())
            {
                var name = Json.String(tool, "name") ?? Json.String(tool, "type") ?? "tool";
                var segment = ContextSegment.FromJson(SegmentKind.ToolDefinition, name, tool);
                segment.CacheBreakpoint = tool.TryGetProperty("cache_control", out _);
                tools.Children.Add(segment);
            }
        }

        var messages = ContextSegment.Group(MessagesGroup);
        if (Json.TryArray(body, "messages", out var messageArray))
        {
            var number = 0;
            foreach (var message in messageArray.EnumerateArray())
            {
                number++;
                var role = Json.String(message, "role") ?? "?";
                var node = new ContextSegment { Kind = SegmentKind.Message, Label = $"#{number} {role}", Role = role };
                if (message.TryGetProperty("content", out var content))
                {
                    if (content.ValueKind == JsonValueKind.String)
                    {
                        node.Children.Add(ContextSegment.FromText(SegmentKind.Text, "text", content.GetString()));
                    }
                    else if (content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            node.Children.Add(ParseBlock(block, toolNames));
                        }
                    }
                }

                SetRole(node, role);
                messages.Children.Add(node);
            }
        }

        root.Children.AddRange([system, tools, messages]);
        return root;
    }

    public override IEnumerable<ContextSegment> CacheOrder(ContextSegment request) =>
        // Anthropic's cache prefix is tools, then system, then messages.
        [request.Children[1], request.Children[0], request.Children[2]];

    public override ContextSegment ParseResponse(JsonElement message)
    {
        var root = ContextSegment.Group("Output");
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Json.TryArray(message, "content", out var content))
        {
            foreach (var block in content.EnumerateArray())
            {
                root.Children.Add(ParseBlock(block, toolNames));
            }
        }

        if (Json.TryObject(message, "error", out var error))
        {
            root.Children.Add(ContextSegment.FromJson(SegmentKind.Other, "error", error));
        }

        SetRole(root, "assistant");
        root.Note = Json.String(message, "stop_reason") is { } stop ? $"stop reason: {stop}" : null;
        return root;
    }


    private static ContextSegment ParseBlock(JsonElement block, Dictionary<string, string> toolNames)
    {
        var type = Json.String(block, "type");
        var segment = type switch
        {
            "text" => ContextSegment.FromText(SegmentKind.Text, "text", Json.String(block, "text")),
            "thinking" => ContextSegment.FromText(SegmentKind.Thinking, "thinking", Json.String(block, "thinking")),
            "redacted_thinking" => new ContextSegment
            {
                Kind = SegmentKind.Thinking, Label = "redacted thinking", Text = "",
                Weight = Json.String(block, "data")?.Length ?? 0, Note = "encrypted; size from the encrypted payload",
            },
            "tool_use" or "server_tool_use" or "mcp_tool_use" => ToolUse(block, toolNames),
            "image" => Image(block),
            "document" => Document(block, toolNames),
            _ when type?.EndsWith("tool_result", StringComparison.Ordinal) == true => ToolResult(block, toolNames),
            _ => Unknown(block, type),
        };

        segment.CacheBreakpoint = block.TryGetProperty("cache_control", out _);
        return segment;
    }

    private static ContextSegment ToolUse(JsonElement block, Dictionary<string, string> toolNames)
    {
        var name = Json.String(block, "name") ?? "tool";
        if (Json.String(block, "id") is { } id)
        {
            toolNames[id] = name;
        }

        return block.TryGetProperty("input", out var input)
            ? ContextSegment.FromJson(SegmentKind.ToolCall, name, input)
            : ContextSegment.FromText(SegmentKind.ToolCall, name, "");
    }

    private static ContextSegment ToolResult(JsonElement block, Dictionary<string, string> toolNames)
    {
        var id = Json.String(block, "tool_use_id");
        var name = id is not null && toolNames.TryGetValue(id, out var known) ? known : Json.String(block, "type") ?? "tool_result";
        var segment = new ContextSegment { Kind = SegmentKind.ToolResult, Label = name };
        if (block.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            segment.Note = "error";
        }

        if (!block.TryGetProperty("content", out var content))
        {
            segment.Text = "";
        }
        else if (content.ValueKind == JsonValueKind.String)
        {
            segment.Text = content.GetString();
            segment.Weight = segment.Text?.Length ?? 0;
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                var child = ParseBlock(part, toolNames);
                child.Label = $"{name} · {child.Label}";
                segment.Children.Add(child);
            }
        }
        else
        {
            // Server tool results (web search etc.) carry structured content.
            segment.Json = Json.Format(content);
            segment.Weight = Json.Format(content, indented: false).Length;
        }

        return segment;
    }

    private static ContextSegment Image(JsonElement block)
    {
        if (!Json.TryObject(block, "source", out var source))
        {
            return ImageFromBase64("image", null);
        }

        return Json.String(source, "type") switch
        {
            "base64" => ImageFromBase64($"image ({Json.String(source, "media_type")})", Json.String(source, "data")),
            "url" => ImageFromUrl("image", Json.String(source, "url")),
            _ => ImageFromBase64("image", null),
        };
    }

    private static ContextSegment Document(JsonElement block, Dictionary<string, string> toolNames)
    {
        var title = Json.String(block, "title") is { } t ? $"document: {t}" : "document";
        if (!Json.TryObject(block, "source", out var source))
        {
            return Unknown(block, "document");
        }

        switch (Json.String(source, "type"))
        {
            case "base64" when Json.String(source, "media_type") == "application/pdf":
                return PdfFromBase64(title, Json.String(source, "data"));
            case "text":
                var text = ContextSegment.FromText(SegmentKind.Document, title, Json.String(source, "data"));
                return text;
            case "content" when Json.TryArray(source, "content", out var parts):
                var group = new ContextSegment { Kind = SegmentKind.Document, Label = title };
                foreach (var part in parts.EnumerateArray())
                {
                    group.Children.Add(ParseBlock(part, toolNames));
                }

                return group;
            default:
                return new ContextSegment
                {
                    Kind = SegmentKind.Document, Label = title, FixedTokens = 0, Note = "size unknown",
                    Json = Json.Format(source),
                };
        }
    }
}
