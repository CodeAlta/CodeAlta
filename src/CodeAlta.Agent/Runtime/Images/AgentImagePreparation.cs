using System.Diagnostics.CodeAnalysis;
using SkiaSharp;

namespace CodeAlta.Agent.Runtime.Images;

/// <summary>Limits an image must fit before it is sent to a model.</summary>
/// <param name="MaximumDimension">Longest side sent, in pixels.</param>
/// <param name="MaximumBytes">Largest encoded image sent, in bytes.</param>
/// <param name="MaximumSourceBytes">Largest file accepted, in bytes.</param>
/// <param name="MaximumSourcePixels">Largest image decoded, in pixels.</param>
internal sealed record AgentImageLimits(
    int MaximumDimension = 2048,
    int MaximumBytes = 3_750_000,
    long MaximumSourceBytes = 64L * 1024 * 1024,
    long MaximumSourcePixels = 100_000_000)
{
    /// <summary>
    /// The limits of a session: 2048 pixels on the longest side, and 3.75 MB, which is 5 MB once in base64, the
    /// smallest size a provider accepts for one image.
    /// </summary>
    public static AgentImageLimits Default { get; } = new();
}

/// <summary>An image ready to be sent to a model.</summary>
/// <param name="Bytes">The encoded image.</param>
/// <param name="MediaType">Its media type: PNG, JPEG or WebP.</param>
/// <param name="Width">Width in pixels, or 0 when the image could not be measured.</param>
/// <param name="Height">Height in pixels, or 0 when the image could not be measured.</param>
/// <param name="SourceWidth">Width of the image it was made from.</param>
/// <param name="SourceHeight">Height of the image it was made from.</param>
internal sealed record AgentPreparedImage(byte[] Bytes, string MediaType, int Width, int Height, int SourceWidth, int SourceHeight)
{
    /// <summary>Whether the image is smaller than the one it was made from.</summary>
    public bool Resized => Width != SourceWidth || Height != SourceHeight;
}

/// <summary>
/// Checks an image a tool returns and makes it fit what a model accepts: a PNG, JPEG or WebP image within
/// <see cref="AgentImageLimits"/>. An image that already fits is passed as it is.
/// </summary>
internal static class AgentImagePreparation
{
    private const int MaximumAttempts = 8;

