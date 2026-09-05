using CodeAlta.Catalog;
using CodeAlta.Tui.Presentation.Prompting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class PromptImageAttachmentStoreTests
{
    [TestMethod]
    public async Task SaveAsync_CopiesSourceBytesAndPreservesFrontendTitleAndAgentInput()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"CodeAlta.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            var source = Path.Combine(rootPath, "source.png");
            await File.WriteAllBytesAsync(source, [1, 2, 3]);
            var bytes = await File.ReadAllBytesAsync(source);
            var image = PromptImageAttachmentFactory.Create("  Screen\tshot  ", bytes, "image/png", "png");
            bytes[0] = 99;
            Assert.AreEqual("Screen shot", image.Title);
            Assert.AreEqual(SR.T("Image"), image.WithTitle(" ").Title);
            var submission = PromptSubmission.Create("Describe", [image]);
            var store = new PromptImageAttachmentStore(new CatalogOptions { GlobalRoot = rootPath });
            var references = await store.SaveAsync(new SessionViewDescriptor { SessionId = "session" }, submission.Images);
            var input = submission.AppendImageItems(CodeAlta.Agent.AgentInput.Text(submission.Text), references);
            var item = input.Items.OfType<CodeAlta.Agent.AgentInputItem.LocalImage>().Single();
            Assert.AreEqual(references[0].Path, item.Path);
            Assert.AreEqual("Screen shot", item.DisplayName);
            Assert.AreEqual("image/png", item.MediaType);
            Assert.AreNotEqual(source, item.Path);
            await File.WriteAllBytesAsync(source, [4, 5, 6]);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(item.Path));
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(source));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [TestMethod]
    public void AttachmentPersistence_IsCatalogOwnedAndActualDispatchStillSavesBeforeAugmentation()
    {
        Assert.AreEqual(typeof(CatalogOptions).Assembly, typeof(PromptImageAttachmentStore).Assembly);
        Assert.AreEqual(typeof(CatalogOptions).Assembly, typeof(PromptImageAttachment).Assembly);
        Assert.AreEqual(typeof(CatalogOptions).Assembly, typeof(PromptImageAttachmentReference).Assembly);
        // Fixed repository-relative source paths, not a host startup or ancestor instruction/discovery walk.
        var sourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var dispatch = File.ReadAllText(Path.Combine(sourceRoot, "CodeAlta.Tui", "App", "SessionPromptDispatchCoordinator.cs"));
        var save = dispatch.IndexOf("await _promptImageAttachmentStore.SaveAsync(session, prompt.Images, cancellationToken)", StringComparison.Ordinal);
        var append = dispatch.IndexOf("prompt.AppendImageItems(promptInput.Input, imageReferences)", StringComparison.Ordinal);
        var augment = dispatch.IndexOf("await _pluginHostBridge.BuildAgentRunAugmentationAsync", StringComparison.Ordinal);
        var submit = dispatch.IndexOf("await _orchestrator.SubmitPromptAsync", StringComparison.Ordinal);
        Assert.IsTrue(save >= 0 && append > save && augment > append && submit > augment);
        Assert.IsFalse(File.Exists(Path.Combine(sourceRoot, "CodeAlta.Tui", "Presentation", "Prompting", "PromptImageAttachmentStore.cs")));
        var store = File.ReadAllText(Path.Combine(sourceRoot, "CodeAlta.Catalog", "PromptImageAttachmentStore.cs"));
        StringAssert.Contains(store, "FileMode.CreateNew");
        Assert.IsFalse(store.Contains("File.Exists", StringComparison.Ordinal), "Creation must not use an existence-check/write race.");
    }

    [TestMethod]
    public async Task SaveAsync_WritesImagesBesideDateShardedSessionJournal()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"CodeAlta.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new PromptImageAttachmentStore(new CatalogOptions { GlobalRoot = rootPath });
            var createdAt = new DateTimeOffset(2026, 4, 27, 12, 30, 0, TimeSpan.Zero);
            var session = new SessionViewDescriptor
            {
                SessionId = "session-one",
                CreatedAt = createdAt,
            };
            var image = PromptImageAttachmentFactory.Create("Screenshot", [1, 2, 3, 4], "image/png", ".png");

            var references = await store.SaveAsync(session, [image]);

            Assert.AreEqual(1, references.Count);
            var reference = references[0];
            var expectedDirectory = Path.Combine(rootPath, "sessions", "2026", "04", "27", "session-one.attachments");
            Assert.AreEqual(expectedDirectory, Path.GetDirectoryName(reference.Path));
            Assert.IsTrue(File.Exists(reference.Path));
            CollectionAssert.AreEqual(image.Bytes, File.ReadAllBytes(reference.Path));
            Assert.AreEqual("Screenshot", reference.Title);
            Assert.AreEqual("image/png", reference.MediaType);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }
}
