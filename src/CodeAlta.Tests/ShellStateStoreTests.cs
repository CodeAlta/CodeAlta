using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Threading;
using CodeAlta.ViewModels;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellStateStoreTests
{
    [TestMethod]
    public void Mutate_PublishesImmutableSnapshots()
    {
        var store = new ShellStateStore();
        var first = store.Snapshot;

        var updated = store.Mutate(snapshot => snapshot
            .UpsertTab(new ShellFrontendTabSnapshot("thread-1", "Thread", "thread"))
            .SetStatus("Ready"));

        Assert.AreEqual(0, first.Tabs.Count);
        Assert.AreEqual(1, updated.Tabs.Count);
        Assert.AreEqual("thread-1", updated.ActiveTabId);
        Assert.AreEqual("Ready", updated.StatusText);
        Assert.AreSame(updated, store.Snapshot);
    }

    [TestMethod]
    public void RemoveTab_SelectsRemainingTabWhenActiveTabIsRemoved()
    {
        var snapshot = ShellFrontendStateSnapshot.Empty
            .UpsertTab(new ShellFrontendTabSnapshot("thread-1", "Thread 1", "thread"))
            .UpsertTab(new ShellFrontendTabSnapshot("thread-2", "Thread 2", "thread"))
            .SelectTab("thread-2");

        var updated = snapshot.RemoveTab("thread-2");

        Assert.AreEqual(1, updated.Tabs.Count);
        Assert.AreEqual("thread-1", updated.ActiveTabId);
    }

    [TestMethod]
    public void SelectTab_RejectsMissingTab()
    {
        var snapshot = ShellFrontendStateSnapshot.Empty;

        Assert.ThrowsExactly<InvalidOperationException>(() => snapshot.SelectTab("missing"));
    }

    [TestMethod]
    public async Task Snapshot_RejectsAccessFromNonOwnerThread()
    {
        var store = new ShellStateStore();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Task.Run(() => _ = store.Snapshot));
    }

    [TestMethod]
    public void SetCatalogAndSelection_CapturesFrontendStateSnapshots()
    {
        var project = new ProjectDescriptor { Id = "project-1", Name = "Project", ProjectPath = "C:\\repo" };
        var thread = new WorkThreadDescriptor { ThreadId = "thread-1", ProjectRef = "project-1", Title = "Thread" };
        var navigatorSettings = new NavigatorSettings
        {
            SortMode = NavigatorProjectSortMode.Date,
            RecentThreadsPerProject = 7,
            ThemeSchemeName = "Elderberry Dark Soft",
        };

        var updated = ShellFrontendStateSnapshot.Empty
            .SetCatalog([project], [thread])
            .SetSelection(ShellSelection.Thread("thread-1", "project-1"), ["thread-1", "thread-1"], navigatorSettings);

        Assert.AreEqual(1, updated.Projects.Count);
        Assert.AreEqual(1, updated.Threads.Count);
        Assert.AreEqual("thread-1", updated.Selection.SelectedThreadId);
        Assert.AreEqual(1, updated.OpenThreadIds.Count);
        Assert.AreEqual(NavigatorProjectSortMode.Date, updated.NavigatorSettings.SortMode);
        Assert.AreEqual("Elderberry Dark Soft", updated.NavigatorSettings.ThemeSchemeName);
        Assert.AreNotSame(navigatorSettings, updated.NavigatorSettings);
    }

    [TestMethod]
    public void ModelProviderSelectorStateStore_RejectsAccessOffUiThread()
    {
        var store = new ModelProviderSelectorStateStore(
            new ThreadWorkspaceViewModel(),
            new NonOwnerUiDispatcher());

        Assert.ThrowsExactly<InvalidOperationException>(store.VerifyBindableAccess);
    }

    private sealed class NonOwnerUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => false;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
        }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return Task.FromResult(action());
        }
    }
}
