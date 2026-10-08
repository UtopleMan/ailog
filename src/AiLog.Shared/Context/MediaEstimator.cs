using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace AiLog.Shared.Context;

/// <summary>
/// Rough token costs for images and documents, which providers bill by dimensions or pages rather than by their
/// encoded size. Uses Anthropic's published formula (pixels / 750 after downscaling to fit 1568px and ~1.15MP).
/// </summary>
public static partial class MediaEstimator
{
    public const long UnknownImageTokens = 1600;
    public const long TokensPerPdfPage = 3000;

    private const int MaxEdge = 1568;
    private const double MaxPixels = 1_150_000;

    public static (long Tokens, string Note) Image(string? base64)
    {
        if (base64 is not null && TryReadSize(DecodePrefix(base64, 256 * 1024), out var width, out var height))
        {
            return (ImageTokens(width, height), $"{width}×{height} px");
        }

        return (UnknownImageTokens, "size unknown, flat estimate");
    }

    public static long ImageTokens(int width, int height)
    {
        double w = width, h = height;
        var scale = Math.Min(1, MaxEdge / Math.Max(w, h));
        w *= scale;
        h *= scale;
        if (w * h > MaxPixels)
        {
            var shrink = Math.Sqrt(MaxPixels / (w * h));
            w *= shrink;
            h *= shrink;
        }

        return (long)Math.Ceiling(w * h / 750);
    }

    public static (long Tokens, string Note) Pdf(string? base64)
    {
        if (base64 is null)
        {
            return (0, "size unknown");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return (0, "size unknown");
        }

        var pages = PageObject().Count(Encoding.Latin1.GetString(bytes));
        return pages == 0
            ? (0, "page count unknown")
            : (pages * TokensPerPdfPage, $"{pages} page{(pages == 1 ? "" : "s")}, ≈{TokensPerPdfPage:N0} tokens each");
    }

    /// <summary>Splits a data URL ("data:image/png;base64,...") into its base64 payload.</summary>
    public static string? DataUrlPayload(string? url)
    {
        if (url is null || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comma = url.IndexOf(',');
        return comma < 0 ? null : url[(comma + 1)..];
    }

    private static byte[] DecodePrefix(string base64, int maxChars)
    {
        var length = Math.Min(base64.Length, maxChars) / 4 * 4;
        try
        {
            return Convert.FromBase64String(base64[..length]);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    public static bool TryReadSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;

        // PNG: IHDR is always the first chunk.
        if (data.Length >= 24 && data[..8].SequenceEqual((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            width = BinaryPrimitives.ReadInt32BigEndian(data[16..]);
            height = BinaryPrimitives.ReadInt32BigEndian(data[20..]);
        }
        // GIF
        else if (data.Length >= 10 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        }
        // WebP: RIFF....WEBP then a VP8, VP8L or VP8X chunk.
        else if (data.Length >= 30 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            var chunk = data[12..16];
            if (chunk.SequenceEqual("VP8 "u8))
            {
                width = BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF;
                height = BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF;
            }
            else if (chunk.SequenceEqual("VP8L"u8))
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
                width = (int)(bits & 0x3FFF) + 1;
                height = (int)((bits >> 14) & 0x3FFF) + 1;
            }
            else if (chunk.SequenceEqual("VP8X"u8))
            {
                width = (data[24] | data[25] << 8 | data[26] << 16) + 1;
                height = (data[27] | data[28] << 8 | data[29] << 16) + 1;
            }
        }
        // JPEG: walk the segments to the first start-of-frame marker.
        else if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < data.Length && data[i] == 0xFF)
            {
                var marker = data[i + 1];
                var length = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..]);
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 5)..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 7)..]);
                    break;
                }

                i += 2 + length;
            }
        }

        return width > 0 && height > 0;
    }

    // Page objects, not the /Pages tree nodes.
    [GeneratedRegex(@"/Type\s*/Page(?![a-zA-Z])")]
    private static partial Regex PageObject();
}
