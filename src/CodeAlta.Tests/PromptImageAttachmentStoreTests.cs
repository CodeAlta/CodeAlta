using CodeAlta.Catalog;
using CodeAlta.Presentation.Prompting;

namespace CodeAlta.Tests;

[TestClass]
public sealed class PromptImageAttachmentStoreTests
{
    [TestMethod]
    public async Task SaveAsync_WritesImagesBesideDateShardedSessionJournal()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"CodeAlta.Tests.{Guid.NewGuid():N}");
        try
        {
            var store = new PromptImageAttachmentStore(new CatalogOptions { GlobalRoot = rootPath });
            var createdAt = new DateTimeOffset(2026, 4, 27, 12, 30, 0, TimeSpan.Zero);
            var thread = new WorkThreadDescriptor
            {
                ThreadId = "codex:ignored-thread-id",
                BackendSessionId = "session/one",
                CreatedAt = createdAt,
            };
            var image = PromptImageAttachment.Create("Screenshot", [1, 2, 3, 4], "image/png", ".png");

            var references = await store.SaveAsync(thread, [image]);

            Assert.AreEqual(1, references.Count);
            var reference = references[0];
            var expectedDirectory = Path.Combine(rootPath, "sessions", "2026", "04", "27", "session_one.attachments");
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
