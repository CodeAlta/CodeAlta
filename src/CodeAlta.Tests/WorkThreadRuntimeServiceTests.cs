using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Roles;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Persistence;

namespace CodeAlta.Tests;

[TestClass]
public sealed class WorkThreadRuntimeServiceTests
{
    [TestMethod]
    public async Task EnsureCoordinatorSessionAsync_ReplacesUnstartedProviderManagedDraftInsteadOfResuming()
    {
        using var temp = TempDirectory.Create();
        var backend = new RecordingBackend(AgentBackendIds.Codex);
        await using var hub = await CreateHubAsync(temp.Path, backend).ConfigureAwait(false);
        var runtimeService = CreateRuntimeService(temp.Path, hub);
        var initialOptions = CreateExecutionOptions(backend.BackendId, temp.Path, model: "first-model");
        var replacementOptions = CreateExecutionOptions(backend.BackendId, temp.Path, model: "second-model");

        var thread = await runtimeService.CreateGlobalThreadAsync(initialOptions, title: "Draft").ConfigureAwait(false);
        var originalThreadId = thread.ThreadId;
        await runtimeService.EnsureCoordinatorSessionAsync(thread, replacementOptions).ConfigureAwait(false);

        Assert.AreEqual(2, backend.CreateSessionCount);
        Assert.AreEqual(0, backend.ResumeSessionCount);
        Assert.AreEqual("codex-session-2", thread.BackendSessionId);
        Assert.AreEqual("codex:codex-session-2", thread.ThreadId);
        Assert.AreNotEqual(originalThreadId, thread.ThreadId);
    }

    [TestMethod]
    public async Task EnsureCoordinatorSessionAsync_ResumesStartedProviderManagedThreadWhenOptionsChange()
    {
        using var temp = TempDirectory.Create();
        var backend = new RecordingBackend(AgentBackendIds.Codex);
        await using var hub = await CreateHubAsync(temp.Path, backend).ConfigureAwait(false);
        var runtimeService = CreateRuntimeService(temp.Path, hub);
        var initialOptions = CreateExecutionOptions(backend.BackendId, temp.Path, model: "first-model");
        var replacementOptions = CreateExecutionOptions(backend.BackendId, temp.Path, model: "second-model");

        var thread = await runtimeService.CreateGlobalThreadAsync(initialOptions, title: "Started").ConfigureAwait(false);
        thread.MarkStarted(DateTimeOffset.UtcNow);
        await runtimeService.EnsureCoordinatorSessionAsync(thread, replacementOptions).ConfigureAwait(false);

        Assert.AreEqual(1, backend.CreateSessionCount);
        Assert.AreEqual(1, backend.ResumeSessionCount);
        Assert.AreEqual("codex-session-1", backend.LastResumedSessionId);
        Assert.AreEqual("codex-session-1", thread.BackendSessionId);
    }

    private static async Task<AgentHub> CreateHubAsync(string rootPath, IAgentBackend backend)
    {
        var dbPath = Path.Combine(rootPath, "state", "db", "codealta.db");
        var db = new CodeAltaDb(new CodeAltaDbOptions { DatabasePath = dbPath });
        await db.InitializeAsync().ConfigureAwait(false);
        var backendFactory = new AgentBackendFactory();
        backendFactory.Register(backend.BackendId.Value, () => backend);
        return new AgentHub(backendFactory, new AgentRepository(db));
    }

    private static WorkThreadRuntimeService CreateRuntimeService(string rootPath, AgentHub hub)
    {
        var catalogOptions = new CatalogOptions { GlobalRoot = rootPath };
        return new WorkThreadRuntimeService(
            hub,
            new ProjectCatalog(catalogOptions),
            new WorkThreadCatalog(catalogOptions),
            new RoleProfileStore(),
            new AgentInstructionTemplateProvider(),
            catalogOptions);
    }

    private static WorkThreadExecutionOptions CreateExecutionOptions(
        AgentBackendId backendId,
        string workingDirectory,
        string? model)
    {
        return new WorkThreadExecutionOptions
        {
            BackendId = backendId,
            ProviderKey = backendId.Value,
            WorkingDirectory = workingDirectory,
            Model = model,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            OnUserInputRequest = static (_, _) => Task.FromResult(new AgentUserInputResponse(new Dictionary<string, string>())),
        };
    }

    private sealed class RecordingBackend : IAgentBackend
    {
        private int _nextSessionId;

        public RecordingBackend(AgentBackendId backendId)
        {
            BackendId = backendId;
        }

        public AgentBackendId BackendId { get; }

        public string DisplayName => "Recording Backend";

        public int CreateSessionCount { get; private set; }

        public int ResumeSessionCount { get; private set; }

        public string? LastResumedSessionId { get; private set; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>([]);

        public Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(
            AgentSessionListFilter? filter = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentSessionMetadata>>([]);

        public Task<IAgentSession> CreateSessionAsync(
            AgentSessionCreateOptions options,
            CancellationToken cancellationToken = default)
        {
            CreateSessionCount++;
            var sessionId = $"{BackendId.Value}-session-{++_nextSessionId}";
            return Task.FromResult<IAgentSession>(new RecordingSession(this, sessionId));
        }

        public Task<IAgentSession> ResumeSessionAsync(
            string sessionId,
            AgentSessionResumeOptions options,
            CancellationToken cancellationToken = default)
        {
            ResumeSessionCount++;
            LastResumedSessionId = sessionId;
            return Task.FromResult<IAgentSession>(new RecordingSession(this, sessionId));
        }
    }

    private sealed class RecordingSession : IAgentSession
    {
        private readonly RecordingBackend _backend;

        public RecordingSession(RecordingBackend backend, string sessionId)
        {
            _backend = backend;
            SessionId = sessionId;
        }

        public AgentBackendId BackendId => _backend.BackendId;

        public string SessionId { get; }

        public string? WorkspacePath => null;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async IAsyncEnumerable<AgentEvent> StreamEventsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public IDisposable Subscribe(Action<AgentEvent> handler)
            => DisposableAction.Instance;

        public Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentRunId("recording-run"));

        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentRunId("recording-run"));

        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CompactAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
    }

    private sealed class DisposableAction : IDisposable
    {
        public static readonly IDisposable Instance = new DisposableAction();

        public void Dispose()
        {
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
                "CodeAlta.Tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
