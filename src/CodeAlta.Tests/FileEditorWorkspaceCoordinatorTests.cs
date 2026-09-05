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
}
