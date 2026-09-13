using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Tests;

/// <summary>Lifecycle and separately auditable inert-provider runtime fixtures. No Host/configured providers/tools.</summary>
[TestClass]
public sealed class OwnedSessionUserInputRuntimeTests
{
    [TestMethod]
    public async Task IsolatedRuntime_OrdinaryAskResponseAndClaimedQueueBindInputOnly()
    {
        foreach (var route in new[] { "ordinary", "ask-response", "claimed-queue" })
            await RuntimeFixture.Run(route);
    }

    [TestMethod]
    public async Task PartialStartedFailure_StartsBothClosingObligations()
    {
        // No start failure, first start failure, or partial (second) start failure; all close masks,
        // with synchronous throws and asynchronously faulted originals represented separately.
        foreach (var startFailure in new[] { 0, 1, 2 })
        foreach (var closeFailures in new[] { 0, 1, 2, 3 })
        foreach (var synchronous in new[] { false, true })
            await LifecycleCase(startFailure, closeFailures, synchronous);
    }

    [TestMethod]
    public async Task FixtureCleanup_CancellationFaultDoesNotCountAsSuccess()
    {
        await RequireSuccessfulCleanupAsync(null, [], Task.CompletedTask);
        foreach (var canceled in new[] { false, true })
        foreach (var withPrimary in new[] { false, true })
        {
            var primary = withPrimary ? new InvalidOperationException("primary") : null;
            var cancellation = new TaskCanceledException("cleanup cancellation fault");
            var aggregate = new AggregateException(cancellation);
            var stop = canceled ? Task.FromCanceled(new CancellationToken(true)) : Task.FromException(aggregate);
            AggregateException? failure = null;
            try { await RequireSuccessfulCleanupAsync(primary, primary is null ? [] : [Task.FromException(primary)], stop); }
            catch (AggregateException ex) { failure = ex; }
            Assert.IsNotNull(failure);
            Assert.AreEqual(withPrimary ? 2 : 1, failure.InnerExceptions.Count);
            if (primary is not null) Assert.AreSame(primary, failure.InnerExceptions[0]);
            var cleanup = failure.InnerExceptions[^1];
            if (canceled)
            {
                Assert.IsInstanceOfType<TaskCanceledException>(cleanup);
                Assert.AreSame(stop, ((TaskCanceledException)cleanup).Task);
            }
            else
            {
                Assert.AreSame(aggregate, cleanup);
                Assert.AreSame(cancellation, aggregate.InnerExceptions.Single());
            }
        }
    }

