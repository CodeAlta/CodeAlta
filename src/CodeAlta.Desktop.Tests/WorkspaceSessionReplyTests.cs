using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class WorkspaceSessionReplyTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public async Task SessionCallRightAfterAReply_IsNeverRefusedBusy()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-reply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); Directory.CreateDirectory(global);
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = global });
            var source = await catalog.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "source")).FullName);
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var services = new List<WorkspaceService>();
            var refused = new List<string>();

            // Each operation ends off the caller's thread, and the answer resumes this test inline, before the worker
            // that answered has ended: its slot must already be free for the next call.
            WorkspaceService Inline(WorkspaceService service) { services.Add(service); return service; }
            async Task RepeatAsync<T>(WorkspaceService rpc, string name, string expected, Func<WorkspaceService, Task<T>> call, Func<T, string> status)
            {
                for (var i = 0; i < 200; i++)
                {
                    var actual = status(await call(rpc));
                    if (actual == expected) continue;
                    refused.Add($"{name} {i}: {actual}");
                    return;
                }
            }

            Func<string, string?, string, string, Task<bool>> rename = async (_, _, _, _) => { await Task.Yield(); return true; };
            await RepeatAsync(Inline(new WorkspaceService(reads, catalog, Epoch, rename) { CatalogReplyOptions = TaskCreationOptions.None }), "rename", "ok",
                rpc => rpc.RenameSessionAsync(new(Epoch, "global", null, global, "exact", "New"), CancellationToken.None), reply => reply.Status);

            Func<string, string?, string, string, Task<string>> delete = async (_, _, _, _) => { await Task.Yield(); return "ok"; };
            await RepeatAsync(Inline(new WorkspaceService(reads, catalog, Epoch, delete) { CatalogReplyOptions = TaskCreationOptions.None }), "delete", "ok",
                rpc => rpc.DeleteSessionAsync(new(Epoch, "global", null, global, "exact", "Title"), CancellationToken.None), reply => reply.Status);

            Func<string, string?, string, string?, string?, Task<SessionBranchOutcome>> fork = async (_, _, _, _, _) => { await Task.Yield(); return new("session_in_use"); };
            Func<string, string?, string, string, bool, Task<SessionBranchOutcome>> rewind = async (_, _, _, _, _) => { await Task.Yield(); return new("session_in_use"); };
            await RepeatAsync(Inline(new WorkspaceService(reads, catalog, Epoch, fork, rewind) { CatalogReplyOptions = TaskCreationOptions.None }), "fork", "session_in_use",
                rpc => rpc.ForkSessionAsync(new(Epoch, "global", null, global, "exact", null, null), CancellationToken.None), reply => reply.Status);
            await RepeatAsync(Inline(new WorkspaceService(reads, catalog, Epoch, fork, rewind) { CatalogReplyOptions = TaskCreationOptions.None }), "rewind", "session_in_use",
                rpc => rpc.RewindSessionAsync(new(Epoch, "global", null, global, "exact", "run", false), CancellationToken.None), reply => reply.Status);

            Func<string, string, string, string, string, Task<OwnedSessionMoveResult>> move = async (_, _, _, _, _) => { await Task.Yield(); return new("ok", [], []); };
            await RepeatAsync(Inline(new WorkspaceService(reads, catalog, Epoch, move) { CatalogReplyOptions = TaskCreationOptions.None }), "move", "ok",
                rpc => rpc.MoveSessionAsync(new(Epoch, "exact", source.Id, source.ProjectPath, "target", Path.Combine(root, "target")), CancellationToken.None),
                reply => reply.Status);

            Func<string, string, Task<OwnedSessionSpaceMoveResult>> moveToSpace = async (_, _) => { await Task.Yield(); return new("ok", [], [], []); };
            await RepeatAsync(Inline(new WorkspaceService(reads, catalog, Epoch, moveToSpace) { CatalogReplyOptions = TaskCreationOptions.None }), "move to space", "ok",
                rpc => rpc.MoveSessionToSpaceAsync(new(Epoch, "exact", SpaceDescriptor.DefaultId), CancellationToken.None), reply => reply.Status);

            Func<string, bool, bool, Task<SessionArchiveResult>> archive = async (_, _, _) => { await Task.Yield(); return new("ok", [], null); };
            await RepeatAsync(Inline(new WorkspaceService(reads, catalog, Epoch, archive) { CatalogReplyOptions = TaskCreationOptions.None }), "archive", "ok",
                rpc => rpc.ArchiveSessionAsync(new(Epoch, "exact", false), CancellationToken.None), reply => reply.Status);
            await RepeatAsync(Inline(new WorkspaceService(reads, catalog, Epoch, archive) { CatalogReplyOptions = TaskCreationOptions.None }), "restore", "ok",
                rpc => rpc.RestoreSessionAsync(new(Epoch, "exact", false), CancellationToken.None), reply => reply.Status);

            Func<IReadOnlyCollection<string>, Task<IReadOnlySet<string>>> atWork = _ => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
            Func<string, string?, string, string, IReadOnlyCollection<string>, Task<OwnedSessionTreeDeletion>> deleteTree = async (_, _, _, _, _) =>
            {
                await Task.Yield(); return new("ok", [], null);
            };
            await RepeatAsync(Inline(new WorkspaceService(reads, catalog, Epoch, atWork, deleteTree) { CatalogReplyOptions = TaskCreationOptions.None }), "delete tree", "ok",
                rpc => rpc.DeleteSessionTreeAsync(new(Epoch, "global", null, global, "exact", "Title", []), CancellationToken.None), reply => reply.Status);

            Func<string, string, Task<DirectoryCompletionResult>> complete = async (_, _) =>
            {
                await Task.Yield(); return new(DirectoryCompletionStatus.Complete, [], 0, false);
            };
            await RepeatAsync(Inline(new WorkspaceService(reads, catalog, Epoch, complete) { CatalogReplyOptions = TaskCreationOptions.None }), "complete directory", "complete",
                rpc => rpc.CompleteDirectoryAsync(new(Epoch, root, ""), CancellationToken.None), reply => reply.Status);

            foreach (var service in services)
            {
                await service.CloseSessionsAsync();
                await service.CloseImportsAsync();
            }
            Assert.AreEqual(0, refused.Count, string.Join("; ", refused));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
