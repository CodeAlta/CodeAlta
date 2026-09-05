using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Threading;
using CodeAlta.Tui.ViewModels;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellStateStoreTests
{
    [TestMethod]
    public void AutoApproveSettings_DefaultToEnabled()
    {
        Assert.IsTrue(new NavigatorSettings().AutoApprove);
        Assert.IsTrue(new NavigatorSettingsDialogViewModel().AutoApprove);
    }

    [TestMethod]
    public void Mutate_PublishesImmutableSnapshots()
    {
        var store = new ShellStateStore();
        var first = store.Snapshot;

        var updated = store.Mutate(snapshot => snapshot
            .UpsertTab(new ShellFrontendTabSnapshot("session-1", "Session", "session"))
            .SetStatus("Ready"));

        Assert.AreEqual(0, first.Tabs.Count);
        Assert.AreEqual(1, updated.Tabs.Count);
        Assert.AreEqual("session-1", updated.ActiveTabId);
        Assert.AreEqual("Ready", updated.StatusText);
        Assert.AreSame(updated, store.Snapshot);
    }

    [TestMethod]
    public void RemoveTab_SelectsRemainingTabWhenActiveTabIsRemoved()
    {
        var snapshot = ShellFrontendStateSnapshot.Empty
            .UpsertTab(new ShellFrontendTabSnapshot("session-1", "Session 1", "session"))
            .UpsertTab(new ShellFrontendTabSnapshot("session-2", "Session 2", "session"))
            .SelectTab("session-2");

        var updated = snapshot.RemoveTab("session-2");

        Assert.AreEqual(1, updated.Tabs.Count);
        Assert.AreEqual("session-1", updated.ActiveTabId);
    }

    [TestMethod]
    public void SelectTab_RejectsMissingTab()
    {
        var snapshot = ShellFrontendStateSnapshot.Empty;

        Assert.ThrowsExactly<InvalidOperationException>(() => snapshot.SelectTab("missing"));
    }

    [TestMethod]
    public void Snapshot_RejectsAccessFromNonOwnerSession()
    {
        var store = new ShellStateStore();
        var ownerThreadId = Environment.CurrentManagedThreadId;
        var workerThreadId = 0;
        Exception? capturedException = null;

        var worker = new Thread(() =>
        {
            workerThreadId = Environment.CurrentManagedThreadId;
            try
            {
                _ = store.Snapshot;
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
        });
        worker.Start();
        Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)), "Timed out waiting for the worker thread to complete.");

        Assert.AreNotEqual(ownerThreadId, workerThreadId);
        Assert.IsInstanceOfType<InvalidOperationException>(capturedException);
    }

    [TestMethod]
    public void SetCatalogAndSelection_CapturesFrontendStateSnapshots()
    {
        var project = new ProjectDescriptor { Id = "project-1", Name = "Project", ProjectPath = "C:\\repo" };
        var session = new SessionViewDescriptor { SessionId = "session-1", ProjectRef = "project-1", Title = "Session" };
        var navigatorSettings = new NavigatorSettings
        {
            SortMode = NavigatorProjectSortMode.Date,
            RecentSessionsPerProject = 7,
            ThemeSchemeName = "Elderberry Dark Soft",
            LanguageName = "en-US",
            AutoApprove = false,
        };

        var updated = ShellFrontendStateSnapshot.Empty
            .SetCatalog([project], [session])
            .SetSelection(ShellSelection.Session("session-1", "project-1"), ["session-1", "session-1"], navigatorSettings);

        Assert.AreEqual(1, updated.Projects.Count);
        Assert.AreEqual(1, updated.Sessions.Count);
        Assert.AreEqual("session-1", updated.Selection.SelectedSessionId);
        Assert.AreEqual(1, updated.OpenSessionIds.Count);
        Assert.AreEqual(NavigatorProjectSortMode.Date, updated.NavigatorSettings.SortMode);
        Assert.AreEqual("Elderberry Dark Soft", updated.NavigatorSettings.ThemeSchemeName);
        Assert.AreEqual("en-US", updated.NavigatorSettings.LanguageName);
        Assert.IsFalse(updated.NavigatorSettings.AutoApprove);
        Assert.AreNotSame(navigatorSettings, updated.NavigatorSettings);
    }

    [TestMethod]
    public void ModelProviderSelectorStateStore_RejectsAccessOffUiSession()
    {
        var store = new ModelProviderSelectorStateStore(
            new SessionWorkspaceViewModel(),
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
