namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ProjectCatalogChangeTests
{
    [TestMethod]
    public async Task TheCatalog_TellsWhatItAddedSavedAndDeleted_AndWhatAddedIt()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CodeAlta-catalog-changes-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "home") });
            var changes = new List<ProjectCatalogChange>();
            // A reader that fails keeps neither the change from being made nor the others from being told.
            catalog.Changed += _ => throw new InvalidOperationException("a reader that fails");
            catalog.Changed += changes.Add;
            var folder = Directory.CreateDirectory(Path.Combine(root, "alpha")).FullName;

            var alpha = await catalog.UpsertFromPathAsync(folder, ProjectOrigins.AltaAdd);
            Assert.AreEqual((ProjectCatalogChangeKind.Added, ProjectOrigins.AltaAdd, alpha.Id), (changes[^1].Kind, changes[^1].Origin, changes[^1].Project.Id));
            Assert.AreNotSame(alpha, changes[^1].Project, "a reader gets a copy");

            // Known already, it is not added again.
            await catalog.UpsertFromPathAsync(folder, ProjectOrigins.Window);
            Assert.AreEqual(1, changes.Count);

            alpha.Archived = true;
            await catalog.SaveAsync(alpha);
            Assert.AreEqual((ProjectCatalogChangeKind.Saved, (string?)null), (changes[^1].Kind, changes[^1].Origin));
            Assert.IsTrue(changes[^1].Project.Archived);

            // Brought back from the archive, it says what brought it back.
            await catalog.UpsertFromPathAsync(folder, ProjectOrigins.Window);
            Assert.AreEqual((ProjectCatalogChangeKind.Saved, ProjectOrigins.Window, false), (changes[^1].Kind, changes[^1].Origin, changes[^1].Project.Archived));

            Assert.IsTrue(await catalog.DeleteAsync(alpha));
            Assert.AreEqual((ProjectCatalogChangeKind.Deleted, alpha.Id), (changes[^1].Kind, changes[^1].Project.Id));
            Assert.AreEqual(4, changes.Count);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
