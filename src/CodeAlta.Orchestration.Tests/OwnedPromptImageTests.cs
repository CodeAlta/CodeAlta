using System.Buffers.Binary;
using System.IO.Compression;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class OwnedPromptImageTests
{
    [TestMethod]
    public void ValidatesExactRasterAndRejectsMalformedOrOversizedImages()
    {
        var png = Png(1, 1);
        CollectionAssert.AreEqual(png, OwnedPromptImages.Decode(new("Image 1", "image/png", Convert.ToBase64String(png))));
        foreach (var invalid in new[] { png[..^1], Png(4097, 1), Png(1, 1, 5), new byte[65_537] })
            Assert.ThrowsExactly<ArgumentException>(() => OwnedPromptImages.Decode(new("Image 1", "image/png", Convert.ToBase64String(invalid))));
        png[^5] ^= 1;
        Assert.ThrowsExactly<ArgumentException>(() => OwnedPromptImages.Decode(new("Image 1", "image/png", Convert.ToBase64String(png))));
        Assert.ThrowsExactly<ArgumentException>(() => OwnedPromptImages.Decode(new("Image 1", "image/svg+xml", "AA==")));
        var image = new OwnedPromptImage("Image 1", "image/png", Convert.ToBase64String(Png(1, 1)));
        Assert.ThrowsExactly<ArgumentException>(() => OwnedPromptImages.Freeze([image, image, image, image]));
        var noisy = new OwnedPromptImage("Image 1", "image/png", Convert.ToBase64String(Png(100, 100, noise: true)));
        Assert.IsTrue(OwnedPromptImages.Decode(noisy).Length > 32_768);
        Assert.ThrowsExactly<ArgumentException>(() => OwnedPromptImages.Freeze([noisy, noisy, noisy]));
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
