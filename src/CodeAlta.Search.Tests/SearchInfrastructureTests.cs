using CodeAlta.Persistence;

namespace CodeAlta.Search.Tests;

[TestClass]
public sealed class SearchInfrastructureTests
{
    [TestMethod]
    public async Task Indexer_ProcessNextAsync_IndexesDocuments()
    {
        using var temp = TempDirectory.Create();
        var (indexer, searchService) = await CreateSearchPipelineAsync(temp.Path).ConfigureAwait(false);

        await indexer.EnqueueAsync(
            new IndexingJob
            {
                Documents =
                [
                    new DocumentInput
                    {
                        SourceKind = "artifact",
                        SourceId = "artifact://wk-core/knowledge/perf",
                        WorkspaceId = "workspace-1",
                        ProjectId = "project-1",
                        Title = "Performance Notes",
                        Text = "Use Span<T> and ArrayPool<T> to reduce allocations in tight loops.",
                    },
                ],
            }).ConfigureAwait(false);

        await indexer.ProcessNextAsync().ConfigureAwait(false);

        var fts = await searchService.QueryFtsAsync(
            new SearchQuery
            {
                Text = "allocations",
                WorkspaceId = "workspace-1",
                ProjectId = "project-1",
                PrefilterLimit = 10,
            }).ConfigureAwait(false);

        Assert.AreEqual(1, fts.Count);
        Assert.AreEqual("artifact://wk-core/knowledge/perf", fts[0].SourceId);
        StringAssert.Contains(fts[0].Snippet ?? string.Empty, "allocations");
    }

    [TestMethod]
    public async Task SearchService_QueryHybridAsync_ReranksAndReturnsSourceLinks()
    {
        using var temp = TempDirectory.Create();
        var (indexer, searchService) = await CreateSearchPipelineAsync(temp.Path).ConfigureAwait(false);

        await indexer.EnqueueAsync(
            new IndexingJob
            {
                Documents =
                [
                    new DocumentInput
                    {
                        SourceKind = "artifact",
                        SourceId = "artifact://wk-core/knowledge/architecture",
                        WorkspaceId = "workspace-1",
                        ProjectId = "project-1",
                        Title = "Architecture",
                        Text = "Architecture overview for orchestration and durable state.",
                    },
                    new DocumentInput
                    {
                        SourceKind = "artifact",
                        SourceId = "artifact://wk-core/knowledge/testing",
                        WorkspaceId = "workspace-1",
                        ProjectId = "project-1",
                        Title = "Testing",
                        Text = "Testing notes for durable architecture, persistence, and search behavior.",
                    },
                ],
            }).ConfigureAwait(false);

        await indexer.ProcessNextAsync().ConfigureAwait(false);

        var results = await searchService.QueryHybridAsync(
            new SearchQuery
            {
                Text = "durable architecture",
                WorkspaceId = "workspace-1",
                ProjectId = "project-1",
                Limit = 2,
                PrefilterLimit = 10,
            }).ConfigureAwait(false);

        Assert.AreEqual(2, results.Count);
        StringAssert.StartsWith(results[0].LinkUri, "artifact://");
        Assert.IsTrue(results[0].CombinedScore >= results[1].CombinedScore);
    }

    [TestMethod]
    public async Task Indexer_Status_TracksQueueAndCompletion()
    {
        using var temp = TempDirectory.Create();
        var (indexer, _) = await CreateSearchPipelineAsync(temp.Path).ConfigureAwait(false);

        await indexer.EnqueueAsync(
            new IndexingJob
            {
                Documents =
                [
                    new DocumentInput
                    {
                        SourceKind = "artifact",
                        SourceId = "artifact://wk-core/knowledge/status",
                        WorkspaceId = "workspace-1",
                        ProjectId = "project-1",
                        Title = "Status",
                        Text = "queue depth status sample",
                    },
                ],
            }).ConfigureAwait(false);

        var pending = indexer.Status;
        Assert.AreEqual(1, pending.QueueDepth);
        Assert.IsNull(pending.LastCompletedAt);

        await indexer.ProcessNextAsync().ConfigureAwait(false);
        var done = indexer.Status;
        Assert.AreEqual(0, done.QueueDepth);
        Assert.IsNotNull(done.LastCompletedAt);
    }

    private static async Task<(Indexer Indexer, SearchService Service)> CreateSearchPipelineAsync(string rootPath)
    {
        var dbPath = Path.Combine(rootPath, "state", "db", "codealta.db");
        var db = new CodeAltaDb(
            new CodeAltaDbOptions
            {
                DatabasePath = dbPath,
            });
        await db.InitializeAsync().ConfigureAwait(false);

        var store = new DocumentIndexStore(db);
        var manager = new EmbeddingModelManager(new HashEmbedder());
        var queue = new IndexingQueue();
        var indexer = new Indexer(queue, store, manager);
        var service = new SearchService(store, manager);
        return (indexer, service);
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CodeAlta.Search.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
