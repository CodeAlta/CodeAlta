using System.Buffers.Binary;
using CodeAlta.Catalog;
using SkiaSharp;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Presentation.Prompting;

internal static class PromptImageClipboardReader
{
    private static readonly ClipboardImageFormat[] PreferredFormats =
    [
        new(TerminalClipboardFormats.Png, "image/png", ".png"),
        new("image/jpeg", "image/jpeg", ".jpg"),
        new("image/jpg", "image/jpeg", ".jpg"),
        new("image/webp", "image/webp", ".webp"),
        new("image/gif", "image/gif", ".gif"),
        new(TerminalClipboardFormats.WindowsDeviceIndependentBitmapV5, "image/png", ".png", IsWindowsDib: true),
        new(TerminalClipboardFormats.WindowsDeviceIndependentBitmap, "image/png", ".png", IsWindowsDib: true),
    ];

    public static bool TryReadImage(
        TextEditorClipboardPasteContext context,
        string title,
        out PromptImageAttachment? image,
        out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        foreach (var format in EnumerateCandidateFormats(context))
        {
            if (!context.TryGetData(format.Format, out var data) || data.IsEmpty)
            {
                continue;
            }

            if (format.IsWindowsDib)
            {
                if (!TryConvertDibToPng(data.Span, out var pngBytes))
                {
                    continue;
                }

                image = PromptImageAttachment.Create(title, pngBytes, format.MediaType, format.Extension);
                failureReason = null;
                return true;
            }

            if (!IsSupportedEncodedImage(data.Span, format.MediaType))
            {
                continue;
            }

            image = PromptImageAttachment.Create(title, data.Span, format.MediaType, format.Extension);
            failureReason = null;
            return true;
        }

        image = null;
        failureReason = context.Formats.Count > 0
            ? SR.T("The clipboard does not contain a supported image payload.")
            : SR.T("Clipboard image access is not supported by this terminal backend.");
        return false;
    }

    private static IEnumerable<ClipboardImageFormat> EnumerateCandidateFormats(TextEditorClipboardPasteContext context)
    {
        HashSet<string>? advertisedFormats = null;
        if (context.Formats is { Count: > 0 } formats)
        {
            advertisedFormats = new HashSet<string>(formats, StringComparer.OrdinalIgnoreCase);
            foreach (var preferred in PreferredFormats)
            {
                if (advertisedFormats.Contains(preferred.Format))
                {
                    yield return preferred;
                }
            }

        }

        foreach (var preferred in PreferredFormats)
        {
            if (advertisedFormats is null || !advertisedFormats.Contains(preferred.Format))
            {
                yield return preferred;
            }
        }
    }

