using System.Reflection;
using CodeAlta.Catalog;
using CodeAlta.Tui.App;
using CodeAlta.Tui.Presentation.Editing;
using CodeAlta.Tui.Presentation.Prompting;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Hosting;

namespace CodeAlta.Tests;

[TestClass]
[DoNotParallelize]
public sealed class FileEditorWorkspaceCoordinatorTests
{
    [TestMethod]
    public void QueueExternalStateRefresh_UnattachedAndStoppedAppDoNotPostOrLatch()
    {
        WithEditorTab(tab =>
        {
            // Call the watcher entry point directly: no dependence on OS event delivery timing.
            Task.Run(tab.QueueExternalStateRefresh).GetAwaiter().GetResult();
            using (var terminal = new EditorTestTerminal(tab.Root))
            {
                File.Delete(tab.FullPath);
                Task.Run(tab.QueueExternalStateRefresh).GetAwaiter().GetResult();
                terminal.Tick();
                Assert.IsFalse(tab.ExistsOnDisk, "An ignored unattached notification must not latch the queue flag.");
            }

            Task.Run(tab.QueueExternalStateRefresh).GetAwaiter().GetResult();
        });
    }

    [TestMethod]
    public void QueueExternalStateRefresh_DetachInvalidatesPendingCallbackAndReattachmentReconciles()
    {
        WithEditorTab(tab =>
        {
            var host = new Border().Content(tab.Root);
            using (var terminal = new EditorTestTerminal(host))
            {
                terminal.Tick();
                Task.Run(tab.QueueExternalStateRefresh).GetAwaiter().GetResult();
                host.Content = null;
                File.Delete(tab.FullPath);
                Task.Run(tab.QueueExternalStateRefresh).GetAwaiter().GetResult();
                terminal.Tick();
                Assert.IsTrue(tab.ExistsOnDisk, "A callback queued before detach must not update the detached tab.");

                host.Content = tab.Root;
                terminal.Tick();
                Assert.IsFalse(tab.ExistsOnDisk, "Reattachment must reconcile changes missed while detached.");
            }
        });
    }

    [TestMethod]
    public void QueueExternalStateRefresh_MountedTabOnlyChangesStateWhenUiQueueDrains()
    {
        WithEditorTab(tab =>
        {
            using var terminal = new EditorTestTerminal(tab.Root);
            terminal.Tick();
            File.Delete(tab.FullPath);
            Task.Run(() =>
            {
                tab.QueueExternalStateRefresh();
                tab.QueueExternalStateRefresh();
            }).GetAwaiter().GetResult();
            Assert.IsTrue(tab.ExistsOnDisk, "Watcher threads must not mutate UI state.");
            terminal.Tick();
            Assert.IsFalse(tab.ExistsOnDisk);
            Assert.IsTrue(tab.HasExternalChanges);
        });
    }

    [TestMethod]
    public void QueueExternalStateRefresh_DisposeInvalidatesPendingAndLateCallbacks()
    {
        WithEditorTab(tab =>
        {
            using var terminal = new EditorTestTerminal(tab.Root);
            terminal.Tick();
            Task.Run(tab.QueueExternalStateRefresh).GetAwaiter().GetResult();
            tab.DisposeAsync().AsTask().GetAwaiter().GetResult();
            File.Delete(tab.FullPath);
            Task.Run(tab.QueueExternalStateRefresh).GetAwaiter().GetResult();
            terminal.Tick();
            Assert.IsTrue(tab.ExistsOnDisk, "Neither pending nor late callbacks may mutate a disposed tab.");
        });
    }

    [TestMethod]
    public async Task OpenFilePathAsync_RegistersAndSelectsEditorShellTab()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var filePath = Path.Combine(tempDirectory.FullName, "notes.txt");
            await File.WriteAllTextAsync(filePath, "hello");
            var resolvedPath = Path.GetFullPath(filePath);
            var tabId = $"file:{resolvedPath}";
            var shellTabs = new InMemoryShellTabService();
            var syncCount = 0;

            await using var coordinator = new FileEditorWorkspaceCoordinator(
                new TextFileCodec(),
                NullProjectFileSearchService.Instance,
                shellTabs,
                () => tempDirectory.FullName,
                () => null,
                () => null,
                static build => new ComputedVisual(build),
                _ => { },
                () => syncCount++,
                static (_, _, _) => { });

            await coordinator.OpenFilePathAsync(filePath);

            CollectionAssert.AreEqual(new[] { tabId }, coordinator.OpenTabIds.ToArray());
            Assert.AreEqual(tabId, coordinator.SelectedTabId);
            Assert.IsTrue(shellTabs.TryGetTab(new ShellTabId(tabId), out var shellTab));
            Assert.AreEqual(ShellTabKind.Editor, shellTab.Kind);
            Assert.IsTrue(shellTab.IsSelected);
            Assert.IsInstanceOfType<ShellTabAssociation.Editor>(shellTab.Association);
            Assert.AreEqual(resolvedPath, ((ShellTabAssociation.Editor)shellTab.Association).FullPath);
            Assert.IsNotNull(coordinator.GetSelectedFileTab());
            Assert.AreEqual(1, syncCount);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task CloseFileTabAsync_RemovesEditorShellTab()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var filePath = Path.Combine(tempDirectory.FullName, "notes.txt");
            await File.WriteAllTextAsync(filePath, "hello");
            var tabId = $"file:{Path.GetFullPath(filePath)}";
            var shellTabs = new InMemoryShellTabService();

