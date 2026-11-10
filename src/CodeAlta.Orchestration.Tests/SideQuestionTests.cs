using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class SideQuestionTests
{
    [TestMethod]
    public void Transcript_StaysWithinBudgetAndKeepsLatestText()
    {
        var messages = new[]
        {
            new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("old message")]),
            new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(new string('x', 100) + "latest")]),
        };
        foreach (var budget in new[] { 1, 10, 40, 100 })
        {
            var transcript = SideQuestionTranscript.Build(messages, budget);
            Assert.IsTrue(transcript.Length <= budget, $"Budget {budget}, length {transcript.Length}");
            Assert.IsTrue(transcript.EndsWith(budget == 1 ? "…" : "latest", StringComparison.Ordinal));
        }
        Assert.AreEqual(8_000, SideQuestionTranscript.BudgetFor(4_000));
        Assert.AreEqual(SideQuestionTranscript.MaximumBudget, SideQuestionTranscript.BudgetFor(long.MaxValue));
    }

    [TestMethod]
    public async Task ProviderTurn_StreamsOnlyAssistantTextAndReturnsTheFinalAnswer()
    {
        var runtime = new StreamingProvider();
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(runtime.Descriptor, () => runtime);
        await using var hub = new AgentHub(registry, Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        var chunks = new List<string>();
        var answer = await hub.AskSideQuestionAsync("streaming", "chosen", AgentReasoningEffort.High, Environment.CurrentDirectory,
            [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("context")])], "question",
            (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; }, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "answer ", "text" }, chunks);
        Assert.AreEqual("answer text", answer);
        Assert.IsNotNull(runtime.Request);
        Assert.AreEqual("chosen", runtime.Request.ModelId);
        Assert.AreEqual(AgentReasoningEffort.High, runtime.Request.ReasoningEffort);
        Assert.IsTrue(runtime.Request.IsStandalone);
        Assert.AreEqual(0, runtime.Request.Tools.Count);
    }

    [TestMethod]
    public async Task AProviderThatKeepsItsConversation_AnswersInAStandaloneTurn_NeverInATurnOfASession()
    {
        var runtime = new KeptConversationProvider(standalone: true);
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(runtime.Descriptor, () => runtime);
        await using var hub = new AgentHub(registry, Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        var chunks = new List<string>();
        var answer = await hub.AskSideQuestionAsync("kept", "chosen", null, Environment.CurrentDirectory,
            [new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text("context")])], "question",
            (text, _) => { chunks.Add(text); return ValueTask.CompletedTask; }, CancellationToken.None);
        Assert.AreEqual("standalone answer", answer);
        CollectionAssert.AreEqual(new[] { "standalone answer" }, chunks);
        Assert.AreEqual(0, runtime.SessionTurns, "a turn of a session would be kept by the agent, with its tools");
        Assert.IsNotNull(runtime.StandaloneRequest);
        Assert.IsTrue(runtime.StandaloneRequest.IsStandalone);
        Assert.AreEqual(0, runtime.StandaloneRequest.Tools.Count);
        StringAssert.Contains(((AgentMessagePart.Text)runtime.StandaloneRequest.Conversation.Single().Parts.Single()).Value, "context");
    }

    [TestMethod]
    public async Task AProviderThatKeepsItsConversation_WithoutStandaloneTurns_DoesNotAnswer()
    {
        var runtime = new KeptConversationProvider(standalone: false);
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(runtime.Descriptor, () => runtime);
        await using var hub = new AgentHub(registry, Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => hub.AskSideQuestionAsync("kept", "chosen", null, Environment.CurrentDirectory, [], "question",
            static (_, _) => ValueTask.CompletedTask, CancellationToken.None));
        Assert.AreEqual(0, runtime.SessionTurns);
    }

    [TestMethod]
    public async Task ProviderTurn_CancellationReachesProvider()
    {
        var runtime = new StreamingProvider { WaitForCancellation = true };
        var registry = new ModelProviderRegistry();
        registry.RegisterOrReplace(runtime.Descriptor, () => runtime);
        await using var hub = new AgentHub(registry, Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        using var stop = new CancellationTokenSource();
        var work = hub.AskSideQuestionAsync("streaming", "chosen", null, Environment.CurrentDirectory, [], "question",
            static (_, _) => ValueTask.CompletedTask, stop.Token);
        await runtime.Started.Task.WithinAsync(TestDeadlines.Hang);
        await stop.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await work);
    }

    [TestMethod]
    public async Task RunningSession_UsesItsContextAndModelWithoutCreatingOrWritingASession()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider(model: "selected-model");
        var host = await root.CreateHostAsync(provider);
        var options = HeadlessTestRoot.Options(provider, root.ProjectRoot, reasoningEffort: AgentReasoningEffort.High);
        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Original");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Turns.Enqueue(ScriptedTurnProvider.WaitThenReply(release.Task, "main answer", started));
        var send = host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text("original context") });
        try
        {
            await started.Task.WithinAsync(TestDeadlines.Hang);
            var descriptor = await host.RuntimeService.TryGetResumableSessionAsync(session.SessionId);
            Assert.IsNotNull(descriptor);
            var before = await host.RuntimeService.TryReadStoredHistoryAsync(descriptor);
            Assert.IsNotNull(before);
            provider.Turns.Enqueue(ScriptedTurnProvider.Reply("side answer"));

            var answer = await host.RuntimeService.AskSideQuestionAsync(session.SessionId, "side question", static (_, _) => ValueTask.CompletedTask)
                .WithinAsync(TestDeadlines.Hang);
            Assert.AreEqual("side answer", answer);
            var request = provider.Requests.Single(static item => item.IsStandalone);
            Assert.AreEqual(provider.ProviderId, request.ProviderId);
            Assert.AreEqual("selected-model", request.ModelId);
            Assert.AreEqual(AgentReasoningEffort.High, request.ReasoningEffort);
            Assert.AreNotEqual(session.SessionId, request.SessionId);
            Assert.AreEqual(0, request.Tools.Count);
            var prompt = ((AgentMessagePart.Text)request.Conversation.Single().Parts.Single()).Value;
            StringAssert.Contains(prompt, "original context");
            StringAssert.Contains(prompt, "side question");
            var after = await host.RuntimeService.TryReadStoredHistoryAsync(descriptor);
            Assert.IsNotNull(after);
            Assert.AreEqual(before.Count, after.Count);
            var sessions = new List<string>();
            await foreach (var listed in host.RuntimeService.ListRecoverableSessionsAsync()) sessions.Add(listed.SessionId);
            CollectionAssert.AreEquivalent(new[] { session.SessionId }, sessions);
        }
        finally
        {
            release.TrySetResult();
            await send.WithinAsync(TestDeadlines.Hang);
        }
    }

    // A provider that keeps the conversation of its sessions, as an agent CLI does; with standalone turns or not.
    private sealed class KeptConversationProvider(bool standalone) : IAgentModelProviderRuntime
    {
        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("kept"), "Kept") { DefaultModelId = "default" };
        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test",
            ProviderKey = "kept",
            DisplayName = "Kept",
            TransportKind = AgentTransportKind.OpenAIResponses,
        };
        public IModelProviderModelCatalog? ModelCatalog => null;
        public int SessionTurns { get; set; }
        public AgentTurnRequest? StandaloneRequest { get; set; }
        public AgentRuntimeProviderRegistration CreateProviderRegistration()
            => new() { Provider = RuntimeDescriptor, TurnExecutor = standalone ? new StandaloneExecutor(this) : new KeptExecutor(this) };
        public IModelProviderTurnExecutor CreateTurnExecutor() => CreateProviderRegistration().TurnExecutor;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Ready });
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private class KeptExecutor(KeptConversationProvider owner) : IModelProviderTurnExecutor, IAgentProviderCompaction
        {
            protected KeptConversationProvider Owner => owner;
            public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
                CancellationToken cancellationToken = default)
            {
                owner.SessionTurns++;
                return Task.FromResult(ScriptedTurnProvider.Answer("session answer"));
            }

            public Task<AgentCompactionOutcome> CompactAsync(AgentTurnRequest request, CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }

        private sealed class StandaloneExecutor(KeptConversationProvider owner) : KeptExecutor(owner), IAgentProviderStandaloneTurns
        {
            public async Task<AgentTurnResponse> ExecuteStandaloneTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
                CancellationToken cancellationToken)
            {
                Owner.StandaloneRequest = request;
                await onUpdate(new AgentTurnDelta { Kind = AgentContentKind.Assistant, ContentId = "a", Text = "standalone answer" }, cancellationToken);
                return ScriptedTurnProvider.Answer("standalone answer");
            }
        }
    }

    private sealed class StreamingProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("streaming"), "Streaming") { DefaultModelId = "default" };
        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test",
            ProviderKey = "streaming",
            DisplayName = "Streaming",
            TransportKind = AgentTransportKind.OpenAIResponses,
        };
        public IModelProviderModelCatalog? ModelCatalog => null;
        public AgentTurnRequest? Request { get; private set; }
        public bool WaitForCancellation { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new() { Provider = RuntimeDescriptor, TurnExecutor = this };
        public IModelProviderTurnExecutor CreateTurnExecutor() => this;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult { ProviderId = Descriptor.ProviderId, Availability = ModelProviderAvailability.Ready });
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            Started.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            await onUpdate(new AgentTurnDelta { Kind = AgentContentKind.Reasoning, ContentId = "r", Text = "private" }, cancellationToken);
            await onUpdate(new AgentTurnDelta { Kind = AgentContentKind.Assistant, ContentId = "a", Text = "answer " }, cancellationToken);
            await onUpdate(new AgentTurnDelta { Kind = AgentContentKind.Assistant, ContentId = "a", Text = "text" }, cancellationToken);
            return ScriptedTurnProvider.Answer("answer text");
        }
    }
}
