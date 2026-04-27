using System.Buffers.Binary;
using XenoAtom.Terminal;

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
        new(TerminalClipboardFormats.Tiff, "image/tiff", ".tiff"),
        new(TerminalClipboardFormats.WindowsDeviceIndependentBitmapV5, "image/bmp", ".bmp", IsWindowsDib: true),
        new(TerminalClipboardFormats.WindowsDeviceIndependentBitmap, "image/bmp", ".bmp", IsWindowsDib: true),
    ];

    public static bool TryReadImage(
        TerminalClipboard clipboard,
        string title,
        out PromptImageAttachment? image,
        out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        foreach (var format in EnumerateCandidateFormats(clipboard))
        {
            if (!clipboard.TryGetData(format.Format, out var data) || data is null || data.Length == 0)
            {
                continue;
            }

            if (format.IsWindowsDib)
            {
                if (!TryConvertDibToBmp(data, out var bmpBytes))
                {
                    continue;
                }

                image = PromptImageAttachment.Create(title, bmpBytes, format.MediaType, format.Extension);
                failureReason = null;
                return true;
            }

            image = PromptImageAttachment.Create(title, data, format.MediaType, format.Extension);
            failureReason = null;
            return true;
        }

        image = null;
        failureReason = clipboard.CanGetFormats || clipboard.CanGetText
            ? "The clipboard does not contain a supported image payload."
            : "Clipboard image access is not supported by this terminal backend.";
        return false;
    }

    private static IEnumerable<ClipboardImageFormat> EnumerateCandidateFormats(TerminalClipboard clipboard)
    {
        HashSet<string>? advertisedFormats = null;
        if (clipboard.TryGetFormats(out var formats) && formats is { Count: > 0 })
        {
            advertisedFormats = new HashSet<string>(formats, StringComparer.OrdinalIgnoreCase);
            foreach (var preferred in PreferredFormats)
            {
                if (advertisedFormats.Contains(preferred.Format))
                {
                    yield return preferred;
                }
            }

            foreach (var format in formats)
            {
                if (!format.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                    PreferredFormats.Any(preferred => string.Equals(preferred.Format, format, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                yield return new ClipboardImageFormat(format, format, GuessExtension(format));
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

    private static string GuessExtension(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" or "image/x-ms-bmp" => ".bmp",
            "image/tiff" => ".tiff",
            _ => ".img",
        };

    private readonly record struct ClipboardImageFormat(
        string Format,
        string MediaType,
        string Extension,
        bool IsWindowsDib = false);
}
