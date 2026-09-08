using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// Execution requires separate source/body admission. Only the registered fake implements provider operations.
/// Every fixture keeps its explicit root, original work and observers on failure or unconfirmed cleanup.
/// </summary>
[TestClass]
public sealed class SessionRuntimeForwardingLifetimeTests
{
    [TestMethod]
    public Task PendingAgentPrompt_QueuedTailReplacesAttachmentWithoutSelfJoin() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var old = f.Provider.Latest;
        var first = await f.Wait(f.Runtime.QueuePromptAsync(f.Session, "queued", "send", null));
        await f.Wait(f.Runtime.SetActiveSessionAgentPromptIdAsync(f.Session.SessionId, "plan"));
        f.Provider.HoldPreparation = true;
        old.EmitIdle();
        await f.Ready(f.Provider.ReplacementPreparationStarted.Task);
        // Replacement owns a transition, but no actor: an admitted queue mutation must finish
        // while provider setup remains held, and survive setup's later local-state update.
        var second = await f.Wait(f.Runtime.QueuePromptAsync(f.Session, "during replacement", "send", null));
        Assert.IsFalse(f.Provider.ReleasePreparation.Task.IsCompleted);
        f.Provider.ReleasePreparation.TrySetResult();
        await f.Ready(f.Provider.SendStarted.Task);
        Assert.AreNotSame(old, f.Provider.Latest);
        Assert.AreEqual("plan", f.Provider.Latest.Options.AgentPromptId);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Ready(f.Provider.SecondSendStarted.Task);
        await f.Wait(f.Runtime.DisposeAsync().AsTask());
        var state = await f.Wait(f.Journal.ReadLatestStateAsync(f.Session.SessionId, f.Session.CreatedAt));
        Assert.IsNotNull(state);
        Assert.AreEqual(2, state.QueuedPrompts.Count);
        CollectionAssert.AreEquivalent(new[] { first.QueueItemId, second.QueueItemId }, state.QueuedPrompts.Select(item => item.QueueItemId).ToArray());
        Assert.IsTrue(state.QueuedPrompts.All(item => item.State == "submitted"));
        Assert.AreEqual("plan", state.AgentPromptId);
    });

    [TestMethod]
    public Task FastQueuedRun_PreservesCompletionAndRecursiveDrain() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var first = await f.Wait(f.Runtime.QueuePromptAsync(f.Session, "first", "send", null));
        var second = await f.Wait(f.Runtime.QueuePromptAsync(f.Session, "second", "send", null));
        f.Provider.ReleaseSend.TrySetResult();
        f.Provider.Latest.EmitIdle();
        await f.Ready(f.Provider.SecondSendStarted.Task);
        await f.Wait(f.Runtime.DisposeAsync().AsTask());
        var state = await f.Wait(f.Journal.ReadLatestStateAsync(f.Session.SessionId, f.Session.CreatedAt));
        Assert.IsNotNull(state);
        Assert.AreEqual(2, state.QueuedPrompts.Count);
        CollectionAssert.AreEquivalent(new[] { first.QueueItemId, second.QueueItemId }, state.QueuedPrompts.Select(item => item.QueueItemId).ToArray());
        Assert.IsTrue(state.QueuedPrompts.All(item => item.State == "submitted"));
    });

    [TestMethod]
    public Task ConcurrentEnsure_SerializesReplacementAndRevalidatesOptions() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        f.Provider.HoldAbort = true;
        var first = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.OptionsFor("first-model")));
        await f.Ready(f.Provider.AbortStarted.Task);
        var second = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.OptionsFor("second-model")));
        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(second.IsCompleted);
        Assert.AreEqual(1, f.Provider.AttachmentCount);
        f.Provider.ReleaseAbort.TrySetResult();
        Assert.AreNotEqual(await f.Wait(first), await f.Wait(second));
        await f.Wait(f.Runtime.DisposeAsync().AsTask());
        // Either waiter can win mailbox admission after the ticket settles. Both must revalidate
        // and receive distinct handles for their incompatible options, irrespective of winner.
        Assert.IsTrue(f.Provider.AttachmentCount >= 3);
        CollectionAssert.Contains(f.Provider.AttachmentModels, "first-model");
        CollectionAssert.Contains(f.Provider.AttachmentModels, "second-model");
    });

    [TestMethod]
    public Task BlankSession_ConcurrentEnsureReservesOneIdentity() => Fixture.Run(async f =>
    {
        f.Session.SessionId = string.Empty;
        var first = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var second = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        Assert.AreEqual(await f.Wait(first), await f.Wait(second));
        Assert.IsTrue(Guid.TryParse(f.Session.SessionId, out _));
        Assert.AreEqual(1, f.Provider.Creates);
    });

    [TestMethod]
    public Task Compact_UsesRetirementProtocolOutsideActor() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        await f.Wait(f.Runtime.SetActiveSessionAgentPromptIdAsync(f.Session.SessionId, "plan"));
        await f.Wait(f.Runtime.CompactAsync(f.Session, f.Options));
        Assert.AreEqual(1, f.Provider.Compactions);
        Assert.AreEqual(2, f.Provider.AttachmentCount);
    });

    [TestMethod]
    public Task History_ReusesLiveAttachmentAndResumesMissingAttachment() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.GetOrResumeHistoryAsync(f.Session, f.Options));
        var first = f.Provider.Latest;
        await f.Wait(f.Runtime.GetHistoryAsync(f.Session.SessionId));
        await f.Wait(f.Runtime.GetOrResumeHistoryAsync(f.Session, f.OptionsFor("ignored-live-model")));
        Assert.AreSame(first, f.Provider.Latest);
        Assert.AreEqual(3, f.Provider.HistoryReads);
        await f.Wait(f.Runtime.DetachRuntimeSessionAsync(f.Session.SessionId));
        await f.Wait(f.Runtime.GetOrResumeHistoryAsync(f.Session, f.OptionsFor("resumed-model")));
        Assert.AreNotSame(first, f.Provider.Latest);
        Assert.AreEqual("resumed-model", f.Provider.Latest.Options.Model);
    });

    [TestMethod]
    public Task Detach_ConcurrentEnsureDoesNotRemoveReplacementActor() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        f.Provider.HoldAbort = true;
        var detach = f.Track(f.Runtime.DetachRuntimeSessionAsync(f.Session.SessionId));
        await f.Ready(f.Provider.AbortStarted.Task);
        var ensure = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        Assert.IsFalse(detach.IsCompleted);
        Assert.IsFalse(ensure.IsCompleted);
        f.Provider.ReleaseAbort.TrySetResult();
        await f.Wait(detach);
        await f.Wait(ensure);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        Assert.IsTrue(await f.Wait(f.Runtime.HasActiveCoordinatorSessionAsync(f.Session.SessionId)));
    });

    [TestMethod]
    public Task Shutdown_AbortsQueuedRunBeforeJoiningItsTail() => Fixture.Run(async f =>
    {
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        await f.Wait(f.Runtime.QueuePromptAsync(f.Session, "held", "send", null));
        f.Provider.Latest.EmitIdle();
        await f.Ready(f.Provider.SendStarted.Task);
        var close = f.Track(f.Runtime.DisposeAsync().AsTask());
        await f.Ready(f.Provider.AbortStarted.Task);
        await f.Wait(close);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    });

    [TestMethod]
    public async Task Shutdown_RetiresLateAttachmentAndSubscription()
    {
        await Fixture.Run(async f =>
        {
        f.Provider.HoldPreparation = true;
        var ensure = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        await f.Ready(f.Provider.PreparationStarted.Task);
        var close = f.Track(f.Runtime.DisposeAsync().AsTask());
        Assert.IsFalse(close.IsCompleted);
        f.Provider.ReleasePreparation.TrySetResult();
        await f.Expect<ObjectDisposedException>(ensure);
        await f.Wait(close);
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
        });
        await Fixture.Run(async f =>
        {
            var closed = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Provider.BeforeSubscriptionReturn = () => closed.TrySetResult(f.Track(f.Runtime.DisposeAsync().AsTask()));
            var ensure = f.Track(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
            await f.Ready(closed.Task);
            await f.Expect<ObjectDisposedException>(ensure);
            await f.Wait(await closed.Task);
            Assert.AreEqual(1, f.Provider.SubscriptionsDisposed);
            Assert.AreEqual(0, f.Provider.EarlyDisposals);
        });
    }

    [TestMethod]
    public Task ParentDelivery_SelfAndCyclicRoutesReleaseSourceUses() => Fixture.Run(async f =>
    {
        // Cached metadata lookup is the real parent finishing route; no catalog enumeration/reader.
        f.Session.ParentSessionId = f.Session.SessionId;
        await f.Persist(f.Session);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var self = f.Provider.Latest;
        self.EmitNotification("self");
        await f.Ready(self.SendStarted.Task);
        f.Provider.ReleaseSend.TrySetResult();
        await f.Ready(self.SendFinished.Task);
        var parent = f.NewSession();
        parent.ParentSessionId = f.Session.SessionId;
        f.Session.ParentSessionId = parent.SessionId;
        await f.Persist(parent);
        await f.Persist(f.Session);
        await f.Wait(f.Runtime.DetachRuntimeSessionAsync(f.Session.SessionId));
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));
        var source = f.Provider.Latest;
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(parent, f.Options));
        var target = f.Provider.Latest;
        source.EmitNotification("to-parent");
        await f.Ready(target.SendStarted.Task);
        await f.Ready(target.SendFinished.Task);
        target.EmitNotification("to-child");
        await f.Ready(source.SendStarted.Task);
        await f.Ready(source.SendFinished.Task);
        await f.Wait(f.Runtime.DisposeAsync().AsTask());
        Assert.AreEqual(0, f.Provider.EarlyDisposals);
    });

    private sealed class Fixture
    {
        private readonly object _gate = new();
        private readonly List<Task> _work = [];
        private readonly List<Task> _observers = [];
        private readonly List<Exception> _failures = [];
        private readonly List<Exception> _observed = [];
        private readonly HashSet<Exception> _expected = [];
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-forwarding-" + Guid.NewGuid().ToString("N"));
        private CodeAltaHost? _host;
        private Task? _close;
        private Task? _setup;
        private Task? _body;
        private bool _cleaning;
        private string _projectId = string.Empty;
        internal Provider Provider { get; } = new();
        internal SessionViewDescriptor Session { get; private set; } = null!;
        internal SessionViewJournalStore Journal { get; private set; } = null!;
        internal SessionRuntimeService Runtime { get { lock (_gate) return _host!.RuntimeService; } }
        internal SessionExecutionOptions Options => OptionsFor("fixture-model");
        internal SessionExecutionOptions OptionsFor(string model) => new()
        {
            ProviderId = Provider.Descriptor.ProviderId,
            ProviderKey = Provider.Descriptor.ProviderId.Value,
            WorkingDirectory = Path.Combine(_root, "project"),
            Model = model,
            ProjectRoots = [Path.Combine(_root, "project")],
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
            OnUserInputRequest = static (_, _) => Task.FromCanceled<AgentUserInputResponse>(new CancellationToken(true)),
        };

        internal static async Task Run(Func<Fixture, Task> body)
        {
            var f = new Fixture();
            try
            {
                await f.Wait(f.Start(f.Setup, setup: true));
                await f.Wait(f.Start(() => body(f), setup: false));
            }
            catch (Exception ex) { lock (f._gate) f._failures.Add(ex); }
            finally
            {
                lock (f._gate) f._cleaning = true;
                f.Attempt(f.Provider.ReleaseAll);
                // Observe setup, body and available cleanup independently, even if setup times out.
                var producers = new List<Task>();
                if (f._setup is not null) producers.Add(f._setup);
                if (f._body is not null) producers.Add(f._body);
                f.Attempt(() => { if (f.HasHost) producers.Add(f.Close()); });
                await f.Drain(producers);
                var lateCleanup = new List<Task>();
                f.Attempt(() => { if (f.HasHost) lateCleanup.Add(f.Close()); });
                await f.Drain(lateCleanup);
                Task[] available;
                lock (f._gate) available = [.. f._work, .. f._observers];
                await f.Drain(available);
                bool settled;
                lock (f._gate) settled = (f._setup is null || f._setup.IsCompleted) && (f._body is null || f._body.IsCompleted) && (f._close is null || f._close.IsCompleted);
                if (settled)
                {
                    Task[] final;
                    lock (f._gate) final = [.. f._work, .. f._observers];
                    await f.Drain(final);
                }
                // Roots deliberately remain available for parent inspection. No fixture deletes data.
            }
            Exception[] failures;
            lock (f._gate) failures = f._failures.Concat(f._observed.Where(ex => !f._expected.Contains(ex))).Distinct().ToArray();
            if (failures.Length != 0)
            {
                var failure = new AggregateException("Forwarding fixture retained at " + f._root, failures);
                failure.Data["RetainedFixture"] = f;
                throw failure;
            }
        }

        private Task Start(Func<Task> operation, bool setup)
        {
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = Track(Invoke());
            lock (_gate) { if (setup) _setup = work; else _body = work; }
            launch.TrySetResult();
            return work;
            async Task Invoke() { await launch.Task.ConfigureAwait(false); await operation().ConfigureAwait(false); }
        }

        private bool HasHost { get { lock (_gate) return _host is not null; } }
        private void Attempt(Action action) { try { action(); } catch (Exception ex) { lock (_gate) _failures.Add(ex); } }

        internal async Task<T> Expect<T>(Task task) where T : Exception
        {
            var error = await Assert.ThrowsExactlyAsync<T>(() => task);
            lock (_gate) _expected.Add(error);
            return error;
        }

        private async Task Setup()
        {
            for (var node = new DirectoryInfo(Path.GetDirectoryName(_root)!); node is not null; node = node.Parent)
                if ((node.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse fixture ancestry.");
            if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("Fixture root already exists.");
            var home = Path.Combine(_root, "home");
            var global = Path.Combine(_root, "global");
            var project = Path.Combine(_root, "project");
            var builtin = Path.Combine(_root, "builtin");
            foreach (var path in new[] { home, global, project, builtin }) Directory.CreateDirectory(path);
            // Close the existing builtin Glob/Git configuration lookup to this task-owned root.
            var git = Path.Combine(builtin, ".git");
            Directory.CreateDirectory(git);
            File.WriteAllText(Path.Combine(git, "config"), "[core]\nignorecase = true\nexcludesfile = fixture.ignore\n");
            File.WriteAllText(Path.Combine(git, "fixture.ignore"), "");
            // SystemPromptContentLocator maps GlobalRoot/prompts; the builder requires a named
            // replacement resource. Default system content remains the existing shipped resource.
            var prompts = Path.Combine(global, "prompts", "agents");
            Directory.CreateDirectory(prompts);
            File.WriteAllText(Path.Combine(prompts, "plan.prompt.md"), "---\nname: plan\ndescription: Task-owned forwarding fixture.\nmode: replace\nsystem: default\n---\nFixture plan instructions.\n");
            var catalog = new CatalogOptions { GlobalRoot = global };
            _projectId = (await Track(new ProjectCatalog(catalog).UpsertFromPathAsync(project))).Id;
            Journal = new SessionViewJournalStore(catalog);
            Session = NewSession();
            await Persist(Session);
            var creation = Track(CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = project,
                DiscoveryScope = new SessionDiscoveryScope(home, _root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty,
                StartPlugins = false, OwnsLogging = false, IsHeadless = true,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(Provider.Descriptor, Provider.CreateRuntime),
            }));
            var host = await creation;
            bool close;
            lock (_gate) { _host = host; close = _cleaning; }
            if (close) await Close();
        }

        internal SessionViewDescriptor NewSession() => new()
        {
            SessionId = Guid.CreateVersion7().ToString(), Kind = SessionViewKind.ProjectSession,
            ProjectRef = _projectId, ProviderId = Provider.Descriptor.ProviderId.Value,
            ProviderKey = Provider.Descriptor.ProviderId.Value, WorkingDirectory = Path.Combine(_root, "project"),
            Title = "Forwarding fixture", ModelId = "fixture-model", AgentPromptId = "default",
            CreatedAt = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero),
        };

        internal async Task Persist(SessionViewDescriptor session)
        {
            var workingDirectory = session.WorkingDirectory;
            ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
            var providerKey = session.ProviderKey;
            ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
            await Track(Journal.EnsureHeaderAsync(session));
            var store = Journal.CreateSessionStore();
            await Track(store.UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = session.SessionId, ProviderId = Provider.Descriptor.ProviderId,
                ProviderKey = providerKey, WorkingDirectory = workingDirectory,
                Title = session.Title, CreatedAt = session.CreatedAt, UpdatedAt = session.CreatedAt,
                ModelId = session.ModelId, AgentPromptId = session.AgentPromptId,
            }));
            await Track(Journal.AppendStateAsync(session, new SessionViewLocalState
            {
                ProviderKey = providerKey, ModelId = session.ModelId,
                AgentPromptId = session.AgentPromptId, ParentSessionId = session.ParentSessionId,
            }));
            var readback = await Track(store.GetSessionAsync(session.SessionId));
            Assert.IsNotNull(readback);
            Assert.AreEqual(session.SessionId, readback.SessionId);
            Assert.AreEqual(session.WorkingDirectory, readback.WorkspacePath);
            Assert.AreEqual(session.ParentSessionId, readback.ViewState?.ParentSessionId);
        }

        internal Task Track(Task task)
        {
            lock (_gate) { _work.Add(task); _observers.Add(Observe(task)); }
            return task;
        }
        internal Task<T> Track<T>(Task<T> task) { Track((Task)task); return task; }
        internal Task Wait(Task task) => Track(Track(task).WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task<T> Wait<T>(Task<T> task) => Track(Track(task).WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task Ready(Task readiness) => Track(readiness.WaitAsync(TimeSpan.FromSeconds(5)));
        private async Task Observe(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { lock (_gate) _observed.Add(ex); }
        }
        private Task Close()
        {
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task close;
            CodeAltaHost host;
            lock (_gate)
            {
                if (_close is not null) return _close;
                host = _host!;
                close = Track(Dispose());
                _close = close;
            }
            launch.TrySetResult();
            return close;
            async Task Dispose() { await launch.Task.ConfigureAwait(false); await host.DisposeAsync().ConfigureAwait(false); }
        }
        private async Task Drain(IEnumerable<Task> work)
        {
            var waits = work.Select(task => task.WaitAsync(TimeSpan.FromSeconds(5))).ToArray();
            var observers = waits.Select(ObserveCleanup).ToArray();
            foreach (var observer in observers) await observer.ConfigureAwait(false);
        }

        private async Task ObserveCleanup(Task wait)
        {
            try { await wait.ConfigureAwait(false); }
            catch (TimeoutException ex) { lock (_gate) _failures.Add(ex); }
            catch { /* Original observers retain all other errors; Expect confirms exact instances. */ }
        }
    }

    private sealed class Provider
    {
        private readonly object _gate = new();
        private readonly List<Session> _sessions = [];
        private int _sends, _creates, _compactions, _historyReads, _earlyDisposals, _subscriptionsDisposed;
        private bool _cleaning, _holdPreparation, _holdAbort;
        internal bool HoldPreparation { get { lock (_gate) return _holdPreparation; } set { lock (_gate) _holdPreparation = !_cleaning && value; } }
        internal bool HoldAbort { get { lock (_gate) return _holdAbort; } set { lock (_gate) _holdAbort = !_cleaning && value; } }
        internal Action? BeforeSubscriptionReturn { get; set; }
        internal TaskCompletionSource PreparationStarted { get; } = NewGate();
        internal TaskCompletionSource ReplacementPreparationStarted { get; } = NewGate();
        internal TaskCompletionSource ReleasePreparation { get; } = NewGate();
        internal TaskCompletionSource SendStarted { get; } = NewGate();
        internal TaskCompletionSource SecondSendStarted { get; } = NewGate();
        internal TaskCompletionSource ReleaseSend { get; } = NewGate();
        internal TaskCompletionSource AbortStarted { get; } = NewGate();
        internal TaskCompletionSource ReleaseAbort { get; } = NewGate();
        internal ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("forwarding-fixture"), "Forwarding fixture") { DefaultModelId = "fixture-model" };
        internal Session Latest { get { lock (_gate) return _sessions[^1]; } }
        internal int AttachmentCount { get { lock (_gate) return _sessions.Count; } }
        internal string?[] AttachmentModels { get { lock (_gate) return _sessions.Select(session => session.Options.Model).ToArray(); } }
        internal int Creates => Volatile.Read(ref _creates);
        internal int Compactions => Volatile.Read(ref _compactions);
        internal int HistoryReads => Volatile.Read(ref _historyReads);
        internal int EarlyDisposals => Volatile.Read(ref _earlyDisposals);
        internal int SubscriptionsDisposed => Volatile.Read(ref _subscriptionsDisposed);
        private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IModelProviderRuntime CreateRuntime() => new Runtime(this);
        internal void ReleaseAll()
        {
            lock (_gate) { _cleaning = true; _holdPreparation = false; _holdAbort = false; }
            ReleasePreparation.TrySetResult(); ReleaseSend.TrySetResult(); ReleaseAbort.TrySetResult();
        }

        private sealed class Runtime(Provider owner) : IModelProviderSessionRuntime
        {
            private Session? _session;
            public ModelProviderDescriptor Descriptor => owner.Descriptor;
            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Probe forbidden.");
            public IModelProviderTurnExecutor CreateTurnExecutor() => throw new InvalidOperationException("Turn executor forbidden.");
            public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
            { Interlocked.Increment(ref owner._creates); return Prepare(options.SessionId!, options); }
            public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default) => Prepare(sessionId, options);
            private async Task<IAgentSession> Prepare(string id, AgentSessionCreateOptions options)
            {
                owner.PreparationStarted.TrySetResult();
                if (owner.AttachmentCount > 0) owner.ReplacementPreparationStarted.TrySetResult();
                if (owner.HoldPreparation) await owner.ReleasePreparation.Task.ConfigureAwait(false);
                _session = new Session(owner, id, options);
                lock (owner._gate) owner._sessions.Add(_session);
                return _session;
            }
            public ValueTask DisposeAsync()
            {
                if (_session?.Active != 0 && _session is not null) Interlocked.Increment(ref owner._earlyDisposals);
                return ValueTask.CompletedTask;
            }
        }

        internal sealed class Session(Provider owner, string id, AgentSessionCreateOptions options) : IAgentSession
        {
            private readonly object _gate = new();
            private Action<AgentEvent>? _handler;
            private int _active;
            internal int Active => Volatile.Read(ref _active);
            internal TaskCompletionSource SendStarted { get; } = NewGate();
            internal TaskCompletionSource SendFinished { get; } = NewGate();
            internal AgentSessionCreateOptions Options => options;
            public ModelProviderId ProviderId => owner.Descriptor.ProviderId;
            public string SessionId => id;
            public string? WorkspacePath => options.WorkingDirectory;
            public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
            { await Task.CompletedTask; yield break; }
            public IDisposable Subscribe(Action<AgentEvent> handler)
            {
                lock (_gate) _handler = handler;
                EmitIdle(); // Deliberately synchronous and before the receipt is returned.
                owner.BeforeSubscriptionReturn?.Invoke();
                return new Subscription(this);
            }
            private void Emit(AgentEvent value) { Action<AgentEvent>? handler; lock (_gate) handler = _handler; handler?.Invoke(value); }
            internal void EmitIdle() => Emit(new AgentSessionUpdateEvent(ProviderId, id, DateTimeOffset.UtcNow, null, AgentSessionUpdateKind.Idle, "fixture"));
            internal void EmitNotification(string content) => Emit(new AgentContentCompletedEvent(ProviderId, id, DateTimeOffset.UtcNow, null, AgentContentKind.Assistant, Guid.NewGuid().ToString(), null, "<notify-parent>" + content + "</notify-parent>"));
            public async Task<AgentRunId> SendAsync(AgentSendOptions send, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _active);
                try
                {
                    var count = Interlocked.Increment(ref owner._sends);
                    (count == 1 ? owner.SendStarted : owner.SecondSendStarted).TrySetResult();
                    SendStarted.TrySetResult();
                    // Abort, not cancellation alone, releases this controlled provider operation.
                    await owner.ReleaseSend.Task.ConfigureAwait(false);
                    EmitIdle();
                    return new AgentRunId("fixture-" + count);
                }
                finally { Interlocked.Decrement(ref _active); SendFinished.TrySetResult(); }
            }
            public async Task AbortAsync(CancellationToken cancellationToken = default)
            {
                owner.AbortStarted.TrySetResult();
                if (owner.HoldAbort) await owner.ReleaseAbort.Task.ConfigureAwait(false);
                owner.ReleaseSend.TrySetResult();
            }
            public Task<AgentRunId> SteerAsync(AgentSteerOptions steer, CancellationToken cancellationToken = default) => Task.FromResult(new AgentRunId("steered"));
            public Task CompactAsync(CancellationToken cancellationToken = default)
            { Interlocked.Increment(ref owner._compactions); EmitIdle(); return Task.CompletedTask; }
            public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default)
            { Interlocked.Increment(ref owner._historyReads); return Task.FromResult<IReadOnlyList<AgentEvent>>([]); }
            public ValueTask DisposeAsync()
            { if (Active != 0) Interlocked.Increment(ref owner._earlyDisposals); return ValueTask.CompletedTask; }
            private void Unsubscribe()
            {
                lock (_gate) _handler = null;
                Interlocked.Increment(ref owner._subscriptionsDisposed);
            }
            private sealed class Subscription(Session session) : IDisposable
            { public void Dispose() => session.Unsubscribe(); }
        }
    }
}