            await using var coordinator = new FileEditorWorkspaceCoordinator(
                new TextFileCodec(),
                NullProjectFileSearchService.Instance,
                shellTabs,
                () => tempDirectory.FullName,
                () => null,
                () => null,
                static build => new ComputedVisual(build),
                _ => { },
                static () => { },
                static (_, _, _) => { });
            await coordinator.OpenFilePathAsync(filePath);

            await coordinator.CloseFileTabAsync(tabId);

            CollectionAssert.AreEqual(Array.Empty<string>(), coordinator.OpenTabIds.ToArray());
            Assert.IsNull(coordinator.SelectedTabId);
            Assert.IsFalse(shellTabs.TryGetTab(new ShellTabId(tabId), out _));
            Assert.IsNull(coordinator.GetSelectedFileTab());
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task EditorSave_ConflictsRetainDirtyTextAndOverwriteRequiresObservedRevision()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(tempDirectory.FullName, "notes.txt");
            await File.WriteAllTextAsync(path, "original");
            var store = new TextFileCodec();
            var snapshot = await store.LoadAsync(path);
            await using var coordinator = new FileEditorWorkspaceCoordinator(
                store,
                NullProjectFileSearchService.Instance,
                new InMemoryShellTabService(),
                () => tempDirectory.FullName,
                () => null,
                () => null,
                static build => new ComputedVisual(build),
                _ => { },
                static () => { },
                static (_, _, _) => { });
            await coordinator.OpenFilePathAsync(path);
            var tab = coordinator.GetSelectedFileTab()!;
            tab.Editor.TextDocument.Insert(0, "my edits ");
            Assert.IsTrue(tab.IsDirty);
            await File.WriteAllTextAsync(path, "external");
            File.SetLastWriteTimeUtc(path, snapshot.LastWriteTimeUtc.UtcDateTime);

            var conflict = await tab.SaveCurrentTextAsync(snapshot.Revision);
            Assert.IsTrue(conflict.IsConflict);
            Assert.IsTrue(tab.IsDirty);
            Assert.IsTrue(tab.HasExternalChanges);
            Assert.AreEqual("my edits original", CodeAlta.Tui.Presentation.Editing.CodeEditorFactory.GetText(tab.Editor));
            Assert.AreEqual("external", await File.ReadAllTextAsync(path));

            await File.WriteAllTextAsync(path, "another change");
            var staleOverwrite = await tab.SaveCurrentTextAsync(conflict.CurrentRevision);
            Assert.IsTrue(staleOverwrite.IsConflict);
            Assert.IsTrue(tab.IsDirty);
            var saved = await tab.SaveCurrentTextAsync(staleOverwrite.CurrentRevision);
            Assert.IsFalse(saved.IsConflict);
            Assert.IsFalse(tab.IsDirty);
            Assert.IsFalse(tab.HasExternalChanges);
            Assert.AreEqual("my edits original", await File.ReadAllTextAsync(path));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    private static void WithEditorTab(Action<FileEditorTab> test)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("codealta-editor-watcher-");
        try
        {
            var path = Path.Combine(tempDirectory.FullName, "notes.txt");
            File.WriteAllText(path, "original");
            var coordinator = new FileEditorWorkspaceCoordinator(
                new TextFileCodec(),
                NullProjectFileSearchService.Instance,
                new InMemoryShellTabService(),
                () => tempDirectory.FullName,
                () => null,
                () => null,
                static build => new ComputedVisual(build),
                _ => { },
                static () => { },
                static (_, _, _) => { });
            try
            {
                coordinator.OpenFilePathAsync(path).GetAwaiter().GetResult();
                var tab = coordinator.GetSelectedFileTab();
                Assert.IsNotNull(tab);
                test(tab);
            }
            finally
            {
                coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    // Only XenoAtom's disposable in-memory backend: no CodeAlta host, profile, native
    // terminal, clipboard access, or provider/plugin initialization. Keep all UI work
    // synchronous on the test thread and explicitly drain posted actions with Tick.
    private sealed class EditorTestTerminal : IDisposable
    {
        private readonly TerminalSession _session;
        private readonly TerminalApp _app;

        public EditorTestTerminal(Visual root)
        {
            _session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(100, 30)),
                new TerminalOptions { ImplicitStartInput = true }, force: true);
            try
            {
                _app = new TerminalApp(root, _session.Instance,
                    new TerminalAppOptions { HostKind = TerminalHostKind.Fullscreen });
                Invoke("BeginRun");
            }
            catch
            {
                if (_app is not null)
                {
                    Dispose();
                }
                else
                {
                    _session.Dispose();
                }
                throw;
            }
        }

        public void Tick() => Invoke("Tick", [null]);

        public void Dispose()
        {
            try
            {
                Invoke("EndRun");
            }
            finally
            {
                try
                {
                    _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    _session.Dispose();
                }
            }
        }

        private void Invoke(string name, object?[]? arguments = null)
            => typeof(TerminalApp).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_app, arguments);
    }
}
