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
    internal static IReadOnlyList<OwnedPromptImage> Freeze(IReadOnlyList<OwnedPromptImage>? images)
    {
        if (images is null || images.Count == 0) return [];
        var copy = images.ToArray();
        foreach (var image in copy)
        {
            Decode(image);
        }
        return Array.AsReadOnly(copy);
    }

    internal static byte[] Decode(OwnedPromptImage image)
    {
        static ArgumentException Invalid() => new("Invalid PNG attachment: RGB/RGBA 8-bit non-interlaced PNG required.");
        if (image is null || string.IsNullOrWhiteSpace(image.Title) || image.Title.Length > 80 || image.Title.Any(char.IsControl)
            || image.MediaType != "image/png" || image.Base64 is null) throw Invalid();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(image.Base64); }
        catch (FormatException) { throw Invalid(); }
        if (bytes.Length < 57 || Convert.ToBase64String(bytes) != image.Base64
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
                if (width < 1 || height < 1
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
            // Validate scanlines with fixed scratch space, not a width-sized allocation.
            var buffer = new byte[64 * 1024];
            for (var y = 0; y < height; y++)
            {
                var filter = zlib.ReadByte();
                if (filter is < 0 or > 4) throw Invalid();
                for (long remaining = (long)width * channels; remaining > 0;)
                {
                    var count = (int)Math.Min(remaining, buffer.Length);
                    zlib.ReadExactly(buffer.AsSpan(0, count));
                    remaining -= count;
                }
            }
            if (zlib.ReadByte() != -1) throw Invalid();
        }
        catch (IOException) { throw Invalid(); }
        return bytes;
    }
}
