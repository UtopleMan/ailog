using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace AiLog.Shared.Context;

/// <summary>A media token estimate and how it was reached.</summary>
public sealed record MediaEstimate(long Tokens, string Note);

/// <summary>Pixel dimensions read from an image header.</summary>
public readonly record struct ImageSize(int Width, int Height);

/// <summary>
/// Rough token costs for images and documents, which providers bill by dimensions or pages rather than by their
/// encoded size. Uses Anthropic's published formula (pixels / 750 after downscaling to fit 1568px and ~1.15MP).
/// </summary>
public static partial class MediaEstimator
{
    /// <summary>Flat estimate for an image whose dimensions cannot be read.</summary>
    public const long UnknownImageTokens = 1600;

    /// <summary>Estimated cost of one PDF page (text plus page image).</summary>
    public const long TokensPerPdfPage = 3000;

    private const int MaxEdge = 1568;
    private const double MaxPixels = 1_150_000;
    private const double PixelsPerToken = 750;

    /// <summary>Enough base64 to reach the dimensions in any common image header.</summary>
    private const int HeaderBase64Chars = 256 * 1024;

    private const int Base64QuantumChars = 4;
    private const string DataUrlScheme = "data:";
    private const int WebPDimensionMask = 0x3FFF;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Estimates an image from the dimensions in its base64-encoded header.</summary>
    public static MediaEstimate Image(string? base64)
    {
        if (base64 is not null && ReadSize(DecodePrefix(base64, HeaderBase64Chars)) is { } size)
        {
            return new MediaEstimate(ImageTokens(size.Width, size.Height), $"{size.Width}×{size.Height} px");
        }

        return new MediaEstimate(UnknownImageTokens, "size unknown, flat estimate");
    }

    /// <summary>Tokens for an image of the given size after the provider's downscaling.</summary>
    public static long ImageTokens(int width, int height)
    {
        double w = width;
        double h = height;
        double scale = Math.Min(1, MaxEdge / Math.Max(w, h));
        w *= scale;
        h *= scale;
        if (w * h > MaxPixels)
        {
            double shrink = Math.Sqrt(MaxPixels / (w * h));
            w *= shrink;
            h *= shrink;
        }

        return (long)Math.Ceiling(w * h / PixelsPerToken);
    }

    /// <summary>Estimates a base64-encoded PDF by counting its page objects.</summary>
    public static MediaEstimate Pdf(string? base64)
    {
        if (base64 is null || TryDecodeBase64(base64) is not { } bytes)
        {
            return new MediaEstimate(0, "size unknown");
        }

        int pages = PageObject().Count(Encoding.Latin1.GetString(bytes));
        if (pages == 0)
        {
            return new MediaEstimate(0, "page count unknown");
        }

        string plural = pages == 1 ? "" : "s";
        return new MediaEstimate(pages * TokensPerPdfPage, $"{pages} page{plural}, ≈{TokensPerPdfPage:N0} tokens each");
    }

    /// <summary>Splits a data URL ("data:image/png;base64,...") into its base64 payload.</summary>
    public static string? DataUrlPayload(string? url)
    {
        if (url is null || !url.StartsWith(DataUrlScheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int comma = url.IndexOf(',');
        return comma < 0 ? null : url[(comma + 1)..];
    }

    private static byte[] DecodePrefix(string base64, int maxChars)
    {
        int length = Math.Min(base64.Length, maxChars) / Base64QuantumChars * Base64QuantumChars;
        return TryDecodeBase64(base64[..length]) ?? [];
    }

    private static byte[]? TryDecodeBase64(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Reads the pixel size from a PNG, GIF, WebP or JPEG header; null when unrecognised or empty.</summary>
    public static ImageSize? ReadSize(ReadOnlySpan<byte> data) =>
        ReadHeaderSize(data) is { Width: > 0, Height: > 0 } size ? size : null;

    private static ImageSize? ReadHeaderSize(ReadOnlySpan<byte> data)
    {
        if (IsPng(data))
        {
            return ReadPngSize(data);
        }

        if (IsGif(data))
        {
            return ReadGifSize(data);
        }

        if (IsWebP(data))
        {
            return ReadWebPSize(data);
        }

        if (IsJpeg(data))
        {
            return ReadJpegSize(data);
        }

        return null;
    }

    private static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 24 && data[..8].SequenceEqual(PngSignature);

    private static bool IsGif(ReadOnlySpan<byte> data) => data.Length >= 10 && data[..3].SequenceEqual("GIF"u8);

    private static bool IsWebP(ReadOnlySpan<byte> data) =>
        data.Length >= 30 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8);

    private static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8;

    /// <summary>IHDR is always the first chunk.</summary>
    private static ImageSize ReadPngSize(ReadOnlySpan<byte> data) =>
        new ImageSize(BinaryPrimitives.ReadInt32BigEndian(data[16..]), BinaryPrimitives.ReadInt32BigEndian(data[20..]));

    private static ImageSize ReadGifSize(ReadOnlySpan<byte> data) =>
        new ImageSize(BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));

    /// <summary>RIFF....WEBP is followed by a VP8, VP8L or VP8X chunk, each storing the size differently.</summary>
    private static ImageSize? ReadWebPSize(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> chunk = data[12..16];
        if (chunk.SequenceEqual("VP8 "u8))
        {
            return new ImageSize(BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & WebPDimensionMask,
                BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & WebPDimensionMask);
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
            return new ImageSize((int)(bits & WebPDimensionMask) + 1, (int)((bits >> 14) & WebPDimensionMask) + 1);
        }

        if (chunk.SequenceEqual("VP8X"u8))
        {
            return new ImageSize(ReadUInt24LittleEndian(data[24..]) + 1, ReadUInt24LittleEndian(data[27..]) + 1);
        }

        return null;
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> data) => data[0] | data[1] << 8 | data[2] << 16;

    /// <summary>Walks the segments to the first start-of-frame marker.</summary>
    private static ImageSize? ReadJpegSize(ReadOnlySpan<byte> data)
    {
        int position = 2;
        while (position + 9 < data.Length && data[position] == 0xFF)
        {
            byte marker = data[position + 1];
            if (IsStartOfFrame(marker))
            {
                return new ImageSize(BinaryPrimitives.ReadUInt16BigEndian(data[(position + 7)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(data[(position + 5)..]));
            }

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);
            position += 2 + segmentLength;
        }

        return null;
    }

    /// <summary>SOF0 to SOF15, except DHT (C4), JPG (C8) and DAC (CC) which share the range.</summary>
    private static bool IsStartOfFrame(byte marker) => marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;

    /// <summary>Page objects, not the /Pages tree nodes.</summary>
    [GeneratedRegex(@"/Type\s*/Page(?![a-zA-Z])")]
    private static partial Regex PageObject();
}
