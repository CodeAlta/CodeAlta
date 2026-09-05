using System.Text;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class PromptDraftStoreTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeleteAsync_UnlinksDraftEntryWithoutDeletingExternalTarget(bool dangling)
    {
        using var temp = new DraftDirectory();
        var store = temp.Store;
        var link = store.GetPath("session");
        var target = Path.Combine(temp.Root, "external.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!dangling)
        {
            await File.WriteAllTextAsync(target, "external target");
        }

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Inconclusive("Creating a task-owned symbolic link requires platform permission.");
        }

        var loaded = await store.LoadAsync("session");
        var deleted = await store.SaveAsync("session", null, loaded.Revision);
        Assert.IsFalse(deleted.IsConflict);
        Assert.IsNull(new FileInfo(link).LinkTarget, "Deletion must unlink even a dangling draft link.");
        Assert.AreEqual(!dangling, File.Exists(target), "Deleting a draft must not delete its external target.");
        if (!dangling)
        {
            Assert.AreEqual("external target", await File.ReadAllTextAsync(target));
        }
    }

    [TestMethod]
    public async Task ScopePathsAndPlainText_RetainLegacyGlobalStorage()
    {
        using var temp = new DraftDirectory();
        var store = temp.Store;
        Assert.AreEqual("__draft__:global", PromptDraftStore.GetDraftScopeKey(null, false));
        Assert.AreEqual("__draft__:global", PromptDraftStore.GetDraftScopeKey("project", true));
        Assert.AreEqual("__draft__:project:project", PromptDraftStore.GetDraftScopeKey(" project ", false));
        Assert.AreEqual("__draft__:global", PromptDraftStore.NormalizeDraftScopeKey(" "));
        foreach (var key in new[] { "session-1", "__draft__:global", "__draft__:project:project", "a/b:c" })
        {
            var sanitized = new string(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());
            Assert.AreEqual(Path.Combine(temp.Root, "saved_prompts", $"saved_prompt_{sanitized}.md"), store.GetPath(key));
            var missing = await store.LoadAsync(key);
            Assert.IsNull(missing.Text);
            Assert.AreEqual(TextFileRevision.Missing, missing.Revision);
            const string text = " \tHello 世界 🌍\r\n\n ";
            var saved = await store.SaveAsync(key, text, missing.Revision);
            Assert.IsFalse(saved.IsConflict);
            Assert.AreEqual(text, saved.Snapshot.Text);
            CollectionAssert.AreEqual(new UTF8Encoding(false).GetBytes(text), await File.ReadAllBytesAsync(store.GetPath(key)));
            Assert.AreEqual(saved.Snapshot, await store.LoadAsync(key));
            var deleted = await store.SaveAsync(key, " \t\n", saved.Snapshot.Revision);
            Assert.IsFalse(deleted.IsConflict);
            Assert.IsNull(deleted.Snapshot.Text);
            Assert.IsFalse(File.Exists(store.GetPath(key)));
        }
    }

    [TestMethod]
    public async Task LegacyReads_DistinguishEmptyAndMissingAndDecodeBom()
    {
        using var temp = new DraftDirectory();
        var path = temp.Store.GetPath("session");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var encoding in new Encoding[] { new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true), new UTF32Encoding(false, true), new UTF32Encoding(true, true) })
        {
            foreach (var text in new[] { "", " \t\r\n", "日本語 🌍\r\n" })
            {
                await File.WriteAllTextAsync(path, text, encoding);
                var snapshot = await temp.Store.LoadAsync("session");
                Assert.AreEqual(text, snapshot.Text);
                Assert.IsTrue(snapshot.Revision.Exists);
                var saved = await temp.Store.SaveAsync("session", "updated", snapshot.Revision);
                Assert.IsFalse(saved.IsConflict);
                CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("updated"), await File.ReadAllBytesAsync(path));
            }
        }

        await File.WriteAllBytesAsync(path, []);
        var empty = await temp.Store.LoadAsync("session");
        Assert.AreEqual(string.Empty, empty.Text);
        Assert.AreNotEqual(TextFileRevision.Missing, empty.Revision);
        Assert.IsFalse((await temp.Store.SaveAsync("session", null, empty.Revision)).IsConflict);
        Assert.IsNull((await temp.Store.LoadAsync("session")).Text);
    }

    [TestMethod]
    public async Task ConditionalSaveAndDelete_RejectStaleAndSameTimestampRevisions()
    {
        using var temp = new DraftDirectory();
        var store = temp.Store;
        var initial = await store.SaveAsync("session", "initial", TextFileRevision.Missing);
        Assert.IsNotNull(initial.Snapshot);
        var path = store.GetPath("session");
        var timestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, "external");
        File.SetLastWriteTimeUtc(path, timestamp);
        var staleSave = await store.SaveAsync("session", "stale", initial.Snapshot.Revision);
        Assert.IsTrue(staleSave.IsConflict);
        Assert.IsNull(staleSave.Snapshot);
        var staleDelete = await store.SaveAsync("session", null, initial.Snapshot.Revision);
        Assert.IsTrue(staleDelete.IsConflict);
        Assert.IsNull(staleDelete.Snapshot);
        Assert.AreEqual(staleSave.CurrentRevision, staleDelete.CurrentRevision);
        Assert.AreEqual("external", await File.ReadAllTextAsync(path));
        var current = await store.LoadAsync("session");
        var deleted = await store.SaveAsync("session", null, current.Revision);
        Assert.IsFalse(deleted.IsConflict);
        Assert.IsTrue((await store.SaveAsync("session", "resurrect", current.Revision)).IsConflict);
        var recreated = await store.SaveAsync("session", "new", deleted.Snapshot.Revision);
        Assert.IsFalse(recreated.IsConflict);
        Assert.IsTrue((await store.SaveAsync("session", null, deleted.Snapshot.Revision)).IsConflict);
        Assert.AreEqual("new", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task SameOwner_SaveAndDeleteWithSameRevisionCannotBothCommit()
    {
        using var temp = new DraftDirectory();
        var store = temp.Store;
        var initial = await store.SaveAsync("session", "initial", TextFileRevision.Missing);
        Assert.IsNotNull(initial.Snapshot);
        var results = await Task.WhenAll(
            store.SaveAsync("session", "changed", initial.Snapshot.Revision),
            store.SaveAsync("session", null, initial.Snapshot.Revision));
        Assert.AreEqual(1, results.Count(result => !result.IsConflict));
        Assert.AreEqual(results.Single(result => !result.IsConflict).Snapshot, await store.LoadAsync("session"));
    }

    [TestMethod]
    public async Task SharedCodec_OrdersDraftDeleteWithEditorSave()
    {
        using var temp = new DraftDirectory();
        var codec = new TextFileCodec();
        var store = new PromptDraftStore(new CatalogOptions { GlobalRoot = temp.Root }, codec);
        var initial = await store.SaveAsync("session", "initial", TextFileRevision.Missing);
        Assert.IsNotNull(initial.Snapshot);
        var path = store.GetPath("session");
        var editorSave = codec.SaveAsync(new TextFileSaveRequest(path, "edited", Encoding.UTF8, false, initial.Snapshot.Revision));
        var draftDelete = store.SaveAsync("session", null, initial.Snapshot.Revision);
        await Task.WhenAll(editorSave, draftDelete);

        Assert.AreNotEqual(editorSave.Result.IsConflict, draftDelete.Result.IsConflict);
        if (!editorSave.Result.IsConflict)
        {
            Assert.AreEqual("edited", await File.ReadAllTextAsync(path));
        }
        else
        {
            Assert.IsFalse(File.Exists(path));
        }
    }

    [TestMethod]
    public async Task InvalidUnicodeAndCancellation_DoNotChangeTheDraft()
    {
        using var temp = new DraftDirectory();
        var store = temp.Store;
        var original = await store.SaveAsync("session", "original", TextFileRevision.Missing);
        Assert.IsNotNull(original.Snapshot);
        await Assert.ThrowsAsync<EncoderFallbackException>(() => store.SaveAsync("session", "\uD800", original.Snapshot.Revision));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync("session", null, original.Snapshot.Revision, cancellation.Token));
        Assert.AreEqual(original.Snapshot, await store.LoadAsync("session"));
        await File.WriteAllBytesAsync(store.GetPath("session"), [0xFF]);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => store.LoadAsync("session"));
    }

    private sealed class DraftDirectory : IDisposable
    {
        public DraftDirectory()
        {
            Store = new PromptDraftStore(new CatalogOptions { GlobalRoot = Root });
        }

        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"CodeAlta.Drafts.{Guid.NewGuid():N}");

        public PromptDraftStore Store { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
