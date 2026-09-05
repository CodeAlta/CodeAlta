using CodeAlta.Catalog;
using CodeAlta.Tui.App;
using CodeAlta.Tui.Presentation.Prompting;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class FileEditorWorkspaceCoordinatorTests
{
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
}
