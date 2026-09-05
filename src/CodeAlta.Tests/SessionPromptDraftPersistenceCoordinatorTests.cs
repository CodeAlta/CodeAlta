using CodeAlta.Tui.App;
using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class SessionPromptDraftPersistenceCoordinatorTests
{
    [TestMethod]
    public async Task ObservePromptDraft_PersistsPromptAfterDelay()
    {
        using var temp = TempDirectory.Create();
        var coordinator = new SessionPromptDraftPersistenceCoordinator(
            new CatalogOptions { GlobalRoot = temp.Path },
            TimeSpan.FromMilliseconds(10));

        coordinator.ObservePromptDraft("session-1", "persist me");
        await Task.Delay(50).ConfigureAwait(false);

        Assert.AreEqual("persist me", coordinator.LoadPromptDraft("session-1"));
        Assert.IsTrue(coordinator.HasPromptDraft("session-1"));
    }

    [TestMethod]
    public async Task ObservePromptDraft_DeletingPromptRemovesSavedFile()
    {
        using var temp = TempDirectory.Create();
        var coordinator = new SessionPromptDraftPersistenceCoordinator(
            new CatalogOptions { GlobalRoot = temp.Path },
            TimeSpan.FromMilliseconds(10));

        coordinator.ObservePromptDraft("session-1", "persist me");
        await Task.Delay(50).ConfigureAwait(false);

        coordinator.ObservePromptDraft("session-1", string.Empty);
        await Task.Delay(20).ConfigureAwait(false);

        Assert.IsNull(coordinator.LoadPromptDraft("session-1"));
        Assert.IsFalse(coordinator.HasPromptDraft("session-1"));
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

        var reloaded = new SessionPromptDraftPersistenceCoordinator(new CatalogOptions { GlobalRoot = temp.Path });
        Assert.AreEqual("persist on dispose", reloaded.LoadPromptDraft("session-1"));
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
