using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class WorkspaceUnreadableProjectTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public async Task Snapshot_ListsTheOtherProjects_AndNamesTheFileItCannotRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-unreadable-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var folder = Path.Combine(root, "folder");
            Directory.CreateDirectory(folder);
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "catalog") });
            var good = await catalog.UpsertFromPathAsync(folder);
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var service = new WorkspaceService(reads, catalog, Epoch);
            var clean = await service.SnapshotAsync(new WorkspaceRequest(), CancellationToken.None);
            Assert.IsNull(clean.UnreadableProjectFiles);
            Assert.DoesNotContain("unreadableProjectFiles", JsonSerializer.Serialize(clean, DesktopJsonContext.Default.WorkspaceSnapshot));

            var broken = Path.Combine(catalog.Options.ProjectsRoot, "broken.md");
            await File.WriteAllTextAsync(broken, "---\nid: [unclosed\npath: {\n---\n");
            var snapshot = await service.SnapshotAsync(new WorkspaceRequest(), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { good.Id }, snapshot.Projects.Select(static project => project.Id).ToArray());
            CollectionAssert.AreEqual(new[] { broken }, snapshot.UnreadableProjectFiles);
            var wire = JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, DesktopJsonContext.Default.WorkspaceSnapshot),
                DesktopJsonContext.Default.WorkspaceSnapshot)!;
            CollectionAssert.AreEqual(new[] { broken }, wire.UnreadableProjectFiles);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
