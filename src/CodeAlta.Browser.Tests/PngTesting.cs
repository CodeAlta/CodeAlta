using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace CodeAlta.Browser.Tests;

/// <summary>Reads the pixels of the PNG pictures a browser takes: 8 bits a channel, RGB or RGBA, not interlaced.</summary>
internal static class PngTesting
{
    /// <summary>How many pixels of a picture are within a tolerance of a color, on each channel.</summary>
    public static int Count(byte[] png, (byte R, byte G, byte B) color, int tolerance)
    {
        ArgumentNullException.ThrowIfNull(png);
        var (width, height, channels, rows) = Decode(png);
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = y * width * channels + x * channels;
                if (Math.Abs(rows[at] - color.R) <= tolerance && Math.Abs(rows[at + 1] - color.G) <= tolerance && Math.Abs(rows[at + 2] - color.B) <= tolerance) count++;
            }
        }

        return count;
    }

    private static (int Width, int Height, int Channels, byte[] Pixels) Decode(byte[] png)
    {
        int width = 0, height = 0, channels = 0;
        using var data = new MemoryStream();
        for (var offset = 8; offset + 8 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            var body = png.AsSpan(offset + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(body);
                height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                if (body[8] != 8 || body[12] != 0 || body[9] is not (2 or 6)) throw new NotSupportedException("Only 8-bit RGB or RGBA pictures, not interlaced, are read.");
                channels = body[9] == 6 ? 4 : 3;
            }
            else if (type == "IDAT")
            {
                data.Write(body);
            }
            else if (type == "IEND")
            {
                break;
            }

            offset += 12 + length;
        }

        data.Position = 0;
        var stride = width * channels;
        var raw = new byte[height * (stride + 1)];
        using (var inflate = new ZLibStream(data, CompressionMode.Decompress)) inflate.ReadExactly(raw);
        var pixels = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            for (var x = 0; x < stride; x++)
            {
                int value = raw[y * (stride + 1) + 1 + x];
                int left = x >= channels ? pixels[y * stride + x - channels] : 0;
                int up = y > 0 ? pixels[(y - 1) * stride + x] : 0;
                int corner = x >= channels && y > 0 ? pixels[(y - 1) * stride + x - channels] : 0;
                value += filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, corner),
                    _ => 0,
                };
                pixels[y * stride + x] = (byte)value;
            }
        }

        return (width, height, channels, pixels);
    }

    private static int Paeth(int left, int up, int corner)
    {
        var estimate = left + up - corner;
        var toLeft = Math.Abs(estimate - left);
        var toUp = Math.Abs(estimate - up);
        var toCorner = Math.Abs(estimate - corner);
        return toLeft <= toUp && toLeft <= toCorner ? left : toUp <= toCorner ? up : corner;
    }
}
