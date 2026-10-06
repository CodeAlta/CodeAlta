using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;
using CodeAlta.Agent.Runtime.Images;
using CodeAlta.Agent.Runtime.Tools;
using SkiaSharp;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentToolImagesTests
{
    [TestMethod]
    public void Preparation_SendsAnImageThatFitsAsItIs()
    {
        var png = CreateImage(320, 200, SKEncodedImageFormat.Png);

        Assert.IsTrue(AgentImagePreparation.TryPrepare(png, AgentImageLimits.Default, out var image, out var error), error);

        Assert.AreSame(png, image.Bytes);
        Assert.AreEqual("image/png", image.MediaType);
        Assert.AreEqual((320, 200), (image.Width, image.Height));
        Assert.IsFalse(image.Resized);
    }

    [TestMethod]
    public void Preparation_ScalesDownAnImageLargerThanTheLimit()
    {
        var png = CreateImage(3000, 1500, SKEncodedImageFormat.Png);

        Assert.IsTrue(AgentImagePreparation.TryPrepare(png, AgentImageLimits.Default, out var image, out var error), error);

        Assert.AreEqual("image/png", image.MediaType);
        Assert.AreEqual((2048, 1024), (image.Width, image.Height));
        Assert.AreEqual((3000, 1500), (image.SourceWidth, image.SourceHeight));
        Assert.IsTrue(image.Resized);
        Assert.AreEqual((2048, 1024), Measure(image.Bytes));
    }

    [TestMethod]
    public void Preparation_ConvertsAFormatAModelDoesNotRead()
    {
        // A bitmap: 2x2 pixels, 24 bits, written by hand since no encoder produces one.
        byte[] bmp =
        [
            0x42, 0x4d, 70, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0,
            40, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 1, 0, 24, 0, 0, 0, 0, 0, 16, 0, 0, 0, 0x13, 0x0b, 0, 0, 0x13, 0x0b, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 255, 255, 255, 255, 0, 0,
            255, 0, 0, 0, 255, 0, 0, 0,
        ];
        Assert.AreEqual("image/bmp", AgentImagePreparation.FindMediaType(bmp));

        Assert.IsTrue(AgentImagePreparation.TryPrepare(bmp, AgentImageLimits.Default, out var image, out var error), error);

        Assert.AreEqual("image/png", image.MediaType);
        Assert.AreEqual("image/png", AgentImagePreparation.FindMediaType(image.Bytes));
        Assert.AreEqual((2, 2), (image.Width, image.Height));
    }

    [TestMethod]
    public void Preparation_TurnsAHeavyImageIntoJpegThenShrinksIt()
    {
        // Noise does not compress: the PNG is far above the limit and the JPEG still is at full size.
        var png = CreateImage(600, 600, SKEncodedImageFormat.Png, noise: true);
        var limits = new AgentImageLimits(MaximumBytes: 60_000);
        Assert.IsTrue(png.Length > limits.MaximumBytes);

        Assert.IsTrue(AgentImagePreparation.TryPrepare(png, limits, out var image, out var error), error);

        Assert.AreEqual("image/jpeg", image.MediaType);
        Assert.IsTrue(image.Bytes.Length <= limits.MaximumBytes);
        Assert.IsTrue(image.Width < 600, "The image is smaller than its source.");
        Assert.AreEqual(image.Width, image.Height);
    }

    [TestMethod]
    public void Preparation_RefusesWhatCannotBeSent()
    {
        Assert.IsFalse(AgentImagePreparation.TryPrepare([], AgentImageLimits.Default, out _, out var empty));
        StringAssert.Contains(empty, "empty");

        Assert.IsFalse(AgentImagePreparation.TryPrepare("This is a text file, not an image."u8.ToArray(), AgentImageLimits.Default, out _, out var text));
        StringAssert.Contains(text, "not an image");

        // A PNG header followed by nothing an image has.
        Assert.IsFalse(AgentImagePreparation.TryPrepare([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3, 4], AgentImageLimits.Default, out _, out var corrupt));
        Assert.IsNotNull(corrupt);

        var png = CreateImage(400, 300, SKEncodedImageFormat.Png);
        Assert.IsFalse(AgentImagePreparation.TryPrepare(png, new AgentImageLimits(MaximumSourcePixels: 10_000), out _, out var pixels));
        StringAssert.Contains(pixels, "400x300");
        Assert.IsFalse(AgentImagePreparation.TryPrepare(png, new AgentImageLimits(MaximumSourceBytes: 100), out _, out var bytes));
        StringAssert.Contains(bytes, "too large");
    }

    [TestMethod]
    public async Task ViewImageTool_ReturnsTheImageWithItsSize()
    {
        using var temp = TestTempDirectory.Create();
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "shot.png"), CreateImage(640, 480, SKEncodedImageFormat.Png));

        var result = await InvokeAsync(temp.Path, "view_image", """{"path":"shot.png"}""");

        Assert.IsTrue(result.Success, result.Error);
        var text = Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items[0]).Value;
        StringAssert.Contains(text, "640x480");
        StringAssert.Contains(text, Path.Combine(temp.Path, "shot.png"));
        var image = Assert.IsInstanceOfType<AgentToolResultItem.Image>(result.Items[1]);
        Assert.AreEqual("image/png", image.MediaType);
        Assert.AreEqual("shot.png", image.DisplayName);
        Assert.AreEqual((640, 480), Measure(Convert.FromBase64String(image.Base64Data)));
    }

    [TestMethod]
    public async Task ViewImageTool_ScalesDownALargeScreenshot_AndSaysSo()
    {
        using var temp = TestTempDirectory.Create();
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "wide.jpg"), CreateImage(4096, 1024, SKEncodedImageFormat.Jpeg));

        var result = await InvokeAsync(temp.Path, "view_image", """{"path":"wide.jpg"}""");

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items[0]).Value, "scaled down to 2048x512");
        var image = Assert.IsInstanceOfType<AgentToolResultItem.Image>(result.Items[1]);
        Assert.AreEqual("image/jpeg", image.MediaType);
        Assert.AreEqual((2048, 512), Measure(Convert.FromBase64String(image.Base64Data)));
    }

    [TestMethod]
    public async Task ViewImageTool_FailsForAFileThatIsNotAnImage()
    {
        using var temp = TestTempDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "notes.txt"), "plain text");

        var text = await InvokeAsync(temp.Path, "view_image", """{"path":"notes.txt"}""");
        Assert.IsFalse(text.Success);
        StringAssert.Contains(text.Error, "cannot be viewed");

        var missing = await InvokeAsync(temp.Path, "view_image", """{"path":"nothing.png"}""");
        Assert.IsFalse(missing.Success);
        StringAssert.Contains(missing.Error, "was not found");

        var directory = await InvokeAsync(temp.Path, "view_image", """{"path":"."}""");
        Assert.IsFalse(directory.Success);
        StringAssert.Contains(directory.Error, "is a directory");
    }

    [TestMethod]
    public async Task ReadFileTool_PointsToViewImageForAnImage()
    {
        using var temp = TestTempDirectory.Create();
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "shot.png"), CreateImage(32, 32, SKEncodedImageFormat.Png));

        var result = await InvokeAsync(temp.Path, "read_file", """{"path":"shot.png"}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "is an image");
        StringAssert.Contains(result.Error, "view_image");
    }

    [TestMethod]
    public void MoveToUserMessages_AttachesTheImagesOfConsecutiveToolResultsToOneUserMessage()
    {
        var first = Convert.ToBase64String(CreateImage(8, 8, SKEncodedImageFormat.Png));
        var second = Convert.ToBase64String(CreateImage(9, 9, SKEncodedImageFormat.Png));
        AgentConversationMessage[] conversation =
        [
            new(AgentConversationRole.User, [new AgentMessagePart.Text("Look at both.")]),
            new(AgentConversationRole.Assistant,
            [
                new AgentMessagePart.ToolCall("call-1", "view_image", JsonDocument.Parse("{}").RootElement.Clone()),
                new AgentMessagePart.ToolCall("call-2", "mcp__browser__screenshot", JsonDocument.Parse("{}").RootElement.Clone()),
            ]),
            new(AgentConversationRole.Tool, [new AgentMessagePart.ToolResult("call-1", new AgentToolResult(true,
                [new AgentToolResultItem.Text("Viewed image a.png."), new AgentToolResultItem.Image(first, "image/png", "a.png")]))]),
            new(AgentConversationRole.Tool, [new AgentMessagePart.ToolResult("call-2", new AgentToolResult(true,
                [new AgentToolResultItem.Image(second, "image/png", "page")]))]),
            new(AgentConversationRole.User, [new AgentMessagePart.Text("A steering message.")]),
        ];

        var moved = AgentToolResultImages.MoveToUserMessages(conversation);

        Assert.AreEqual(6, moved.Count);
        // The tool results keep their text and a line that names the image.
        var firstResult = moved[2].Parts.OfType<AgentMessagePart.ToolResult>().Single().Result;
        Assert.IsFalse(AgentToolResultImages.HasImages(firstResult));
        StringAssert.Contains(AgentToolResultImages.RenderText(firstResult), "Viewed image a.png.");
        StringAssert.Contains(AgentToolResultImages.RenderText(firstResult), "[Image: a.png (image/png)] The image is attached to the message that follows");
        Assert.IsFalse(AgentToolResultImages.HasImages(moved[3].Parts.OfType<AgentMessagePart.ToolResult>().Single().Result));
        // One user message after the last tool result, before what the user said next.
        Assert.IsTrue(AgentToolResultImages.IsAttachedImagesMessage(moved[4]));
        var images = moved[4].Parts.OfType<AgentMessagePart.Data>().ToArray();
        CollectionAssert.AreEqual(new[] { first, second }, images.Select(static image => image.Base64Data).ToArray());
        var labels = moved[4].Parts.OfType<AgentMessagePart.Text>().Select(static text => text.Value).ToArray();
        StringAssert.Contains(labels[1], "from view_image");
        StringAssert.Contains(labels[2], "from mcp__browser__screenshot");
        Assert.AreSame(conversation[4], moved[5]);

        // A conversation without an image is not rewritten.
        AgentConversationMessage[] plain = [conversation[0], conversation[4]];
        Assert.AreSame(plain, AgentToolResultImages.MoveToUserMessages(plain));
    }

    [TestMethod]
    public void TokenEstimateAndPruning_TreatAToolImageLikeAUserImage()
    {
        var base64 = Convert.ToBase64String(new byte[600 * 1024]);
        var inline = new AgentConversationMessage(AgentConversationRole.Tool, [new AgentMessagePart.ToolResult("call-1", new AgentToolResult(true,
            [new AgentToolResultItem.Text("Viewed."), new AgentToolResultItem.Image(base64, "image/png", "shot.png")]))]);
        var saved = new AgentConversationMessage(AgentConversationRole.Tool, [new AgentMessagePart.ToolResult("call-2", new AgentToolResult(true,
            [new AgentToolResultItem.LocalImage(@"C:\sessions\s.attachments\shot.png", "image/png", "shot.png", 1280, 720)]))]);

        // An image costs a fixed number of tokens, not its base64 length.
        var estimate = AgentTokenEstimator.EstimateMessage(inline) + AgentTokenEstimator.EstimateMessage(saved);
        Assert.IsTrue(estimate is > 2_000 and < 3_000, estimate.ToString());

        Assert.IsTrue(AgentMediaCompaction.ContainsPrunableInlineImages([inline, saved]));
        var pruned = AgentMediaCompaction.PruneInlineImages([inline, saved], message => ReferenceEquals(message, saved));
        Assert.AreEqual(1, pruned.PrunedImageCount);
        Assert.AreSame(saved, pruned.Messages[1]);
        var result = pruned.Messages[0].Parts.OfType<AgentMessagePart.ToolResult>().Single().Result;
        Assert.IsFalse(AgentToolResultImages.HasImages(result));
        Assert.AreEqual("Viewed.", Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items[0]).Value);
        Assert.AreEqual("[Image omitted from retained context: shot.png (image/png).]", Assert.IsInstanceOfType<AgentToolResultItem.Text>(result.Items[1]).Value);
    }

    private static async Task<AgentToolResult> InvokeAsync(string workingDirectory, string toolName, string arguments)
    {
        var tool = AgentBuiltInToolFactory.CreateDefaultTools(new AgentBuiltInToolOptions
        {
            ProviderId = ModelProviderIds.OpenAIResponses,
            SessionId = "session-1",
            WorkingDirectory = workingDirectory,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        }).Single(tool => tool.Spec.Name == toolName);
        using var document = JsonDocument.Parse(arguments);
        return await tool.Handler(
            new AgentToolInvocation(ModelProviderIds.OpenAIResponses, "session-1", "call-1", toolName, document.RootElement.Clone()),
            CancellationToken.None);
    }

    internal static byte[] CreateImage(int width, int height, SKEncodedImageFormat format, bool noise = false)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        if (noise)
        {
            var pixels = new byte[width * height * 4];
            new Random(7).NextBytes(pixels);
            for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        }
        else
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor(0x1e, 0x66, 0xf5));
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(width / 4f, height / 4f, width / 2f, height / 2f, paint);
        }

        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    internal static (int Width, int Height) Measure(byte[] image)
    {
        using var data = SKData.CreateCopy(image);
        using var codec = SKCodec.Create(data);
        return (codec.Info.Width, codec.Info.Height);
    }
}
