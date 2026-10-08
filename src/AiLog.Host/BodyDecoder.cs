using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AiLog.Contracts;

namespace AiLog.Host;

/// <summary>Body bytes exactly as they crossed the wire, with the headers needed to decode them.</summary>
internal sealed record CapturedBody(ReadOnlyMemory<byte> Wire, string? ContentType, string? ContentEncoding);

/// <summary>Turns captured wire bytes into a readable <see cref="LoggedBody"/>.</summary>
internal static class BodyDecoder
{
    private const string IdentityEncoding = "identity";
    private const string JsonNotParsedNote = "Content-Type is JSON but the body did not parse; stored as text.";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static LoggedBody? Decode(CapturedBody body)
    {
        if (body.Wire.IsEmpty)
        {
            return null;
        }

        DecodedContent decoded = body.ContentEncoding is { } encoding && IsContentEncoded(encoding)
            ? DecodeEncoded(body, encoding)
            : DecodeContent(body.Wire.ToArray(), body.ContentType);

        return new LoggedBody
        {
            Format = decoded.Format,
            SizeBytes = body.Wire.Length,
            ContentEncoding = body.ContentEncoding,
            Note = decoded.Note,
            Content = decoded.Content,
        };
    }

    private static bool IsContentEncoded(string encoding) =>
        !string.IsNullOrWhiteSpace(encoding) && !encoding.Equals(IdentityEncoding, StringComparison.OrdinalIgnoreCase);

    /// <summary>Falls back to the raw wire bytes as base64 when the content-encoding cannot be undone.</summary>
    private static DecodedContent DecodeEncoded(CapturedBody body, string encoding)
    {
        byte[] decompressed;
        try
        {
            decompressed = Decompress(body.Wire.ToArray(), encoding.Trim());
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return DecodedContent.Base64(body.Wire.ToArray()) with
            {
                Note = $"Could not decode content-encoding '{encoding}': {ex.Message}",
            };
        }

        return DecodeContent(decompressed, body.ContentType);
    }

    private static byte[] Decompress(byte[] data, string encoding)
    {
        using MemoryStream input = new(data);
        using Stream decoder = encoding.ToLowerInvariant() switch
        {
            "gzip" or "x-gzip" => new GZipStream(input, CompressionMode.Decompress),
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            "deflate" => new ZLibStream(input, CompressionMode.Decompress),
            _ => throw new NotSupportedException($"Unsupported content-encoding '{encoding}'."),
        };
        using MemoryStream output = new();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    private static DecodedContent DecodeContent(byte[] bytes, string? contentType)
    {
        if (TryDecodeUtf8(bytes) is not { } text)
        {
            return DecodedContent.Base64(bytes);
        }

        if (!IsJson(contentType))
        {
            return DecodedContent.Text(text);
        }

        return TryParseJson(bytes) ?? DecodedContent.Text(text) with { Note = JsonNotParsedNote };
    }

    private static string? TryDecodeUtf8(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool IsJson(string? contentType) =>
        contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;

    private static DecodedContent? TryParseJson(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            return new DecodedContent(BodyFormat.Json, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record DecodedContent(BodyFormat Format, JsonElement Content, string? Note = null)
    {
        public static DecodedContent Text(string text) =>
            new(BodyFormat.Text, JsonSerializer.SerializeToElement(text, AiLogJsonContext.Default.String));

        public static DecodedContent Base64(byte[] bytes) =>
            new(BodyFormat.Base64, JsonSerializer.SerializeToElement(Convert.ToBase64String(bytes), AiLogJsonContext.Default.String));
    }
}
