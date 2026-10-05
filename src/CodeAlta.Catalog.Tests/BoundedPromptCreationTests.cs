using System.Text;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class BoundedPromptCreationTests
{
    [TestMethod]
    public async Task CreatesSharedFormatAndNeverReadsOrReplacesCollisionBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "alta-create-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PromptResourceStore(Path.Combine(root, "built"), Path.Combine(root, "global"), null, new TextFileCodec());
            var id = new PromptResourceIdentity(PromptResourceScope.Global, PromptResourceKind.Agent, "example");
            var content = new PromptFileContent("Example", "Description", null, "body", true);
            Assert.IsTrue(await store.TryCreateAsync(id, content, default));
            Assert.AreEqual(content, store.Load(id).Content);
            var path = store.GetPath(id);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.IsFalse(await store.TryCreateAsync(id, content with { Body = "replacement" }, default));
            Assert.AreEqual(content, store.Load(id).Content);
            if (OperatingSystem.IsWindows())
                Assert.IsFalse(await store.TryCreateAsync(id with { Id = "EXAMPLE" }, content, default));
            Assert.IsFalse(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task IndependentCreatorsHaveExactlyOneWinner()
    {
        var root = Path.Combine(Path.GetTempPath(), "alta-create-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = new PromptResourceIdentity(PromptResourceScope.Global, PromptResourceKind.Agent, "race");
            var tasks = Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
            {
                var store = new PromptResourceStore(Path.Combine(root, "built"), Path.Combine(root, "global"), null, new TextFileCodec());
                return await store.TryCreateAsync(id, new("Race", null, null, $"body-{i}", false), default);
            })).ToArray();
            Assert.AreEqual(1, (await Task.WhenAll(tasks)).Count(result => result));
            Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(root, "global", "agents")).Count());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task InvalidEncodingAndUnsafeIdentitiesHaveNoWriteSideEffects()
    {
        var root = Path.Combine(Path.GetTempPath(), "alta-create-invalid-" + Guid.NewGuid().ToString("N"));
        var store = new PromptResourceStore(Path.Combine(root, "built"), Path.Combine(root, "global"), null, new TextFileCodec());
        var id = new PromptResourceIdentity(PromptResourceScope.Global, PromptResourceKind.Agent, "valid");
        var content = new PromptFileContent("Name", null, null, "body", false);
        await Assert.ThrowsAsync<EncoderFallbackException>(() => store.TryCreateAsync(id, content with { Body = "bad\ud800" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryCreateAsync(id with { Id = "../escape" }, content, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.TryCreateAsync(id with { Scope = PromptResourceScope.BuiltIn }, content, default));
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public async Task ObservedLinkedAncestorIsRejectedWithoutPublishingOutside()
    {
        var root = Path.Combine(Path.GetTempPath(), "alta-create-link-" + Guid.NewGuid().ToString("N"));
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        var link = Path.Combine(root, "global");
        try
        {
            // ERROR_PRIVILEGE_NOT_HELD: Windows without Developer Mode or elevation cannot create symbolic links.
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070522))
            { Assert.Inconclusive("Creating a disposable symbolic link requires platform permission."); }
            var store = new PromptResourceStore(Path.Combine(root, "built"), link, null, new TextFileCodec());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.TryCreateAsync(new(PromptResourceScope.Global, PromptResourceKind.Agent, "example"), new("Name", null, null, "Body", false), default));
            Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(outside).Count());
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); Directory.Delete(root, true); }
    }
}
