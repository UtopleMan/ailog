using System.Text.Json;
using System.Text.Json.Nodes;
using AiLog.Contracts;
using AiLog.Shared.Context;

namespace AiLog.Shared.Providers;

/// <summary>OpenAI Responses API (<c>POST /responses</c>), used by e.g. Codex.</summary>
public sealed class OpenAiResponsesAdapter : ProviderAdapter
{
    public override string Name => "OpenAI Responses";

    public override bool Matches(ExchangeLog log) =>
        PathOf(log).EndsWith("/responses", StringComparison.OrdinalIgnoreCase);

    protected override void ApplyUsage(JsonElement document, UsageBuilder usage)
    {
        // SSE: response.completed / response.incomplete carry "response.usage"; JSON bodies have it at the top.
        JsonElement body;
        if (Json.TryObject(document, "response", out var response) && Json.TryObject(response, "usage", out var inner))
        {
            body = inner;
        }
        // Anthropic usage can carry output_tokens_details as well; input_tokens_details is OpenAI's alone.
        else if (Json.TryObject(document, "usage", out var top) && top.TryGetProperty("input_tokens_details", out _))
        {
            body = top;
        }
        else
        {
            return;
        }

        // input_tokens already includes cached tokens.
        usage.InputIncludesCache = true;
        usage.Input(body, "input_tokens");
        usage.Output(body, "output_tokens");
        if (Json.TryObject(body, "input_tokens_details", out var details))
        {
            usage.CacheRead(details, "cached_tokens");
        }
    }

    public override JsonElement? Reassemble(IReadOnlyList<SseEvent> events)
    {
        JsonObject? response = null;
        var items = new SortedDictionary<int, JsonObject>();
        var done = new HashSet<int>();

        foreach (var item in events)
        {
            if (Json.TryParseNode(item.Data) is not JsonObject data)
            {
                continue;
            }

            var type = Json.String(data, "type") ?? item.EventType;
            var index = Json.Int(data, "output_index") ?? 0;
            switch (type)
            {
                // The terminal events carry the complete response; nothing else is needed.
                case "response.completed" or "response.incomplete" or "response.failed" when data["response"] is JsonObject final:
                    return Json.ToElement(final);

                case "response.created" or "response.in_progress" when data["response"] is JsonObject started:
                    response = (JsonObject)started.DeepClone();
                    break;

                case "response.output_item.added" when data["item"] is JsonObject added:
                    items[index] = (JsonObject)added.DeepClone();
                    break;

                case "response.output_item.done" when data["item"] is JsonObject finished:
                    items[index] = (JsonObject)finished.DeepClone();
                    done.Add(index);
                    break;

                case "response.output_text.delta" when items.TryGetValue(index, out var message) && !done.Contains(index):
                    var part = Part(message, "content", Json.Int(data, "content_index") ?? 0, "output_text");
                    Json.Append(part, "text", Json.String(data, "delta"));
                    break;

                case "response.reasoning_summary_text.delta" when items.TryGetValue(index, out var reasoning) && !done.Contains(index):
                    var summary = Part(reasoning, "summary", Json.Int(data, "summary_index") ?? 0, "summary_text");
                    Json.Append(summary, "text", Json.String(data, "delta"));
                    break;

                case "response.function_call_arguments.delta" when items.TryGetValue(index, out var call) && !done.Contains(index):
                    Json.Append(call, "arguments", Json.String(data, "delta"));
                    break;

                case "error":
                    response ??= new JsonObject();
                    response["error"] = data.DeepClone();
                    break;
            }
        }

        if (response is null && items.Count == 0)
        {
            return null;
        }

        response ??= new JsonObject { ["object"] = "response" };
        response["output"] = new JsonArray(items.Values.Select(i => (JsonNode)i).ToArray());
        return Json.ToElement(response);
    }

    private static JsonObject Part(JsonObject item, string arrayName, int index, string type)
    {
        if (item[arrayName] is not JsonArray array)
        {
            item[arrayName] = array = new JsonArray();
        }

        while (array.Count <= index)
        {
            array.Add((JsonNode)new JsonObject { ["type"] = type, ["text"] = "" });
        }

        return (JsonObject)array[index]!;
    }

    public override ContextSegment ParseRequest(JsonElement body)
    {
        var root = ContextSegment.Group("Request");
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);

        var system = ContextSegment.Group(SystemGroup);
        if (Json.String(body, "instructions") is { } instructions)
        {
            var segment = ContextSegment.FromText(SegmentKind.Text, "instructions", instructions);
            segment.Role = "system";
            system.Children.Add(segment);
        }

        var tools = ContextSegment.Group(ToolsGroup);
        if (Json.TryArray(body, "tools", out var toolArray))
        {
            foreach (var tool in toolArray.EnumerateArray())
            {
                tools.Children.Add(ContextSegment.FromJson(SegmentKind.ToolDefinition, Json.String(tool, "name") ?? Json.String(tool, "type") ?? "tool", tool));
            }
        }

