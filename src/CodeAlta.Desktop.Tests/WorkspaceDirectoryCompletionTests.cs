using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class WorkspaceDirectoryCompletionTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public async Task OwnedReader_EchoesLiteralRequestAndProjectsBoundedResults()
    {
        var root = FixtureRoot();
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Alpha"));
            Directory.CreateDirectory(Path.Combine(root, "Alpine"));
            Directory.CreateDirectory(Path.Combine(root, ".AlreadyHidden"));
            File.WriteAllText(Path.Combine(root, "Alfile"), "fixture");
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "catalog") });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var service = new WorkspaceService(reads, catalog, Epoch);
            var request = new WorkspaceDirectoryCompletionRequest(Epoch, root, "Al");
            Assert.AreEqual(request, JsonSerializer.Deserialize(JsonSerializer.Serialize(request,
                DesktopJsonContext.Default.WorkspaceDirectoryCompletionRequest), DesktopJsonContext.Default.WorkspaceDirectoryCompletionRequest));
            var response = await service.CompleteDirectoryAsync(request, CancellationToken.None);
            Assert.AreEqual("incomplete", response.Status);
            Assert.AreEqual(Epoch, response.HostEpoch);
            Assert.AreEqual(root, response.DirectoryPath);
            Assert.AreEqual("Al", response.Prefix);
            CollectionAssert.AreEquivalent(new[] { Path.Combine(root, "Alpha"), Path.Combine(root, "Alpine") }, response.Directories);
            Assert.IsTrue(response.OmittedUnsafeEntries);
            Assert.AreEqual(4, response.EntriesVisited);
            var roundtrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(response,
                DesktopJsonContext.Default.WorkspaceDirectoryCompletionResponse), DesktopJsonContext.Default.WorkspaceDirectoryCompletionResponse);
            Assert.IsNotNull(roundtrip);
            Assert.AreEqual(response.Status, roundtrip.Status);
            Assert.AreEqual(response.DirectoryPath, roundtrip.DirectoryPath);
            CollectionAssert.AreEqual(response.Directories, roundtrip.Directories);
            Assert.IsTrue(WorkspaceService.DirectoryCompletionEnvelopeBytes(response) <= WorkspaceService.MaximumDirectoryCompletionEnvelopeBytes);
            Directory.Delete(Path.Combine(root, ".AlreadyHidden"));
            Assert.AreEqual("complete", (await service.CompleteDirectoryAsync(new(Epoch, root, "Alpha"), CancellationToken.None)).Status);
            var trailingDirectory = root + Path.DirectorySeparatorChar;
            var trailing = await service.CompleteDirectoryAsync(new(Epoch, trailingDirectory, "Alpha"), CancellationToken.None);
            Assert.AreEqual("complete", trailing.Status, "A canonical directory with a trailing separator must retain its valid child suggestions.");
            Assert.AreEqual(trailingDirectory, trailing.DirectoryPath, "The response must still echo the exact request.");
            CollectionAssert.AreEqual(new[] { Path.Combine(root, "Alpha") }, trailing.Directories);
            Assert.AreEqual("missing", (await service.CompleteDirectoryAsync(new(Epoch, Path.Combine(root, "missing"), ""), CancellationToken.None)).Status);
            Assert.AreEqual("not_directory", (await service.CompleteDirectoryAsync(new(Epoch, Path.Combine(root, "Alfile"), ""), CancellationToken.None)).Status);
            Assert.IsFalse(Directory.Exists(catalog.Options.ProjectsRoot));
            await service.CloseImportsAsync();
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task PreflightAndClosed_NeverInvokeReader()
    {
        var root = FixtureRoot();
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "catalog") });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var calls = 0;
            var service = new WorkspaceService(reads, catalog, Epoch, (_, _) =>
            {
                calls++;
                return Task.FromResult(new DirectoryCompletionResult(DirectoryCompletionStatus.Complete, [], 0, false));
            });
            foreach (var request in new[] { new WorkspaceDirectoryCompletionRequest("bad", root, ""),
                new(Epoch, "relative", ""), new(Epoch, Path.Combine(root, "."), ""),
                new(Epoch, Path.GetPathRoot(root)!, ""), new(Epoch, root, "a/b"),
                new(Epoch, root, "\ud800"), new(Epoch, new string('x', 1025), "") })
                Assert.AreEqual("invalid_request", (await service.CompleteDirectoryAsync(request, CancellationToken.None)).Status);
            Assert.AreEqual("stale_epoch", (await service.CompleteDirectoryAsync(new(Guid.NewGuid().ToString("D"), root, ""), CancellationToken.None)).Status);
            Assert.AreEqual("unconfigured", (await new WorkspaceService((string?)null).CompleteDirectoryAsync(new(Epoch, root, ""), CancellationToken.None)).Status);
            var catalogOnly = new WorkspaceService(Path.Combine(root, "catalog-only"));
            Assert.AreEqual("unconfigured", (await catalogOnly.CompleteDirectoryAsync(new(Epoch, root, ""), CancellationToken.None)).Status);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "catalog-only")));
            await service.CloseImportsAsync();
            Assert.AreEqual("closed", (await service.CompleteDirectoryAsync(new(Epoch, root, ""), CancellationToken.None)).Status);
            Assert.AreEqual(0, calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CanceledWaitAndClose_RetainAdmissionUntilWorkSettles()
    {
        var root = FixtureRoot();
        Directory.CreateDirectory(root);
        var pending = new TaskCompletionSource<DirectoryCompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "catalog") });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var calls = 0;
            var service = new WorkspaceService(reads, catalog, Epoch, (_, _) => { calls++; return pending.Task; });
            var request = new WorkspaceDirectoryCompletionRequest(Epoch, root, "");
            using var canceledBefore = new CancellationTokenSource();
            canceledBefore.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await service.CompleteDirectoryAsync(request, canceledBefore.Token));
            Assert.AreEqual(0, calls);
            using var canceledAfter = new CancellationTokenSource();
            var first = service.CompleteDirectoryAsync(request, canceledAfter.Token);
            canceledAfter.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);
            Assert.AreEqual("busy", (await service.CompleteDirectoryAsync(request, CancellationToken.None)).Status);
            var close = service.CloseImportsAsync();
            Assert.IsFalse(close.IsCompleted);
            Assert.AreEqual("closed", (await service.CompleteDirectoryAsync(request, CancellationToken.None)).Status);
            Assert.AreEqual(1, calls);
            pending.SetException(new IOException("private error details"));
            await close;
            Assert.IsFalse(Directory.Exists(catalog.Options.ProjectsRoot));
        }
        finally { pending.TrySetCanceled(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ProjectedChildren_RequireExactDirectParentWithOrWithoutTrailingSeparator()
    {
        var root = FixtureRoot();
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "catalog") });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var child = Path.Combine(root, "Alpha");
            var rejected = new[]
            {
                Path.Combine(root + "-sibling", "Alpha"),
                Path.Combine(root, "Nested", "Alpha"),
                Path.Combine(Path.GetDirectoryName(root)!, "foreign", "Alpha"),
            };
            foreach (var directory in new[] { root, root + Path.DirectorySeparatorChar })
            {
                var accepted = new WorkspaceService(reads, catalog, Epoch, (_, _) =>
                    Task.FromResult(new DirectoryCompletionResult(DirectoryCompletionStatus.Complete, [child], 1, false)));
                var response = await accepted.CompleteDirectoryAsync(new(Epoch, directory, "Al"), CancellationToken.None);
                Assert.AreEqual("complete", response.Status);
                Assert.AreEqual(directory, response.DirectoryPath);
                Assert.AreEqual("Al", response.Prefix);
                CollectionAssert.AreEqual(new[] { child }, response.Directories);
                await accepted.CloseImportsAsync();

                foreach (var other in rejected)
                {
                    var service = new WorkspaceService(reads, catalog, Epoch, (_, _) =>
                        Task.FromResult(new DirectoryCompletionResult(DirectoryCompletionStatus.Complete, [other], 1, false)));
                    var refusal = await service.CompleteDirectoryAsync(new(Epoch, directory, "Al"), CancellationToken.None);
                    Assert.AreEqual("read_error", refusal.Status, other);
                    Assert.AreEqual(directory, refusal.DirectoryPath);
                    Assert.AreEqual("Al", refusal.Prefix);
                    Assert.IsEmpty(refusal.Directories);
                    await service.CloseImportsAsync();
                }
            }
            Assert.IsFalse(Directory.Exists(catalog.Options.ProjectsRoot));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ActualReader_StopsAfterSentinelAndDoesNotTreatNonmatchesAsFree()
    {
        var root = FixtureRoot();
        Directory.CreateDirectory(root);
        try
        {
            for (var i = 0; i < 129; i++) File.WriteAllText(Path.Combine(root, i.ToString("D3")), "fixture");
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "catalog") });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var service = new WorkspaceService(reads, catalog, Epoch);
            var result = await service.CompleteDirectoryAsync(new(Epoch, root, "no-match"), CancellationToken.None);
            Assert.AreEqual("incomplete", result.Status);
            Assert.AreEqual(129, result.EntriesVisited);
            Assert.IsEmpty(result.Directories);
            await service.CloseImportsAsync();
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task BudgetAndFailure_AreProjectedWithoutLeakingDetails()
    {
        var root = FixtureRoot();
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Path.Combine(root, "catalog") });
            await using var reads = new OwnedSessionWorkspace(catalog, new SessionViewJournalStore(catalog.Options));
            var paths = Enumerable.Range(0, 16).Select(i => Path.Combine(root, i.ToString("D2") + new string('\u00e9', 900))).ToArray();
            var result = new DirectoryCompletionResult(DirectoryCompletionStatus.Complete, paths, 16, false);
            var service = new WorkspaceService(reads, catalog, Epoch, (_, _) => Task.FromResult(result));
            var response = await service.CompleteDirectoryAsync(new(Epoch, root, ""), CancellationToken.None);
            Assert.AreEqual("incomplete", response.Status);
            Assert.IsTrue(response.Directories.Length < 16);
            Assert.IsTrue(WorkspaceService.DirectoryCompletionEnvelopeBytes(response) <= WorkspaceService.MaximumDirectoryCompletionEnvelopeBytes);
            Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(response, DesktopJsonContext.Default.WorkspaceDirectoryCompletionResponse).Length
                < WorkspaceService.DirectoryCompletionEnvelopeBytes(response));
            Assert.IsFalse(Directory.Exists(catalog.Options.ProjectsRoot));
            var error = new WorkspaceService(reads, catalog, Epoch, (_, _) => Task.FromException<DirectoryCompletionResult>(new IOException("private error details")));
            var failure = await error.CompleteDirectoryAsync(new(Epoch, root, ""), CancellationToken.None);
            Assert.AreEqual("read_error", failure.Status);
            Assert.IsEmpty(failure.Directories);
            Assert.IsFalse(JsonSerializer.Serialize(failure, DesktopJsonContext.Default.WorkspaceDirectoryCompletionResponse).Contains("private error details", StringComparison.Ordinal));
            var denied = new WorkspaceService(reads, catalog, Epoch, (_, _) =>
                Task.FromResult(new DirectoryCompletionResult(DirectoryCompletionStatus.Denied, [], 0, false)));
            Assert.AreEqual("denied", (await denied.CompleteDirectoryAsync(new(Epoch, root, ""), CancellationToken.None)).Status);
            var unsafeResult = new WorkspaceService(reads, catalog, Epoch, (_, _) =>
                Task.FromResult(new DirectoryCompletionResult(DirectoryCompletionStatus.Complete, ["private path"], 1, false)));
            Assert.AreEqual("read_error", (await unsafeResult.CompleteDirectoryAsync(new(Epoch, root, ""), CancellationToken.None)).Status);
            await Task.WhenAll(service.CloseImportsAsync(), error.CloseImportsAsync(), denied.CloseImportsAsync(), unsafeResult.CloseImportsAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    private static string FixtureRoot() => Path.Combine(Path.GetFullPath("tmp"), "codealta-completion-" + Guid.NewGuid().ToString("N"));
}
