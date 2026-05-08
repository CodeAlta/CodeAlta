using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class WorkThreadRuntimeStressTests
{
    [TestMethod]
    public async Task MultipleThreads_RunConcurrentlyAndReturnIdleEvents()
    {
        using var fixture = RuntimeFixture.Create();
        var threads = Enumerable.Range(0, 8)
            .Select(i => CreateThread($"thread-{i}", fixture.BackendId, fixture.Root))
            .ToArray();

        var sends = threads
            .Select(thread => fixture.Runtime.SendAsync(thread, CreateOptions(fixture.BackendId, fixture.Root), new AgentSendOptions { Input = AgentInput.Text($"prompt {thread.ThreadId}") }))
            .ToArray();
        await Task.WhenAll(sends);

        Assert.AreEqual(8, fixture.Backend.SessionsCreated);
    }

    [TestMethod]
    public async Task SameThreadPromptRaces_AreSerializedThroughActor()
    {
        using var fixture = RuntimeFixture.Create();
        var thread = CreateThread("thread-1", fixture.BackendId, fixture.Root);
        var firstStarted = fixture.Backend.ExpectNextSendBlock();
        var first = fixture.Runtime.SendAsync(thread, CreateOptions(fixture.BackendId, fixture.Root), new AgentSendOptions { Input = AgentInput.Text("first") });
        await firstStarted.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = fixture.Runtime.SendAsync(thread, CreateOptions(fixture.BackendId, fixture.Root), new AgentSendOptions { Input = AgentInput.Text("second") });
        Assert.IsFalse(second.IsCompleted, "The same-thread send should wait behind the actor-owned in-flight send.");

        firstStarted.Release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new[] { "first", "second" }, fixture.Backend.Inputs.ToArray());
    }

    [TestMethod]
    public async Task SendAsync_StreamsSessionEventsWhileProviderRunIsInFlight()
    {
        using var fixture = RuntimeFixture.Create();
        var thread = CreateThread("thread-1", fixture.BackendId, fixture.Root);
        var blocked = fixture.Backend.ExpectNextSendBlock();
        var startedEvent = new TaskCompletionSource<WorkThreadAgentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var streamCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var stream = Task.Run(async () =>
        {
            try
            {
                await foreach (var runtimeEvent in fixture.Runtime.StreamEventsAsync(streamCts.Token))
                {
                    if (runtimeEvent is WorkThreadAgentEvent
                        {
                            Event: AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Started },
                        } agentEvent)
                    {
                        startedEvent.TrySetResult(agentEvent);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (streamCts.IsCancellationRequested)
            {
            }
        });

        var send = fixture.Runtime.SendAsync(thread, CreateOptions(fixture.BackendId, fixture.Root), new AgentSendOptions { Input = AgentInput.Text("blocked") });
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var started = await startedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(thread.ThreadId, started.ThreadId);
        Assert.IsFalse(send.IsCompleted, "The started event must be projected before the provider run completes.");

        blocked.Release.SetResult();
        await send.WaitAsync(TimeSpan.FromSeconds(5));
        await streamCts.CancelAsync();
        await stream.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    [TestMethod]
    public async Task AbortDuringSend_ReachesBlockedSessionAndReturns()
    {
        using var fixture = RuntimeFixture.Create();
        var thread = CreateThread("thread-1", fixture.BackendId, fixture.Root);
        var blocked = fixture.Backend.ExpectNextSendBlock();
        var send = fixture.Runtime.SendAsync(thread, CreateOptions(fixture.BackendId, fixture.Root), new AgentSendOptions { Input = AgentInput.Text("blocked") });
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.Runtime.AbortAsync(thread.ThreadId).WaitAsync(TimeSpan.FromSeconds(5));
        blocked.Release.SetResult();
        await send.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, fixture.Backend.AbortCount);
    }

    [TestMethod]
    public async Task CompactAndDispose_DrainActorCommandsWithoutHanging()
    {
        using var fixture = RuntimeFixture.Create();
        var thread = CreateThread("thread-1", fixture.BackendId, fixture.Root);
        await fixture.Runtime.SendAsync(thread, CreateOptions(fixture.BackendId, fixture.Root), new AgentSendOptions { Input = AgentInput.Text("before compact") });

        await fixture.Runtime.CompactAsync(thread, CreateOptions(fixture.BackendId, fixture.Root)).WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, fixture.Backend.CompactCount);
    }

    [TestMethod]
    public async Task CompactAsync_IdleCompactionLifecycleEventsDoNotLeaveActiveRun()
    {
        using var fixture = RuntimeFixture.Create();
        var thread = CreateThread("thread-1", fixture.BackendId, fixture.Root);
        await fixture.Runtime.SendAsync(thread, CreateOptions(fixture.BackendId, fixture.Root), new AgentSendOptions { Input = AgentInput.Text("before compact") });

        await fixture.Runtime.CompactAsync(thread, CreateOptions(fixture.BackendId, fixture.Root)).WaitAsync(TimeSpan.FromSeconds(5));
        var hasActiveRun = await fixture.Runtime.HasActiveRunAsync(thread, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(hasActiveRun);
    }

    [TestMethod]
    public async Task AbortAfterDispose_IsIgnored()
    {
        using var fixture = RuntimeFixture.Create();
        var thread = CreateThread("thread-1", fixture.BackendId, fixture.Root);
        await fixture.Runtime.SendAsync(thread, CreateOptions(fixture.BackendId, fixture.Root), new AgentSendOptions { Input = AgentInput.Text("before dispose") });

        await fixture.Runtime.DisposeAsync();
        await fixture.Runtime.AbortAsync(thread.ThreadId).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, fixture.Backend.AbortCount);
    }

    private static WorkThreadDescriptor CreateThread(string threadId, AgentBackendId backendId, string root)
        => new()
        {
            ThreadId = threadId,
            BackendId = backendId.Value,
            BackendSessionId = string.Empty,
            Kind = WorkThreadKind.GlobalThread,
            Title = threadId,
            WorkingDirectory = root,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

    private static WorkThreadExecutionOptions CreateOptions(AgentBackendId backendId, string root)
        => new()
        {
            BackendId = backendId,
            WorkingDirectory = root,
            ProjectRoots = [root],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly TempDirectory _temp;
        private readonly AgentHub _hub;

        private RuntimeFixture(TempDirectory temp, AgentHub hub, WorkThreadRuntimeService runtime, StressAgentBackend backend, AgentBackendId backendId)
        {
            _temp = temp;
            _hub = hub;
            Runtime = runtime;
            Backend = backend;
            BackendId = backendId;
            Root = temp.Path;
        }

        public WorkThreadRuntimeService Runtime { get; }

        public StressAgentBackend Backend { get; }

        public AgentBackendId BackendId { get; }

        public string Root { get; }

        public static RuntimeFixture Create()
        {
            var temp = new TempDirectory();
            var backendId = new AgentBackendId("stress");
            var backend = new StressAgentBackend(backendId);
            var factory = new AgentBackendFactory();
            factory.Register(backendId, () => backend);
            var hub = new AgentHub(factory);
            var options = new CatalogOptions { GlobalRoot = temp.Path };
            var runtime = new WorkThreadRuntimeService(
                hub,
                new ProjectCatalog(options),
                new WorkThreadCatalog(options),
                new AgentInstructionTemplateProvider(catalogOptions: options),
                options);
            return new RuntimeFixture(temp, hub, runtime, backend, backendId);
        }

        public void Dispose()
        {
            Runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _hub.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _temp.Dispose();
        }
    }

    private sealed class StressAgentBackend(AgentBackendId backendId) : IAgentBackend
    {
        private readonly ConcurrentQueue<SendBlock> _blocks = new();

        public AgentBackendId BackendId { get; } = backendId;

        public string DisplayName => "Stress";

        public ConcurrentQueue<string> Inputs { get; } = new();

        public int SessionsCreated;

        public int AbortCount;

        public int CompactCount;

        public SendBlock ExpectNextSendBlock()
        {
            var block = new SendBlock();
            _blocks.Enqueue(block);
            return block;
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentModelInfo>>([]);
        public Task<IReadOnlyList<AgentSessionMetadata>> ListSessionsAsync(AgentSessionListFilter? filter = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentSessionMetadata>>([]);
        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref SessionsCreated);
            return Task.FromResult<IAgentSession>(new StressAgentSession(this, $"session-{SessionsCreated}"));
        }

        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(new StressAgentSession(this, sessionId));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class StressAgentSession(StressAgentBackend backend, string sessionId) : IAgentSession
        {
            private readonly ConcurrentDictionary<Guid, Action<AgentEvent>> _subscribers = new();

            public AgentBackendId BackendId => backend.BackendId;
            public string SessionId { get; } = sessionId;
            public string? WorkspacePath => null;
            public IAsyncEnumerable<AgentEvent> StreamEventsAsync(CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<AgentEvent>();
            public IDisposable Subscribe(Action<AgentEvent> handler)
            {
                var id = Guid.NewGuid();
                _subscribers[id] = handler;
                return new Subscription(() => _subscribers.TryRemove(id, out _));
            }

            public async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
            {
                backend.Inputs.Enqueue(((AgentInputItem.Text)options.Input.Items[0]).Value);
                var runId = new AgentRunId($"run-{Guid.NewGuid():N}");
                Publish(new AgentSessionUpdateEvent(BackendId, SessionId, DateTimeOffset.UtcNow, runId, AgentSessionUpdateKind.Started, "started"));
                if (backend._blocks.TryDequeue(out var block))
                {
                    block.Started.SetResult();
                    await block.Release.Task.WaitAsync(cancellationToken);
                }

                Publish(new AgentSessionUpdateEvent(BackendId, SessionId, DateTimeOffset.UtcNow, runId, AgentSessionUpdateKind.Idle, "idle"));
                return runId;
            }

            public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default) => SendAsync(new AgentSendOptions { Input = options.Input }, cancellationToken);
            public Task AbortAsync(CancellationToken cancellationToken = default) { Interlocked.Increment(ref backend.AbortCount); return Task.CompletedTask; }
            public Task CompactAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref backend.CompactCount);
                var runId = new AgentRunId($"compact-{Guid.NewGuid():N}");
                Publish(new AgentSessionUpdateEvent(BackendId, SessionId, DateTimeOffset.UtcNow, runId, AgentSessionUpdateKind.CompactionStarted, "compaction started"));
                Publish(new AgentSessionUpdateEvent(BackendId, SessionId, DateTimeOffset.UtcNow, runId, AgentSessionUpdateKind.CompactionCompleted, "compaction completed"));
                return Task.CompletedTask;
            }
            public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private void Publish(AgentEvent agentEvent)
            {
                foreach (var subscriber in _subscribers.Values)
                {
                    subscriber(agentEvent);
                }
            }
        }
    }

    private sealed class SendBlock
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-stress-" + Guid.NewGuid().ToString("N"));
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