        var messages = ContextSegment.Group(MessagesGroup);
        if (body.TryGetProperty("input", out var input))
        {
            if (input.ValueKind == JsonValueKind.String)
            {
                var message = new ContextSegment { Kind = SegmentKind.Message, Label = "#1 user", Role = "user" };
                message.Children.Add(ContextSegment.FromText(SegmentKind.Text, "text", input.GetString()));
                SetRole(message, "user");
                messages.Children.Add(message);
            }
            else if (input.ValueKind == JsonValueKind.Array)
            {
                var number = 0;
                foreach (var item in input.EnumerateArray())
                {
                    messages.Children.Add(ParseItem(item, ++number, toolNames));
                }
            }
        }

        root.Children.AddRange([system, tools, messages]);
        return root;
    }

    public override ContextSegment ParseResponse(JsonElement message)
    {
        var root = ContextSegment.Group("Output");
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Json.TryArray(message, "output", out var output))
        {
            foreach (var item in output.EnumerateArray())
            {
                root.Children.AddRange(ParseItem(item, 0, toolNames).Children);
            }
        }

        if (Json.TryObject(message, "error", out var error))
        {
            root.Children.Add(ContextSegment.FromJson(SegmentKind.Other, "error", error));
        }

        SetRole(root, "assistant");
        root.Note = Json.String(message, "status") is { } status ? $"status: {status}" : null;
        return root;
    }

    /// <summary>Each input/output item becomes a message node holding its blocks.</summary>
    private static ContextSegment ParseItem(JsonElement item, int number, Dictionary<string, string> toolNames)
    {
        var type = Json.String(item, "type") ?? (item.TryGetProperty("role", out _) ? "message" : null);
        var role = Json.String(item, "role") ?? (type is "function_call_output" or "custom_tool_call_output" ? "tool" : "assistant");
        var node = new ContextSegment { Kind = SegmentKind.Message, Label = $"#{number} {(type == "message" ? role : type)}", Role = role };

        switch (type)
        {
            case "message":
                if (item.TryGetProperty("content", out var content))
                {
                    if (content.ValueKind == JsonValueKind.String)
                    {
                        node.Children.Add(ContextSegment.FromText(SegmentKind.Text, "text", content.GetString()));
                    }
                    else if (content.ValueKind == JsonValueKind.Array)
                    {
                        node.Children.AddRange(content.EnumerateArray().Select(ParsePart));
                    }
                }

                break;

            case "function_call" or "custom_tool_call":
            {
                var name = Json.String(item, "name") ?? "tool";
                if (Json.String(item, "call_id") is { } callId)
                {
                    toolNames[callId] = name;
                }

                var arguments = Json.String(item, "arguments") ?? Json.String(item, "input") ?? "";
                node.Children.Add(Json.TryParse(arguments) is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } parsed
                    ? ContextSegment.FromJson(SegmentKind.ToolCall, name, parsed)
                    : ContextSegment.FromText(SegmentKind.ToolCall, name, arguments));
                break;
            }

            case "function_call_output" or "custom_tool_call_output":
            {
                var name = Json.String(item, "call_id") is { } callId && toolNames.TryGetValue(callId, out var known) ? known : "tool";
                if (item.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                {
                    var result = new ContextSegment { Kind = SegmentKind.ToolResult, Label = name };
                    result.Children.AddRange(output.EnumerateArray().Select(ParsePart));
                    node.Children.Add(result);
                }
                else
                {
                    node.Children.Add(ContextSegment.FromText(SegmentKind.ToolResult, name, Json.String(item, "output") ?? ""));
                }

                break;
            }

            case "reasoning":
            {
                var summary = Json.TryArray(item, "summary", out var parts)
                    ? string.Join("\n\n", parts.EnumerateArray().Select(p => Json.String(p, "text")).Where(t => t is not null))
                    : "";
                var segment = ContextSegment.FromText(SegmentKind.Thinking, "reasoning", summary);
                if (Json.String(item, "encrypted_content") is { } encrypted)
                {
                    // The summary is not what the model reads back; the encrypted payload is.
                    segment.Weight = Math.Max(segment.Weight, encrypted.Length);
                    segment.Note = "encrypted content; size from the encrypted payload";
                }

                node.Children.Add(segment);
                break;
            }

            default:
                node.Children.Add(Unknown(item, type));
                break;
        }

        SetRole(node, role);
        return node;
    }

    private static ContextSegment ParsePart(JsonElement part)
    {
        var type = Json.String(part, "type");
        return type switch
        {
            "input_text" or "output_text" or "text" => ContextSegment.FromText(SegmentKind.Text, "text", Json.String(part, "text")),
            "refusal" => ContextSegment.FromText(SegmentKind.Text, "refusal", Json.String(part, "refusal")),
            "input_image" => ImageFromUrl("image", Json.String(part, "image_url")),
            "input_file" => Json.String(part, "file_data") is { } data
                ? PdfFromBase64($"file: {Json.String(part, "filename") ?? "pdf"}", MediaEstimator.DataUrlPayload(data) ?? data)
                : new ContextSegment { Kind = SegmentKind.Document, Label = "file", FixedTokens = 0, Note = "size unknown", Json = Json.Format(part) },
            _ => Unknown(part, type),
        };
    }

}
