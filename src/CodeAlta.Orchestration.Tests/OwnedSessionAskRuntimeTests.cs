using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>Production send-option/lifecycle adapter with deterministic, inert callbacks.</summary>
[TestClass]
public sealed class OwnedSessionAskRuntimeTests
{
    [TestMethod]
    public Task RealOwnedRoute_PropagatesAskIdRejectsPlainReplayAndClosesRetainedProducer()
        => RealFixture.Run();

    [TestMethod]
    public async Task Compose_PreservesAllOptionsAndClosesBothAfterStartFailure()
    {
        var owner = new OwnedSessionAskService(true, static (_, _) => throw new AssertFailedException("No dispatch."));
        var execution = owner.CreateExecution(Guid.NewGuid(), "session", default);
        execution.Bind(Guid.NewGuid(), 1, new("fixture"));
        var failure = new InvalidOperationException("original start failure");
        var hook = new Hook(failure);
        var options = new AgentSendOptions
        {
            Input = AgentInput.Text("answer"), AskId = "original-ask", RunLifecycle = hook,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
        };
        var composed = execution.Compose(options);
        Assert.AreSame(options.Input, composed.Input);
        Assert.AreEqual(options.AskId, composed.AskId);
        Assert.AreSame(options.OnPermissionRequest, composed.OnPermissionRequest);
        Assert.IsNotNull(composed.AdditionalTools);
        Assert.AreSame(execution.Tool, composed.AdditionalTools.Single());
        Task? start = null;
        Task? close = null;
        try
        {
            start = composed.RunLifecycle!.StartedAsync(new("actual"), default);
            var caught = await Assert.ThrowsAsync<InvalidOperationException>(() => start);
            Assert.AreSame(failure, caught);
        }
        finally
        {
            close = composed.RunLifecycle!.ClosingAsync(new("actual"));
            owner.CloseAdmission();
            var drain = owner.DrainAsync();
            var all = Task.WhenAll(close, drain);
            try { await all.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { ex.Data["OriginalJoin"] = all; ex.Data["AskOwner"] = owner; ex.Data["OriginalStart"] = start; throw; }
        }
        Assert.AreEqual(1, hook.Closed);
        Assert.IsFalse((await execution.Tool.Handler(OwnedSessionAskServiceTests.Invocation(), default)).Success);
    }

    [TestMethod]
    public async Task PartialStart_ClosesIndependentComponentsBeforeBlockedJoin()
    {
        var owner = new OwnedSessionAskService(true, static (_, _) => throw new AssertFailedException("No dispatch."));
        var execution = owner.CreateExecution(Guid.NewGuid(), "session", default);
        execution.Bind(Guid.NewGuid(), 1, new("fixture"));
        var first = new GatedClose();
        var failure = new InvalidOperationException("second start failed");
        var second = new Hook(failure);
        var lifecycle = execution.Compose(new() { Input = AgentInput.Text("ask"),
            RunLifecycle = OwnedSessionAskExecution.Combine(first, second) }).RunLifecycle!;
        Task? closing = null;
        try
        {
            var starting = lifecycle.StartedAsync(new("actual"), default);
            Assert.AreSame(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => starting));
            closing = lifecycle.ClosingAsync(new("actual"));
            await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // First closing signals Entered synchronously; Combined invokes the second before awaiting both.
            // Use the second component's own readiness, never a scheduler-dependent counter assertion.
            await second.CloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(closing.IsCompleted);
            Assert.IsFalse((await execution.Tool.Handler(OwnedSessionAskServiceTests.Invocation(), default)).Success);
        }
        finally
        {
            first.Release.TrySetResult();
            execution.Close();
            owner.CloseAdmission();
            var drain = owner.DrainAsync();
            var all = Task.WhenAll(closing ?? Task.CompletedTask, drain);
            try { await all.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { ex.Data["OriginalJoin"] = all; ex.Data["AskOwner"] = owner; ex.Data["FirstComponent"] = first; throw; }
        }
    }

    [TestMethod]
    public void SubmissionContext_IsReferenceOwnedAndPreservesFirstValidReturn()
    {
        var a = new OwnedAskSubmission(Guid.NewGuid(), "ask");
        var b = new OwnedAskSubmission(a.ActionId, a.AskId);
        Assert.IsFalse(OwnedSessionCommandService.SameAskContext(a, b));
        Assert.IsFalse(OwnedSessionCommandService.SameAskContext(a, null));
        Assert.IsFalse(OwnedSessionCommandService.SameAskContext(null, a));
        Assert.IsTrue(OwnedSessionCommandService.SameAskContext(a, a));
        Assert.IsTrue(OwnedSessionCommandService.SameAskContext(null, null));
        a.RecordRunReturned(default);
        Assert.IsNull(a.PositiveRunId);
        a.RecordRunReturned(new("real"));
        a.RecordRunReturned(new("replacement"));
        Assert.AreEqual("real", a.PositiveRunId);
    }

    private sealed class Hook(Exception failure) : AgentRunLifecycle
    {
        internal int Closed { get; private set; }
        internal TaskCompletionSource CloseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task StartedAsync(AgentRunId runId, CancellationToken executionToken) => Task.FromException(failure);
        public override Task ClosingAsync(AgentRunId runId) { Closed++; CloseEntered.TrySetResult(); return Task.CompletedTask; }
    }

    private sealed class GatedClose : AgentRunLifecycle
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task StartedAsync(AgentRunId runId, CancellationToken executionToken) => Task.CompletedTask;
        public override Task ClosingAsync(AgentRunId runId) { Entered.TrySetResult(); return Release.Task; }
    }

