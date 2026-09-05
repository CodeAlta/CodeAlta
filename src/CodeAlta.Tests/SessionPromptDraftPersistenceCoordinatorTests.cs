using CodeAlta.Tui.App;
using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionPromptDraftPersistenceCoordinatorTests
{
    [TestMethod]
    public async Task DeletePromptDraft_RemovesDanglingLinkEvenWithMissingBaseline()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        Directory.CreateDirectory(options.PromptDraftsRoot);
        var link = new PromptDraftStore(options).GetPath("session");
        var target = Path.Combine(temp.Path, "absent.txt");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Inconclusive("Creating a task-owned symbolic link requires platform permission.");
        }

        await using var coordinator = new SessionPromptDraftPersistenceCoordinator(options);
        Assert.IsNull(coordinator.LoadPromptDraft("session"));
        coordinator.DeletePromptDraft("session");
        Assert.IsNull(new FileInfo(link).LinkTarget);
        Assert.IsFalse(File.Exists(target));
    }

    [TestMethod]
    public async Task DeletePromptDraft_ExternalSameTimestampEditIsNotDeleted()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        Directory.CreateDirectory(options.PromptDraftsRoot);
        var path = Path.Combine(options.PromptDraftsRoot, "saved_prompt_session-1.md");
        await File.WriteAllTextAsync(path, "original");
        var coordinator = new SessionPromptDraftPersistenceCoordinator(options, TimeSpan.FromMinutes(1));
        Assert.AreEqual("original", coordinator.LoadPromptDraft("session-1"));
        var timestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, "external");
        File.SetLastWriteTimeUtc(path, timestamp);

        try
        {
            coordinator.DeletePromptDraft("session-1");
        }
        catch (IOException)
        {
            // A synchronous deletion seam must surface a failed acknowledgement.
        }

        Assert.IsTrue(File.Exists(path), "A stale deletion must not remove externally edited text.");
        Assert.AreEqual("external", await File.ReadAllTextAsync(path));
        try
        {
            await coordinator.DisposeAsync();
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public async Task FlushAsync_PersistsPendingPromptWithoutWaitingForDebounce()
    {
        using var temp = TempDirectory.Create();
        await using var coordinator = new SessionPromptDraftPersistenceCoordinator(
            new CatalogOptions { GlobalRoot = temp.Path },
            TimeSpan.FromMinutes(1));

        coordinator.ObservePromptDraft("session-1", "persist me");
        Assert.IsTrue((await coordinator.FlushAsync()).Succeeded);

        Assert.AreEqual("persist me", coordinator.LoadPromptDraft("session-1"));
        Assert.IsTrue(coordinator.HasPromptDraft("session-1"));
        Assert.AreEqual("persist me", await File.ReadAllTextAsync(Path.Combine(temp.Path, "saved_prompts", "saved_prompt_session-1.md")));
    }

    [TestMethod]
    public async Task ObservePromptDraft_DeletingPromptRemovesSavedFile()
    {
        using var temp = TempDirectory.Create();
        await using var coordinator = new SessionPromptDraftPersistenceCoordinator(
            new CatalogOptions { GlobalRoot = temp.Path },
            TimeSpan.FromMinutes(1));

        coordinator.ObservePromptDraft("session-1", "persist me");
        Assert.IsTrue((await coordinator.FlushAsync()).Succeeded);

        coordinator.ObservePromptDraft("session-1", string.Empty);
        Assert.IsTrue((await coordinator.FlushAsync()).Succeeded);

        Assert.IsNull(coordinator.LoadPromptDraft("session-1"));
        Assert.IsFalse(coordinator.HasPromptDraft("session-1"));
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "saved_prompts", "saved_prompt_session-1.md")));
    }

    [TestMethod]
    public async Task DisposeAsync_FlushesPendingPromptSaveImmediately()
    {
        using var temp = TempDirectory.Create();
        var coordinator = new SessionPromptDraftPersistenceCoordinator(
            new CatalogOptions { GlobalRoot = temp.Path },
            TimeSpan.FromMinutes(1));

        coordinator.ObservePromptDraft("session-1", "persist on dispose");
        await coordinator.DisposeAsync().ConfigureAwait(false);

        await using var reloaded = new SessionPromptDraftPersistenceCoordinator(new CatalogOptions { GlobalRoot = temp.Path });
        Assert.AreEqual("persist on dispose", reloaded.LoadPromptDraft("session-1"));
    }

    [TestMethod]
    [DataRow("newer")]
    [DataRow("")]
    public async Task InFlightSave_NewerEditOrClearIsOrderedAndFlushJoins(string newerText)
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var store = new PromptDraftStore(options);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var coordinator = new SessionPromptDraftPersistenceCoordinator(store, TimeSpan.Zero, async (key, text, revision) =>
        {
            if (++calls == 1)
            {
                entered.SetResult();
                await release.Task.ConfigureAwait(false);
            }

            return await store.SaveAsync(key, text, revision).ConfigureAwait(false);
        });

        coordinator.ObservePromptDraft("session", "older");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        coordinator.ObservePromptDraft("session", newerText);
        var flush = coordinator.FlushAsync();
        Assert.IsFalse(flush.IsCompleted, "Flush must join the started write, not just cancel its delay.");
        release.SetResult();
        Assert.IsTrue((await flush).Succeeded);
        await coordinator.DisposeAsync();
        await using var reopened = new SessionPromptDraftPersistenceCoordinator(options);
        Assert.AreEqual(newerText.Length == 0 ? null : newerText, reopened.LoadPromptDraft("session"));
        Assert.AreEqual(newerText.Length != 0, File.Exists(store.GetPath("session")));
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task InFlightDelete_NewerSaveAndDisposalJoinAndPersistToDisk()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var store = new PromptDraftStore(options);
        await store.SaveAsync("session", "original", TextFileRevision.Missing);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new SessionPromptDraftPersistenceCoordinator(store, TimeSpan.Zero, async (key, text, revision) =>
        {
            if (text is null)
            {
                entered.SetResult();
                await release.Task.ConfigureAwait(false);
            }

            return await store.SaveAsync(key, text, revision).ConfigureAwait(false);
        });
        coordinator.ObservePromptDraft("session", null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        coordinator.ObservePromptDraft("session", "newer");
        var disposal = coordinator.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);
        release.SetResult();
        await disposal;
        await using var reopened = new SessionPromptDraftPersistenceCoordinator(options);
        Assert.AreEqual("newer", reopened.LoadPromptDraft("session"));
        Assert.AreEqual("newer", await File.ReadAllTextAsync(store.GetPath("session")));
        Assert.Throws<ObjectDisposedException>(() => coordinator.ObservePromptDraft("session", "too late"));
    }

    [TestMethod]
    public async Task SaveFailure_RetainsDirtyTextAndRetryUsesAcknowledgedRevision()
    {
        using var temp = TempDirectory.Create();
        var store = new PromptDraftStore(new CatalogOptions { GlobalRoot = temp.Path });
        var failing = true;
        await using var coordinator = new SessionPromptDraftPersistenceCoordinator(store, TimeSpan.FromDays(1), (key, text, revision) =>
            failing ? Task.FromException<PromptDraftSaveResult>(new IOException("Injected storage failure")) : store.SaveAsync(key, text, revision));
        coordinator.ObservePromptDraft("session", "dirty 🌍");
        var failure = await coordinator.FlushAsync();
        Assert.IsFalse(failure.Succeeded);
        Assert.IsTrue(failure.Failures.ContainsKey("session"));
        Assert.AreEqual("dirty 🌍", coordinator.LoadPromptDraft("session"));
        Assert.IsFalse(File.Exists(store.GetPath("session")));
        failing = false;
        Assert.IsTrue((await coordinator.FlushAsync()).Succeeded);
        Assert.AreEqual("dirty 🌍", await File.ReadAllTextAsync(store.GetPath("session")));
    }

    [TestMethod]
    public async Task DiskFailure_RetainsDirtyTextAndDisposeReportsFailure()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var coordinator = new SessionPromptDraftPersistenceCoordinator(options, TimeSpan.FromDays(1));
        coordinator.ObservePromptDraft("session", "not saved");
        await File.WriteAllTextAsync(options.PromptDraftsRoot, "block directory creation");
        Assert.IsFalse((await coordinator.FlushAsync()).Succeeded);
        await Assert.ThrowsAsync<IOException>(async () => await coordinator.DisposeAsync());
        Assert.AreEqual("not saved", coordinator.LoadPromptDraft("session"));
        Assert.IsFalse(Directory.Exists(options.PromptDraftsRoot));
        Assert.AreEqual("block directory creation", await File.ReadAllTextAsync(options.PromptDraftsRoot));
    }

    [TestMethod]
    public async Task Conflict_RetainsPendingTextAndNeverAdoptsExternalRevision()
    {
        using var temp = TempDirectory.Create();
        var options = new CatalogOptions { GlobalRoot = temp.Path };
        var store = new PromptDraftStore(options);
        await store.SaveAsync("session", "original", TextFileRevision.Missing);
        var coordinator = new SessionPromptDraftPersistenceCoordinator(options, TimeSpan.FromDays(1));
        Assert.AreEqual("original", coordinator.LoadPromptDraft("session"));
        coordinator.ObservePromptDraft("session", "dirty");
        await File.WriteAllTextAsync(store.GetPath("session"), "external");
        Assert.IsFalse((await coordinator.FlushAsync()).Succeeded);
        Assert.AreEqual("dirty", coordinator.LoadPromptDraft("session"));
        coordinator.ObservePromptDraft("session", "newer dirty");
        Assert.IsFalse((await coordinator.FlushAsync()).Succeeded);
        await Assert.ThrowsAsync<IOException>(async () => await coordinator.DisposeAsync());
        Assert.AreEqual("newer dirty", coordinator.LoadPromptDraft("session"));
        Assert.AreEqual("external", await File.ReadAllTextAsync(store.GetPath("session")));
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CodeAlta.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
