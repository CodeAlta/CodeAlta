using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>The projects of a workspace snapshot, read from a real catalog folder.</summary>
[TestClass]
public sealed class OwnedSessionWorkspaceProjectsTests
{
    [TestMethod]
    public async Task Snapshot_SkipsAProjectFileItCannotRead_AndSaysWhich()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-owned-projects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "catalog") });
            var good = await catalog.UpsertFromPathAsync(Path.Combine(root, "good"));
            var broken = Path.Combine(catalog.Options.ProjectsRoot, "broken.md");
            await File.WriteAllTextAsync(broken, "---\nid: [unclosed\npath: {\n---\n");
            // A Windows path written by hand between double quotes: its single backslashes are invalid YAML escapes.
            var handWritten = Path.Combine(catalog.Options.ProjectsRoot, "hand-written.md");
            await File.WriteAllTextAsync(handWritten, "---\nid: 019a0000-0000-7000-8000-000000000001\nname: hand\npath: \"C:\\Users\\someone\\code\"\n---\n");
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));

            var snapshot = await reads.ReadSnapshotAsync(CancellationToken.None);

            CollectionAssert.AreEqual(new[] { good.Id }, snapshot.Projects.Select(static project => project.Id).ToArray());
            CollectionAssert.AreEquivalent(new[] { broken, handWritten }, snapshot.UnreadableProjects.Select(static file => file.SourcePath).ToArray());
            Assert.IsTrue(snapshot.UnreadableProjects.All(static file => file.Message.Length > 0));

            File.Delete(broken);
            File.Delete(handWritten);
            Assert.IsEmpty((await reads.ReadSnapshotAsync(CancellationToken.None)).UnreadableProjects, "A fixed catalog has nothing to say.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