    // Source-only until parent audits all explicit-root/cache/shipped-content routes. Roots are never deleted.
    private sealed class RealFixture
    {
        private readonly List<Task> _originals = [];
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-owned-ask-" + Guid.NewGuid().ToString("N"));
        private readonly Provider _provider = new();
        private CodeAltaHost? _host;
        private Task? _lifetime;
        private volatile bool _closing;
        private Task Keep(Task value) { lock (_originals) _originals.Add(value); return value; }
        private Task<T> Keep<T>(Task<T> value) { Keep((Task)value); return value; }

        internal static async Task Run()
        {
            var fixture = new RealFixture();
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture._lifetime = fixture.RunOwned(launch.Task);
            launch.TrySetResult();
            var observation = fixture._lifetime.WaitAsync(TimeSpan.FromSeconds(30));
            try { await observation; }
            catch (Exception ex)
            {
                fixture._closing = true;
                ex.Data["RetainedFixture"] = fixture; ex.Data["RetainedRoot"] = fixture._root;
                ex.Data["OriginalLifetime"] = fixture._lifetime; ex.Data["OriginalObservation"] = observation;
                throw;
            }
            finally { fixture._provider.ReleaseAnswer.TrySetResult(); }
        }

        private async Task RunOwned(Task launch)
        {
            await launch;
            try
            {
                for (var node = new DirectoryInfo(Path.GetDirectoryName(_root)!); node is not null; node = node.Parent)
                    if ((node.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse ancestry.");
                if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("Fixture root exists.");
                var home = Path.Combine(_root, "home"); var global = Path.Combine(_root, "global");
                var projectRoot = Path.Combine(_root, "project"); var builtin = Path.Combine(_root, "builtin");
                foreach (var directory in new[] { home, global, projectRoot, builtin }) Directory.CreateDirectory(directory);
                Directory.CreateDirectory(Path.Combine(builtin, ".git"));
                File.WriteAllText(Path.Combine(builtin, ".git", "config"), "[core]\nignorecase = true\nexcludesfile = fixture.ignore\n");
                File.WriteAllText(Path.Combine(builtin, ".git", "fixture.ignore"), "");
                var catalog = new CatalogOptions { GlobalRoot = global };
                var project = await Keep(new ProjectCatalog(catalog).UpsertFromPathAsync(projectRoot, CancellationToken.None));
                var session = new SessionViewDescriptor
                {
                    SessionId = "session", Kind = SessionViewKind.ProjectSession, ProjectRef = project.Id,
                    ProviderId = "fixture", ProviderKey = "fixture", WorkingDirectory = projectRoot,
                    Title = "Ask fixture", ModelId = "fixture-model", AgentPromptId = "default", CreatedAt = DateTimeOffset.UtcNow,
                };
                var journals = new SessionViewJournalStore(catalog);
                await Keep(journals.EnsureHeaderAsync(session, CancellationToken.None));
                var store = journals.CreateSessionStore();
                await Keep(store.UpsertSessionAsync(new AgentSessionSummary
                {
                    SessionId = "session", ProviderId = new("fixture"), ProviderKey = "fixture", WorkingDirectory = projectRoot,
                    Title = session.Title, CreatedAt = session.CreatedAt, UpdatedAt = session.CreatedAt, ModelId = "fixture-model", AgentPromptId = "default",
                }, CancellationToken.None));
                await Keep(journals.AppendStateAsync(session, new SessionViewLocalState { ProviderKey = "fixture", ModelId = "fixture-model", AgentPromptId = "default" }, CancellationToken.None));
                Assert.IsNotNull(await Keep(store.GetSessionAsync("session", CancellationToken.None)));
                _host = await Keep(CodeAltaHost.CreateAsync(new()
                {
                    GlobalRoot = global, CurrentProjectPath = projectRoot, DiscoveryScope = new(home, _root), BuiltInSkillRoot = builtin,
                    StartPlugins = false, OwnsLogging = false, IsHeadless = true, PluginEnvironment = FrozenDictionary<string, string?>.Empty,
                    EnableOwnedAsks = true, ConfigureModelProviders = registry => registry.RegisterOrReplace(_provider.Descriptor, () => new Runtime(_provider)),
                }, CancellationToken.None));
                if (_closing) return; // A timed-out setup still owns and closes a late host.
                var send = _host.Commands.AdmitSend(new("initial", "session", "ask"));
                Assert.IsNotNull(send.Receipt);
                await Keep(send.Receipt.Completion);
                var head = _host.Commands.Asks.List("session").Head;
                Assert.IsNotNull(head);
                // The session is one that waits for the user, for a summary of every session.
                CollectionAssert.AreEqual(new[] { "session" }, _host.Commands.Asks.ListWaitingSessions().ToArray());
                var action = new OwnedAskAction(Guid.NewGuid(), head.Handle, [new() { QuestionIndex = 0, FreeformText = "yes" }]);
                var answer = Keep(_host.Commands.Asks.AnswerAsync(action, default));
                await Keep(Task.WhenAny(_provider.AnswerEntered.Task, answer));
                Assert.IsTrue(_provider.AnswerEntered.Task.IsCompletedSuccessfully, "Answer settled before reaching the fake send.");
                Assert.AreEqual(head.Handle.AskId, _provider.AnswerOptions!.AskId);
                var formatted = AltaAskAnswerMarkdownFormatter.Format(head.Request, action.Answers);
                Assert.AreEqual(OwnedSessionCommandAdmissionKind.Conflict,
                    _host.Commands.AdmitSend(new(action.ActionId.ToString("D"), "session", formatted)).Kind);
                Assert.AreEqual(1, _provider.Attachments);
                Assert.IsFalse((await Keep(_provider.FirstTool!.Handler(OwnedSessionAskServiceTests.Invocation(), default))).Success);
                _provider.ReleaseAnswer.TrySetResult();
                Assert.AreEqual("admitted", (await answer).Status);
                Assert.AreEqual("fake-2", _host.Commands.Asks.Observe(action.ActionId, head.Handle)!.RunId);
                // Reverse collision: a public send reserves the exact key and text before the ask action.
                var next = _host.Commands.Asks.List("session").Head!;
                var second = new OwnedAskAction(Guid.NewGuid(), next.Handle, [new() { QuestionIndex = 0, FreeformText = "next" }]);
                var plain = _host.Commands.AdmitSend(new(second.ActionId.ToString("D"), "session", AltaAskAnswerMarkdownFormatter.Format(next.Request, second.Answers)));
                Assert.IsNotNull(plain.Receipt);
                await Keep(plain.Receipt.Completion);
                Assert.AreEqual("not_admitted", (await Keep(_host.Commands.Asks.AnswerAsync(second, default))).Status);
            }
            finally
            {
                _provider.ReleaseAnswer.TrySetResult(); // Release independently before any dependent join.
                if (_host is not null) await Keep(_host.DisposeAsync().AsTask());
                Task[] originals; lock (_originals) originals = [.. _originals];
                await Task.WhenAll(originals);
                // All task-owned roots are deliberately retained, including on success.
            }
        }
    }

    private sealed class Provider
    {
        internal ModelProviderDescriptor Descriptor { get; } = new(new("fixture"), "Inert ask fixture") { DefaultModelId = "fixture-model" };
        internal int Sends;
        internal int Attachments;
        internal AgentToolDefinition? FirstTool;
        internal AgentSendOptions? AnswerOptions;
        internal TaskCompletionSource AnswerEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseAnswer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Runtime(Provider owner) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor => owner.Descriptor;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException("No probe allowed.");
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new AssertFailedException("No real provider allowed.");
        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
        { owner.Attachments++; return Task.FromResult<IAgentSession>(new Session(owner, options.SessionId!, options.WorkingDirectory)); }
        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
        { owner.Attachments++; return Task.FromResult<IAgentSession>(new Session(owner, sessionId, options.WorkingDirectory)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Session(Provider owner, string sessionId, string? workingDirectory) : IAgentSession
    {
        public ModelProviderId ProviderId => owner.Descriptor.ProviderId;
        public string SessionId => sessionId;
        public string? WorkspacePath => workingDirectory;
        public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public IDisposable Subscribe(Action<AgentEvent> handler) => new Subscription();
        public async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
        {
            var send = ++owner.Sends;
            var run = new AgentRunId("fake-" + send);
            try
            {
                if (options.RunLifecycle is not null) await options.RunLifecycle.StartedAsync(run, cancellationToken);
                var tool = options.AdditionalTools!.Single();
                if (send == 1) owner.FirstTool = tool;
                Assert.IsTrue((await tool.Handler(OwnedSessionAskServiceTests.Invocation(), cancellationToken)).Success);
                if (send == 2)
                {
                    owner.AnswerOptions = options; owner.AnswerEntered.TrySetResult();
                    await owner.ReleaseAnswer.Task;
                }
                cancellationToken.ThrowIfCancellationRequested();
                return run;
            }
            finally { if (options.RunLifecycle is not null) await options.RunLifecycle.ClosingAsync(run); }
        }
        public Task AbortAsync(CancellationToken cancellationToken = default) { owner.ReleaseAnswer.TrySetResult(); return Task.CompletedTask; }
        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default) => throw new AssertFailedException("No steer.");
        public Task CompactAsync(CancellationToken cancellationToken = default) => throw new AssertFailedException("No compact.");
        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Subscription : IDisposable { public void Dispose() { } }
}
