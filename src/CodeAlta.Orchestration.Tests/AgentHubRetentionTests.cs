using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class AgentHubRetentionTests
{
    [TestMethod]
    public async Task RetirementRefusesEveryPreparationBeforeProviderFactory()
    {
        await using var f = new Fixture();
        using var retirement = f.Protection.TryReserveRetirement("target");
        Assert.IsNotNull(retirement);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => f.Start());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => f.Start(null));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => f.Resume());
        Assert.AreEqual(0, f.FactoryCalls);
    }

    [TestMethod]
    public async Task CompletedRetirementRefusesExplicitCreateAndResume()
    {
        await using var f = new Fixture();
        using (var retirement = f.Protection.TryReserveRetirement("target"))
        {
            Assert.IsNotNull(retirement);
            Assert.IsTrue(f.Protection.DeleteIfReady("target", () => true));
        }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => f.Start());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => f.Resume());
        Assert.AreEqual(0, f.FactoryCalls);
    }

    [TestMethod]
    public async Task MissingJournalRefusesResumeBeforeFactoryEvenWithoutProcessTombstone()
    {
        await using var f = new Fixture();
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => f.Resume());
        Assert.AreEqual(0, f.FactoryCalls);
        f.AssertRetirable("target");
    }

    [TestMethod]
    public async Task ProviderFactoryAndHeldStartAreProtectedDespiteCancelledWaiter()
    {
        await using var f = new Fixture();
        f.Runtime.Start.Block = true;
        f.OnFactory = () => f.AssertProtected("target");
        var original = f.Keep(f.Start());
        await f.Runtime.Start.WaitEntered();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => original.WaitAsync(cancelled.Token));
        f.AssertProtected("target");
        Assert.IsFalse(original.IsCompleted);
        f.Runtime.Start.Release();
        var handle = await original;
        f.AssertProtected("target");
        await f.Hub.StopSessionAsync(handle.HandleId);
        f.AssertRetirable("target");
    }

    [TestMethod]
    public async Task CancelledCreateKeepsClaimUntilOriginalPreparationAndBothDisposalsFinish()
    {
        await using var f = new Fixture();
        f.Runtime.Prepare.Block = f.Session.Disposal.Block = f.Runtime.Disposal.Block = true;
        using var cancellation = new CancellationTokenSource();
        var original = f.Keep(f.Start(token: cancellation.Token));
        await f.Runtime.Prepare.WaitEntered();
        cancellation.Cancel();
        f.AssertProtected("target");
        Assert.IsFalse(original.IsCompleted);
        f.Runtime.Prepare.Release();
        await f.Session.Disposal.WaitEntered();
        f.AssertProtected("target");
        f.Session.Disposal.Release();
        await f.Runtime.Disposal.WaitEntered();
        f.AssertProtected("target");
        f.Runtime.Disposal.Release();
        await Assert.ThrowsAsync<OperationCanceledException>(() => original);
        f.AssertRetirable("target");
    }

    [TestMethod]
    public async Task HubShutdownJoinsOriginalPreparationBeforeReleasingClaim()
    {
        await using var f = new Fixture();
        f.Runtime.Prepare.Block = true;
        var original = f.Keep(f.Start());
        await f.Runtime.Prepare.WaitEntered();
        var disposal = f.Keep(f.Hub.DisposeAsync().AsTask());
        Assert.IsFalse(disposal.IsCompleted);
        f.AssertProtected("target");
        f.Runtime.Prepare.Release();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => original);
        await disposal;
        f.AssertRetirable("target");
    }

    [TestMethod]
    public async Task HeldAttachmentCallbackRegistrationIsProtectedAndJoinedOnShutdown()
    {
        await using var f = new Fixture();
        f.Session.Registration.Block = true;
        using var cancellation = new CancellationTokenSource();
        var original = f.Keep(Task.Run(() => f.Start(token: cancellation.Token)));
        await f.Session.Registration.WaitEntered();
        cancellation.Cancel();
        var disposal = f.Keep(f.Hub.DisposeAsync().AsTask());
        Assert.IsFalse(disposal.IsCompleted);
        f.AssertProtected("target");
        f.Session.Registration.Release();
        await Assert.ThrowsAsync<OperationCanceledException>(() => original);
        await disposal;
        f.AssertRetirable("target");
    }

    [TestMethod]
    public async Task GeneratedIdentityNarrowsWithoutGapAndKeepsLineageProtected()
    {
        await using var f = new Fixture();
        f.Runtime.Prepare.Block = true;
        var original = f.Keep(f.Start(null, parent: " parent ", creator: " creator "));
        await f.Runtime.Prepare.WaitEntered();
        f.AssertProtected("unrelated");
        f.OnSpecificClaim = id =>
        {
            if (id == "target") f.AssertProtected("unrelated"); // Still under the unscoped claim.
        };
        f.Runtime.Prepare.Release();
        var handle = await original;
        f.AssertRetirable("unrelated");
        f.AssertProtected("target");
        f.AssertProtected("parent");
        f.AssertProtected("creator");
        await f.Hub.StopSessionAsync(handle.HandleId);
        f.AssertRetirable("target");
        f.AssertRetirable("parent");
        f.AssertRetirable("creator");
    }

    [TestMethod]
    public async Task LineageRefusalUnwindsTargetClaimWithoutConstructingProvider()
    {
        await using var f = new Fixture();
        using var retirement = f.Protection.TryReserveRetirement("parent");
        Assert.IsNotNull(retirement);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => f.Start(parent: "parent"));
        Assert.AreEqual(0, f.FactoryCalls);
        f.AssertRetirable("target");
    }

    [TestMethod]
    public async Task DirectResumeProtectsJournalBeforeOriginalProviderPreparation()
    {
        await using var f = new Fixture();
        await f.SeedJournal();
        f.Runtime.Prepare.Block = true;
        f.OnFactory = () => f.AssertProtected("target");
        var original = f.Keep(f.Resume());
        await f.Runtime.Prepare.WaitEntered();
        f.AssertProtected("target");
        f.Runtime.Prepare.Release();
        var handle = await original;
        await f.Hub.StopSessionAsync(handle.HandleId);
        f.AssertRetirable("target");
    }

    [TestMethod]
    [DataRow("send")]
    [DataRow("steer")]
    [DataRow("compact")]
    [DataRow("idle-compact")]
    public async Task AttachedOriginalOperationAndDisposalOutliveCancelledWaiters(string route)
    {
        await using var f = new Fixture();
        var handle = await f.Start();
        f.Session.Work.Block = f.Session.Disposal.Block = f.Runtime.Disposal.Block = true;
        using var cancellation = new CancellationTokenSource();
        Task original = route switch
        {
            "send" => f.Hub.RunAsync(handle.HandleId, new() { Input = AgentInput.Text("inert host prompt") }, cancellation.Token),
            "steer" => f.Hub.SteerAsync(handle.HandleId, new() { Input = AgentInput.Text("inert steering") }, cancellation.Token),
            "compact" => f.Hub.CompactAsync(handle.HandleId, cancellation.Token),
            _ => f.Hub.TryCompactWhenIdleAsync(handle.HandleId, cancellation.Token),
        };
        _ = f.Keep(original);
        await f.Session.Work.WaitEntered();
        var stop = f.Keep(f.Hub.StopSessionAsync(handle.HandleId));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => stop.WaitAsync(cancellation.Token));
        Assert.IsFalse(original.IsCompleted);
        f.AssertProtected("target");
        f.Session.Work.Release();
        await original;
        await f.Session.Disposal.WaitEntered();
        f.AssertProtected("target");
        f.Session.Disposal.Release();
        await f.Runtime.Disposal.WaitEntered();
        f.AssertProtected("target");
        f.Runtime.Disposal.Release();
        await stop;
        f.AssertRetirable("target");
    }

    [TestMethod]
    public async Task CapturedProviderCallbackKeepsOriginalEntryProtectedThroughStop()
    {
        await using var f = new Fixture();
        var handle = await f.Start();
        f.Session.Work.Block = true;
        f.Session.ProviderCallback!();
        await f.Session.Work.WaitEntered();
        var stop = f.Keep(f.Hub.StopSessionAsync(handle.HandleId));
        Assert.IsFalse(stop.IsCompleted);
        f.AssertProtected("target");
        f.Session.Work.Release();
        await stop;
        f.AssertRetirable("target");
        Assert.IsNull(f.Session.ProviderCallback);
    }

    [TestMethod]
    public async Task HeldEventSubscriberKeepsOriginalEntryProtectedThroughStop()
    {
        await using var f = new Fixture();
        var handle = await f.Start();
        var callback = new Hold { Block = true };
        using var registration = await f.Hub.SubscribeSessionEventsAsync(handle.HandleId,
            _ => callback.Invoke().GetAwaiter().GetResult());
        var captured = f.Session.Subscriber!;
        var invocation = f.Keep(Task.Run(() => captured(null!)));
        try
        {
            await callback.WaitEntered();
            var stop = f.Keep(f.Hub.StopSessionAsync(handle.HandleId));
            Assert.IsFalse(stop.IsCompleted);
            f.AssertProtected("target");
            callback.Release();
            await invocation;
            await stop;
            f.AssertRetirable("target");
            captured(null!); // A stale callback cannot reenter a stopped attachment.
            Assert.AreEqual(1, callback.Calls);
        }
        finally { callback.Release(); }
    }

    [TestMethod]
    public async Task FailedProviderDisposalRetainsProtectionRatherThanAuthorizingRetirement()
    {
        await using var f = new Fixture { ExpectDisposalFailure = true };
        var handle = await f.Start();
        f.Runtime.DisposalFailure = new IOException("inert disposal failure");
        await Assert.ThrowsAsync<AgentDependencyRetentionException>(() => f.Hub.StopSessionAsync(handle.HandleId));
        f.AssertProtected("target");
    }

    [TestMethod]
    public async Task StoppingOneAttachmentDoesNotReleaseAnotherAttachmentsClaim()
    {
        await using var f = new Fixture();
        var first = await f.Start();
        var second = await f.Start();
        await f.Hub.StopSessionAsync(first.HandleId);
        f.AssertProtected("target");
        await f.Hub.StopSessionAsync(second.HandleId);
        f.AssertRetirable("target");
    }

    [TestMethod]
    public async Task HeldUnsubscribeIsJoinedBeforeProtectionRelease()
    {
        await using var f = new Fixture();
        var handle = await f.Start();
        var registration = await f.Hub.SubscribeSessionEventsAsync(handle.HandleId, _ => { });
        f.Session.Unsubscribe.Block = true;
        var unsubscribe = f.Keep(Task.Run(registration.Dispose));
        await f.Session.Unsubscribe.WaitEntered();
        var stop = f.Keep(f.Hub.StopSessionAsync(handle.HandleId));
        Assert.IsFalse(stop.IsCompleted);
        f.AssertProtected("target");
        f.Session.Unsubscribe.Release();
        await unsubscribe;
        await stop;
        f.AssertRetirable("target");
        Assert.AreEqual(1, f.Session.Unsubscribe.Calls);
    }

    [TestMethod]
    public async Task RawAgentRuntimeKeepsClaimUntilTransferredExecutorDisposalFinishes()
    {
        await using var f = new Fixture();
        var executor = new Executor();
        var runtime = new AgentModelProviderRuntime(new(new("raw"), "Inert raw fixture"), new()
        {
            ProviderKey = "raw", ProtocolFamily = "inert", DisplayName = "Inert",
            TransportKind = AgentTransportKind.OpenAIResponses,
        }, executor);
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(runtime.Descriptor, () => { f.AssertProtected("target"); return runtime; });
        await using var hub = new AgentHub(registry, f.Root, null, f.Protection);
        try
        {
            var handle = await hub.StartSessionAsync(new()
            {
                SessionId = "target", ProviderKey = "raw", WorkingDirectory = f.Root,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
            });
            var stop = hub.StopSessionAsync(handle.HandleId);
            await executor.Disposal.WaitEntered();
            Assert.IsFalse(stop.IsCompleted);
            f.AssertProtected("target");
            executor.Disposal.Release();
            await stop;
            f.AssertRetirable("target");
        }
        finally { executor.Disposal.Release(); }
    }

    [TestMethod]
    [DataRow("start")]
    [DataRow("create")]
    [DataRow("resume")]
    public async Task MultiplyFaultedPreparationRetainsSiblingEvidenceAndAdmission(string route)
    {
        await using var f = new Fixture { ExpectDisposalFailure = true };
        var ordinary = new IOException("ordinary first failure");
        var retained = new AgentDependencyRetentionException("inert", "original worker", [new IOException("held")], new object());
        var original = new TaskCompletionSource<IAgentSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (route == "start") f.Runtime.StartOriginal = original.Task;
        else f.Runtime.PreparationOriginal = original.Task;
        if (route == "resume") await f.SeedJournal();
        var preparation = route == "resume" ? f.Resume() : f.Start();
        f.AssertProtected("target");
        original.SetException([ordinary, retained]);
        var failure = await Assert.ThrowsAsync<AgentDependencyRetentionException>(() => preparation);
        Assert.IsTrue(ContainsReference(failure, ordinary));
        Assert.IsTrue(ContainsReference(failure, retained));
        Assert.AreEqual(0, f.Runtime.Disposal.Calls, "Retained start/create/resume must not dispose dependent runtimes.");
        Assert.AreEqual(0, f.Session.Disposal.Calls);
        f.AssertProtected("target");
    }

    [TestMethod]
    [DataRow("failed-start")]
    [DataRow("session")]
    [DataRow("runtime")]
    public async Task MultiplyFaultedPreparationCleanupPreservesEveryOriginalFault(string route)
    {
        await using var f = new Fixture { ExpectDisposalFailure = true };
        var ordinary = new IOException("ordinary cleanup failure");
        var retained = new AgentDependencyRetentionException("inert", "cleanup worker", [new IOException("held")], new object());
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (route == "session")
        {
            f.Session.DisposalOriginal = cleanup.Task;
            f.Session.Identity = "mismatch"; // Fail before an entry owns the session-disposal original.
        }
        else f.Runtime.DisposalOriginal = cleanup.Task;
        using var cancellation = new CancellationTokenSource();
        if (route == "failed-start") f.Runtime.StartOriginal = Task.FromException(new IOException("start failed"));
        else f.Runtime.Prepare.Block = true;
        var preparation = f.Start(token: cancellation.Token);
        if (route != "failed-start")
        {
            await f.Runtime.Prepare.WaitEntered();
            cancellation.Cancel();
            f.Runtime.Prepare.Release();
        }
        f.AssertProtected("target");
        cleanup.SetException([ordinary, retained]);
        var failure = await Assert.ThrowsAsync<AgentDependencyRetentionException>(() => preparation);
        Assert.IsTrue(ContainsReference(failure, ordinary));
        Assert.IsTrue(ContainsReference(failure, retained));
        f.AssertProtected("target");
    }

    private static bool ContainsReference(Exception failure, Exception expected)
        => ReferenceEquals(failure, expected)
            || failure is AggregateException aggregate && aggregate.InnerExceptions.Any(inner => ContainsReference(inner, expected));

    [TestMethod]
    [DataRow("steer")]
    [DataRow("compact")]
    [DataRow("idle-compact")]
    public async Task MultiplyFaultedAttachedOperationCannotHideSiblingRetention(string route)
    {
        await using var f = new Fixture { ExpectDisposalFailure = true };
        var handle = await f.Start();
        var ordinary = new IOException("ordinary operation failure");
        var retained = new AgentDependencyRetentionException("inert", "operation worker", [new IOException("held")], new object());
        var run = new TaskCompletionSource<AgentRunId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var compact = new TaskCompletionSource<AgentCompactionOutcome?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation;
        if (route == "steer")
        {
            f.Session.RunOriginal = run.Task;
            operation = f.Hub.SteerAsync(handle.HandleId, new() { Input = AgentInput.Text("inert") });
            run.SetException([ordinary, retained]);
        }
        else
        {
            f.Session.CompactionOriginal = compact.Task;
            operation = route == "compact" ? f.Hub.CompactAsync(handle.HandleId) : f.Hub.TryCompactWhenIdleAsync(handle.HandleId);
            compact.SetException([ordinary, retained]);
        }
        var failure = await Assert.ThrowsAsync<AgentDependencyRetentionException>(() => operation);
        Assert.IsTrue(ContainsReference(failure, ordinary));
        Assert.IsTrue(ContainsReference(failure, retained));
        await Assert.ThrowsAsync<AgentDependencyRetentionException>(() => f.Hub.StopSessionAsync(handle.HandleId));
        Assert.AreEqual(0, f.Session.Disposal.Calls);
        Assert.AreEqual(0, f.Runtime.Disposal.Calls);
        f.AssertProtected("target");
    }

    private sealed class Fixture : ISessionRetentionAdmission, IAsyncDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "CodeAlta-hub-retention-" + Guid.NewGuid().ToString("N"));
        internal readonly SessionRetentionProtection Protection = new();
        internal readonly Session Session = new();
        internal readonly Runtime Runtime;
        internal readonly AgentHub Hub;
        private readonly List<Task> _tasks = [];
        internal Action? OnFactory;
        internal Action<string>? OnSpecificClaim;
        internal int FactoryCalls;
        internal bool ExpectDisposalFailure;

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Protection.SealParticipants(["fixture"]);
            Assert.IsTrue(Protection.Publish("fixture", [], true));
            Runtime = new Runtime(Session);
            var registry = new ModelProviderRegistry();
            registry.RegisterOrReplace(Runtime.Descriptor, () => { FactoryCalls++; OnFactory?.Invoke(); return Runtime; });
            Hub = new AgentHub(registry, Root, null, this);
        }

        public IDisposable? TryProtectSession(string id) { OnSpecificClaim?.Invoke(id); return Protection.TryProtectSession(id); }
        public IDisposable? TryProtectAll() => Protection.TryProtectAll();
        internal Task<T> Keep<T>(Task<T> task) { _tasks.Add(task); return task; }
        internal Task Keep(Task task) { _tasks.Add(task); return task; }
        internal Task<AgentSessionHandle> Start(string? id = "target", string? parent = null, string? creator = null,
            CancellationToken token = default) => Hub.StartSessionAsync(new()
            {
                SessionId = id, ParentSessionId = parent, CreatedBySessionId = creator,
                ProviderKey = "inert", WorkingDirectory = Root,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
            }, token);
        internal Task<AgentSessionHandle> Resume() => Hub.ResumeSessionAsync(" target ", new()
        {
            ProviderKey = "inert", WorkingDirectory = Root,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
        });
        internal void AssertProtected(string id)
        {
            using var lease = Protection.TryReserveRetirement(id);
            Assert.IsNull(lease, "Unexpected retirement admission for " + id);
        }
        internal void AssertRetirable(string id)
        {
            using var lease = Protection.TryReserveRetirement(id);
            Assert.IsNotNull(lease, "Unexpected retained protection for " + id);
        }
        internal Task SeedJournal() => new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Root)).UpsertSessionAsync(new()
        {
            SessionId = "target", ProviderId = new("inert"), ProtocolFamily = "inert", ProviderKey = "inert",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        public async ValueTask DisposeAsync()
        {
            Runtime.Start.Release(); Runtime.Prepare.Release(); Runtime.Disposal.Release();
            Session.Work.Release(); Session.Disposal.Release(); Session.Unsubscribe.Release(); Session.Registration.Release();
            foreach (var task in _tasks)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) when (task.IsCompleted) { /* Assertions observe the expected failures. */ }
            }
            try { await Hub.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (AgentDependencyRetentionException) when (ExpectDisposalFailure) { }
            Directory.Delete(Root, true);
        }
    }

    private sealed class Hold
    {
        internal bool Block;
        internal int Calls;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task WaitEntered() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        internal void Release() => _release.TrySetResult();
        internal async Task Invoke()
        {
            Interlocked.Increment(ref Calls);
            _entered.TrySetResult();
            if (Block) await _release.Task;
        }
    }

    private sealed class Runtime(Session session) : IModelProviderSessionRuntime
    {
        internal readonly Hold Start = new(), Prepare = new(), Disposal = new();
        internal Exception? DisposalFailure;
        internal Task? StartOriginal, DisposalOriginal;
        internal Task<IAgentSession>? PreparationOriginal;
        public ModelProviderDescriptor Descriptor { get; } = new(new("inert"), "Inert retention fixture");
        public Task StartAsync(CancellationToken cancellationToken = default) => StartOriginal ?? Start.Invoke();
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No real provider.");
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new InvalidOperationException("No real provider.");
        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
            => PreparationOriginal ?? PrepareSessionAsync();
        public Task<IAgentSession> ResumeSessionAsync(string id, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
            => PreparationOriginal ?? PrepareSessionAsync();
        private async Task<IAgentSession> PrepareSessionAsync() { await Prepare.Invoke(); return session; }
        public ValueTask DisposeAsync() => DisposalOriginal is { } original ? new(original) : DisposeCoreAsync();
        private async ValueTask DisposeCoreAsync()
        { await Disposal.Invoke(); if (DisposalFailure is not null) throw DisposalFailure; }
    }

    private sealed class Session : IAgentSession, IAgentProviderInitiatedRuns, IAgentIdleCompactionProvider
    {
        internal readonly Hold Work = new(), Disposal = new(), Unsubscribe = new(), Registration = new();
        internal Task? DisposalOriginal;
        internal string Identity = "target";
        internal Task<AgentRunId>? RunOriginal;
        internal Task<AgentCompactionOutcome?>? CompactionOriginal;
        internal Action? ProviderCallback;
        internal Action<AgentEvent>? Subscriber;
        public ModelProviderId ProviderId => new("inert");
        public string SessionId => Identity;
        public string? WorkspacePath => null;
        public ValueTask DisposeAsync() => new(DisposalOriginal ?? Disposal.Invoke());
        public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public IDisposable Subscribe(Action<AgentEvent> handler)
        {
            Subscriber = handler;
            return new Subscription(() => { Unsubscribe.Invoke().GetAwaiter().GetResult(); Subscriber = null; });
        }
        public Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
            => RunOriginal ?? CompleteWorkAsync();
        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
            => RunOriginal ?? CompleteWorkAsync();
        private async Task<AgentRunId> CompleteWorkAsync() { await Work.Invoke(); return new("run"); }
        public Task CompactAsync(CancellationToken cancellationToken = default) => (Task?)CompactionOriginal ?? Work.Invoke();
        public Task<AgentCompactionOutcome?> TryCompactWhenIdleAsync(CancellationToken cancellationToken = default)
            => CompactionOriginal ?? CompleteCompactionAsync();
        private async Task<AgentCompactionOutcome?> CompleteCompactionAsync() { await Work.Invoke(); return null; }
        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
        public IDisposable OnProviderInitiatedRun(Action handler)
        {
            Registration.Invoke().GetAwaiter().GetResult();
            ProviderCallback = handler;
            return new Subscription(() => ProviderCallback = null);
        }
        public AgentSendOptions? TakeProviderInitiatedRun() => new() { Input = AgentInput.Text("inert callback") };
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class Executor : IModelProviderTurnExecutor, IAsyncDisposable
    {
        internal readonly Hold Disposal = new() { Block = true };
        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The raw fixture never executes provider work.");
        public async ValueTask DisposeAsync() => await Disposal.Invoke();
    }
}
