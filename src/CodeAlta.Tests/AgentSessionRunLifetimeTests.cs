using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Tests;

/// <summary>Actual provider source ownership, using explicit roots and scripted text-only turns.</summary>
[TestClass]
public sealed class AgentSessionRunLifetimeTests
{
    [TestMethod]
    public Task TargetedAbort_StaleRunCannotCancelLaterRunAndPreCancellationDoesNotMutate() => Fixture.Run(async f =>
    {
        var first = f.NewHook();
        first.HoldClosing = true;
        var firstTurn = f.NewTurn();
        var a = f.Send(first, firstTurn);
        await f.Ready(firstTurn.Started.Task, a);
        firstTurn.Release.TrySetResult();
        await f.Ready(first.ClosingEntered.Task, a);
        var callsBeforeCompaction = f.Executor.Calls;
        var compaction = f.Keep(f.Session.TryCompactWhenIdleAsync());
        Assert.IsNull(await f.Wait(compaction));
        Assert.AreEqual(callsBeforeCompaction, f.Executor.Calls, "Held Closing must refuse idle compaction without executing a summary.");
        var overtaking = f.Keep(f.Session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("must not overtake closing") }));
        await f.Expect<InvalidOperationException>(overtaking);
        Assert.IsFalse(a.IsCompleted);
        first.ReleaseClosing.TrySetResult();
        await f.Wait(a);
        var second = f.NewHook();
        var secondTurn = f.NewTurn();
        var b = f.Send(second, secondTurn);
        await f.Ready(secondTurn.Started.Task, b);
        var stale = f.Keep(f.Session.AbortRunAsync(first.RunId));
        Assert.AreEqual(AgentTargetedAbortOutcome.TargetNotActive, await f.Wait(stale));
        var cancelled = f.NewSource();
        var cancellation = f.Keep(cancelled.CancelAsync());
        await f.Wait(cancellation);
        var preCancelled = f.Keep(f.Session.AbortRunAsync(second.RunId, cancelled.Token));
        await f.Expect<OperationCanceledException>(preCancelled);
        Assert.IsFalse(second.Token.IsCancellationRequested);
        Assert.IsFalse(b.IsCompleted);
        secondTurn.Release.TrySetResult();
        await f.Wait(b);
    });

    [TestMethod]
    [DataRow("exact")]
    [DataRow("trusted")]
    [DataRow("caller")]
    [DataRow("dispose")]
    public Task RunLifetime_AllCancellationOriginsJoinOriginalCallbacksAndConcurrentDisposal(string origin) => Fixture.Run(async f =>
    {
        var hook = f.NewHook();
        hook.HoldCancellation = true;
        hook.HoldClosing = true;
        hook.AccessSessionInCallback = origin != "dispose";
        var caller = f.NewSource();
        var turn = f.NewTurn();
        var send = f.Send(hook, turn, caller.Token);
        await f.Ready(turn.Started.Task, send);
        Task control = origin switch
        {
            "exact" => f.Keep(f.Session.AbortRunAsync(hook.RunId)),
            "trusted" => f.Keep(f.Session.AbortAsync()),
            "caller" => f.Keep(caller.CancelAsync()),
            _ => f.Keep(f.Session.DisposeAsync().AsTask()),
        };
        await f.Ready(hook.CallbackEntered.Task, send);
        Assert.IsTrue(hook.Token.IsCancellationRequested);
        turn.Release.TrySetResult();
        await f.Ready(hook.ClosingEntered.Task, send);
        // A second cancellation must join the original worker, not a fresh already-cancelled CTS call.
        Task? repeatedAbort = origin == "dispose" ? null : f.Keep(f.Session.AbortAsync());
        var dispose = f.Keep(f.Session.DisposeAsync().AsTask());
        var again = f.Keep(f.Session.DisposeAsync().AsTask());
        Assert.AreSame(dispose, again);
        Assert.IsFalse(send.IsCompleted);
        Assert.IsFalse(dispose.IsCompleted);
        if (origin is "exact" or "trusted") Assert.IsFalse(control.IsCompleted);
        if (repeatedAbort is not null) Assert.IsFalse(repeatedAbort.IsCompleted);
        Assert.AreEqual(0, f.Executor.CleanupCalls);
        hook.ReleaseCancellation.TrySetResult();
        if (origin != "dispose") await f.Wait(control);
        if (repeatedAbort is not null) await f.Wait(repeatedAbort);
        Assert.IsFalse(send.IsCompleted, "The separate Closing hook still owns the run source.");
        hook.ReleaseClosing.TrySetResult();
        await f.Expect<OperationCanceledException>(send);
        await f.Wait(dispose);
        await f.Wait(again);
        await f.Wait(control);
        Assert.AreEqual(1, hook.ClosingCalls);
        Assert.AreEqual(1, f.Executor.CleanupCalls);
    });

    [TestMethod]
    public Task RunLifecycle_StartedIsAwaitedAndCancellationCannotReleaseItsSourceEarly() => Fixture.Run(async f =>
    {
        var hook = f.NewHook();
        hook.HoldStarted = true;
        var turn = f.NewTurn();
        var send = f.Send(hook, turn);
        await f.Ready(hook.StartedEntered.Task, send);
        Assert.AreEqual(0, f.Executor.Calls);
        var abort = f.Keep(f.Session.AbortRunAsync(hook.RunId));
        Assert.AreEqual(AgentTargetedAbortOutcome.CancellationSignalled, await f.Wait(abort));
        var disposal = f.Keep(f.Session.DisposeAsync().AsTask());
        Assert.IsFalse(disposal.IsCompleted);
        Assert.IsFalse(send.IsCompleted);
        Assert.AreEqual(0, f.Executor.CleanupCalls);
        hook.ReleaseStarted.TrySetResult();
        await f.Expect<OperationCanceledException>(send);
        await f.Wait(disposal);
        Assert.AreEqual(0, f.Executor.Calls);
        Assert.AreEqual(1, hook.ClosingCalls);
    });

    [TestMethod]
    public Task TargetedAbort_CallbackFailureIsObservedByOriginalControlAndSend() => Fixture.Run(async f =>
    {
        var hook = f.NewHook();
        hook.HoldCancellation = true;
        hook.FailCancellation = true;
        var turn = f.NewTurn();
        var send = f.Send(hook, turn);
        await f.Ready(turn.Started.Task, send);
        var abort = f.Keep(f.Session.AbortRunAsync(hook.RunId));
        await f.Ready(hook.CallbackEntered.Task, send);
        turn.Release.TrySetResult();
        hook.ReleaseCancellation.TrySetResult();
        var controlFailure = await f.Expect<AggregateException>(abort);
        var sendFailure = await f.Expect<AggregateException>(send);
        Assert.IsTrue(controlFailure.Flatten().InnerExceptions.Contains(hook.CancellationFailure));
        Assert.IsTrue(sendFailure.Flatten().InnerExceptions.Contains(hook.CancellationFailure));
        Assert.AreEqual(1, hook.ClosingCalls);
    });

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public Task RunLifecycle_ClosingIsJoinedOnStartedOrExecutionFailure(bool failStarted) => Fixture.Run(async f =>
    {
        var hook = f.NewHook();
        hook.FailStarted = failStarted;
        hook.HoldClosing = true;
        var turn = f.NewTurn();
        turn.Fail = !failStarted;
        var send = f.Send(hook, turn);
        if (!failStarted)
        {
            await f.Ready(turn.Started.Task, send);
            Assert.IsTrue(hook.StartedCompleted);
            turn.Release.TrySetResult();
        }
        await f.Ready(hook.ClosingEntered.Task, send);
        Assert.IsFalse(send.IsCompleted);
        Assert.AreEqual(failStarted ? 0 : 1, f.Executor.Calls);
        var disposal = f.Keep(f.Session.DisposeAsync().AsTask());
        Assert.IsFalse(disposal.IsCompleted);
        Assert.AreEqual(0, f.Executor.CleanupCalls);
        hook.ReleaseClosing.TrySetResult();
        await f.Expect<InvalidOperationException>(send);
        await f.Wait(disposal);
        Assert.AreEqual(1, hook.ClosingCalls);
    });

    private sealed class Fixture
    {
        private readonly object _gate = new();
        private readonly List<Task> _work = [];
        private readonly HashSet<Task> _expected = [];
        private readonly List<Exception> _failures = [];
        private readonly List<CancellationTokenSource> _sources = [];
        private readonly List<Hook> _hooks = [];
        private readonly List<Turn> _turns = [];
        private bool _releasing;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-run-lifetime-" + Guid.NewGuid().ToString("N"));
        private Task? _lifetime;
        internal AgentSession Session { get; private set; } = null!;
        internal Executor Executor { get; } = new();
        internal Task Keep(Task task) { lock (_gate) _work.Add(task); return task; }
        internal Task<T> Keep<T>(Task<T> task) { Keep((Task)task); return task; }
        internal Task Wait(Task task) => Keep(Keep(task).WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task<T> Wait<T>(Task<T> task) => Keep(Keep(task).WaitAsync(TimeSpan.FromSeconds(5)));
        internal async Task<T> Expect<T>(Task task) where T : Exception
        {
            _ = Keep(task);
            lock (_gate) _expected.Add(task);
            return await Wait(Keep(Assert.ThrowsAsync<T>(() => task)));
        }
        internal async Task Ready(Task signal, Task operation)
        {
            await Wait(Keep(Task.WhenAny(signal, operation)));
            Assert.IsTrue(signal.IsCompletedSuccessfully, "Original operation settled before fixture readiness.");
        }
        internal CancellationTokenSource NewSource()
        {
            var source = new CancellationTokenSource();
            lock (_gate) _sources.Add(source);
            return source;
        }
        internal Hook NewHook()
        {
            var hook = new Hook(this);
            lock (_gate) { _hooks.Add(hook); if (_releasing) hook.ReleaseAll(); }
            return hook;
        }
        internal Turn NewTurn()
        {
            var turn = new Turn();
            lock (_gate) { _turns.Add(turn); if (_releasing) turn.Release.TrySetResult(); }
            return turn;
        }
        internal Task<AgentRunId> Send(Hook hook, Turn turn, CancellationToken token = default)
        {
            Executor.Next = turn;
            return Keep(Session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("inert prompt"), RunLifecycle = hook }, token));
        }
        private void ReleaseAll()
        {
            lock (_gate)
            {
                _releasing = true;
                foreach (var hook in _hooks) hook.ReleaseAll();
                foreach (var turn in _turns) turn.Release.TrySetResult();
            }
        }
        internal static async Task Run(Func<Fixture, Task> body)
        {
            var f = new Fixture();
            f._lifetime = f.RunOwned(body);
            try { await f._lifetime.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (Exception ex) { lock (f._gate) f._failures.Add(ex); }
            finally
            {
                f.ReleaseAll();
                try { await f._lifetime.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) { lock (f._gate) f._failures.Add(ex); }
            }
            Exception[] failures;
            lock (f._gate) failures = [.. f._failures];
            if (failures.Length > 0)
            {
                var error = new AggregateException("Fixture and task-owned root retained: " + f._root, failures);
                error.Data["RetainedFixture"] = f;
                throw error;
            }
        }
        private async Task RunOwned(Func<Fixture, Task> body)
        {
            try
            {
                for (var node = new DirectoryInfo(Path.GetDirectoryName(_root)!); node is not null; node = node.Parent)
                    if ((node.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse fixture ancestry.");
                if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("Fixture root exists.");
                var working = Path.Combine(_root, "work");
                Directory.CreateDirectory(working);
                var store = new FileSystemAgentSessionStore(new AgentRuntimePathLayout(Path.Combine(_root, "store")));
                var provider = new ModelProviderRuntimeDescriptor
                {
                    ProtocolFamily = "openai-responses", ProviderKey = "run-lifetime-fixture", DisplayName = "Inert fixture",
                    TransportKind = AgentTransportKind.OpenAIResponses, BaseUri = new Uri("https://run-lifetime-fixture.invalid/"),
                    Profile = new AgentProviderProfile(), Compaction = AgentCompactionSettings.Default with { Enabled = false },
                };
                var now = DateTimeOffset.UtcNow;
                var summary = new AgentSessionSummary
                {
                    SessionId = Guid.CreateVersion7().ToString(), ProviderId = ModelProviderIds.OpenAIResponses,
                    ProtocolFamily = provider.ProtocolFamily, ProviderKey = provider.ProviderKey, ModelId = "fixture-model",
                    WorkingDirectory = working, CreatedAt = now, UpdatedAt = now,
                };
                var state = new AgentSessionState { SessionId = summary.SessionId, ProtocolFamily = provider.ProtocolFamily, ProviderKey = provider.ProviderKey, UpdatedAt = now };
                await Keep(store.UpsertSessionAsync(summary));
                await Keep(store.UpsertStateAsync(state));
                Session = new AgentSession(ModelProviderIds.OpenAIResponses, provider, summary, state, [], store, Executor,
                    new AgentSessionCreateOptions
                    {
                        WorkingDirectory = working, ProjectRoots = [working], ProviderKey = provider.ProviderKey, Model = "fixture-model",
                        InstructionsAlreadyComposed = true, SystemMessage = "Inert instructions.", DeveloperInstructions = "No discovery.", Tools = [],
                        OnPermissionRequest = static (_, _) => throw new InvalidOperationException("No tools in this fixture."),
                        OnUserInputRequest = static (_, _) => throw new InvalidOperationException("No input in this fixture."),
                    }, cachedModels: [new AgentModelInfo("fixture-model", "Fixture")]);
                await body(this);
            }
            catch (Exception ex) { lock (_gate) _failures.Add(ex); }
            finally
            {
                ReleaseAll();
                CancellationTokenSource[] sources;
                lock (_gate) sources = [.. _sources];
                foreach (var source in sources) _ = Keep(source.CancelAsync());
                if (Session is not null) _ = Keep(Session.DisposeAsync().AsTask());
                Task[] tasks;
                lock (_gate) tasks = [.. _work];
                await Task.WhenAll(tasks.Distinct().Select(Join));
                Task[] lateTasks;
                lock (_gate) lateTasks = _work.Except(tasks).ToArray();
                await Task.WhenAll(lateTasks.Select(Join));
                // No timeout here may free a live source. The outer deadline retains this lifetime/root.
                foreach (var source in sources) source.Dispose();
            }
        }
        private async Task Join(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { lock (_gate) if (!_expected.Contains(task)) _failures.Add(ex); }
        }
    }

    private sealed class Hook(Fixture fixture) : AgentRunLifecycle
    {
        internal AgentRunId RunId { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal bool HoldCancellation { get; set; }
        internal bool HoldClosing { get; set; }
        internal bool AccessSessionInCallback { get; set; }
        internal bool FailCancellation { get; set; }
        internal bool FailStarted { get; set; }
        internal bool HoldStarted { get; set; }
        internal bool StartedCompleted { get; private set; }
        internal int ClosingCalls { get; private set; }
        internal InvalidOperationException CancellationFailure { get; } = new("Inert callback failure.");
        internal TaskCompletionSource CallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StartedEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ClosingEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseClosing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        internal void ReleaseAll() { ReleaseStarted.TrySetResult(); ReleaseCancellation.TrySetResult(); ReleaseClosing.TrySetResult(); }
        public override async Task StartedAsync(AgentRunId runId, CancellationToken executionToken)
        {
            RunId = runId;
            Token = executionToken;
            if (HoldCancellation)
                _registration = executionToken.Register(() =>
                {
                    if (AccessSessionInCallback)
                    {
                        var stateAccess = fixture.Keep(fixture.Session.SteerAsync(new AgentSteerOptions
                            { Input = AgentInput.Text("inert callback"), ExpectedRunId = runId }));
                        stateAccess.GetAwaiter().GetResult();
                    }
                    CallbackEntered.TrySetResult();
                    ReleaseCancellation.Task.GetAwaiter().GetResult();
                    if (FailCancellation) throw CancellationFailure;
                });
            StartedEntered.TrySetResult();
            if (HoldStarted) await ReleaseStarted.Task;
            if (FailStarted) throw new InvalidOperationException("Inert Started failure.");
            StartedCompleted = true;
        }
        public override async Task ClosingAsync(AgentRunId runId)
        {
            ClosingCalls++;
            ClosingEntered.TrySetResult();
            var registrationDisposal = fixture.Keep(_registration.DisposeAsync().AsTask());
            await registrationDisposal;
            if (HoldClosing) await ReleaseClosing.Task;
        }
    }

    private sealed class Turn
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Fail { get; set; }
    }

    private sealed class Executor : IModelProviderTurnExecutor, IAgentProviderSessionCleanup
    {
        internal Turn Next { get; set; } = null!;
        internal int Calls { get; private set; }
        internal int CleanupCalls { get; private set; }
        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request,
            Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            var turn = Next;
            Calls++;
            turn.Started.TrySetResult();
            await turn.Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            if (turn.Fail) throw new InvalidOperationException("Inert execution failure.");
            return new AgentTurnResponse { AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Inert answer.")]) };
        }
        public ValueTask DisposeProviderSessionAsync(string sessionId)
        {
            CleanupCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
