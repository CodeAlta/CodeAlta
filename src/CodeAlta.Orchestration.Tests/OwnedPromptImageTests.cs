using System.Buffers.Binary;
using System.IO.Compression;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class OwnedPromptImageTests
{
    [TestMethod]
    public void ValidatesExactRasterAndRejectsMalformedImages()
    {
        var png = Png(1, 1);
        CollectionAssert.AreEqual(png, OwnedPromptImages.Decode(new("Image 1", "image/png", Convert.ToBase64String(png))));
        foreach (var invalid in new[] { png[..^1], Png(1, 1, 5), new byte[65_537] })
            Assert.ThrowsExactly<ArgumentException>(() => OwnedPromptImages.Decode(new("Image 1", "image/png", Convert.ToBase64String(invalid))));
        png[^5] ^= 1;
        Assert.ThrowsExactly<ArgumentException>(() => OwnedPromptImages.Decode(new("Image 1", "image/png", Convert.ToBase64String(png))));
        Assert.ThrowsExactly<ArgumentException>(() => OwnedPromptImages.Decode(new("Image 1", "image/svg+xml", "AA==")));
        var image = new OwnedPromptImage("Image 1", "image/png", Convert.ToBase64String(Png(1, 1)));
        Assert.HasCount(4, OwnedPromptImages.Freeze([image, image, image, image]));
    }

    [TestMethod]
    public void AcceptsLargeScreenshotsWithoutFormerByteCountOrDimensionLimits()
    {
        var png = Png(1280, 720, noise: true);
        Assert.IsTrue(png.Length > 65_536);
        var image = new OwnedPromptImage("Screenshot", "image/png", Convert.ToBase64String(png));
        CollectionAssert.AreEqual(png, OwnedPromptImages.Decode(image));
        Assert.HasCount(4, OwnedPromptImages.Freeze([image, image, image, image]));
        var desktop = Png(3840, 2160);
        CollectionAssert.AreEqual(desktop, OwnedPromptImages.Decode(new("Desktop", "image/png", Convert.ToBase64String(desktop))));
        var wide = Png(20_000, 1);
        CollectionAssert.AreEqual(wide, OwnedPromptImages.Decode(new("Wide", "image/png", Convert.ToBase64String(wide))));
    }

    internal static byte[] Png(int width, int height, byte filter = 0, bool noise = false)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6;
        Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            var row = new byte[width * 4 + 1]; row[0] = filter;
            var random = new Random(1234);
            for (var y = 0; y < height; y++)
            {
                if (noise) random.NextBytes(row); row[0] = filter;
                zlib.Write(row);
            }
        }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        return output.ToArray();

        void Chunk(string name, byte[] bytes)
        {
            Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); output.Write(length);
            var type = System.Text.Encoding.ASCII.GetBytes(name); output.Write(type); output.Write(bytes);
            var crc = uint.MaxValue;
            foreach (var b in type.Concat(bytes))
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            BinaryPrimitives.WriteUInt32BigEndian(length, ~crc); output.Write(length);
        }
    }
}
