using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Orchestration;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Persistence;

namespace CodeAlta.Tests;

[TestClass]
public sealed class KnownProjectImporterTests
{
    [TestMethod]
    public async Task ImportAsync_MarksSkippedFailedProvidersAsComplete()
    {
        using var temp = TempDirectory.Create();
        var backendId = new AgentBackendId("failed");
        var createBackendCount = 0;
        var backendFactory = new AgentBackendFactory();
        backendFactory.Register(
            backendId,
            () =>
            {
                createBackendCount++;
                throw new InvalidOperationException("The failed provider should be skipped.");
            });

        var db = new CodeAltaDb(new CodeAltaDbOptions { DatabasePath = Path.Combine(temp.Path, "state.db") });
        await db.InitializeAsync().ConfigureAwait(false);
        await using var hub = new AgentHub(backendFactory, new AgentRepository(db));
        var importer = new KnownProjectImporter(
            hub,
            [new AgentBackendDescriptor(backendId, "Failed Provider")],
            new ProjectCatalog(new CatalogOptions { GlobalRoot = temp.Path }))
        {
            ShouldLoadProviderSessions = _ => false,
        };
        var progress = new List<ProviderSessionLoadProgress>();

        await importer.ImportAsync(progress.Add, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(0, createBackendCount);
        Assert.AreEqual(2, progress.Count);
        Assert.AreEqual(0, progress[0].CompletedProviderCount);
        Assert.AreEqual(1, progress[1].CompletedProviderCount);
        Assert.AreEqual(0, progress[1].LoadingProviderDisplayNames.Count);
    }

    [TestMethod]
    public async Task ImportAsync_LoadsSharedLocalRuntimeSessionsOnce()
    {
        using var temp = TempDirectory.Create();
        var catalogOptions = new CatalogOptions { GlobalRoot = Path.Combine(temp.Path, ".alta") };
        var projectPath = Path.Combine(temp.Path, "repo-main");
        Directory.CreateDirectory(projectPath);

        var store = new FileSystemLocalAgentSessionStore(new LocalAgentRuntimePathLayout(catalogOptions.GlobalRoot));
        await store.UpsertSessionAsync(new LocalAgentSessionSummary
        {
            SessionId = "local-session-1",
            BackendId = new AgentBackendId("provider-b"),
            ProtocolFamily = "openai-responses",
            ProviderKey = "provider-b",
            WorkingDirectory = projectPath,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTimeOffset.UtcNow,
        }).ConfigureAwait(false);

        var providerA = new SharedSessionStoreBackend(new AgentBackendId("provider-a"));
        var providerB = new SharedSessionStoreBackend(new AgentBackendId("provider-b"));
        var backendFactory = new AgentBackendFactory();
        backendFactory.Register(providerA.BackendId, () => providerA);
        backendFactory.Register(providerB.BackendId, () => providerB);

        var db = new CodeAltaDb(new CodeAltaDbOptions { DatabasePath = Path.Combine(temp.Path, "state.db") });
        await db.InitializeAsync().ConfigureAwait(false);
        await using var hub = new AgentHub(backendFactory, new AgentRepository(db));
        var projectCatalog = new ProjectCatalog(catalogOptions);
        var importer = new KnownProjectImporter(
            hub,
            [
                new AgentBackendDescriptor(providerA.BackendId, "Provider A"),
                new AgentBackendDescriptor(providerB.BackendId, "Provider B"),
            ],
            projectCatalog,
            catalogOptions);

        await importer.ImportAsync(CancellationToken.None).ConfigureAwait(false);

        var projects = await projectCatalog.LoadAsync().ConfigureAwait(false);
        Assert.AreEqual(1, projects.Count);
        Assert.AreEqual(0, providerA.ListSessionsCount);
        Assert.AreEqual(0, providerB.ListSessionsCount);
    }

    private sealed class SharedSessionStoreBackend(AgentBackendId backendId) : IAgentBackend, IAgentSharedSessionMetadataBackend
    {
        public AgentBackendId BackendId { get; } = backendId;

        public string DisplayName => BackendId.Value;

        public int ListSessionsCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>([]);

        public Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(
            AgentSessionListFilter? filter = null,
            CancellationToken cancellationToken = default)
        {
            ListSessionsCount++;
            return Task.FromResult<IReadOnlyList<AgentSessionMetadata>>([]);
        }

        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IAgentSession> ResumeSessionAsync(
            string sessionId,
            AgentSessionResumeOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codealta-importer-tests-{Guid.NewGuid():N}");
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
