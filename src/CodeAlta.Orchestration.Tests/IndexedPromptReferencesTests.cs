using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.Prompts;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class IndexedPromptReferencesTests
{
    [TestMethod]
    public async Task Search_RanksIndexedFilesFuzzilyHonorsIgnoreFilesAndReportsTheIndexSize()
    {
        var root = Directory.CreateTempSubdirectory("codealta-references-").FullName;
        try
        {
            foreach (var file in new[] { "README.md", "src/app.ts", "src/deep/a/b/c/d/e/f/Nested.cs", "src/components/Button.tsx", ".config/tool.json", "ignored/secret.txt" })
            {
                var path = Path.Combine(root, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, file);
            }
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "ignored/\n");
            await using var references = new IndexedPromptReferences(new ProjectFileSearchService(new ProjectFileSnapshotCache(), new InMemoryProjectFileUsageStore()));

            var all = await Settled(references, root, "");
            Assert.AreEqual("ok", all.Status);
            Assert.IsTrue(all.Indexed >= 6, $"indexed {all.Indexed}");
            Assert.IsFalse(all.Items.Any(item => item.Path.StartsWith("ignored", StringComparison.Ordinal)), "ignore files are honored");
            Assert.IsTrue(all.Items.Any(item => item.Path == "src" && item.Directory), "folders are listed");
            Assert.IsTrue(all.Items.All(item => !item.Path.Contains('\\')), "paths use forward slashes");

            // A subsequence of the base name finds a file the bounded scan (depth 6) never reaches.
            var fuzzy = await Settled(references, root, "nstd");
            Assert.AreEqual("src/deep/a/b/c/d/e/f/Nested.cs", fuzzy.Items[0].Path);
            Assert.AreEqual("src/components/Button.tsx", (await Settled(references, root, "button")).Items[0].Path);
            // Dot folders are searchable, unlike the bounded scan.
            Assert.AreEqual(".config/tool.json", (await Settled(references, root, "tool.json")).Items[0].Path);
            Assert.AreEqual(0, (await Settled(references, root, "zzzz-no-such-file")).Items.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task Search_AfterDisposalReportsClosedAndCancellationOnlyStopsTheWait()
    {
        var root = Directory.CreateTempSubdirectory("codealta-references-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), "1");
            var references = new IndexedPromptReferences(new ProjectFileSearchService(new ProjectFileSnapshotCache(), new InMemoryProjectFileUsageStore()));
            Assert.AreEqual("one.txt", (await Settled(references, root, "one")).Items[0].Path);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => references.SearchAsync(root, "one", canceled.Token));
            Assert.AreEqual("one.txt", (await Settled(references, root, "one")).Items[0].Path);
            await references.DisposeAsync();
            Assert.AreEqual("closed", (await references.SearchAsync(root, "one", CancellationToken.None)).Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task Search_CanceledWhileTheFirstTraversalRunsStillFinishesTheIndex()
    {
        var root = Directory.CreateTempSubdirectory("codealta-references-").FullName;
        try
        {
            var traversal = new GatedTraversal();
            await using var references = new IndexedPromptReferences(new ProjectFileSearchService(
                new ProjectFileSnapshotCache(), new InMemoryProjectFileUsageStore(), traversal, new ProjectFileSearchScorer()));
            using var canceled = new CancellationTokenSource();
            var first = references.SearchAsync(root, "one", canceled.Token);
            Assert.IsTrue(traversal.Entered.Wait(TimeSpan.FromSeconds(10)), "the first search starts reading the folder");
            // The picker cancels its request when the query changes or the popup closes.
            canceled.Cancel();
            try { await first; } catch (OperationCanceledException) { }
            traversal.Release.Set();

            var result = await references.SearchAsync(root, "one", CancellationToken.None);
            for (var attempt = 0; attempt < 20 && result.Status == "indexing"; attempt++)
            {
                await Task.Delay(25);
                result = await references.SearchAsync(root, "one", CancellationToken.None);
            }
            Assert.AreEqual("ok", result.Status, "the index outlives the request that started it");
            Assert.AreEqual("one.txt", result.Items[0].Path);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // The first answers arrive while the folder is still being read; ask again until the index settles.
    private static async Task<OwnedReferenceSearchResult> Settled(IndexedPromptReferences references, string root, string query)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var result = await references.SearchAsync(root, query, CancellationToken.None);
            if (result.Status != "indexing") return result;
            await Task.Delay(25);
        }
        Assert.Fail("The index did not settle.");
        throw new InvalidOperationException();
    }

    // Reads one file, but only once released; like the real traversal it stops when its token is canceled.
    private sealed class GatedTraversal : IProjectFileSearchTraversal
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public ProjectFileTraversalSnapshot Traverse(string projectRoot, IReadOnlyDictionary<string, ProjectFileUsageEntry> usageByRelativePath,
            int batchSize, Action<IReadOnlyList<ProjectFileSearchItem>> onBatch, CancellationToken cancellationToken)
        {
            Entered.Set();
            Release.Wait(cancellationToken);
            ProjectFileSearchItem[] items =
            [
                new()
                {
                    Kind = ProjectFileSearchItemKind.File, ProjectRoot = projectRoot, RelativePath = "one.txt",
                    FullPath = Path.Combine(projectRoot, "one.txt"), Basename = "one.txt", ParentPath = string.Empty, Extension = ".txt",
                    SearchFields = ProjectFilePathUtilities.CreateSearchFields("one.txt", "one.txt", ".txt"),
                },
            ];
            onBatch(items);
            return new ProjectFileTraversalSnapshot(false, items);
        }
    }
}
