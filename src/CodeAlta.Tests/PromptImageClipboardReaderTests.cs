using System.Buffers.Binary;
using CodeAlta.Tui.Presentation.Prompting;
using SkiaSharp;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class PromptImageClipboardReaderTests
{
    [TestMethod]
    public void TryReadImage_ConvertsWindowsDibClipboardPayloadToPng()
    {
        var dib = CreateTwoByTwoDib32();
        var context = new TextEditorClipboardPasteContext(
            text: null,
            formats: [TerminalClipboardFormats.WindowsDeviceIndependentBitmap],
            data: [new TextEditorClipboardData(TerminalClipboardFormats.WindowsDeviceIndependentBitmap, dib)]);

        var result = PromptImageClipboardReader.TryReadImage(context, "Screenshot", out var image, out var failureReason);

        Assert.IsTrue(result);
        Assert.IsNotNull(image);
        Assert.IsNull(failureReason);
        Assert.AreEqual("Screenshot", image.Title);
        Assert.AreEqual("image/png", image.MediaType);
        Assert.AreEqual(".png", image.FileExtension);
        Assert.IsTrue(IsPng(image.Bytes));

        using var bitmap = SKBitmap.Decode(image.Bytes);
        Assert.IsNotNull(bitmap);
        var topLeft = bitmap.GetPixel(0, 0);
        Assert.AreEqual(byte.MaxValue, topLeft.Alpha);
        Assert.IsTrue(topLeft.Red > 200);
    }

    [TestMethod]
    public void TryReadImage_AcceptsProviderSupportedEncodedClipboardImages()
    {
        AssertAcceptedEncodedImage(TerminalClipboardFormats.Png, "image/png", ".png", [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        AssertAcceptedEncodedImage("image/jpeg", "image/jpeg", ".jpg", [0xFF, 0xD8, 0xFF, 0xE0]);
        AssertAcceptedEncodedImage("image/jpg", "image/jpeg", ".jpg", [0xFF, 0xD8, 0xFF, 0xE1]);
        AssertAcceptedEncodedImage("image/gif", "image/gif", ".gif", [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a']);
        AssertAcceptedEncodedImage("image/webp", "image/webp", ".webp", [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P']);
    }

    [TestMethod]
    public void TryReadImage_IgnoresUnsupportedOrInvalidEncodedClipboardImages()
    {
        byte[] tiffHeader = [(byte)'I', (byte)'I', 42, 0, 0, 0, 0, 0];
        var unsupportedContext = new TextEditorClipboardPasteContext(
            text: null,
            formats: [TerminalClipboardFormats.Tiff],
            data: [new TextEditorClipboardData(TerminalClipboardFormats.Tiff, tiffHeader)]);

        var unsupportedResult = PromptImageClipboardReader.TryReadImage(unsupportedContext, "Photo", out var unsupportedImage, out var unsupportedFailureReason);

        Assert.IsFalse(unsupportedResult);
        Assert.IsNull(unsupportedImage);
        Assert.AreEqual("The clipboard does not contain a supported image payload.", unsupportedFailureReason);

        byte[] invalidJpeg = [(byte)'n', (byte)'o', (byte)'t', (byte)'j', (byte)'p', (byte)'e', (byte)'g'];
        var invalidContext = new TextEditorClipboardPasteContext(
            text: null,
            formats: ["image/jpeg"],
            data: [new TextEditorClipboardData("image/jpeg", invalidJpeg)]);

        var invalidResult = PromptImageClipboardReader.TryReadImage(invalidContext, "Photo", out var invalidImage, out var invalidFailureReason);

        Assert.IsFalse(invalidResult);
        Assert.IsNull(invalidImage);
        Assert.AreEqual("The clipboard does not contain a supported image payload.", invalidFailureReason);
    }

    [TestMethod]
    public void TryConvertDibToPng_RejectsInvalidDibPayload()
    {
        var result = PromptImageClipboardReader.TryConvertDibToPng([1, 2, 3], out var png);

        Assert.IsFalse(result);
        Assert.AreEqual(0, png.Length);
    }

    private static void AssertAcceptedEncodedImage(string format, string expectedMediaType, string expectedExtension, byte[] bytes)
    {
        var context = new TextEditorClipboardPasteContext(
            text: null,
            formats: [format],
            data: [new TextEditorClipboardData(format, bytes)]);

        var result = PromptImageClipboardReader.TryReadImage(context, "Image", out var image, out var failureReason);

        Assert.IsTrue(result);
        Assert.IsNotNull(image);
        Assert.IsNull(failureReason);
        Assert.AreEqual(expectedMediaType, image.MediaType);
        Assert.AreEqual(expectedExtension, image.FileExtension);
        CollectionAssert.AreEqual(bytes, image.Bytes);
    }

    private static byte[] CreateTwoByTwoDib32()
    {
        const int width = 2;
        const int height = 2;
        const int headerSize = 40;
        const int bytesPerPixel = 4;
        var pixelBytes = width * height * bytesPerPixel;
        var dib = new byte[headerSize + pixelBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(0, 4), headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8, 4), height);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14, 2), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(20, 4), (uint)pixelBytes);

        var pixels = dib.AsSpan(headerSize);
        // Positive-height DIBs are stored bottom-up. Pixels are BGRA.
        pixels[0] = 255; // bottom-left: blue
        pixels[1] = 0;
        pixels[2] = 0;
        pixels[3] = 0;
        pixels[4] = 0; // bottom-right: green
        pixels[5] = 255;
        pixels[6] = 0;
        pixels[7] = 0;
        pixels[8] = 0; // top-left: red
        pixels[9] = 0;
        pixels[10] = 255;
        pixels[11] = 0;
        pixels[12] = 255; // top-right: white
        pixels[13] = 255;
        pixels[14] = 255;
        pixels[15] = 0;

        return dib;
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
}
