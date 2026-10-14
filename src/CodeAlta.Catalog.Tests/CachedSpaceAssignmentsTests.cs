namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class CachedSpaceAssignmentsTests
{
    [TestMethod]
    public void TheAssignments_AreReadAgainWhenTheFileChanges_AndKeptWhenItCannotBeParsed()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-cached-spaces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root });
            var cache = new CachedSpaceAssignments(store);
            Assert.AreEqual(0, cache.Current().Plugins.Count, "No file assigns nothing.");

            store.SaveGlobalSpaceAssignment(SpaceResourceKind.Plugin, "jira", ["business"]);
            CollectionAssert.AreEqual(new[] { "business" }, cache.Current().Of(SpaceResourceKind.Plugin, "jira").ToArray());

            File.WriteAllText(store.ConfigPath, "[plugins.jira\nspaces = [");
            CollectionAssert.AreEqual(new[] { "business" }, cache.Current().Of(SpaceResourceKind.Plugin, "jira").ToArray(),
                "A file being written keeps what was read last.");

            File.WriteAllText(store.ConfigPath, "[plugins.jira]\nspaces = [\"business\", \"default\"]\n");
            CollectionAssert.AreEqual(new[] { "business", "default" }, cache.Current().Of(SpaceResourceKind.Plugin, "jira").ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
