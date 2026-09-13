using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// Source-only until separately admitted. Uses one explicit retained root, inert provider and prompt/skill
/// locators. Journal routes can open the existing SQLite projection cache; no host/default providers/plugins.
/// </summary>
[TestClass]
public sealed class SessionRuntimeFileSearchInvalidationTests
{
    [TestMethod]
    public async Task Provider_AllFileChangePhasesAndDiffInvalidateWithoutReader()
    {
        foreach (var phase in Enum.GetValues<AgentActivityPhase>())
            await Fixture.Run(async f =>
            {
                await f.Keep(() => f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options()));
                var original = f.Activity(AgentActivityKind.FileChange, phase);
                f.Provider.Latest.Emit(original);
                await f.CloseRuntime(); // Emission/actor acceptance alone is not the effect join.
                await f.AssertDirty(f.Work, true);
                Assert.AreSame(original, (await f.ReadClosedOriginals()).OfType<SessionAgentEvent>().Single(e => ReferenceEquals(e.Event, original)).Event);
            });
        await Fixture.Run(async f =>
        {
            await f.Keep(() => f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options()));
            f.Provider.Latest.Emit(f.Update(AgentSessionUpdateKind.DiffUpdated));
            await f.CloseRuntime();
            await f.AssertDirty(f.Work, true);
        });
    }

    [TestMethod]
    public Task Append_ExactPredicateMatrixAndAllPhases() => Fixture.Run(async f =>
    {
        foreach (var kind in Enum.GetValues<AgentActivityKind>())
            foreach (var phase in Enum.GetValues<AgentActivityPhase>())
            {
                await f.ResetCache();
                await f.Keep(() => f.Runtime.AppendSessionEventAsync(f.Session, f.Activity(kind, phase)));
                await f.AssertDirty(f.Work, kind == AgentActivityKind.FileChange);
            }
        foreach (var kind in Enum.GetValues<AgentSessionUpdateKind>())
        {
            await f.ResetCache();
            await f.Keep(() => f.Runtime.AppendSessionEventAsync(f.Session, f.Update(kind)));
            await f.AssertDirty(f.Work, kind == AgentSessionUpdateKind.DiffUpdated);
        }
        await f.ResetCache();
        await f.Keep(() => f.Runtime.AppendSessionEventAsync(f.Session, f.Assistant("unrelated")));
        await f.AssertDirty(f.Work, false);
        Assert.AreEqual(0, f.Provider.Preparations);
    });

    [TestMethod]
    public Task Provider_StreamPressureDropsOriginalButNotCacheEffect() => Fixture.Run(async f =>
    {
        await f.Keep(() => f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options()));
        // 1,024 appends necessarily fill the capacity even if preparation also published events.
        for (var i = 0; i < 1024; i++)
            await f.Keep(() => f.Runtime.AppendSessionEventAsync(f.Session, f.Update(AgentSessionUpdateKind.Warning)));
        var droppedBefore = f.Runtime.DroppedRuntimeEventCount;
        var original = f.Update(AgentSessionUpdateKind.DiffUpdated);
        f.Provider.Latest.Emit(original);
        await f.CloseRuntime();
        Assert.IsTrue(f.Runtime.DroppedRuntimeEventCount > droppedBefore);
        await f.AssertDirty(f.Work, true);
        Assert.IsFalse((await f.ReadClosedOriginals()).OfType<SessionAgentEvent>().Any(e => ReferenceEquals(original, e.Event)));
    });

    [TestMethod]
    public Task Provider_ProjectedAssistantIsNotRawAndDoesNotInvalidate() => Fixture.Run(async f =>
    {
        await f.Keep(() => f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options()));
        var raw = f.Assistant("visible\n```codealta_schedule\nhidden\n```");
        f.Provider.Latest.Emit(raw);
        f.Provider.Latest.Emit(f.Assistant("```codealta_schedule\nhidden\n```"));
        await f.CloseRuntime();
        var projected = (await f.ReadClosedOriginals()).OfType<SessionAgentEvent>()
            .Select(e => e.Event).OfType<AgentContentCompletedEvent>().Single(e => e.ContentId == raw.ContentId);
        Assert.AreNotSame(raw, projected);
        Assert.AreEqual("visible", projected.Content);
        await f.AssertDirty(f.Work, false);
    });

    [TestMethod]
    public Task Provider_AdmittedOldCallbackUsesCapturedDirectory_ClosedCallbackIsRejected() => Fixture.Run(async f =>
    {
        await f.Keep(() => f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options()));
        var old = f.Provider.Latest;
        var callback = old.CaptureCallback();
        var hold = f.Keep(() => f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options(f.Tools)));
        await f.Tools.Entered.Task;
        // Hold real actor Matches/Tools.Count, not a test-only runtime gate. Both operations are
        // admitted before release; the event's projector retains the old immutable entry.
        var admitted = f.Activity(AgentActivityKind.FileChange, AgentActivityPhase.Completed);
        callback(admitted);
        f.Session.WorkingDirectory = f.Other;
        f.Provider.HoldAbort = true;
        var replace = f.Keep(() => f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options(directory: f.Other)));
        f.Tools.Release.TrySetResult();
        await f.Provider.AbortEntered.Task;
        Assert.IsFalse(replace.IsCompleted);
        var lateAdmitted = f.Update(AgentSessionUpdateKind.DiffUpdated);
        callback(lateAdmitted); // Callback admission stays open until abort has returned.
        f.Provider.ReleaseAbort.TrySetResult();
        await hold;
        await replace;
        Assert.IsTrue(old.SubscriptionDisposed);
        var rejected = f.Activity(AgentActivityKind.FileChange, AgentActivityPhase.Failed);
        callback(rejected); // Actual unsubscription follows callback-admission closure.
        await f.CloseRuntime();
        await f.AssertDirty(f.Work, true);
        await f.AssertDirty(f.Other, false);
        var events = (await f.ReadClosedOriginals()).OfType<SessionAgentEvent>().ToArray();
        Assert.AreEqual(1, events.Count(e => e.Event is AgentActivityEvent { Kind: AgentActivityKind.FileChange }));
        Assert.AreEqual(1, events.Count(e => e.Event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.DiffUpdated }));
        Assert.AreSame(admitted, events.Single(e => e.Event is AgentActivityEvent { Kind: AgentActivityKind.FileChange }).Event);
        Assert.AreSame(lateAdmitted, events.Single(e => e.Event is AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.DiffUpdated }).Event);
        Assert.IsFalse(events.Any(e => ReferenceEquals(e.Event, rejected)));
        Assert.IsTrue(old.SubscriptionDisposed);
    });

    [TestMethod]
    public Task Append_CapturesDirectoryBeforeAwait_ClosureJoinsAdmittedOriginal() => Fixture.Run(async f =>
    {
        f.Catalog.HoldInvalidation = true;
        var original = f.Update(AgentSessionUpdateKind.DiffUpdated);
        var append = f.Keep(() => f.Runtime.AppendSessionEventAsync(f.Session, original));
        await f.Catalog.Entered.Task;
        f.Session.WorkingDirectory = f.Other;
        var close = f.CloseRuntime();
        await f.ClosureStarted.Task;
        Assert.IsFalse(close.IsCompleted);
        var rejected = f.Keep(() => f.Runtime.AppendSessionEventAsync(f.Session, f.Activity(AgentActivityKind.FileChange, AgentActivityPhase.Started)));
        await f.Expect<ObjectDisposedException>(rejected);
        f.Catalog.Release.TrySetResult();
        await append;
        await close;
        await f.AssertDirty(f.Work, true);
        await f.AssertDirty(f.Other, false);
        Assert.AreSame(original, (await f.ReadClosedOriginals()).OfType<SessionAgentEvent>().Single().Event);
    });

    [TestMethod]
    public Task Append_BlankAndInvalidDirectoriesAreBestEffort_ValidationStillFails() => Fixture.Run(async f =>
    {
        await f.Expect<ArgumentNullException>(f.Keep(() => f.Runtime.AppendSessionEventAsync(null!, f.Update(AgentSessionUpdateKind.DiffUpdated))));
        await f.Expect<ArgumentNullException>(f.Keep(() => f.Runtime.AppendSessionEventAsync(f.Session, null!)));
        await f.Expect<ArgumentException>(f.Keep(() => f.Runtime.AppendSessionEventAsync(f.Session,
            new AgentSessionUpdateEvent(new("cache-inert"), "wrong-session", DateTimeOffset.UnixEpoch, null, AgentSessionUpdateKind.DiffUpdated, null))));
        foreach (var directory in new[] { "", " ", "invalid\0directory" })
        {
            f.Session.WorkingDirectory = directory;
            await f.Keep(() => f.Runtime.AppendSessionEventAsync(f.Session, f.Update(AgentSessionUpdateKind.DiffUpdated)));
        }
        await f.CloseRuntime();
        Assert.AreEqual(3, (await f.ReadClosedOriginals()).OfType<SessionAgentEvent>().Count());
        await f.AssertDirty(f.Work, false);
    });

    [TestMethod]
    public Task Display_ClosedObservationDoesNotReplayCacheInvalidation() => Fixture.Run(async f =>
    {
        await f.Keep(() => f.Runtime.AppendSessionEventAsync(f.Session, f.Update(AgentSessionUpdateKind.DiffUpdated)));
        await f.CloseRuntime();
        await f.AssertDirty(f.Work, true);
        await f.ResetCache();
        await f.Keep(async () =>
        {
            _ = f.Runtime.Display.GetSnapshot();
            await foreach (var snapshot in f.Runtime.Display.ObserveAsync()) Assert.IsTrue(snapshot.Snapshot.IsClosed);
        });
        await f.AssertDirty(f.Work, false);
    });

    [TestMethod]
    public async Task Fixture_CancellationShapedPrimaryAndCleanupRemainDistinct()
    {
        var primary = new OperationCanceledException("fixture primary");
        var cleanup = new OperationCanceledException("fixture cleanup");
        var failure = await Assert.ThrowsExactlyAsync<AggregateException>(() => Fixture.Run(f =>
        {
            f.CleanupFailure = cleanup;
            return Task.FromException(primary);
        }));
        Assert.AreSame(primary, failure.Data["Primary"]);
        Assert.IsTrue(failure.Flatten().InnerExceptions.Contains(cleanup));
        Assert.IsNotNull(failure.Data["RetainedFixture"]);
    }

    private sealed class Fixture
    {
        private readonly object _gate = new();
        private readonly List<Task> _originals = [];
        private readonly List<Task<Exception?>> _outcomes = [];
        private readonly HashSet<Exception> _expected = [];
        private readonly CancellationTokenSource _cancel = new();
        private readonly TaskCompletionSource _setupDone = NewGate(), _stopLaunch = NewGate();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-cache-" + Guid.NewGuid().ToString("N"));
        private ModelProviderRegistry? _registry;
        private AgentHub? _hub;
        private SessionRuntimeService? _runtime;
        private Task? _runtimeClose, _core, _stop;
        private Task<Exception?>? _coreOutcome, _stopOutcome;
        internal readonly Provider Provider = new();
        internal readonly HeldTools Tools = new();
        internal readonly Catalog Catalog = new();
        internal readonly ProjectFileSnapshotCache Cache = new();
        internal readonly TaskCompletionSource ClosureStarted = NewGate();
        internal Exception? CleanupFailure;
        internal SessionViewDescriptor Session = null!;
        internal SessionRuntimeService Runtime => _runtime!;
        internal string Work => Path.Combine(_root, "work");
        internal string Other => Path.Combine(_root, "other");
        internal SessionExecutionOptions Options(IReadOnlyList<AgentToolDefinition>? tools = null, string? directory = null) => new()
        {
            ProviderId = Provider.Descriptor.ProviderId, ProviderKey = "cache-inert", Model = "fixture",
            WorkingDirectory = directory ?? Work, ProjectRoots = [], Tools = tools,
            OnPermissionRequest = Runtime.Permissions.OwnedDefaultPermissionHandler,
            OnUserInputRequest = Runtime.Permissions.OwnedDefaultUserInputHandler,
        };
        internal AgentActivityEvent Activity(AgentActivityKind kind, AgentActivityPhase phase) => new(
            Provider.Descriptor.ProviderId, Session.SessionId, DateTimeOffset.UnixEpoch, null, kind, phase, "activity", null, "fixture", "fixture");
        internal AgentSessionUpdateEvent Update(AgentSessionUpdateKind kind) => new(Provider.Descriptor.ProviderId, Session.SessionId, DateTimeOffset.UnixEpoch, null, kind, "fixture");
        internal AgentContentCompletedEvent Assistant(string text) => new(Provider.Descriptor.ProviderId, Session.SessionId, DateTimeOffset.UnixEpoch, null, AgentContentKind.Assistant, "assistant", null, text);

        internal static async Task Run(Func<Fixture, Task> body)
        {
            var f = new Fixture();
            var launch = NewGate();
            f._stop = f.Stop(); f._stopOutcome = Outcome(f._stop);
            f._core = f.Core(launch.Task, body); f._coreOutcome = Outcome(f._core);
            launch.TrySetResult();
            try
            {
                await f._core.WaitAsync(TimeSpan.FromSeconds(30));
                await f._coreOutcome;
                f._cancel.Dispose();
            }
            catch (Exception ex)
            {
                ex.Data["RetainedFixture"] = f; ex.Data["RetainedRoot"] = f._root;
                throw;
            }
            finally
            {
                f.Release(); // Independent cancellation and gates precede any dependent cleanup joins.
                Console.WriteLine("Retained cache fixture root: " + f._root);
            }
        }

        private async Task Core(Task launch, Func<Fixture, Task> body)
        {
            await launch;
            Exception? primary = null;
            try { await Keep(Setup); await Keep(() => body(this)); }
            catch (Exception ex) { primary = ex; }
            finally { _setupDone.TrySetResult(); Release(); }
            var cleanup = await _stopOutcome!;
            Task<Exception?>[] outcomes;
            lock (_gate) outcomes = [.. _outcomes];
            var errors = (await Task.WhenAll(outcomes)).Where(e => e is not null).Cast<Exception>()
                .Where(e => !_expected.Contains(e)).Append(primary).Append(cleanup).Where(e => e is not null).Cast<Exception>().Distinct().ToArray();
            if (errors.Length != 0)
            {
                var failure = new AggregateException("Cache fixture primary/cleanup outcomes retained.", errors);
                failure.Data["Primary"] = primary; failure.Data["Cleanup"] = cleanup;
                throw failure;
            }
        }

        internal Task Keep(Func<Task> operation) => Keep(async () => { await operation(); return true; });
        internal Task<T> Keep<T>(Func<Task<T>> operation)
        {
            var launch = NewGate();
            var original = Invoke();
            lock (_gate) { _originals.Add(original); _outcomes.Add(Outcome(original)); }
            launch.TrySetResult();
            return original;
            async Task<T> Invoke() { await launch.Task.ConfigureAwait(false); return await operation().ConfigureAwait(false); }
        }
        private static async Task<Exception?> Outcome(Task original)
        { try { await original.ConfigureAwait(false); return null; } catch (Exception ex) { return ex; } }
        internal async Task Expect<T>(Task original) where T : Exception
        { var error = await Assert.ThrowsExactlyAsync<T>(() => original); lock (_gate) _expected.Add(error); }
        private void Release()
        { Tools.Release.TrySetResult(); Catalog.Release.TrySetResult(); Provider.ReleaseAbort.TrySetResult(); _stopLaunch.TrySetResult(); }
        private async Task Stop()
        {
            await _stopLaunch.Task;
            var cancellation = Keep(() => _cancel.CancelAsync());
            var disposal = Keep(DisposeAcquired);
            await Task.WhenAll(cancellation, disposal);
        }
        private async Task DisposeAcquired()
        {
            await _setupDone.Task;
            if (_runtime is not null) await CloseRuntime();
            // Never dispose dependencies after a failed/incomplete runtime join.
            if (_hub is not null) await Keep(() => _hub.DisposeAsync().AsTask());
            if (_registry is not null) await Keep(() => _registry.DisposeAsync().AsTask());
            if (CleanupFailure is { } failure) throw failure;
        }
        internal Task CloseRuntime()
        {
            lock (_gate) return _runtimeClose ??= Keep(() =>
            {
                var original = Runtime.DisposeAsync().AsTask();
                ClosureStarted.TrySetResult();
                return original;
            });
        }

        private async Task Setup()
        {
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(_root)!); parent is not null; parent = parent.Parent)
                if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse fixture ancestry.");
            if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("Fixture root exists.");
            _cancel.Token.ThrowIfCancellationRequested();
            var home = Path.Combine(_root, "home"); var global = Path.Combine(_root, "global");
            var prompts = Path.Combine(_root, "builtin", "prompts");
            foreach (var path in new[] { Work, Other, home, global, Path.Combine(prompts, "agents"), Path.Combine(prompts, "system") }) Directory.CreateDirectory(path);
            await Keep(() => File.WriteAllTextAsync(Path.Combine(prompts, "agents", "default.prompt.md"), "---\nname: Fixture\n---\nInert cache fixture.", _cancel.Token));
            await Keep(() => File.WriteAllTextAsync(Path.Combine(prompts, "system", "default.system-prompt.md"), "---\nname: Fixture\n---\nInert system fixture.", _cancel.Token));
            var options = new CatalogOptions { GlobalRoot = global };
            var views = new SessionViewCatalog(options);
            var skills = new SkillCatalog([new NoSkillRoots(_root)]);
            var locator = new Locator(_root);
            _registry = new ModelProviderRegistry();
            _registry.RegisterOrReplace(Provider.Descriptor, Provider.CreateRuntime);
            _hub = new AgentHub(_registry, global);
            _runtime = new(_hub, Catalog, new ProjectCatalog(options), views,
                new AgentInstructionTemplateProvider(skills, options, locator, null, new(home, _root)), options, skills)
            { PromptCatalog = new AgentPromptCatalog(locator), FileSearchCache = Cache };
            Session = new() { SessionId = "cache-session", ProviderId = "cache-inert", ProviderKey = "cache-inert", Kind = SessionViewKind.GlobalSession,
                WorkingDirectory = Work, Title = "Cache fixture", CreatedAt = DateTimeOffset.UnixEpoch, AgentPromptId = "default", ModelId = "fixture" };
            _setupDone.TrySetResult();
        }
        internal async Task AssertDirty(string directory, bool expected)
        {
            var entry = await Cache.GetAsync(directory);
            Assert.AreEqual(expected, entry?.IsDirty == true);
            if (expected) Assert.AreEqual(ProjectFileInvalidationReason.FileSystemWrite, entry!.LastInvalidationReason);
        }
        internal Task ResetCache() => Keep(() => Cache.SetAsync(new ProjectFileSnapshot
        { ProjectRoot = Work, IsGitAware = false, SnapshotGeneration = 1, BuiltAt = DateTimeOffset.UnixEpoch, Items = [] }).AsTask());
        internal Task<List<SessionRuntimeEvent>> ReadClosedOriginals() => Keep(async () =>
        {
            Assert.IsTrue(_runtimeClose?.IsCompletedSuccessfully == true);
            var events = new List<SessionRuntimeEvent>();
            await foreach (var value in Runtime.StreamEventsAsync()) events.Add(value);
            return events;
        });
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class HeldTools : IReadOnlyList<AgentToolDefinition>
    {
        internal readonly TaskCompletionSource Entered = NewGate(), Release = NewGate();
        public int Count { get { Entered.TrySetResult(); Release.Task.GetAwaiter().GetResult(); return 0; } }
        public AgentToolDefinition this[int index] => throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<AgentToolDefinition> GetEnumerator() => Enumerable.Empty<AgentToolDefinition>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class NoSkillRoots(string root) : ISkillRootProvider
    {
        public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(SkillDiscoveryContext context, CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(Path.Combine(root, "home"), context.UserProfileRoot);
            Assert.AreEqual(Path.Combine(root, "global"), context.UserCodeAltaRoot);
            Assert.AreEqual(0, context.ProjectRoots.Count); Assert.AreEqual(0, context.AdditionalRoots.Count);
            return ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>([]);
        }
    }
    private sealed class Locator(string root) : ISystemPromptContentLocator
    {
        public SystemPromptContentRoots GetRoots(SystemPromptDiscoveryContext context) => new(Path.Combine(root, "builtin", "prompts"), Path.Combine(root, "builtin", "docs"), Path.Combine(root, "global", "prompts"), null, false);
        public string ResolveBuiltInPromptPath(string relativePromptPath) => throw new AssertFailedException("No implicit prompt lookup.");
        public string ResolveBuiltInDocPath(string fileName) => throw new AssertFailedException("No implicit doc lookup.");
    }
    private sealed class Catalog : IAgentSessionCatalog
    {
        internal readonly TaskCompletionSource Entered = NewGate(), Release = NewGate();
        internal bool HoldInvalidation;
        public async Task InvalidateAsync(string sessionId, CancellationToken cancellationToken = default)
        { if (HoldInvalidation) { Entered.TrySetResult(); await Release.Task; } }
        public Task InvalidateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifySessionCreatedAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifySessionResumedAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifySessionUpdatedAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifySessionDeletedAsync(string sessionId, CancellationToken cancellationToken = default) => throw new AssertFailedException("No delete.");
        public Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => throw new AssertFailedException("No delete.");
        public IAsyncEnumerable<AgentSessionMetadata> ListSessionsAsync(AgentSessionListFilter? filter = null, CancellationToken cancellationToken = default) => throw new AssertFailedException("No discovery.");
    }
    private sealed class Provider
    {
        private readonly object _gate = new();
        private readonly List<Session> _sessions = [];
        internal ModelProviderDescriptor Descriptor { get; } = new(new("cache-inert"), "Inert cache fixture") { DefaultModelId = "fixture" };
        internal Session Latest { get { lock (_gate) return _sessions[^1]; } }
        internal int Preparations { get { lock (_gate) return _sessions.Count; } }
        internal bool HoldAbort;
        internal readonly TaskCompletionSource AbortEntered = NewGate(), ReleaseAbort = NewGate();
        internal IModelProviderRuntime CreateRuntime() => new ProviderRuntime(this);
        private sealed class ProviderRuntime(Provider owner) : IModelProviderSessionRuntime
        {
            public ModelProviderDescriptor Descriptor => owner.Descriptor;
            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException("No probe.");
            public IModelProviderTurnExecutor CreateTurnExecutor() => throw new AssertFailedException("No model execution.");
            public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default) => Prepare(options.SessionId!);
            public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default) => Prepare(sessionId);
            private Task<IAgentSession> Prepare(string id)
            { var session = new Session(owner, id); lock (owner._gate) owner._sessions.Add(session); return Task.FromResult<IAgentSession>(session); }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
        internal sealed class Session(Provider owner, string id) : IAgentSession
        {
            private Action<AgentEvent>? _handler;
            internal bool SubscriptionDisposed;
            public ModelProviderId ProviderId => owner.Descriptor.ProviderId;
            public string SessionId => id;
            public string? WorkspacePath => null;
            public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
            public IDisposable Subscribe(Action<AgentEvent> handler) { _handler = handler; return new Subscription(this); }
            internal Action<AgentEvent> CaptureCallback() => _handler ?? throw new AssertFailedException("No subscription.");
            internal void Emit(AgentEvent value) => CaptureCallback()(value);
            public Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default) => throw new AssertFailedException("No send.");
            public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default) => throw new AssertFailedException("No steer.");
            public async Task AbortAsync(CancellationToken cancellationToken = default)
            { owner.AbortEntered.TrySetResult(); if (owner.HoldAbort) await owner.ReleaseAbort.Task; }
            public Task CompactAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException("No compact.");
            public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            private sealed class Subscription(Session owner) : IDisposable { public void Dispose() { owner.SubscriptionDisposed = true; } }
        }
    }
}
