using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiLog.Shared;

/// <summary>Small reflection-free JSON helpers.</summary>
public static class Json
{
    private static readonly JsonWriterOptions indentedOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonWriterOptions compactOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Serialises an element without escaping non-ASCII text; indented unless asked otherwise.</summary>
    public static string Format(JsonElement element, bool indented = true) =>
        Write(writer => element.WriteTo(writer), indented);

    /// <summary>Serialises a node without escaping non-ASCII text; indented unless asked otherwise.</summary>
    public static string Format(JsonNode node, bool indented = true) =>
        Write(writer => node.WriteTo(writer), indented);

    private static string Write(Action<Utf8JsonWriter> write, bool indented)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, indented ? indentedOptions : compactOptions))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Converts a mutable node into a standalone read-only element.</summary>
    public static JsonElement ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(Format(node, indented: false));
        return document.RootElement.Clone();
    }

    /// <summary>Parses text into a mutable node; null when it is not valid JSON.</summary>
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

    /// <summary>Parses text into a standalone element; null when it is not valid JSON.</summary>
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

    /// <summary>A string property of an object; null when missing or of another kind.</summary>
    public static string? String(JsonElement element, string name) =>
        TryProperty(element, name, JsonValueKind.String, out JsonElement value) ? value.GetString() : null;

    /// <summary>Finds an object-valued property of an object.</summary>
    public static bool TryObject(JsonElement element, string name, out JsonElement value) =>
        TryProperty(element, name, JsonValueKind.Object, out value);

    /// <summary>Finds an array-valued property of an object.</summary>
    public static bool TryArray(JsonElement element, string name, out JsonElement value) =>
        TryProperty(element, name, JsonValueKind.Array, out value);

    /// <summary>The items of an array property; empty when the property is missing or not an array.</summary>
    public static IEnumerable<JsonElement> Items(JsonElement element, string name)
    {
        if (!TryArray(element, name, out JsonElement array))
        {
            return [];
        }

        return array.EnumerateArray();
    }

    private static bool TryProperty(JsonElement element, string name, JsonValueKind kind, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.ValueKind == kind;
    }

    /// <summary>A string property of an object node; null when missing or of another kind.</summary>
    public static string? String(JsonNode? node, string name) =>
        node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>An integer property of an object node; null when missing or of another kind.</summary>
    public static int? Int(JsonNode? node, string name) =>
        node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue(out int number) ? number : null;

    /// <summary>Appends text to a string property, creating it when missing; empty text changes nothing.</summary>
    public static void Append(JsonObject obj, string name, string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            obj[name] = (String(obj, name) ?? "") + text;
        }
    }

    /// <summary>The object stored under <paramref name="name"/>, replacing anything else there with a new one.</summary>
    public static JsonObject GetOrAddObject(JsonObject obj, string name)
    {
        if (obj[name] is not JsonObject value)
        {
            obj[name] = value = new JsonObject();
        }

        return value;
    }

    /// <summary>The array stored under <paramref name="name"/>, replacing anything else there with a new one.</summary>
    public static JsonArray GetOrAddArray(JsonObject obj, string name)
    {
        if (obj[name] is not JsonArray value)
        {
            obj[name] = value = new JsonArray();
        }

        return value;
    }
}