    internal static bool TryConvertDibToPng(ReadOnlySpan<byte> dib, out byte[] png)
    {
        png = [];
        try
        {
            if (!TryConvertDibToBmp(dib, out var bmp))
            {
                return false;
            }

            NormalizeFullyTransparentDibAlpha(bmp.AsSpan(14));

            using var bitmap = SKBitmap.Decode(bmp);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return false;
            }

            using var normalizedBitmap = NormalizeFullyTransparentBitmap(bitmap);
            using var image = SKImage.FromBitmap(normalizedBitmap ?? bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, quality: 100);
            if (data is null)
            {
                return false;
            }

            png = data.ToArray();
            return IsPng(png);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or OverflowException)
        {
            png = [];
            return false;
        }
    }

    internal static bool TryConvertDibToBmp(ReadOnlySpan<byte> dib, out byte[] bmp)
    {
        bmp = [];
        if (dib.Length < 4)
        {
            return false;
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib[..4]);
        if (headerSize < 12 || headerSize > dib.Length)
        {
            return false;
        }

        ushort bitsPerPixel;
        uint compression = 0;
        uint colorsUsed = 0;
        var paletteEntrySize = 4;
        if (headerSize == 12)
        {
            if (dib.Length < 12)
            {
                return false;
            }

            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(10, 2));
            paletteEntrySize = 3;
        }
        else
        {
            if (dib.Length < 40)
            {
                return false;
            }

            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(14, 2));
            compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(16, 4));
            colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(32, 4));
        }

        var maskBytes = headerSize == 40 && compression is 3 or 6
            ? compression == 6 ? 16 : 12
            : 0;
        var colorCount = colorsUsed > 0
            ? colorsUsed
            : bitsPerPixel <= 8
                ? 1u << bitsPerPixel
                : 0;
        var paletteBytes = checked((int)colorCount * paletteEntrySize);
        var offBits = checked(14 + (int)headerSize + maskBytes + paletteBytes);
        if (offBits > dib.Length + 14)
        {
            offBits = checked(14 + (int)headerSize);
        }

        var fileSize = checked(14 + dib.Length);
        bmp = new byte[fileSize];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2, 4), (uint)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10, 4), (uint)offBits);
        dib.CopyTo(bmp.AsSpan(14));
        return true;
    }

    private static void NormalizeFullyTransparentDibAlpha(Span<byte> dib)
    {
        if (dib.Length < 40)
        {
            return;
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib[..4]);
        if (headerSize < 40 || headerSize > dib.Length)
        {
            return;
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(dib.Slice(4, 4));
        var heightRaw = BinaryPrimitives.ReadInt32LittleEndian(dib.Slice(8, 4));
        var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(14, 2));
        if (width <= 0 || heightRaw == 0 || heightRaw == int.MinValue || bitsPerPixel != 32)
        {
            return;
        }

        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(16, 4));
        if (compression is not (0 or 3 or 6) || !HasDefaultByteAlignedDibColorMasks(dib, headerSize, compression))
        {
            return;
        }

        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(32, 4));
        var maskBytes = headerSize == 40 && compression is 3 or 6
            ? compression == 6 ? 16 : 12
            : 0;
        var paletteBytes = colorsUsed > 0 ? checked((int)colorsUsed * 4) : 0;
        var pixelOffset = checked((int)headerSize + maskBytes + paletteBytes);
        var height = Math.Abs(heightRaw);
        var rowStride = checked(width * 4);
        var requiredLength = checked(pixelOffset + (rowStride * height));
        if (pixelOffset < 0 || requiredLength > dib.Length)
        {
            return;
        }

        for (var y = 0; y < height; y++)
        {
            var rowOffset = pixelOffset + (y * rowStride);
            for (var x = 0; x < width; x++)
            {
                if (dib[rowOffset + (x * 4) + 3] != 0)
                {
                    return;
                }
            }
        }

        for (var y = 0; y < height; y++)
        {
            var rowOffset = pixelOffset + (y * rowStride);
            for (var x = 0; x < width; x++)
            {
                dib[rowOffset + (x * 4) + 3] = byte.MaxValue;
            }
        }
    }

    private static bool HasDefaultByteAlignedDibColorMasks(ReadOnlySpan<byte> dib, uint headerSize, uint compression)
    {
        if (compression == 0)
        {
            return true;
        }

        uint redMask;
        uint greenMask;
        uint blueMask;
        uint alphaMask = 0;
        if (headerSize >= 56)
        {
            redMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(40, 4));
            greenMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(44, 4));
            blueMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(48, 4));
            alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(52, 4));
        }
        else if (headerSize == 40 && dib.Length >= 52)
        {
            redMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(40, 4));
            greenMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(44, 4));
            blueMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(48, 4));
            if (compression == 6 && dib.Length >= 56)
            {
                alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(52, 4));
            }
        }
        else
        {
            return false;
        }

        return redMask == 0x00FF0000
            && greenMask == 0x0000FF00
            && blueMask == 0x000000FF
            && alphaMask is 0 or 0xFF000000;
    }

    private static SKBitmap? NormalizeFullyTransparentBitmap(SKBitmap bitmap)
    {
        if (!HasOnlyTransparentPixels(bitmap))
        {
            return null;
        }

        var normalized = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                normalized.SetPixel(x, y, new SKColor(color.Red, color.Green, color.Blue, byte.MaxValue));
            }
        }

        return normalized;
    }

    private static bool HasOnlyTransparentPixels(SKBitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsPng(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 8
        && bytes[0] == 0x89
        && bytes[1] == (byte)'P'
        && bytes[2] == (byte)'N'
        && bytes[3] == (byte)'G'
        && bytes[4] == 0x0D
        && bytes[5] == 0x0A
        && bytes[6] == 0x1A
        && bytes[7] == 0x0A;

    private static bool IsSupportedEncodedImage(ReadOnlySpan<byte> bytes, string mediaType)
        => mediaType.Trim().ToLowerInvariant() switch
        {
            "image/png" => IsPng(bytes),
            "image/jpeg" or "image/jpg" => IsJpeg(bytes),
            "image/gif" => IsGif(bytes),
            "image/webp" => IsWebp(bytes),
            _ => false,
        };

    private static bool IsJpeg(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 3
        && bytes[0] == 0xFF
        && bytes[1] == 0xD8
        && bytes[2] == 0xFF;

    private static bool IsGif(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 6
        && bytes[0] == (byte)'G'
        && bytes[1] == (byte)'I'
        && bytes[2] == (byte)'F'
        && bytes[3] == (byte)'8'
        && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9')
        && bytes[5] == (byte)'a';

    private static bool IsWebp(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 12
        && bytes[0] == (byte)'R'
        && bytes[1] == (byte)'I'
        && bytes[2] == (byte)'F'
        && bytes[3] == (byte)'F'
        && bytes[8] == (byte)'W'
        && bytes[9] == (byte)'E'
        && bytes[10] == (byte)'B'
        && bytes[11] == (byte)'P';

    private readonly record struct ClipboardImageFormat(
        string Format,
        string MediaType,
        string Extension,
        bool IsWindowsDib = false);
}
