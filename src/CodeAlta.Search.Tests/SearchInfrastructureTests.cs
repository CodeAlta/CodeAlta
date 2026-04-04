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
                        SourceId = "artifact://project-1/knowledge/perf",
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
                ProjectId = "project-1",
                PrefilterLimit = 10,
            }).ConfigureAwait(false);

        Assert.AreEqual(1, fts.Count);
        Assert.AreEqual("artifact://project-1/knowledge/perf", fts[0].SourceId);
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
                        SourceId = "artifact://project-1/knowledge/architecture",
                        ProjectId = "project-1",
                        Title = "Architecture",
                        Text = "Architecture overview for orchestration and durable state.",
                    },
                    new DocumentInput
                    {
                        SourceKind = "artifact",
                        SourceId = "artifact://project-1/knowledge/testing",
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
                        SourceId = "artifact://project-1/knowledge/status",
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

    [TestMethod]
    public async Task SqliteVec_WhenExtensionAvailable_IndexesVecTable()
    {
        var extensionPath = Environment.GetEnvironmentVariable("CODEALTA_SQLITE_VEC_EXTENSION_PATH");
        if (string.IsNullOrWhiteSpace(extensionPath) || !File.Exists(extensionPath))
        {
            Assert.Inconclusive("Set CODEALTA_SQLITE_VEC_EXTENSION_PATH to a valid sqlite-vec extension path to run this test.");
        }

        using var temp = TempDirectory.Create();
        var dbPath = Path.Combine(temp.Path, "state", "db", "codealta.db");
        var db = new CodeAltaDb(
            new CodeAltaDbOptions
            {
                DatabasePath = dbPath,
                SqliteVecExtensionPath = extensionPath,
                RequireSqliteVec = false,
            });
        await db.InitializeAsync().ConfigureAwait(false);

        var store = new DocumentIndexStore(db);
        var manager = new EmbeddingModelManager(new HashEmbedder());
        var queue = new IndexingQueue();
        var indexer = new Indexer(queue, store, manager);

        await indexer.EnqueueAsync(
            new IndexingJob
            {
                Documents =
                [
                    new DocumentInput
                    {
                        SourceKind = "artifact",
                        SourceId = "artifact://project-1/knowledge/vec",
                        ProjectId = "project-1",
                        Title = "Vec Fixture",
                        Text = "sqlite-vec fixture document",
                    },
                ],
            }).ConfigureAwait(false);

        await indexer.ProcessNextAsync().ConfigureAwait(false);

        try
        {
            await using var connection = await db.CreateOpenConnectionAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM document_embeddings_vec;";
            var count = (long)(await command.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
            Assert.AreEqual(1, count);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Assert.Inconclusive($"sqlite-vec extension was not usable: {ex.Message}");
        }
    }

    [TestMethod]
    public async Task ProjectFileSnapshotCache_PreservesSnapshotWhileDirty()
    {
        var cache = new ProjectFileSnapshotCache();
        var item = new ProjectFileSearchItem
        {
            Kind = ProjectFileSearchItemKind.File,
            ProjectRoot = @"C:\repo",
            RelativePath = "src/App.cs",
            FullPath = @"C:\repo\src\App.cs",
            Basename = "App.cs",
            ParentPath = "src",
            Extension = ".cs",
            SearchFields = new ProjectFileSearchFields("app.cs", "src/app.cs", ["src", "app.cs"], ".cs"),
        };

        await cache.SetAsync(
            new ProjectFileSnapshot
            {
                ProjectRoot = @"C:\repo",
                IsGitAware = true,
                SnapshotGeneration = 3,
                BuiltAt = DateTimeOffset.UtcNow,
                Items = [item],
            }).ConfigureAwait(false);
        await cache.MarkDirtyAsync(@"C:\repo", ProjectFileInvalidationReason.FileSystemWrite).ConfigureAwait(false);

        var entry = await cache.GetAsync(@"C:\repo").ConfigureAwait(false);
        Assert.IsNotNull(entry);
        Assert.IsTrue(entry.IsDirty);
        Assert.IsNotNull(entry.Snapshot);
        Assert.AreEqual(3L, entry.Snapshot.SnapshotGeneration);
        Assert.AreEqual(ProjectFileInvalidationReason.FileSystemWrite, entry.LastInvalidationReason);
    }

    [TestMethod]
    public async Task PersistentProjectFileUsageStore_RecordsAndLoadsTypedUsage()
    {
        using var temp = TempDirectory.Create();
        var dbPath = Path.Combine(temp.Path, "state", "db", "codealta.db");
        var db = new CodeAltaDb(
            new CodeAltaDbOptions
            {
                DatabasePath = dbPath,
            });
        await db.InitializeAsync().ConfigureAwait(false);

        var repository = new ProjectFileUsageRepository(db);
        var store = new PersistentProjectFileUsageStore(repository);
        await store.RecordAsync(
            new ProjectFileUsageEvent(
                temp.Path,
                "src/CodeAlta/App.cs",
                ProjectFileSearchItemKind.File,
                DateTimeOffset.UtcNow,
                ProjectFileUsageAccessKind.PopupAccepted)).ConfigureAwait(false);

        var recent = await store.GetRecentAsync(temp.Path, limit: 5).ConfigureAwait(false);
        Assert.AreEqual(1, recent.Count);
        Assert.AreEqual(ProjectFileSearchItemKind.File, recent[0].Kind);
        Assert.AreEqual(ProjectFileUsageAccessKind.PopupAccepted, recent[0].LastAccessKind);

        var byPath = await store.GetUsageByRelativePathAsync(temp.Path).ConfigureAwait(false);
        Assert.IsTrue(byPath.TryGetValue("src/CodeAlta/App.cs", out var entry));
        Assert.AreEqual(1L, entry.AccessCount);
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
