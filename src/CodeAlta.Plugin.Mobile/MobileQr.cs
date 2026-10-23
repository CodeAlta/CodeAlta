using System.Buffers.Binary;
using System.IO.Compression;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// Draws the QR code as a grayscale PNG with the Tailscale mark (its 3×3 dot grid: the middle row and the bottom middle
/// dot dark, the others light; drawn here, not a fetched asset; Tailscale is a trademark of Tailscale Inc.) on a white
/// rounded square in the middle, about 21% of the width. The code is made at error correction level H, which reads
/// through it.
/// </summary>
internal static class MobileQr
{
    internal static byte[] Png(bool[][] modules, int scale)
    {
        var n = modules.Length; var size = n * scale;
        var pixels = new byte[size, size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                pixels[y, x] = modules[y / scale][x / scale] ? (byte)0 : (byte)255;

        double side = Math.Round(size * 0.21), x0 = (size - side) / 2, radius = side * 0.18;
        double step = side * 0.7 / 3, dot = step * 0.38, start = x0 + side * 0.15 + step / 2;
        for (var y = (int)x0; y < (int)(x0 + side); y++)
            for (var x = (int)x0; x < (int)(x0 + side); x++)
            {
                double px = x + 0.5, py = y + 0.5;
                // The rounded square: outside the corners' circles is left as it is.
                double cx = Math.Clamp(px, x0 + radius, x0 + side - radius), cy = Math.Clamp(py, x0 + radius, x0 + side - radius);
                if ((px - cx) * (px - cx) + (py - cy) * (py - cy) > radius * radius) continue;
                byte value = 255;
                for (var row = 0; row < 3; row++)
                    for (var col = 0; col < 3; col++)
                    {
                        double dx = px - (start + col * step), dy = py - (start + row * step);
                        if (dx * dx + dy * dy <= dot * dot) value = row == 1 || (row == 2 && col == 1) ? (byte)0x24 : (byte)0xC8;
                    }
                pixels[y, x] = value;
            }

        var raw = new byte[size * (size + 1)];
        for (var y = 0; y < size; y++)
        {
            raw[y * (size + 1)] = 0;
            for (var x = 0; x < size; x++) raw[y * (size + 1) + 1 + x] = pixels[y, x];
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, size); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8; header[9] = 0; // 8-bit grayscale
        Chunk(png, "IHDR", header); Chunk(png, "IDAT", compressed.ToArray()); Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length); png.Write(four);
        var body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, body); data.CopyTo(body, 4);
        png.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(four, Crc(body)); png.Write(four);
    }

    private static uint Crc(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
