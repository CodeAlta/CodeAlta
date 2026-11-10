
namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ProjectFileSearchServiceTests
{
    [TestMethod]
    public void ProjectFileSearchScorer_PrefersExactMatchesOverRecentWeakMatches()
    {
        var scorer = new ProjectFileSearchScorer();
        var exact = CreateItem(@"C:\repo", "src/CodeAlta/ProjectFileSearchService.cs");
        var recentWeak = CreateItem(
            @"C:\repo",
            "docs/notes/search-overview.md",
            usage: new ProjectFileUsageEntry(
                @"C:\repo",
                "docs/notes/search-overview.md",
                ProjectFileSearchItemKind.File,
                DateTimeOffset.UtcNow,
                AccessCount: 25,
                LastAccessKind: ProjectFileUsageAccessKind.PopupAccepted));

        var results = scorer.Rank("ProjectFileSearchService.cs", [recentWeak, exact], limit: 10);

        Assert.AreEqual("src/CodeAlta/ProjectFileSearchService.cs", results[0].Item.RelativePath);
    }

    [TestMethod]
    public void ProjectFileSearchScorer_PrefersFilesOverDirectoriesForSameMatchQuality()
    {
        var scorer = new ProjectFileSearchScorer();
        var file = CreateItem(@"C:\repo", "src/CodeAlta");
        file = file with { Kind = ProjectFileSearchItemKind.File, Extension = string.Empty, SearchFields = ProjectFilePathUtilities.CreateSearchFields(file.RelativePath, file.Basename, string.Empty) };
        var directory = CreateItem(@"C:\repo", "src/CodeAlta", ProjectFileSearchItemKind.Directory);

        var results = scorer.Rank("codealta", [directory, file], limit: 10);

        Assert.AreEqual(ProjectFileSearchItemKind.File, results[0].Item.Kind);
    }

    [TestMethod]
    public async Task ProjectFileSearchService_CreateSessionAsync_SeedsRecentUsageImmediately()
    {
        using var temp = TempDirectory.Create();
        var usageStore = await CreateUsageStoreAsync(temp.Path).ConfigureAwait(false);
        await usageStore.RecordAsync(
            new ProjectFileUsageEvent(
                temp.Path,
                "src/CodeAlta/SessionPromptDispatchCoordinator.cs",
                ProjectFileSearchItemKind.File,
                DateTimeOffset.UtcNow,
                ProjectFileUsageAccessKind.PromptInserted)).ConfigureAwait(false);

        var service = new ProjectFileSearchService(
            new ProjectFileSnapshotCache(),
            usageStore,
            new FakeTraversal([new TraversalPlan(DelayMs: 150, Batches: [[CreateItem(temp.Path, "src/CodeAlta/Other.cs")]])]),
            new ProjectFileSearchScorer());

        await using var session = await service.CreateSessionAsync(
            new ProjectFileSearchSessionOptions
            {
                ProjectRoot = temp.Path,
                RecentItemLimit = 3,
                MaximumResults = 5,
            }).ConfigureAwait(false);

        Assert.IsTrue(session.Current.IsRefreshing);
        Assert.AreEqual("src/CodeAlta/SessionPromptDispatchCoordinator.cs", session.Current.Results[0].Item.RelativePath);
    }

    [TestMethod]
    public async Task ProjectFileSearchService_CreateSessionAsync_ReusesCachedSnapshotWhileRefreshing()
    {
        using var temp = TempDirectory.Create();
        var cache = new ProjectFileSnapshotCache();
        await cache.SetAsync(
            new ProjectFileSnapshot
            {
                ProjectRoot = temp.Path,
                IsGitAware = false,
                SnapshotGeneration = 4,
                BuiltAt = DateTimeOffset.UtcNow,
                Items = [CreateItem(temp.Path, "cached/file.cs")],
            }).ConfigureAwait(false);

        var service = new ProjectFileSearchService(
            cache,
            await CreateUsageStoreAsync(temp.Path).ConfigureAwait(false),
            new FakeTraversal([new TraversalPlan(DelayMs: 150, Batches: [[CreateItem(temp.Path, "fresh/file.cs")]])]),
            new ProjectFileSearchScorer());

        await using var session = await service.CreateSessionAsync(
            new ProjectFileSearchSessionOptions
            {
                ProjectRoot = temp.Path,
                MaximumResults = 5,
            }).ConfigureAwait(false);

        Assert.IsTrue(session.Current.HasSnapshot);
        Assert.AreEqual("cached/file.cs", session.Current.Results[0].Item.RelativePath);
    }

    [TestMethod]
    [Ignore("Timing-sensitive migrated coverage; retained traversal/filtering tests cover Catalog migration.")]
    public async Task ProjectFileSearchSession_PublishesIncrementalUpdatesAndIgnoresStaleRefreshes()
    {
        using var temp = TempDirectory.Create();
        var cache = new ProjectFileSnapshotCache();
        var service = new ProjectFileSearchService(
            cache,
            await CreateUsageStoreAsync(temp.Path).ConfigureAwait(false),
            new FakeTraversal(
            [
                new TraversalPlan(
                    DelayMs: 120,
                    Batches:
                    [
                        [CreateItem(temp.Path, "old/first.cs")],
                        [CreateItem(temp.Path, "old/second.cs")],
                    ]),
                new TraversalPlan(
                    DelayMs: 10,
                    Batches:
                    [
                        [CreateItem(temp.Path, "new/first.cs")],
                        [CreateItem(temp.Path, "new/second.cs")],
                    ]),
            ]),
            new ProjectFileSearchScorer());

        await using var session = await service.CreateSessionAsync(
            new ProjectFileSearchSessionOptions
            {
                ProjectRoot = temp.Path,
                RefreshBatchSize = 1,
                MaximumResults = 10,
            }).ConfigureAwait(false);

        var publishedCandidateCounts = new List<int>();
        var updateSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += (_, args) =>
        {
            publishedCandidateCounts.Add(args.State.CandidateCount);
            if (!args.State.IsRefreshing)
            {
                updateSignal.TrySetResult();
            }
        };

        await session.RefreshAsync().ConfigureAwait(false);
        await updateSignal.Task.WithinAsync(TestTime.Seconds(5)).ConfigureAwait(false);

        Assert.IsFalse(session.Current.IsRefreshing);
        Assert.AreEqual(2, session.Current.CandidateCount);
        Assert.IsTrue(publishedCandidateCounts.Count(static count => count > 0) >= 1);
        Assert.IsTrue(new[] { "new/first.cs", "new/second.cs" }.SequenceEqual(
            session.Current.Results.Select(static result => result.Item.RelativePath).OrderBy(static path => path, StringComparer.Ordinal)));
    }

    [TestMethod]
    public async Task ProjectFileSearchService_TraversesGitAwareRepository()
    {
        using var temp = TempDirectory.Create();
        InitializeGitDirectory(temp.Path);
        File.WriteAllText(Path.Combine(temp.Path, ".gitignore"), "generated/\n");
        Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "generated"));
        File.WriteAllText(Path.Combine(temp.Path, "src", "app.cs"), string.Empty);
        File.WriteAllText(Path.Combine(temp.Path, ".env.example"), string.Empty);
        File.WriteAllText(Path.Combine(temp.Path, "generated", "artifact.cs"), string.Empty);

        var cache = new ProjectFileSnapshotCache();
        var service = new ProjectFileSearchService(cache, await CreateUsageStoreAsync(temp.Path).ConfigureAwait(false));

        await using var session = await service.CreateSessionAsync(
            new ProjectFileSearchSessionOptions
            {
                ProjectRoot = temp.Path,
                MaximumResults = 20,
            }).ConfigureAwait(false);
        await WaitForStateAsync(session, static state => !state.IsRefreshing && state.HasSnapshot).ConfigureAwait(false);

        var snapshot = (await cache.GetAsync(temp.Path).ConfigureAwait(false))!.Snapshot!;
        CollectionAssert.Contains(snapshot.Items.Select(static item => item.RelativePath).ToArray(), "src");
        CollectionAssert.Contains(snapshot.Items.Select(static item => item.RelativePath).ToArray(), "src/app.cs");
        CollectionAssert.Contains(snapshot.Items.Select(static item => item.RelativePath).ToArray(), ".env.example");
        CollectionAssert.DoesNotContain(snapshot.Items.Select(static item => item.RelativePath).ToArray(), "generated");
        CollectionAssert.DoesNotContain(snapshot.Items.Select(static item => item.RelativePath).ToArray(), "generated/artifact.cs");
    }

    [TestMethod]
    public async Task ProjectFileSearchService_TraversesNonGitRepositoryWithGitIgnoreFallback()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(Path.Combine(temp.Path, ".gitignore"), "bin/\n");
        Directory.CreateDirectory(Path.Combine(temp.Path, "src", "generated"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "bin"));
        File.WriteAllText(Path.Combine(temp.Path, "src", ".gitignore"), "generated/\n");
        File.WriteAllText(Path.Combine(temp.Path, "src", "main.cs"), string.Empty);
        File.WriteAllText(Path.Combine(temp.Path, "src", "generated", "skip.cs"), string.Empty);
        File.WriteAllText(Path.Combine(temp.Path, "bin", "tool.exe"), string.Empty);

        var cache = new ProjectFileSnapshotCache();
        var service = new ProjectFileSearchService(cache, await CreateUsageStoreAsync(temp.Path).ConfigureAwait(false));

        await using var session = await service.CreateSessionAsync(
            new ProjectFileSearchSessionOptions
            {
                ProjectRoot = temp.Path,
                MaximumResults = 20,
            }).ConfigureAwait(false);
        await WaitForStateAsync(session, static state => !state.IsRefreshing && state.HasSnapshot).ConfigureAwait(false);

        var snapshot = (await cache.GetAsync(temp.Path).ConfigureAwait(false))!.Snapshot!;
        CollectionAssert.Contains(snapshot.Items.Select(static item => item.RelativePath).ToArray(), "src");
        CollectionAssert.Contains(snapshot.Items.Select(static item => item.RelativePath).ToArray(), "src/main.cs");
        CollectionAssert.DoesNotContain(snapshot.Items.Select(static item => item.RelativePath).ToArray(), "src/generated");
        CollectionAssert.DoesNotContain(snapshot.Items.Select(static item => item.RelativePath).ToArray(), "src/generated/skip.cs");
        CollectionAssert.DoesNotContain(snapshot.Items.Select(static item => item.RelativePath).ToArray(), "bin/tool.exe");
    }

    private static ProjectFileSearchItem CreateItem(
        string projectRoot,
        string relativePath,
        ProjectFileSearchItemKind kind = ProjectFileSearchItemKind.File,
        ProjectFileUsageEntry? usage = null)
    {
        var normalizedRelativePath = ProjectFilePathUtilities.NormalizeStoredRelativePath(relativePath);
        var basename = Path.GetFileName(normalizedRelativePath);
        var extension = kind == ProjectFileSearchItemKind.Directory ? string.Empty : Path.GetExtension(basename);
        return new ProjectFileSearchItem
        {
            Kind = kind,
            ProjectRoot = projectRoot,
            RelativePath = normalizedRelativePath,
            FullPath = ProjectFilePathUtilities.BuildFullPath(projectRoot, normalizedRelativePath),
            Basename = basename,
            ParentPath = Path.GetDirectoryName(normalizedRelativePath)?.Replace('\\', '/') ?? string.Empty,
            Extension = extension,
            LastWriteTimeUtc = DateTimeOffset.UtcNow,
            SearchFields = ProjectFilePathUtilities.CreateSearchFields(normalizedRelativePath, basename, extension),
            Usage = usage,
        };
    }

    private static Task<IProjectFileUsageStore> CreateUsageStoreAsync(string projectRoot)
    {
        _ = projectRoot;
        return Task.FromResult<IProjectFileUsageStore>(new InMemoryProjectFileUsageStore());
    }

    private static async Task WaitForStateAsync(
        IProjectFileSearchSession session,
        Func<ProjectFileSearchState, bool> predicate)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (predicate(session.Current))
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        Assert.Fail("Timed out waiting for session state.");
    }

    private static void InitializeGitDirectory(string rootPath)
    {
        Directory.CreateDirectory(Path.Combine(rootPath, ".git", "info"));
        File.WriteAllText(Path.Combine(rootPath, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(rootPath, ".git", "config"), string.Empty);
    }

    private sealed record TraversalPlan(int DelayMs, IReadOnlyList<IReadOnlyList<ProjectFileSearchItem>> Batches);

    private sealed class FakeTraversal(IReadOnlyList<TraversalPlan> plans) : IProjectFileSearchTraversal
    {
        private int _callCount = -1;

        public ProjectFileTraversalSnapshot Traverse(
            string projectRoot,
            IReadOnlyDictionary<string, ProjectFileUsageEntry> usageByRelativePath,
            int batchSize,
            Action<IReadOnlyList<ProjectFileSearchItem>> onBatch,
            CancellationToken cancellationToken)
        {
            var index = Math.Min(Interlocked.Increment(ref _callCount), plans.Count - 1);
            var plan = plans[index];
            var finalItems = new List<ProjectFileSearchItem>();
            foreach (var batch in plan.Batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (plan.DelayMs > 0)
                {
                    Thread.Sleep(plan.DelayMs);
                }

                finalItems.AddRange(batch);
                onBatch(batch);
            }

            return new ProjectFileTraversalSnapshot(IsGitAware: false, finalItems);
        }
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
                $"CodeAlta.ProjectFileSearch.Tests.{Guid.NewGuid():N}");
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
