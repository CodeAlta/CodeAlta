using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaConfigStorePluginTests
{
    [TestMethod]
    public void SaveGlobalDefaultProviderPreservesUnknownPluginEntries()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "CodeAltaConfigStorePluginTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempPath);
            var options = new CatalogOptions { GlobalRoot = tempPath };
            File.WriteAllText(options.ConfigPath, """
[plugins.unknown_plugin]
enabled = true

[chat]
default_provider = "codex"
""");
            var store = new CodeAltaConfigStore(options);

            store.SaveGlobalDefaultProvider("copilot");
            var document = store.LoadGlobal();

            Assert.IsNotNull(document.Plugins);
            Assert.IsTrue(document.Plugins.TryGetValue("unknown_plugin", out var settings));
            Assert.AreEqual(true, settings.Enabled);
        }
        finally
        {
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SaveGlobalPluginEnabled_PersistsPluginOverride()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "CodeAltaConfigStorePluginTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempPath);
            var options = new CatalogOptions { GlobalRoot = tempPath };
            var store = new CodeAltaConfigStore(options);

            store.SaveGlobalPluginEnabled("sample-plugin", enabled: false);
            var document = store.LoadGlobal();

            Assert.IsNotNull(document.Plugins);
            Assert.IsTrue(document.Plugins.TryGetValue("sample-plugin", out var settings));
            Assert.AreEqual(false, settings.Enabled);
        }
        finally
        {
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SaveProjectPluginEnabled_PersistsProjectPluginOverride()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "CodeAltaConfigStorePluginTests", Guid.NewGuid().ToString("N"));
        try
        {
            var projectPath = Path.Combine(tempPath, "project");
            Directory.CreateDirectory(projectPath);
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = Path.Combine(tempPath, "home") });

            store.SaveProjectPluginEnabled(projectPath, "project-plugin", enabled: true);
            var document = store.LoadProject(projectPath);

            Assert.IsNotNull(document.Plugins);
            Assert.IsTrue(document.Plugins.TryGetValue("project-plugin", out var settings));
            Assert.AreEqual(true, settings.Enabled);
        }
        finally
        {
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }
    }
}
