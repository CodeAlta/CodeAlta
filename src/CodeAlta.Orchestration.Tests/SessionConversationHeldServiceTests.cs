using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The questions of runs whose conversation another process holds: pending until the user answers, on the live
/// attachment of their session, and ended with their run or attachment.
/// </summary>
[TestClass]
public sealed class SessionConversationHeldServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly ModelProviderId Provider = new("claude-code");

    [TestMethod]
    public async Task Question_IsPendingUntilAnswered_AndOnlyTheExactHandleWithAnOfferedChoiceResolvesIt()
    {
        await using var fixture = new Fixture();
        var ask = fixture.Service.Handler(Request(), CancellationToken.None);

        var pending = await fixture.WaitForPendingAsync();
        Assert.IsFalse(ask.IsCompleted, "The run waits for the user, with no time limit.");
        var snapshot = pending.Single();
        Assert.AreEqual("session", snapshot.Handle.SessionId);
        Assert.AreEqual("run-1", snapshot.Handle.RunId);
        Assert.AreEqual("request-1", snapshot.Handle.RequestId);
        Assert.AreEqual(fixture.Attachment.Ordinal, snapshot.Handle.AttachmentGeneration);
        Assert.AreEqual(fixture.RuntimeId, snapshot.Handle.RuntimeInstanceId);
        Assert.AreEqual("conversation-1", snapshot.ConversationId);
        Assert.AreEqual(AgentConversationHolderOrigin.VsCode, snapshot.Holder.Origin);
        CollectionAssert.Contains(snapshot.Choices.ToArray(), AgentConversationHeldChoice.OpenAnyway);
        CollectionAssert.AreEqual(new[] { "session" }, (await fixture.Service.ListWaitingSessionsAsync()).ToArray());

        // Another attempt, attachment or session is not this question; a choice it did not offer is refused.
        Assert.IsFalse(await fixture.Service.ResolveAsync(snapshot.Handle with { AttemptId = Guid.NewGuid() }, AgentConversationHeldChoice.Wait, default));
        Assert.IsFalse(await fixture.Service.ResolveAsync(snapshot.Handle with { AttachmentGeneration = snapshot.Handle.AttachmentGeneration + 1 }, AgentConversationHeldChoice.Wait, default));
        Assert.IsFalse(await fixture.Service.ResolveAsync(snapshot.Handle with { SessionId = "other" }, AgentConversationHeldChoice.Wait, default));
        Assert.IsFalse(await fixture.Service.ResolveAsync(snapshot.Handle with { RuntimeInstanceId = Guid.NewGuid() }, AgentConversationHeldChoice.Wait, default));
        Assert.IsFalse(await fixture.Service.ResolveAsync(snapshot.Handle, (AgentConversationHeldChoice)42, default));
        Assert.IsFalse(ask.IsCompleted);

        Assert.IsTrue(await fixture.Service.ResolveAsync(snapshot.Handle, AgentConversationHeldChoice.ContinueAsCopy, default));
        Assert.AreEqual(AgentConversationHeldChoice.ContinueAsCopy, await ask.WaitAsync(Timeout));
        Assert.IsFalse(await fixture.Service.ResolveAsync(snapshot.Handle, AgentConversationHeldChoice.Wait, default), "A question is answered once.");
        Assert.AreEqual(0, (await fixture.Service.ListAsync("session", default)).Count);
        Assert.AreEqual(0, (await fixture.Service.ListWaitingSessionsAsync()).Count);
    }

    [TestMethod]
    public async Task Observer_IsToldOfEachQuestion_UntilItEnds_AndNoLongerOnceStopped()
    {
        await using var fixture = new Fixture();
        var asked = new List<(SessionConversationHeldSnapshot Snapshot, CancellationToken Ended)>();
        var told = new SemaphoreSlim(0);
        var observation = fixture.Service.Observe((snapshot, ended) =>
        {
            lock (asked) asked.Add((snapshot, ended));
            told.Release();
            return Task.CompletedTask;
        });
        // An observer that fails does not stop the question.
        using var failing = fixture.Service.Observe((_, _) => throw new InvalidOperationException("broken view"));

        var ask = fixture.Service.Handler(Request(), CancellationToken.None);
        Assert.IsTrue(await told.WaitAsync(Timeout));
        var (snapshot, ended) = asked.Single();
        Assert.AreEqual((await fixture.WaitForPendingAsync()).Single().Handle, snapshot.Handle);
        Assert.IsFalse(ended.IsCancellationRequested, "The question waits.");
        Assert.IsTrue(await fixture.Service.ResolveAsync(snapshot.Handle, AgentConversationHeldChoice.Wait, default));
        Assert.AreEqual(AgentConversationHeldChoice.Wait, await ask.WaitAsync(Timeout));
        Assert.IsTrue(ended.IsCancellationRequested, "An answered question has ended.");

        using var run = new CancellationTokenSource();
        var second = fixture.Service.Handler(Request(), run.Token);
        Assert.IsTrue(await told.WaitAsync(Timeout));
        var secondEnded = asked[1].Ended;
        await run.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => second.WaitAsync(Timeout));
        Assert.IsTrue(secondEnded.IsCancellationRequested, "A question ends with its run.");

        observation.Dispose();
        var third = fixture.Service.Handler(Request(), CancellationToken.None);
        var handle = (await fixture.WaitForPendingAsync()).Single().Handle;
        Assert.IsFalse(await told.WaitAsync(TimeSpan.FromMilliseconds(200)), "A stopped observation is told nothing.");
        Assert.IsTrue(await fixture.Service.ResolveAsync(handle, AgentConversationHeldChoice.Cancel, default));
        Assert.AreEqual(AgentConversationHeldChoice.Cancel, await third.WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task ChoiceTheQuestionDidNotOffer_IsRefused()
    {
        await using var fixture = new Fixture();
        var ask = fixture.Service.Handler(Request(choices: [AgentConversationHeldChoice.Wait, AgentConversationHeldChoice.ContinueAsCopy, AgentConversationHeldChoice.Cancel]), CancellationToken.None);
        var handle = (await fixture.WaitForPendingAsync()).Single().Handle;

        Assert.IsFalse(await fixture.Service.ResolveAsync(handle, AgentConversationHeldChoice.OpenAnyway, default));
        Assert.IsTrue(await fixture.Service.ResolveAsync(handle, AgentConversationHeldChoice.Wait, default));
        Assert.AreEqual(AgentConversationHeldChoice.Wait, await ask.WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task Question_EndsWithItsRun()
    {
        await using var fixture = new Fixture();
        using var run = new CancellationTokenSource();
        var ask = fixture.Service.Handler(Request(), run.Token);
        var handle = (await fixture.WaitForPendingAsync()).Single().Handle;

        await run.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ask.WaitAsync(Timeout));
        Assert.AreEqual(0, (await fixture.Service.ListAsync("session", default)).Count);
        Assert.AreEqual(0, (await fixture.Service.ListWaitingSessionsAsync()).Count, "The session no longer waits for the user.");
        Assert.IsFalse(await fixture.Service.ResolveAsync(handle, AgentConversationHeldChoice.Wait, default));
    }

    [TestMethod]
    public async Task Question_EndsWithTheAttachmentOfItsSession()
    {
        await using var fixture = new Fixture();
        var ask = fixture.Service.Handler(Request(), CancellationToken.None);
        await fixture.WaitForPendingAsync();

        await fixture.Forwarding.RetireAsync(fixture.Attachment);

        await Assert.ThrowsAsync<OperationCanceledException>(() => ask.WaitAsync(Timeout));
        Assert.AreEqual(0, (await fixture.Service.ListAsync("session", default)).Count);
    }

    [TestMethod]
    public async Task Question_OfASessionWithoutLiveAttachment_OrNotItsOwn_IsRefused()
    {
        await using var fixture = new Fixture();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Service.Handler(Request(sessionId: "unknown"), CancellationToken.None));
        // The attachment of the session named is another session's.
        fixture.TargetSession = "unknown";
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Service.Handler(Request(sessionId: "unknown"), CancellationToken.None));
        fixture.TargetSession = "session";
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Service.Handler(Request(choices: [AgentConversationHeldChoice.Wait]), CancellationToken.None));
        Assert.AreEqual(0, (await fixture.Service.ListWaitingSessionsAsync()).Count);
    }

    [TestMethod]
    public async Task Disposal_AnswersPendingQuestionsWithCancel()
    {
        var fixture = new Fixture();
        var ask = fixture.Service.Handler(Request(), CancellationToken.None);
        await fixture.WaitForPendingAsync();

        await fixture.DisposeAsync();

        Assert.AreEqual(AgentConversationHeldChoice.Cancel, await ask.WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task Runtime_GivesItsSessionsTheHandler_AndAsksOnTheirAttachment()
    {
        using var temp = new TempDirectory();
        var provider = new AskingProviderRuntime(Provider);
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(new ModelProviderDescriptor(Provider, "Claude Code") { DefaultModelId = "model" }, () => provider);
        await using var hub = new AgentHub(registry, temp.Path);
        await using var runtime = CreateRuntime(temp.Path, hub, ask: true);
        var session = CreateSession("session-1", temp.Path);

        await runtime.EnsureCoordinatorSessionAsync(session, CreateOptions(temp.Path));
        Assert.AreSame(runtime.ConversationHolds.Handler, provider.Handler);
        var send = runtime.SendAsync(session, CreateOptions(temp.Path), new AgentSendOptions { Input = AgentInput.Text("hello") });

        IReadOnlyList<SessionConversationHeldSnapshot> pending;
        while ((pending = await runtime.ConversationHolds.ListAsync("session-1", default)).Count == 0)
            await Task.Delay(10);
        CollectionAssert.Contains((await runtime.ConversationHolds.ListWaitingSessionsAsync()).ToArray(), "session-1");
        Assert.IsTrue(await runtime.ConversationHolds.ResolveAsync(pending.Single().Handle, AgentConversationHeldChoice.OpenAnyway, default));

        await send.WaitAsync(Timeout);
        Assert.AreEqual(AgentConversationHeldChoice.OpenAnyway, await provider.Answer.Task.WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task Runtime_ThatDoesNotAsk_GivesItsSessionsNoHandler()
    {
        using var temp = new TempDirectory();
        var provider = new AskingProviderRuntime(Provider);
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(new ModelProviderDescriptor(Provider, "Claude Code") { DefaultModelId = "model" }, () => provider);
        await using var hub = new AgentHub(registry, temp.Path);
        await using var runtime = CreateRuntime(temp.Path, hub, ask: false);

        await runtime.EnsureCoordinatorSessionAsync(CreateSession("session-1", temp.Path), CreateOptions(temp.Path));

        Assert.IsTrue(provider.Created);
        Assert.IsNull(provider.Handler);
    }

    private static AgentConversationHeldRequest Request(string sessionId = "session", IReadOnlyList<AgentConversationHeldChoice>? choices = null)
        => new(Provider, sessionId, new AgentRunId("run-1"), "request-1", DateTimeOffset.UnixEpoch, "conversation-1",
            new AgentConversationHolder(AgentConversationHolderOrigin.VsCode, "VS Code", 4100, "claude-vscode", "interactive", "idle"),
            choices ?? [AgentConversationHeldChoice.Wait, AgentConversationHeldChoice.ContinueAsCopy, AgentConversationHeldChoice.OpenAnyway, AgentConversationHeldChoice.Cancel]);

    private static SessionRuntimeService CreateRuntime(string root, AgentHub hub, bool ask)
    {
        var options = new CatalogOptions { GlobalRoot = root };
        var sessionViewCatalog = new SessionViewCatalog(options);
        var agentSessionCatalog = new AgentSessionCatalog(sessionViewCatalog.JournalStore.CreateSessionStore());
        return new SessionRuntimeService(hub, agentSessionCatalog, new ProjectCatalog(options), sessionViewCatalog,
            new AgentInstructionTemplateProvider(catalogOptions: options), options) { AskWhenConversationHeld = ask };
    }

    private static SessionViewDescriptor CreateSession(string sessionId, string root)
        => new()
        {
            SessionId = sessionId,
            ProviderId = Provider.Value,
            ProviderKey = Provider.Value,
            Kind = SessionViewKind.GlobalSession,
            Status = SessionViewStatus.Active,
            Title = sessionId,
            WorkingDirectory = root,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

    private static SessionExecutionOptions CreateOptions(string root)
        => new()
        {
            ProviderId = Provider,
            ProviderKey = Provider.Value,
            WorkingDirectory = root,
            ProjectRoots = [root],
            Model = "model",
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-held-" + Guid.NewGuid().ToString("N"));
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

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture()
        {
            Attachment = Forwarding.RegisterAttachment("session", "handle", static () => Task.CompletedTask, static () => Task.CompletedTask);
            Attachment.CompleteSetup();
            Service.ResolveTarget = sessionId => sessionId == TargetSession ? new(RuntimeId, Attachment, Provider) : null;
        }

        public SessionConversationHeldService Service { get; } = new();

        public OwnedProviderEventForwarding Forwarding { get; } = new();

        public OwnedProviderEventForwarding.Attachment Attachment { get; }

        public Guid RuntimeId { get; } = Guid.NewGuid();

        public string TargetSession { get; set; } = "session";

        public async Task<IReadOnlyList<SessionConversationHeldSnapshot>> WaitForPendingAsync()
        {
            using var timeout = new CancellationTokenSource(Timeout);
            while (true)
            {
                var pending = await Service.ListAsync("session", default);
                if (pending.Count > 0) return pending;
                await Task.Delay(10, timeout.Token);
            }
        }

        public async ValueTask DisposeAsync() => await Service.DisposeAsync();
    }

    // A provider whose sessions ask the handler they were created with at each send, as the Claude Code provider does
    // for a conversation another process holds.
    private sealed class AskingProviderRuntime(ModelProviderId providerId) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor { get; } = new(providerId, "Claude Code") { DefaultModelId = "model" };

        public bool Created { get; private set; }

        public AgentConversationHeldHandler? Handler { get; private set; }

        public TaskCompletionSource<AgentConversationHeldChoice> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(Session(options.SessionId ?? Guid.CreateVersion7().ToString(), options));

        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(Session(sessionId, options));

        private AskingSession Session(string sessionId, AgentSessionCreateOptions options)
        {
            Created = true;
            Handler = options.OnConversationHeld;
            return new AskingSession(this, sessionId);
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Ready });

        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class AskingSession(AskingProviderRuntime owner, string sessionId) : IAgentSession
        {
            public ModelProviderId ProviderId => owner.Descriptor.ProviderId;

            public string SessionId { get; } = sessionId;

            public string? WorkspacePath => null;

            public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }

            public IDisposable Subscribe(Action<AgentEvent> handler) => new Subscription();

            public async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
            {
                var runId = new AgentRunId("run-held");
                var handler = options.OnConversationHeld ?? owner.Handler ?? throw new InvalidOperationException("Nobody to ask.");
                owner.Answer.TrySetResult(await handler(new AgentConversationHeldRequest(ProviderId, SessionId, runId, "request-1",
                    DateTimeOffset.UtcNow, "conversation-1", new AgentConversationHolder(AgentConversationHolderOrigin.Terminal, "a terminal", 4200),
                    [AgentConversationHeldChoice.Wait, AgentConversationHeldChoice.ContinueAsCopy, AgentConversationHeldChoice.OpenAnyway, AgentConversationHeldChoice.Cancel]),
                    cancellationToken));
                return runId;
            }

            public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

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
    }
}
