using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class WorkspaceArchiveTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public async Task ExactConfirmationBothTransitionsConflictsAndThrowAfterWriteStayDistinct()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-archive-rpc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "global") });
            var p = await catalog.UpsertFromPathAsync(root);
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var rpc = new WorkspaceService(reads, catalog, Epoch);
            var read = new WorkspaceArchiveProjectRequest(Epoch, p.Id, root, false, true, false, null, null);
            Assert.AreEqual("stale_epoch", (await rpc.ArchiveProjectAsync(read with { ExpectedHostEpoch = Guid.NewGuid().ToString("D") }, default)).Status);
            Assert.AreEqual("unsupported", (await rpc.ArchiveProjectAsync(read with { ProjectPath = catalog.Options.GlobalRoot }, default)).Status);
            var evidence = await rpc.ArchiveProjectAsync(read, default);
            Assert.AreEqual("confirmation_required", evidence.Status);
            var write = read with { Confirmed = true, SourcePath = evidence.SourcePath, Revision = evidence.Revision };
            Assert.AreEqual("conflict", (await rpc.ArchiveProjectAsync(write with { SourcePath = root }, default)).Status);
            Assert.AreEqual("conflict", (await rpc.ArchiveProjectAsync(write with { Revision = new string('A', 64) }, default)).Status);
            Assert.AreEqual("ok", (await rpc.ArchiveProjectAsync(write, default)).Status);
            Assert.AreEqual("conflict", (await rpc.ArchiveProjectAsync(write, default)).Status);
            evidence = await rpc.ArchiveProjectAsync(read, default);
            var unarchive = write with { ExpectedArchived = true, Archived = false, Revision = evidence.Revision };
            Assert.AreEqual("ok", (await rpc.ArchiveProjectAsync(unarchive, default)).Status);
            evidence = await rpc.ArchiveProjectAsync(read, default);
            rpc.ArchiveWriter = async (request, revision) =>
            {
                Assert.AreEqual(ProjectDisplayNameRenameStatus.Updated, await catalog.SetArchivedAsync(request.ProjectId, request.ProjectPath,
                    request.SourcePath!, revision, request.ExpectedArchived, request.Archived));
                throw new IOException("After commit");
            };
            Assert.AreEqual("archive_unconfirmed", (await rpc.ArchiveProjectAsync(write with { Revision = evidence.Revision }, default)).Status);
            Assert.IsTrue((await catalog.GetByIdAsync(p.Id))!.Archived);
            await rpc.CloseImportsAsync();
            Assert.AreEqual("closed", (await rpc.ArchiveProjectAsync(read, default)).Status);
        }
        finally { Directory.Delete(root, true); }
    }
}
