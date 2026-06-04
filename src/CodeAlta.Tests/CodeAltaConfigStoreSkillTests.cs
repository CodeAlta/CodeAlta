using CodeAlta.Catalog;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeAltaConfigStoreSkillTests
{
    [TestMethod]
    public void SaveDisabledSkillNames_NormalizesRoundTripsAndPrunesEmptySkillsSection()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "CodeAltaConfigStoreSkillTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        try
        {
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = tempPath });

            store.SaveGlobalDisabledSkillNames(["Sample-Skill", "sample-skill", "other-skill", "  zed-skill  "]);

            CollectionAssert.AreEqual(
                new[] { "other-skill", "sample-skill", "zed-skill" },
                store.LoadGlobalDisabledSkillNames().OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray());
            var content = File.ReadAllText(Path.Combine(tempPath, "config.toml"));
            StringAssert.Contains(content, "[skills]");
            StringAssert.Contains(content, "disabled = [\"other-skill\", \"sample-skill\", \"zed-skill\"]");

            store.SaveGlobalSkillEnabled("sample-skill", enabled: true);
            CollectionAssert.AreEqual(
                new[] { "other-skill", "zed-skill" },
                store.LoadGlobalDisabledSkillNames().OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray());

            store.SaveGlobalDisabledSkillNames([]);
            content = File.ReadAllText(Path.Combine(tempPath, "config.toml"));
            Assert.IsFalse(content.Contains("[skills]", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(0, store.LoadGlobalDisabledSkillNames().Count);
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
    public void SaveProjectSkillEnabled_PersistsProjectDisabledSkillName()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "CodeAltaConfigStoreSkillTests", Guid.NewGuid().ToString("N"));
        var globalRoot = Path.Combine(tempPath, "home");
        var projectRoot = Path.Combine(tempPath, "project");
        Directory.CreateDirectory(globalRoot);
        Directory.CreateDirectory(projectRoot);
        try
        {
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = globalRoot });

            store.SaveProjectSkillEnabled(projectRoot, "project-skill", enabled: false);

            CollectionAssert.AreEqual(new[] { "project-skill" }, store.LoadProjectDisabledSkillNames(projectRoot).ToArray());
            Assert.IsTrue(File.Exists(Path.Combine(projectRoot, ".alta", "config.toml")));
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
