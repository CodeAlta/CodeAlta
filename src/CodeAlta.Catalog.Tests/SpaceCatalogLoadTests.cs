namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SpaceCatalogLoadTests
{
    [TestMethod]
    public async Task Load_ReadsTheSpacesAsLoadAsyncDoes()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-space-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new CatalogOptions { GlobalRoot = root };
            var spaces = new SpaceCatalog(new ProjectCatalog(options));
            Assert.AreEqual(Describe(await spaces.LoadAsync()), Describe(spaces.Load()), "No folder: the default space alone.");

            Assert.IsTrue(await spaces.SeedAsync());
            Assert.IsTrue((await spaces.CreateAsync("Open Source", "Libraries.", "rocket", "#d33d17")).Succeeded);
            Assert.IsTrue((await spaces.UpdateAsync("default", new SpaceEdit(Name: "Everything"))).Succeeded);
            // Files that are not spaces are left out by both.
            File.WriteAllText(Path.Combine(options.SpacesRoot, "Not An Id.md"), "---\nname: Wrong\n---\n");
            File.WriteAllText(Path.Combine(options.SpacesRoot, "broken.md"), "---\nname: [unclosed\n---\n");
            File.WriteAllText(Path.Combine(options.SpacesRoot, "notes.txt"), "Not a space.");

            var expected = Describe(await spaces.LoadAsync());
            Assert.AreEqual(expected, Describe(spaces.Load()));
            StringAssert.StartsWith(expected, "default:Everything");
            StringAssert.Contains(expected, "open-source:Open Source");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Describe(IReadOnlyList<SpaceDescriptor> spaces)
        => string.Join("\n", spaces.Select(static space => $"{space.Id}:{space.Name}:{space.Icon}:{space.Color}:{space.Order}:{space.Description}:{space.SourcePath}:{space.Settings}"));
}
