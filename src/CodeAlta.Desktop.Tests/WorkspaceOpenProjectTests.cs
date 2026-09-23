using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class WorkspaceOpenProjectTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public async Task ExistingFolder_IsPreviewedThenPersistedOnceInOwnedCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-project-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var folder = Path.Combine(root, "folder");
            Directory.CreateDirectory(folder);
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "catalog") });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var service = new WorkspaceService(reads, catalog, Epoch);
            var before = await service.OpenProjectAsync(new(Epoch, folder + Path.DirectorySeparatorChar, false), CancellationToken.None);
            Assert.AreEqual("confirmation_required", before.Status);
            Assert.AreEqual(folder, before.ProjectPath);
            Assert.IsFalse(Directory.Exists(catalog.Options.ProjectsRoot));
            var wireRequest = new WorkspaceOpenProjectRequest(Epoch, folder, true);
            Assert.AreEqual(wireRequest, JsonSerializer.Deserialize(
                JsonSerializer.Serialize(wireRequest, DesktopJsonContext.Default.WorkspaceOpenProjectRequest),
                DesktopJsonContext.Default.WorkspaceOpenProjectRequest));
            var created = await service.OpenProjectAsync(new(Epoch, folder, true), CancellationToken.None);
            Assert.AreEqual("ok", created.Status);
            Assert.AreEqual(folder, created.ProjectPath);
            Assert.IsFalse(string.IsNullOrWhiteSpace(created.ProjectId));
            Assert.AreEqual(created.ProjectId, (await catalog.GetByPathAsync(folder))?.Id);
            Assert.AreEqual(created, JsonSerializer.Deserialize(
                JsonSerializer.Serialize(created, DesktopJsonContext.Default.WorkspaceOpenProjectResponse),
                DesktopJsonContext.Default.WorkspaceOpenProjectResponse));
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(folder)); // Import writes to the catalog, not the chosen folder.
            var again = await service.OpenProjectAsync(new(Epoch, folder, true), CancellationToken.None);
            Assert.AreEqual(created.ProjectId, again.ProjectId);
            Assert.HasCount(1, await catalog.LoadAsync());
            var archived = await catalog.GetByPathAsync(folder);
            Assert.IsNotNull(archived);
            archived.Archived = true;
            await catalog.SaveAsync(archived);
            Assert.AreEqual(created.ProjectId, (await service.OpenProjectAsync(new(Epoch, folder, true), CancellationToken.None)).ProjectId);
            Assert.IsTrue((await catalog.GetByPathAsync(folder))!.Archived); // This slice does not unarchive existing projects.
            await service.CloseImportsAsync();
            Assert.AreEqual("closed", (await service.OpenProjectAsync(new(Epoch, folder, true), CancellationToken.None)).Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task InvalidAndMissingFoldersCannotCreateCatalogEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-project-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "catalog") });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var service = new WorkspaceService(reads, catalog, Epoch);
            var file = Path.Combine(root, "file");
            File.WriteAllText(file, "test-owned");
            foreach (var path in new[] { "", "relative", "~/home", "./relative", "  " + root, root + "\0", new string('x', 4097) })
                Assert.AreEqual("invalid_request", (await service.OpenProjectAsync(new(Epoch, path, true), CancellationToken.None)).Status, path);
            Assert.AreEqual("missing_directory", (await service.OpenProjectAsync(new(Epoch, file, true), CancellationToken.None)).Status);
            Assert.AreEqual("missing_directory", (await service.OpenProjectAsync(new(Epoch, Path.Combine(root, "absent"), true), CancellationToken.None)).Status);
            Assert.AreEqual("stale_epoch", (await service.OpenProjectAsync(new(Guid.NewGuid().ToString("D"), root, true), CancellationToken.None)).Status);
            Assert.AreEqual("invalid_request", (await service.OpenProjectAsync(new WorkspaceOpenProjectRequest("invalid", root, false), CancellationToken.None)).Status);
            Assert.AreEqual("confirmation_required", (await service.OpenProjectAsync(new(Epoch, root, false), CancellationToken.None)).Status);
            var filesystemRoot = Path.GetPathRoot(root)!;
            Assert.AreEqual(filesystemRoot, (await service.OpenProjectAsync(new(Epoch, filesystemRoot, false), CancellationToken.None)).ProjectPath);
            Assert.AreEqual("unconfigured", (await new WorkspaceService((string?)null).OpenProjectAsync(new(Epoch, root, true), CancellationToken.None)).Status);
            Assert.IsFalse(Directory.Exists(catalog.Options.ProjectsRoot));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task CanceledWait_DoesNotReleaseOriginalImportOrShutdownDrain()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-project-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var completion = new TaskCompletionSource<ProjectDescriptor>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "catalog") });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var started = 0;
            var service = new WorkspaceService(reads, catalog, Epoch, path =>
            {
                Assert.AreEqual(root, path);
                started++;
                return completion.Task;
            });
            using var cancel = new CancellationTokenSource();
            var first = service.OpenProjectAsync(new(Epoch, root, true), cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);
            Assert.AreEqual("busy", (await service.OpenProjectAsync(new(Epoch, root, true), CancellationToken.None)).Status);
            var closing = service.CloseImportsAsync();
            Assert.IsFalse(closing.IsCompleted);
            Assert.AreEqual("closed", (await service.OpenProjectAsync(new(Epoch, root, true), CancellationToken.None)).Status);
            Assert.AreEqual(1, started);
            completion.SetException(new IOException("Post-commit feedback could fail"));
            await closing;
            Assert.IsFalse(Directory.Exists(catalog.Options.ProjectsRoot));
            var attempts = 0;
            var uncertain = new WorkspaceService(reads, catalog, Epoch, _ =>
            {
                attempts++;
                return Task.FromException<ProjectDescriptor>(new IOException("Post-commit feedback could fail"));
            });
            Assert.AreEqual("import_unconfirmed", (await uncertain.OpenProjectAsync(new(Epoch, root, true), CancellationToken.None)).Status);
            Assert.AreEqual(1, attempts); // No server-side retry after a potentially admitted write.
            await uncertain.CloseImportsAsync();
        }
        finally
        {
            completion.TrySetCanceled();
            Directory.Delete(root, recursive: true);
        }
    }
}
