using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiLog.Shared;

/// <summary>Small reflection-free JSON helpers.</summary>
public static class Json
{
    private static readonly JsonWriterOptions Pretty = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Format(JsonElement element, bool indented = true)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, indented ? Pretty : Compact))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string Format(JsonNode node, bool indented = true)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, indented ? Pretty : Compact))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static JsonElement ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(Format(node, indented: false));
        return document.RootElement.Clone();
    }

    public static JsonNode? TryParseNode(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonElement? TryParse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool TryObject(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }

    public static bool TryArray(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array;
    }

    public static string? String(JsonNode? node, string name) =>
        node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static int? Int(JsonNode? node, string name) =>
        node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    public static void Append(JsonObject obj, string name, string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            obj[name] = (String(obj, name) ?? "") + text;
        }
    }
}
