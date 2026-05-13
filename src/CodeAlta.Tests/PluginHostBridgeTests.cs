using CodeAlta.App;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Presentation.Prompting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class PluginHostBridgeTests
{
    [TestMethod]
    public void ApplyPromptProcessingResult_PreservesImagesWhenPluginLeavesPromptUnchanged()
    {
        var image = PromptImageAttachment.Create("Screenshot", [1, 2, 3], "image/png", ".png");
        var prompt = PromptSubmission.Create("Describe this", [image]);
        var result = PluginPromptResult.Replace(
            "Describe this",
            [new PluginPromptAttachment
            {
                Kind = PluginPromptAttachmentKind.Image,
                Path = image.Id,
                DisplayName = image.Title,
                MediaType = image.MediaType,
            }]);

        var processed = PluginHostBridge.ApplyPromptProcessingResult(prompt, result);

        Assert.AreSame(prompt, processed);
        Assert.AreEqual(1, processed.Images.Count);
        Assert.AreEqual(image.Id, processed.Images[0].Id);
    }

    [TestMethod]
    public void ApplyPromptProcessingResult_PreservesImagesWhenPluginReplacesTextOnly()
    {
        var image = PromptImageAttachment.Create("Screenshot", [1, 2, 3], "image/png", ".png");
        var prompt = PromptSubmission.Create("Describe this", [image]);
        var result = PluginPromptResult.Replace("Please describe this image");

        var processed = PluginHostBridge.ApplyPromptProcessingResult(prompt, result);

        Assert.AreEqual("Please describe this image", processed.Text);
        Assert.AreEqual(1, processed.Images.Count);
        Assert.AreEqual(image.Id, processed.Images[0].Id);
        CollectionAssert.AreEqual(image.Bytes, processed.Images[0].Bytes);
    }
}
