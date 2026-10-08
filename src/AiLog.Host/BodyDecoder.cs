using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AiLog.Contracts;

namespace AiLog.Host;

/// <summary>Turns captured wire bytes into a readable <see cref="LoggedBody"/>.</summary>
internal static class BodyDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static LoggedBody? Decode(ReadOnlySpan<byte> wire, string? contentType, string? contentEncoding)
    {
        if (wire.IsEmpty)
        {
            return null;
        }

        string? note = null;
        var bytes = wire.ToArray();
        if (!string.IsNullOrWhiteSpace(contentEncoding) && !contentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                bytes = Decompress(bytes, contentEncoding.Trim());
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
            {
                note = $"Could not decode content-encoding '{contentEncoding}': {ex.Message}";
                return Build(BodyFormat.Base64, wire.ToArray(), wire.Length, contentEncoding, note);
            }
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Build(BodyFormat.Base64, bytes, wire.Length, contentEncoding, note);
        }

        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                using var document = JsonDocument.Parse(bytes);
                return new LoggedBody
                {
                    Format = BodyFormat.Json,
                    SizeBytes = wire.Length,
                    ContentEncoding = contentEncoding,
                    Note = note,
                    Content = document.RootElement.Clone(),
                };
            }
            catch (JsonException)
            {
                note = "Content-Type is JSON but the body did not parse; stored as text.";
            }
        }

        return new LoggedBody
        {
            Format = BodyFormat.Text,
            SizeBytes = wire.Length,
            ContentEncoding = contentEncoding,
            Note = note,
            Content = JsonSerializer.SerializeToElement(text, AiLogJsonContext.Default.String),
        };
    }

    private static LoggedBody Build(BodyFormat format, byte[] bytes, long size, string? encoding, string? note) => new()
    {
        Format = format,
        SizeBytes = size,
        ContentEncoding = encoding,
        Note = note,
        Content = JsonSerializer.SerializeToElement(Convert.ToBase64String(bytes), AiLogJsonContext.Default.String),
    };

    private static byte[] Decompress(byte[] data, string encoding)
    {
        using var input = new MemoryStream(data);
        using Stream decoder = encoding.ToLowerInvariant() switch
        {
            "gzip" or "x-gzip" => new GZipStream(input, CompressionMode.Decompress),
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            "deflate" => new ZLibStream(input, CompressionMode.Decompress),
            _ => throw new NotSupportedException($"Unsupported content-encoding '{encoding}'."),
        };
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }
}