    /// <summary>Names the image format a file starts with.</summary>
    /// <param name="bytes">The start of the file, or all of it.</param>
    /// <returns>The media type of a PNG, JPEG, GIF, WebP or BMP image; otherwise null.</returns>
    public static string? FindMediaType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])) return "image/png";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xff, 0xd8, 0xff])) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (bytes.Length >= 26 && bytes.StartsWith("BM"u8)) return "image/bmp";
        return null;
    }

    /// <summary>Reads the start of a file and names the image format it holds.</summary>
    /// <param name="path">An existing file.</param>
    /// <returns>The media type, or null when the file is not a PNG, JPEG, GIF, WebP or BMP image.</returns>
    public static string? FindFileMediaType(string path)
    {
        Span<byte> header = stackalloc byte[32];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        var count = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        return FindMediaType(header[..count]);
    }

    /// <summary>Checks an encoded image and makes it fit the limits.</summary>
    /// <param name="source">The encoded image file.</param>
    /// <param name="limits">The limits to fit.</param>
    /// <param name="image">The image to send; <paramref name="source"/> itself when it already fits.</param>
    /// <param name="error">Why the image cannot be sent, as a sentence for the model.</param>
    /// <returns>True when <paramref name="image"/> is set.</returns>
    public static bool TryPrepare(byte[] source, AgentImageLimits limits, [NotNullWhen(true)] out AgentPreparedImage? image, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        image = null;
        error = null;
        if (source.Length == 0)
        {
            error = "The image is empty.";
            return false;
        }

        if (source.Length > limits.MaximumSourceBytes)
        {
            error = $"The image is too large to read ({FormatBytes(source.Length)}; the limit is {FormatBytes(limits.MaximumSourceBytes)}).";
            return false;
        }

        var sniffed = FindMediaType(source);
        try
        {
            return TryPrepareCore(source, sniffed, limits, out image, out error);
        }
        catch (Exception exception) when (exception is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or BadImageFormatException)
        {
            // The image library is not installed on this system: an image that needs no work is still sent.
            if (sniffed is "image/png" or "image/jpeg" or "image/webp" && source.Length <= limits.MaximumBytes)
            {
                image = new AgentPreparedImage(source, sniffed, 0, 0, 0, 0);
                return true;
            }

            error = sniffed is null
                ? "The file is not a PNG, JPEG, GIF, WebP or BMP image."
                : "The image has to be converted or resized, and the image library is not available on this system.";
            return false;
        }
    }

    /// <summary>Formats a byte count for a message.</summary>
    /// <param name="bytes">The count.</param>
    /// <returns>The count in B, KB or MB.</returns>
    public static string FormatBytes(long bytes)
        => bytes < 1024
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes} B")
            : bytes < 1024 * 1024
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB")
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.#} MB");

    private static bool TryPrepareCore(byte[] source, string? sniffed, AgentImageLimits limits, out AgentPreparedImage? image, out string? error)
    {
        image = null;
        using var data = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            error = "The file is not an image that can be decoded. PNG, JPEG, GIF, WebP and BMP images are supported.";
            return false;
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
        {
            error = "The image has no pixels.";
            return false;
        }

        if ((long)info.Width * info.Height > limits.MaximumSourcePixels)
        {
            error = $"The image is too large to decode ({info.Width}x{info.Height} pixels).";
            return false;
        }

        var origin = codec.EncodedOrigin;
        var turned = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var sourceWidth = turned ? info.Height : info.Width;
        var sourceHeight = turned ? info.Width : info.Height;
        // Sent as it is: a format every provider reads, one frame, upright, within the limits.
        if (sniffed is "image/png" or "image/jpeg" or "image/webp" && codec.FrameCount <= 1 && origin == SKEncodedOrigin.TopLeft
            && Math.Max(info.Width, info.Height) <= limits.MaximumDimension && source.Length <= limits.MaximumBytes)
        {
            image = new AgentPreparedImage(source, sniffed, info.Width, info.Height, info.Width, info.Height);
            error = null;
            return true;
        }

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
        {
            error = "The image could not be decoded.";
            return false;
        }

        // The decoded image itself when it is already upright.
        using var turnedBitmap = Orient(decoded, origin);
        var upright = turnedBitmap ?? decoded;
        var photo = sniffed == "image/jpeg";
        var scale = Math.Min(1.0, limits.MaximumDimension / (double)Math.Max(sourceWidth, sourceHeight));
        for (var attempt = 0; attempt < MaximumAttempts; attempt++, scale *= 0.75)
        {
            var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
            using var resized = width == upright.Width && height == upright.Height
                ? null
                : upright.Resize(new SKImageInfo(width, height, upright.ColorType, upright.AlphaType), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            var bitmap = resized ?? upright;
            // A screenshot keeps its sharp text as PNG; a photograph, or a PNG that is too heavy, becomes JPEG.
            foreach (var (format, quality, mediaType) in photo
                         ? [(SKEncodedImageFormat.Jpeg, 85, "image/jpeg"), (SKEncodedImageFormat.Jpeg, 70, "image/jpeg")]
                         : new[] { (SKEncodedImageFormat.Png, 100, "image/png"), (SKEncodedImageFormat.Jpeg, 85, "image/jpeg"), (SKEncodedImageFormat.Jpeg, 70, "image/jpeg") })
            {
                var encoded = Encode(bitmap, format, quality);
                if (encoded is null || encoded.Length > limits.MaximumBytes) continue;
                image = new AgentPreparedImage(encoded, mediaType, width, height, sourceWidth, sourceHeight);
                error = null;
                return true;
            }
        }

        error = $"The image could not be made smaller than {FormatBytes(limits.MaximumBytes)}.";
        return false;
    }

    private static byte[]? Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        if (format != SKEncodedImageFormat.Jpeg || bitmap.AlphaType == SKAlphaType.Opaque)
        {
            using var encoded = bitmap.Encode(format, quality);
            return encoded?.ToArray();
        }

        // JPEG has no transparency: the image is laid on white.
        using var flat = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(flat))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(bitmap, 0, 0);
        }

        using var data = flat.Encode(format, quality);
        return data?.ToArray();
    }

    // Applies the orientation a camera recorded, so that the image is sent the way a viewer shows it.
    private static SKBitmap? Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        float width = source.Width, height = source.Height;
        SKMatrix? matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, height, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, height, -1, 0, width, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, width, 0, 0, 1),
            _ => null,
        };
        if (matrix is not { } transform) return null;
        var turned = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(new SKImageInfo(turned ? source.Height : source.Width, turned ? source.Width : source.Height, source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(transform);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }
}