    private static async Task LifecycleCase(int startFailure, int closeFailures, bool synchronous)
    {
        var first = new Lifecycle { FailStart = startFailure == 1, FailClose = (closeFailures & 1) != 0, Synchronous = synchronous };
        var second = new Lifecycle { FailStart = startFailure == 2, FailClose = (closeFailures & 2) != 0, Synchronous = synchronous };
        var combined = OwnedSessionAskExecution.Combine(first, second);
        var work = new List<Task>(); Exception? primary = null; Task? closing = null;
        var expected = new HashSet<Exception>();
        if (startFailure != 0) expected.Add(startFailure == 1 ? first.StartError : second.StartError);
        if (first.FailClose) expected.Add(first.CloseError);
        if (second.FailClose) expected.Add(second.CloseError);
        Task Keep(Func<Task> start)
        {
            var acquisition = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observer = Observe(acquisition.Task); work.Add(observer);
            try { acquisition.SetResult(start()); } catch (Exception ex) { acquisition.SetException(ex); }
            return observer;
        }
        static async Task Observe(Task<Task> acquisition) => await await acquisition;
        try
        {
            var start = Keep(() => combined.StartedAsync(new("run"), default));
            if (startFailure == 0) await start;
            else Assert.AreSame(startFailure == 1 ? first.StartError : second.StartError, await Assert.ThrowsAsync<InvalidOperationException>(() => start));
            Assert.AreEqual(1, first.Starts); Assert.AreEqual(startFailure == 1 ? 0 : 1, second.Starts);
            closing = Keep(() => combined.ClosingAsync(new("run")));
            await Keep(() => Task.WhenAll(first.Entered.Task, second.Entered.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            if (!synchronous || closeFailures != 3) Assert.IsFalse(closing.IsCompleted);
            first.Release.TrySetResult(); second.Release.TrySetResult();
            await Settle(closing);
            var closeErrors = closing.Exception?.Flatten().InnerExceptions.ToArray() ?? [];
            Assert.AreEqual((first.FailClose ? 1 : 0) + (second.FailClose ? 1 : 0), closeErrors.Length);
            if (first.FailClose) CollectionAssert.Contains(closeErrors, first.CloseError);
            if (second.FailClose) CollectionAssert.Contains(closeErrors, second.CloseError);
        }
        catch (Exception ex) { primary = ex; ex.Data["RetainedOriginals"] = work; ex.Data["First"] = first; ex.Data["Second"] = second; throw; }
        finally
        {
            first.Release.TrySetResult(); second.Release.TrySetResult();
            closing ??= Keep(() => combined.ClosingAsync(new("run")));
            try
            {
                await Task.WhenAll(work.Select(Settle)).WaitAsync(TimeSpan.FromSeconds(5));
                var failures = work.Where(t => t.Exception is not null).SelectMany(t => t.Exception!.Flatten().InnerExceptions).Where(e => !expected.Contains(e)).Distinct().ToList();
                if (primary is not null && !failures.Contains(primary)) failures.Insert(0, primary);
                if (failures.Count > 0) throw new AggregateException(failures);
            }
            catch (Exception ex)
            {
                var failure = new AggregateException(primary is null ? [ex] : [primary, ex]);
                failure.Data["RetainedOriginals"] = work; failure.Data["First"] = first; failure.Data["Second"] = second; throw failure;
            }
        }
    }
    private static async Task Settle(Task task) { try { await task; } catch { /* Asserted original failure. */ } }
    private sealed class Lifecycle : AgentRunLifecycle
    {
        internal bool FailStart;
        internal bool FailClose;
        internal bool Synchronous;
        internal int Starts;
        internal InvalidOperationException StartError { get; } = new("inert start");
        internal InvalidOperationException CloseError { get; } = new("inert close");
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task StartedAsync(AgentRunId runId, CancellationToken executionToken)
        {
            Starts++;
            return !FailStart ? Task.CompletedTask : Synchronous ? throw StartError : Task.FromException(StartError);
        }
        public override Task ClosingAsync(AgentRunId runId)
        {
            Entered.TrySetResult();
            if (FailClose && Synchronous) throw CloseError;
            return CloseAfterRelease();
        }
        private async Task CloseAfterRelease() { await Release.Task; if (FailClose) throw CloseError; }
    }

    private static async Task RequireSuccessfulCleanupAsync(Exception? primary, IEnumerable<Task> originals, Task stop)
    {
        var failures = new List<Exception>();
        if (primary is not null) failures.Add(primary);
        // Await the original stop, not its swallowing observer. No cancellation is expected here.
        foreach (var original in originals.Append(stop).Distinct())
        {
            try { await original; }
            catch (Exception ex)
            {
                // Preserve all direct faults (including aggregate identity), not just await's first fault.
                if (original.Exception is { } fault)
                {
                    foreach (var error in fault.InnerExceptions)
                        if (!failures.Contains(error)) failures.Add(error);
                }
                else if (!failures.Contains(ex)) failures.Add(ex);
            }
        }
        if (failures.Count > 0) throw new AggregateException("Runtime fixture originals or cleanup failed.", failures);
    }

    private sealed class RuntimeFixture
    {
        private readonly object _gate = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-owned-input-runtime-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource _cancel = new();
        private readonly List<Task> _originals = [];
        private readonly List<CancellationTokenSource> _runs = [];
        private readonly TaskCompletionSource _stopLaunch = new(TaskCreationOptions.RunContinuationsAsynchronously), _setupDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<SessionOwnedUserInputPage> _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private AgentHub? _hub;
        private ModelProviderRegistry? _registry;
        private OwnedSessionCommandService? _commands;
        private Task? _commandDisposal, _runtimeDisposal, _hubDisposal, _registryDisposal;
        private string? _expectedAskId;
        private SessionRuntimeService? _runtime;
        private Task? _body, _observer, _stop, _stopObserver;
        private string _route = "";
        private int _providerPreparations;
        private Locator? _metadataLocator;
        internal Task<T> Keep<T>(Func<Task<T>> start)
        {
            var acquired = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observer = Observe(acquired.Task); lock (_gate) _originals.Add(observer);
            try { acquired.SetResult(start()); } catch (Exception ex) { acquired.SetException(ex); }
            return observer;
        }
        internal Task Keep(Func<Task> start) => Keep(async () => { await start(); return true; });
        private static async Task<T> Observe<T>(Task<Task<T>> task) => await await task;
        internal static async Task Run(string route)
        {
            var f = new RuntimeFixture { _route = route }; var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f._stop = f.Stop(); f._stopObserver = Settle(f._stop); f._body = f.Core(launch.Task); f._observer = Settle(f._body); launch.SetResult();
            try
            {
                await f._body.WaitAsync(TimeSpan.FromSeconds(30)); await f._observer;
                foreach (var run in f._runs) run.Dispose();
                f._cancel.Dispose();
            }
            catch (Exception ex) { ex.Data["RetainedFixture"] = f; ex.Data["RetainedRoot"] = f._root; throw; }
            finally { f._stopLaunch.TrySetResult(); Console.WriteLine("Retained input runtime root: " + f._root); }
        }
        private async Task Core(Task launch)
        {
            await launch; Exception? primary = null;
            try { await Keep(Exercise); }
            catch (Exception ex) { primary = ex; throw; }
            finally
            {
                _setupDone.TrySetResult(); _stopLaunch.TrySetResult();
                Task[] work; lock (_gate) work = [.. _originals]; await Task.WhenAll(work.Select(Settle));
                await _stopObserver!;
                lock (_gate) work = [.. _originals]; await Task.WhenAll(work.Select(Settle));
                await RequireSuccessfulCleanupAsync(primary, work, _stop!);
            }
        }
        private async Task Stop()
        {
            await _stopLaunch.Task;
            var cancellation = Keep(() => _cancel.CancelAsync()); var disposal = Keep(DisposeAcquired);
            var all = Task.WhenAll(cancellation, disposal);
            try { await all; } catch { throw all.Exception ?? new AggregateException(new TaskCanceledException(all)); }
        }
        private async Task DisposeAcquired()
        {
            await _setupDone.Task;
            // Start independent command/ask and runtime closure before joining either. If either fails
            // or stays pending, retain Hub/registry: a finally block cannot manufacture safe release.
            _commandDisposal = Keep(() => _commands?.DisposeAsync().AsTask() ?? Task.CompletedTask);
            _runtimeDisposal = Keep(() => _runtime?.DisposeAsync().AsTask() ?? Task.CompletedTask);
            var predecessors = Task.WhenAll(_commandDisposal, _runtimeDisposal);
            try { await predecessors; } catch { throw predecessors.Exception ?? new AggregateException(new TaskCanceledException(predecessors)); }
            _hubDisposal = Keep(() => _hub?.DisposeAsync().AsTask() ?? Task.CompletedTask); await _hubDisposal;
            _registryDisposal = Keep(() => _registry?.DisposeAsync().AsTask() ?? Task.CompletedTask); await _registryDisposal;
        }
        private async Task Exercise()
        {
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(_root)!); parent is not null; parent = parent.Parent)
                if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse ancestry refused.");
            if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("Root already exists.");
            var work = Path.Combine(_root, "work"); var home = Path.Combine(_root, "home"); var global = Path.Combine(_root, "global");
            var prompts = Path.Combine(_root, "builtin", "prompts");
            foreach (var path in new[] { work, home, global, Path.Combine(prompts, "agents"), Path.Combine(prompts, "system") }) Directory.CreateDirectory(path);
            await Keep(() => File.WriteAllTextAsync(Path.Combine(prompts, "agents", "default.prompt.md"), "---\nname: Fixture\n---\nInert fixture agent.", _cancel.Token));
            await Keep(() => File.WriteAllTextAsync(Path.Combine(prompts, "system", "default.system-prompt.md"), "---\nname: Fixture\n---\nInert fixture system.", _cancel.Token));
            var options = new CatalogOptions { GlobalRoot = global }; var catalog = new SessionViewCatalog(options); var projects = new ProjectCatalog(options);
            var noRoots = new NoSkillRoots(_root);
            var skills = new SkillCatalog([noRoots]); _registry = new ModelProviderRegistry(); var provider = new Provider(this);
            _registry.RegisterOrReplace(provider.Descriptor, () => provider); _hub = new AgentHub(_registry, global);
            var template = new AgentInstructionTemplateProvider(skills, options, new Locator(_root), null, new(home, _root));
            _metadataLocator = new Locator(_root);
            _runtime = new(_hub, new AgentSessionCatalog(catalog.JournalStore.CreateSessionStore()), projects, catalog, template, options, skills)
            { PromptCatalog = new AgentPromptCatalog(_metadataLocator) };
            _commands = new(_runtime, projects, options, 16, reviewPermissions: false, enableAsks: _route == "ask-response", enableUserInput: true);
            _setupDone.TrySetResult();
            var session = new SessionViewDescriptor { SessionId = "input-session", ProviderId = "input-inert", ProviderKey = "input-inert", Kind = SessionViewKind.GlobalSession,
                Status = SessionViewStatus.Active, Title = "Input fixture", WorkingDirectory = work, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch, LastActiveAt = DateTimeOffset.UnixEpoch };
            var executionOptions = new SessionExecutionOptions { ProviderId = new("input-inert"), ProviderKey = "input-inert", Model = "fixture", WorkingDirectory = work, ProjectRoots = [work],
                OnPermissionRequest = _runtime.Permissions.OwnedDefaultPermissionHandler, OnUserInputRequest = _runtime.Permissions.OwnedDefaultUserInputHandler };
            Task send; Task<OwnedSessionCommandResult>? queueSend = null; Task<OwnedAskDisposition>? answer = null;
            if (_route == "claimed-queue")
            {
                await Keep(() => _runtime.EnsureOwnedCoordinatorSessionAsync(session, executionOptions));
                var state = await Keep(() => _runtime.GetCurrentStateAsync(session.SessionId));
                var receipt = new OwnedSessionCommandReceipt("queue", OwnedSessionCommandKind.Queue, session.SessionId);
                Assert.IsNotNull(state.Entry);
                send = queueSend = Keep(() => _runtime.QueueOwnedCommandAsync(new("queue", session.SessionId, state.RuntimeInstanceId, state.Entry.AttachmentGeneration, "inert"), receipt, false, _cancel.Token, true));
            }
            else if (_route == "ask-response")
            {
                // Real owner preparation requires persisted identity; only this explicit fixture store is seeded.
                var store = catalog.JournalStore.CreateSessionStore();
                await Keep(() => store.UpsertSessionAsync(new AgentSessionSummary { SessionId = session.SessionId, ProviderId = new("input-inert"),
                    ProtocolFamily = "inert", ProviderKey = "input-inert", ModelId = "fixture", WorkingDirectory = global,
                    AgentPromptId = AgentPromptCatalog.DefaultPromptName,
                    CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch }, _cancel.Token));
                var producer = _commands.AdmitSend(new("ask-producer", session.SessionId, "Produce one restricted ask"), _cancel.Token);
                Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, producer.Kind); Assert.IsNotNull(producer.Receipt);
                var produced = await Keep(() => producer.Receipt.Completion);
                Assert.AreEqual(OwnedSessionCommandOutcome.Completed, produced.Outcome);
                var head = _commands.Asks.List(session.SessionId).Head; Assert.IsNotNull(head);
                _expectedAskId = head.Handle.AskId;
                var action = new OwnedAskAction(Guid.NewGuid(), head.Handle, [new() { QuestionIndex = 0, SelectedChoiceIndexes = [], FreeformText = "Explicit answer" }]);
                send = answer = Keep(() => _commands.Asks.AnswerAsync(action, _cancel.Token));
            }
            else
            {
                var permission = await Keep(() => _runtime.Permissions.CreateOwnedExecutionAsync(Guid.NewGuid(), session.SessionId, _cancel.Token, false, true).AsTask());
                Assert.IsNotNull(permission);
                send = Keep(() => _runtime.SendOwnedCommandAsync(session, executionOptions,
                    new() { Input = AgentInput.Text("inert") }, permission, _cancel.Token));
            }
            var page = await _published.Task.WaitAsync(_cancel.Token); Assert.AreEqual(1, page.Entries.Count);
            Assert.IsNull(page.Entries[0].Handle.RunId);
            Assert.IsTrue(await Keep(() => _runtime.Permissions.ResolveOwnedUserInputAsync(page.Entries[0].Handle, [new("q", "literal")], default).AsTask()));
            await send;
            if (queueSend is not null) Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await queueSend).Outcome);
            if (answer is not null) { Assert.AreEqual("admitted", (await answer).Status); Assert.IsNotNull((await answer).RunId); }
            Assert.AreEqual(0, (await Keep(() => _runtime.Permissions.ListOwnedUserInputsAsync(session.SessionId, default).AsTask())).Entries.Count);
            Assert.IsTrue(noRoots.Calls > 0); Assert.IsTrue(_providerPreparations > 0);
        }
        private sealed class NoSkillRoots(string root) : ISkillRootProvider
        {
            internal int Calls { get; private set; }
            public ValueTask<IReadOnlyList<SkillRootRegistration>> GetRootsAsync(SkillDiscoveryContext context, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested(); Calls++;
                Assert.AreEqual(Path.Combine(root, "home"), context.UserProfileRoot);
                Assert.AreEqual(Path.Combine(root, "global"), context.UserCodeAltaRoot);
                Assert.AreEqual(0, context.ProjectRoots.Count); Assert.AreEqual(0, context.AdditionalRoots.Count);
                return ValueTask.FromResult<IReadOnlyList<SkillRootRegistration>>([]);
            }
        }
        private sealed class Locator(string root) : ISystemPromptContentLocator
        {
            internal int Calls { get; private set; }
            public SystemPromptContentRoots GetRoots(SystemPromptDiscoveryContext context)
            { Calls++; return new(Path.Combine(root, "builtin", "prompts"), Path.Combine(root, "builtin", "docs"), Path.Combine(root, "global", "prompts"), null, false); }
            public string ResolveBuiltInPromptPath(string relativePromptPath) => throw new AssertFailedException("No implicit prompt lookup.");
            public string ResolveBuiltInDocPath(string fileName) => throw new AssertFailedException("No implicit docs lookup.");
        }
        private sealed class Provider(RuntimeFixture fixture) : IModelProviderSessionRuntime
        {
            public ModelProviderDescriptor Descriptor { get; } = new(new("input-inert"), "Inert input") { DefaultModelId = "fixture" };
            public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
            { ValidatePreparation(options); return Task.FromResult<IAgentSession>(new Session(fixture, options.SessionId!)); }
            public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
            { ValidatePreparation(options); return Task.FromResult<IAgentSession>(new Session(fixture, sessionId)); }
            private void ValidatePreparation(AgentSessionCreateOptions options)
            {
                Assert.AreEqual(Path.Combine(fixture._root, fixture._route == "ask-response" ? "global" : "work"), options.WorkingDirectory);
                Assert.IsNotNull(options.AgentPromptUsage);
                Assert.AreEqual(AgentPromptCatalog.DefaultPromptName, options.AgentPromptUsage.PromptName);
                // All three descriptors are global sessions (no project), independent of ProjectRoots.
                var expected = SessionRuntimeService.FormatAgentPromptSourcePathForTimeline(
                    Path.Combine(fixture._root, "builtin", "prompts", "agents", "default.prompt.md"), projectRoot: null);
                Assert.AreEqual(expected, options.AgentPromptUsage.SourcePath);
                Assert.IsNotNull(fixture._metadataLocator);
                // The persisted ask seed also exercises known-prompt recovery before usage lookup.
                Assert.IsTrue(fixture._metadataLocator.Calls >= (fixture._route == "ask-response" ? 2 : 1));
                fixture._providerPreparations++;
            }
            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException("No probe.");
            public IModelProviderTurnExecutor CreateTurnExecutor() => throw new AssertFailedException("No model execution.");
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
        private sealed class Session(RuntimeFixture fixture, string sessionId) : IAgentSession
        {
            public ModelProviderId ProviderId => new("input-inert"); public string SessionId => sessionId; public string? WorkspacePath => null;
            public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
            public IDisposable Subscribe(Action<AgentEvent> handler) => new Subscription();
            public async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
            {
                var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lock (fixture._gate) fixture._runs.Add(run);
                var id = new AgentRunId(Guid.NewGuid().ToString("D")); Exception? primary = null;
                Assert.IsNotNull(options.RunLifecycle); Assert.IsNotNull(options.OnUserInputRequest); Assert.IsNotNull(options.OnPermissionRequest);
                try
                {
                    await fixture.Keep(() => options.RunLifecycle.StartedAsync(id, run.Token));
                    Assert.IsTrue(options.EnableUserInputTool);
                    Assert.AreEqual(fixture._expectedAskId, options.AskId);
                    var permission = await fixture.Keep(() => options.OnPermissionRequest(new AgentCommandPermissionRequest(ProviderId, SessionId, DateTimeOffset.UnixEpoch, id,
                        "command", null, "never executed", "inert", null, "fixture", null, null, null), default));
                    Assert.AreEqual(AgentPermissionDecisionKind.Deny, permission.Kind);
                    if (fixture._route == "ask-response" && options.AskId is null)
                    {
                        var tool = options.AdditionalTools!.Single(t => t.Spec.Name == "alta");
                        using var document = JsonDocument.Parse("""{"args":["ask","--stdin"],"stdin":"{\"questions\":[{\"title\":\"Decision\",\"question\":\"Continue?\",\"freeform\":{}}]}"}""");
                        var result = await fixture.Keep(() => tool.Handler(new(ProviderId, SessionId, "ask", "alta", document.RootElement.Clone()), run.Token));
                        Assert.IsTrue(result.Success); return id;
                    }
                    var input = fixture.Keep(() => options.OnUserInputRequest(new(ProviderId, SessionId, DateTimeOffset.UnixEpoch, null, "same-interaction", new([new("q", "Question")])), default));
                    var page = await fixture.Keep(() => fixture._runtime!.Permissions.ListOwnedUserInputsAsync(SessionId, default).AsTask()); fixture._published.TrySetResult(page);
                    Assert.AreEqual("literal", (await input).Answers["q"]); return id;
                }
                catch (Exception ex) { primary = ex; throw; }
                finally
                {
                    try { await fixture.Keep(() => options.RunLifecycle.ClosingAsync(id)); }
                    catch (Exception ex) { throw primary is null ? new AggregateException(ex) : new AggregateException(primary, ex); }
                }
            }
            public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default) => throw new AssertFailedException("No steer.");
            public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task CompactAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException("No compaction.");
            public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            private sealed class Subscription : IDisposable { public void Dispose() { } }
        }
    }
}
