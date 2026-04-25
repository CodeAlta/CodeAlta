using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration;
using CodeAlta.Persistence;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentHubBackendReloadTests
{
    [TestMethod]
    public async Task UnloadBackendAsync_DisposesCachedBackendWithoutActiveSession()
    {
        using var temp = TempDirectory.Create();
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        var backendFactory = new AgentBackendFactory();
        var backend = new ReloadableBackend();
        backendFactory.Register("reloadable", () => backend);

        await using var hub = new AgentHub(backendFactory, repository);

        var models = await hub.ListModelsAsync(new AgentBackendId("reloadable")).ConfigureAwait(false);
        Assert.AreEqual(1, models.Count);

        var unloaded = await hub.UnloadBackendAsync(new AgentBackendId("reloadable")).ConfigureAwait(false);

        Assert.IsTrue(unloaded);
        Assert.AreEqual(1, backend.StopCount);
        Assert.AreEqual(1, backend.DisposeCount);
    }

    [TestMethod]
    public async Task UnloadBackendAsync_ThrowsWhenBackendHasActiveSession()
    {
        using var temp = TempDirectory.Create();
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        var backendFactory = new AgentBackendFactory();
        var backend = new ReloadableBackend();
        backendFactory.Register("reloadable", () => backend);

        await using var hub = new AgentHub(backendFactory, repository);
        var agent = await hub.RegisterAgentAsync(
                "test",
                new AgentScope
                {
                    Kind = AgentScopeKind.Global,
                    Id = "global",
                },
                new AgentBackendId("reloadable"))
            .ConfigureAwait(false);
        _ = await hub.StartSessionAsync(
                agent.AgentId,
                new AgentSessionCreateOptions
                {
                    WorkingDirectory = Environment.CurrentDirectory,
                    OnPermissionRequest = static (_, _) =>
                        Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                })
            .ConfigureAwait(false);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => hub.UnloadBackendAsync(new AgentBackendId("reloadable")))
            .ConfigureAwait(false);

        StringAssert.Contains(exception.Message, "active sessions");
    }

    [TestMethod]
    public async Task SteerAsync_DoesNotBlockBehindActiveRunGate()
    {
        using var temp = TempDirectory.Create();
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        var backendFactory = new AgentBackendFactory();
        var backend = new BlockingSteerBackend();
        backendFactory.Register("blocking-steer", () => backend);

        await using var hub = new AgentHub(backendFactory, repository);
        var agent = await hub.RegisterAgentAsync(
                "test",
                new AgentScope
                {
                    Kind = AgentScopeKind.Global,
                    Id = "global",
                },
                new AgentBackendId("blocking-steer"))
            .ConfigureAwait(false);
        _ = await hub.StartSessionAsync(
                agent.AgentId,
                new AgentSessionCreateOptions
                {
                    WorkingDirectory = Environment.CurrentDirectory,
                    OnPermissionRequest = static (_, _) =>
                        Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
                })
            .ConfigureAwait(false);

        var runTask = hub.RunAsync(
            agent.AgentId,
            new AgentSendOptions
            {
                Input = AgentInput.Text("Initial prompt"),
            });
        _ = await backend.Session.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var steerRunId = await hub.SteerAsync(
                agent.AgentId,
                new AgentSteerOptions
                {
                    Input = AgentInput.Text("Steer prompt"),
                    ExpectedRunId = new AgentRunId("blocking-run"),
                })
            .ConfigureAwait(false);

        Assert.AreEqual(new AgentRunId("blocking-run"), steerRunId);
        Assert.AreEqual(1, backend.Session.SteerCallCount);

        backend.Session.ReleaseSend.TrySetResult();
        var completedRunId = await runTask.ConfigureAwait(false);
        Assert.AreEqual(new AgentRunId("blocking-run"), completedRunId);
    }

    [TestMethod]
    public async Task ListSessionsAsync_CachesProcessBackedBackendSessions()
    {
        using var temp = TempDirectory.Create();
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        var backendFactory = new AgentBackendFactory();
        var backend = new CountingSessionBackend(AgentBackendIds.Codex)
        {
            Sessions =
            [
                CreateSession("session-a", @"C:\repo-a", "owner/repo-a", "main"),
                CreateSession("session-b", @"C:\repo-b", "owner/repo-b", "dev"),
            ],
        };
        backendFactory.Register(AgentBackendIds.Codex, () => backend);

        await using var hub = new AgentHub(backendFactory, repository);

        var first = await hub.ListSessionsAsync(AgentBackendIds.Codex).ConfigureAwait(false);
        var filtered = await hub.ListSessionsAsync(
                AgentBackendIds.Codex,
                new AgentSessionListFilter(Cwd: @"C:\repo-b"))
            .ConfigureAwait(false);
        var second = await hub.ListSessionsAsync(AgentBackendIds.Codex).ConfigureAwait(false);

        Assert.AreEqual(1, backend.ListSessionsCount);
        Assert.AreEqual(2, first.Count);
        Assert.AreEqual(1, filtered.Count);
        Assert.AreEqual("session-b", filtered[0].SessionId);
        Assert.AreEqual(2, second.Count);
    }

    [TestMethod]
    public async Task ListSessionsAsync_DoesNotCacheRegularBackendSessions()
    {
        using var temp = TempDirectory.Create();
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        var backendFactory = new AgentBackendFactory();
        var backendId = new AgentBackendId("regular");
        var backend = new CountingSessionBackend(backendId)
        {
            Sessions = [CreateSession("session-a", @"C:\repo-a", "owner/repo-a", "main")],
        };
        backendFactory.Register(backendId, () => backend);

        await using var hub = new AgentHub(backendFactory, repository);

        _ = await hub.ListSessionsAsync(backendId).ConfigureAwait(false);
        _ = await hub.ListSessionsAsync(backendId).ConfigureAwait(false);

        Assert.AreEqual(2, backend.ListSessionsCount);
    }

    [TestMethod]
    public async Task DeleteSessionAsync_InvalidatesProcessBackedSessionCache()
    {
        using var temp = TempDirectory.Create();
        var db = await CreateDbAsync(temp.Path).ConfigureAwait(false);
        var repository = new AgentRepository(db);
        var backendFactory = new AgentBackendFactory();
        var backend = new CountingSessionBackend(AgentBackendIds.Copilot)
        {
            Sessions =
            [
                CreateSession("session-a", @"C:\repo-a", "owner/repo-a", "main"),
                CreateSession("session-b", @"C:\repo-b", "owner/repo-b", "dev"),
            ],
        };
        backendFactory.Register(AgentBackendIds.Copilot, () => backend);

        await using var hub = new AgentHub(backendFactory, repository);

        _ = await hub.ListSessionsAsync(AgentBackendIds.Copilot).ConfigureAwait(false);
        var deleted = await hub.DeleteSessionAsync(AgentBackendIds.Copilot, "session-a").ConfigureAwait(false);
        var afterDelete = await hub.ListSessionsAsync(AgentBackendIds.Copilot).ConfigureAwait(false);

        Assert.IsTrue(deleted);
        Assert.AreEqual(2, backend.ListSessionsCount);
        Assert.AreEqual(1, afterDelete.Count);
        Assert.AreEqual("session-b", afterDelete[0].SessionId);
    }

    private static async Task<CodeAltaDb> CreateDbAsync(string rootPath)
    {
        var dbPath = Path.Combine(rootPath, "state", "db", "codealta.db");
        var db = new CodeAltaDb(new CodeAltaDbOptions { DatabasePath = dbPath });
        await db.InitializeAsync().ConfigureAwait(false);
        return db;
    }

    private static AgentSessionMetadata CreateSession(
        string sessionId,
        string cwd,
        string repository,
        string branch)
        => new(
            sessionId,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            Context: new AgentSessionContext(
                Cwd: cwd,
                GitRoot: cwd,
                Repository: repository,
                Branch: branch),
            WorkspacePath: cwd);

    private sealed class ReloadableBackend : IAgentBackend
    {
        public AgentBackendId BackendId => new("reloadable");

        public string DisplayName => "Reloadable";

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>([new AgentModelInfo("model-a")]);

        public Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(
            AgentSessionListFilter? filter = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentSessionMetadata>>([]);

        public Task<IAgentSession> CreateSessionAsync(
            AgentSessionCreateOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(new ReloadableSession(this));

        public Task<IAgentSession> ResumeSessionAsync(
            string sessionId,
            AgentSessionResumeOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(new ReloadableSession(this, sessionId));

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        private sealed class ReloadableSession : IAgentSession
        {
            private readonly ReloadableBackend _backend;

            public ReloadableSession(ReloadableBackend backend, string? sessionId = null)
            {
                _backend = backend;
                SessionId = sessionId ?? "reloadable-session";
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
            {
                ArgumentNullException.ThrowIfNull(handler);
                return DisposableAction.Create(static () => { });
            }

            public Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
                => Task.FromResult(new AgentRunId("reloadable-run"));

            public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task CompactAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
        }
    }

    private sealed class BlockingSteerBackend : IAgentBackend
    {
        public AgentBackendId BackendId => new("blocking-steer");

        public string DisplayName => "Blocking Steer";

        public BlockingSteerSession Session { get; } = new();

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>([new AgentModelInfo("model-a")]);

        public Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(
            AgentSessionListFilter? filter = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentSessionMetadata>>([]);

        public Task<IAgentSession> CreateSessionAsync(
            AgentSessionCreateOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(Session);

        public Task<IAgentSession> ResumeSessionAsync(
            string sessionId,
            AgentSessionResumeOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(Session);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingSteerSession : IAgentSession
    {
        public TaskCompletionSource<bool> SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SteerCallCount { get; private set; }

        public AgentBackendId BackendId => new("blocking-steer");

        public string SessionId => "blocking-steer-session";

        public string? WorkspacePath => null;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async IAsyncEnumerable<AgentEvent> StreamEventsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public IDisposable Subscribe(Action<AgentEvent> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            return DisposableAction.Create(static () => { });
        }

        public async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
        {
            SendStarted.TrySetResult(true);
            await ReleaseSend.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new AgentRunId("blocking-run");
        }

        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
        {
            SteerCallCount++;
            return Task.FromResult(options.ExpectedRunId ?? new AgentRunId("blocking-run"));
        }

        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CompactAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
    }

    private sealed class CountingSessionBackend(AgentBackendId backendId) : IAgentBackend
    {
        public AgentBackendId BackendId { get; } = backendId;

        public string DisplayName => BackendId.Value;

        public int ListSessionsCount { get; private set; }

        public List<AgentSessionMetadata> Sessions { get; set; } = [];

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelInfo>>([]);

        public Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(
            AgentSessionListFilter? filter = null,
            CancellationToken cancellationToken = default)
        {
            ListSessionsCount++;
            return Task.FromResult<IReadOnlyList<AgentSessionMetadata>>([.. Sessions]);
        }

        public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            var removed = Sessions.RemoveAll(session => string.Equals(session.SessionId, sessionId, StringComparison.Ordinal)) > 0;
            return Task.FromResult(removed);
        }

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

    private sealed class DisposableAction(Action dispose) : IDisposable
    {
        private bool _disposed;

        public static IDisposable Create(Action dispose)
        {
            ArgumentNullException.ThrowIfNull(dispose);
            return new DisposableAction(dispose);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            dispose();
        }
    }

    private sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codealta-agenthub-tests-{Guid.NewGuid():N}");
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
