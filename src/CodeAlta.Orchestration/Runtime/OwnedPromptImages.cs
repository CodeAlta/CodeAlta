using System.Buffers.Binary;
using System.IO.Compression;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>An encoded PNG copy; never a renderer filesystem path or remote URL.</summary>
/// <param name="Title">Bounded plain display title.</param>
/// <param name="MediaType">Exactly image/png.</param>
/// <param name="Base64">Canonical base64 of the exact original PNG bytes.</param>
public sealed record OwnedPromptImage(string Title, string MediaType, string Base64);

internal static class OwnedPromptImages
{
    internal const int MaxBytes = 65_536;
    internal const int MaxTotalBytes = 98_304;
    internal const int MaxCount = 3;

    internal static IReadOnlyList<OwnedPromptImage> Freeze(IReadOnlyList<OwnedPromptImage>? images)
    {
        if (images is null || images.Count == 0) return [];
        if (images.Count > MaxCount) throw new ArgumentException("At most three PNG images are supported.");
        var copy = images.ToArray();
        var total = 0;
        foreach (var image in copy)
        {
            total = checked(total + Decode(image).Length);
            if (total > MaxTotalBytes) throw new ArgumentException("PNG attachments exceed the 96 KiB total budget.");
        }
        return Array.AsReadOnly(copy);
    }

    internal static byte[] Decode(OwnedPromptImage image)
    {
        static ArgumentException Invalid() => new("Invalid PNG attachment: RGB/RGBA 8-bit non-interlaced PNG, at most 64 KiB, 2048 pixels per side and 4 megapixels required.");
        if (image is null || string.IsNullOrWhiteSpace(image.Title) || image.Title.Length > 80 || image.Title.Any(char.IsControl)
            || image.MediaType != "image/png" || image.Base64 is null || image.Base64.Length > ((MaxBytes + 2) / 3) * 4) throw Invalid();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(image.Base64); }
        catch (FormatException) { throw Invalid(); }
        if (bytes.Length is < 57 or > MaxBytes || Convert.ToBase64String(bytes) != image.Base64
            || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw Invalid();
        using var compressed = new MemoryStream();
        var offset = 8; var width = 0; var height = 0; var channels = 0; var ended = false; var dataEnded = false;
        while (offset <= bytes.Length - 12)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
            if (size > bytes.Length - offset - 12) throw Invalid();
            var length = (int)size;
            var type = bytes.AsSpan(offset + 4, 4);
            var data = bytes.AsSpan(offset + 8, length);
            var crc = uint.MaxValue;
            foreach (var b in bytes.AsSpan(offset + 4, length + 4))
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + length))) throw Invalid();
            if (type.SequenceEqual("IHDR"u8))
            {
                if (offset != 8 || length != 13) throw Invalid();
                width = BinaryPrimitives.ReadInt32BigEndian(data); height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                if (width is < 1 or > 2048 || height is < 1 or > 2048 || (long)width * height > 4_000_000
                    || data[8] != 8 || data[9] is not (2 or 6) || data[10] != 0 || data[11] != 0 || data[12] != 0) throw Invalid();
                channels = data[9] == 2 ? 3 : 4;
            }
            else if (width == 0) throw Invalid();
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (dataEnded) throw Invalid();
                compressed.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || compressed.Length == 0 || offset + 12 != bytes.Length) throw Invalid();
                ended = true; break;
            }
            else
            {
                // No compressed metadata/profile bombs, animation, or unknown chunks.
                if (!((type.SequenceEqual("sRGB"u8) && length == 1) || (type.SequenceEqual("gAMA"u8) && length == 4)
                    || (type.SequenceEqual("cHRM"u8) && length == 32) || (type.SequenceEqual("pHYs"u8) && length == 9))) throw Invalid();
                if (compressed.Length != 0) dataEnded = true;
            }
            offset += length + 12;
        }
        if (!ended) throw Invalid();
        compressed.Position = 0;
        try
        {
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            var row = new byte[width * channels + 1];
            for (var y = 0; y < height; y++)
            {
                zlib.ReadExactly(row);
                if (row[0] > 4) throw Invalid();
            }
            if (zlib.ReadByte() != -1) throw Invalid();
        }
        catch (IOException) { throw Invalid(); }
        return bytes;
    }
}
