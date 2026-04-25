using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Persistence;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ChatBackendInitializationCoordinatorTests
{
    [TestMethod]
    public async Task InitializeAsync_SkipsLoadedCodexBackend()
    {
        using var temp = TempDirectory.Create();
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var backendFactory = new AgentBackendFactory();
        var backend = new CountingBackend(AgentBackendIds.Codex);
        var factoryCreateCount = 0;
        backendFactory.Register(
            AgentBackendIds.Codex,
            () =>
            {
                factoryCreateCount++;
                return backend;
            });

        await using var hub = new AgentHub(backendFactory, new AgentRepository(db));
        var state = new ChatBackendState(AgentBackendIds.Codex, "Codex")
        {
            Availability = ChatBackendAvailability.Ready,
            StatusMessage = "Ready",
        };
        state.Models.Add(new AgentModelInfo("gpt-5"));

        var coordinator = CreateCoordinator(
            hub,
            [new AgentBackendDescriptor(AgentBackendIds.Codex, "Codex")],
            new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase)
            {
                [AgentBackendIds.Codex.Value] = state,
            });

        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(0, factoryCreateCount);
        Assert.AreEqual(0, backend.StartCount);
        Assert.AreEqual(0, backend.ListModelsCount);
        Assert.AreEqual(ChatBackendAvailability.Ready, state.Availability);
        Assert.AreEqual(1, state.Models.Count);
    }

    [TestMethod]
    public async Task InitializeAsync_RefreshesLoadedNonProcessBackedBackend()
    {
        using var temp = TempDirectory.Create();
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var backendId = new AgentBackendId("openai");
        var backendFactory = new AgentBackendFactory();
        var backend = new CountingBackend(backendId);
        backendFactory.Register(backendId, () => backend);

        await using var hub = new AgentHub(backendFactory, new AgentRepository(db));
        var state = new ChatBackendState(backendId, "OpenAI")
        {
            Availability = ChatBackendAvailability.Ready,
            StatusMessage = "Ready",
        };
        state.Models.Add(new AgentModelInfo("old-model"));

        var coordinator = CreateCoordinator(
            hub,
            [new AgentBackendDescriptor(backendId, "OpenAI")],
            new Dictionary<string, ChatBackendState>(StringComparer.OrdinalIgnoreCase)
            {
                [backendId.Value] = state,
            });

        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, backend.StartCount);
        Assert.AreEqual(1, backend.ListModelsCount);
        Assert.AreEqual(ChatBackendAvailability.Ready, state.Availability);
        CollectionAssert.AreEqual(new[] { "new-model" }, state.Models.Select(static model => model.Id).ToArray());
    }

    private static ChatBackendInitializationCoordinator CreateCoordinator(
        AgentHub hub,
        IReadOnlyList<AgentBackendDescriptor> descriptors,
        Dictionary<string, ChatBackendState> states)
        => new(
            hub,
            descriptors,
            states,
            static action => action(),
            static () => { });

    private static async Task<CodeAltaDb> CreateDbAsync(string rootPath)
    {
        var dbPath = Path.Combine(rootPath, "state", "db", "codealta.db");
        var db = new CodeAltaDb(new CodeAltaDbOptions { DatabasePath = dbPath });
        await db.InitializeAsync().ConfigureAwait(false);
        return db;
    }

    private sealed class CountingBackend(AgentBackendId backendId) : IAgentBackend
    {
        public AgentBackendId BackendId { get; } = backendId;

        public string DisplayName => BackendId.Value;

        public int StartCount { get; private set; }

        public int ListModelsCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
        {
            ListModelsCount++;
            return Task.FromResult<IReadOnlyList<AgentModelInfo>>([new AgentModelInfo("new-model")]);
        }

        public Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(
            AgentSessionListFilter? filter = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentSessionMetadata>>([]);

        public Task<IAgentSession> CreateSessionAsync(
            AgentSessionCreateOptions options,
            CancellationToken cancellationToken = default)
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
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codealta-chatbackend-tests-{Guid.NewGuid():N}");
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
