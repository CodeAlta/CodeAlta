using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaConfigStoreConcurrencyTests
{
    [TestMethod]
    public void Saves_OfTwoStoresSideBySideLoseNoSetting()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAltaConfigStoreConcurrencyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Two pages of the desktop save their own settings at once, each through a store of its own.
            var skills = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root });
            var plugins = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root });
            Parallel.Invoke(
                () => { for (var index = 0; index < 20; index++) skills.SaveGlobalSkillEnabled($"skill-{index}", enabled: false); },
                () => { for (var index = 0; index < 20; index++) plugins.SaveGlobalPluginEnabled($"plugin-{index}", enabled: false); });

            var document = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root }).LoadGlobal();
            Assert.AreEqual(20, skills.LoadGlobalDisabledSkillNames().Count, "every skill that was turned off is off");
            Assert.AreEqual(20, document.Plugins?.Count, "every plugin that was turned off is off");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LockGlobalConfig_MakesTheStoresWriteAfterIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAltaConfigStoreConcurrencyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root });
            Task saved;
            // The lock belongs to a thread: it is held and released without an await between.
            using (store.LockGlobalConfig())
            {
                saved = Task.Run(() => new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root }).SaveGlobalWorktreeSettings("custom", root));
                Assert.IsFalse(saved.Wait(200), "a save waits while the file is held");
                Assert.IsFalse(File.Exists(store.ConfigPath));
            }

            await saved.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("custom", store.LoadGlobal().Worktrees?.Location);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
