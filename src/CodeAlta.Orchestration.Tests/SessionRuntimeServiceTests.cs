using CodeAlta.Agent;
using CodeAlta.Agent.LocalRuntime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class SessionRuntimeServiceTests
{
    [TestMethod]
    public async Task ListRecoverableSessionsAsync_IncludesLocalRuntimeSessionsForUnregisteredProviders()
    {
        using var temp = new TempDirectory();
        var factory = new AgentBackendFactory();
        await using var hub = new AgentHub(factory);
        await using var runtime = CreateRuntime(temp.Path, hub);
        var store = new WorkThreadCatalog(new CatalogOptions { GlobalRoot = temp.Path }).JournalStore.CreateSessionStore();
        var createdAt = DateTimeOffset.Parse("2026-05-16T12:00:00+00:00");
        await store.UpsertSessionAsync(
            new LocalAgentSessionSummary
            {
                SessionId = "session-1",
                BackendId = new AgentBackendId("old-provider"),
                ProtocolFamily = "openai-responses",
                ProviderKey = "old-provider",
                WorkingDirectory = temp.Path,
                Title = "Recovered old provider",
                CreatedAt = createdAt,
                UpdatedAt = createdAt.AddMinutes(1),
            }).ConfigureAwait(false);

        var threads = await CollectAsync(runtime.ListRecoverableSessionsAsync()).ConfigureAwait(false);

        Assert.AreEqual(1, threads.Count);
        Assert.AreEqual("session-1", threads[0].ThreadId);
        Assert.AreEqual("old-provider", threads[0].BackendId);
        Assert.AreEqual("old-provider", threads[0].ProviderKey);
    }

    [TestMethod]
    public async Task ListRecoverableSessionsAsync_DoesNotInitializeProviders()
    {
        using var temp = new TempDirectory();
        var backendId = new AgentBackendId("registered-provider");
        var backend = new ThrowingProviderBackend(backendId);
        var factory = new AgentBackendFactory();
        factory.Register(backendId, () => backend);
        await using var hub = new AgentHub(factory);
        await using var runtime = CreateRuntime(temp.Path, hub);
        var store = new WorkThreadCatalog(new CatalogOptions { GlobalRoot = temp.Path }).JournalStore.CreateSessionStore();
        var createdAt = DateTimeOffset.Parse("2026-05-16T12:00:00+00:00");
        await store.UpsertSessionAsync(
            new LocalAgentSessionSummary
            {
                SessionId = "session-1",
                BackendId = backendId,
                ProtocolFamily = "openai-responses",
                ProviderKey = backendId.Value,
                WorkingDirectory = temp.Path,
                Title = "Recovered provider",
                CreatedAt = createdAt,
                UpdatedAt = createdAt.AddMinutes(1),
            }).ConfigureAwait(false);

        var threads = await CollectAsync(runtime.ListRecoverableSessionsAsync()).ConfigureAwait(false);

        Assert.AreEqual(1, threads.Count);
        Assert.AreEqual(0, backend.StartAttempts);
    }

    [TestMethod]
    public async Task EnsureCoordinatorSessionAsync_RecreatesSessionWhenResumeTargetIsMissing()
    {
        using var temp = new TempDirectory();
        var backendId = new AgentBackendId("shared-missing");
        var backend = new MissingResumeBackend(backendId);
        var factory = new AgentBackendFactory();
        factory.Register(backendId, () => backend);
        await using var hub = new AgentHub(factory);
        await using var runtime = CreateRuntime(temp.Path, hub);
        var thread = CreateThread("thread-1", backendId, temp.Path);

        var agentId = await runtime.EnsureCoordinatorSessionAsync(thread, CreateOptions(backendId, temp.Path)).ConfigureAwait(false);
        var history = await runtime.GetHistoryAsync(thread.ThreadId).ConfigureAwait(false);

        Assert.AreNotEqual(Guid.Empty, agentId.Value);
        Assert.AreEqual("thread-1", thread.ThreadId);
        Assert.AreEqual(1, backend.ResumeAttempts);
        Assert.AreEqual(1, backend.CreateAttempts);
        Assert.AreEqual("thread-1", backend.CreatedThreadIds.Single());
        Assert.AreEqual(0, history.Count);
    }

    private static async Task<IReadOnlyList<SessionViewDescriptor>> CollectAsync(
        IAsyncEnumerable<SessionViewDescriptor> sessions)
    {
        var results = new List<SessionViewDescriptor>();
        await foreach (var session in sessions.ConfigureAwait(false))
        {
            results.Add(session);
        }

        return results;
    }

    private static SessionRuntimeService CreateRuntime(string root, AgentHub hub)
    {
        var options = new CatalogOptions { GlobalRoot = root };
        var threadCatalog = new WorkThreadCatalog(options);
        var sessionCatalog = new AgentSessionCatalog(threadCatalog.JournalStore.CreateSessionStore());
        return new SessionRuntimeService(
            hub,
            sessionCatalog,
            new ProjectCatalog(options),
            threadCatalog,
            new AgentInstructionTemplateProvider(catalogOptions: options),
            options);
    }

    private static SessionViewDescriptor CreateThread(string threadId, AgentBackendId backendId, string root)
        => new()
        {
            ThreadId = threadId,
            BackendId = backendId.Value,
            ProviderKey = backendId.Value,
            Kind = WorkThreadKind.GlobalThread,
            Status = WorkThreadStatus.Active,
            Title = threadId,
            WorkingDirectory = root,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

    private static SessionExecutionOptions CreateOptions(AgentBackendId backendId, string root)
        => new()
        {
            BackendId = backendId,
            ProviderKey = backendId.Value,
            WorkingDirectory = root,
            ProjectRoots = [root],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };

    private sealed class MissingResumeBackend(AgentBackendId backendId) : IAgentBackend
    {
        public AgentBackendId BackendId { get; } = backendId;

        public string DisplayName => "Missing Resume";

        public int ResumeAttempts { get; private set; }

        public int CreateAttempts { get; private set; }

        public List<string?> CreatedThreadIds { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>([]);

        public Task<IAgentSession> CreateSessionAsync(
            AgentSessionCreateOptions options,
            CancellationToken cancellationToken = default)
        {
            CreateAttempts++;
            CreatedThreadIds.Add(options.ThreadId);
            return Task.FromResult<IAgentSession>(new EmptyAgentSession(BackendId, options.ThreadId ?? "created-session"));
        }

        public Task<IAgentSession> ResumeSessionAsync(
            string sessionId,
            AgentSessionResumeOptions options,
            CancellationToken cancellationToken = default)
        {
            ResumeAttempts++;
            throw new KeyNotFoundException($"The session '{sessionId}' was not found for backend '{BackendId.Value}'.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingProviderBackend(AgentBackendId backendId) : IAgentBackend
    {
        public AgentBackendId BackendId { get; } = backendId;

        public string DisplayName => "Throwing Provider";

        public int StartAttempts { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartAttempts++;
            throw new InvalidOperationException("Provider should not be initialized while listing recoverable sessions.");
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Provider models should not be listed while listing recoverable sessions.");

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

    private sealed class EmptyAgentSession(AgentBackendId backendId, string sessionId) : IAgentSession
    {
        public AgentBackendId BackendId { get; } = backendId;

        public string SessionId { get; } = sessionId;

        public string? WorkspacePath => null;

        public IAsyncEnumerable<AgentEvent> StreamEventsAsync(CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<AgentEvent>();

        public IDisposable Subscribe(Action<AgentEvent> handler) => new Subscription();

        public Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentRunId($"run-{Guid.NewGuid():N}"));

        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CompactAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentEvent>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Subscription : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
