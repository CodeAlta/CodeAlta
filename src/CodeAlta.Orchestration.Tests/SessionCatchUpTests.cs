using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The catch-up of an idle session: the turns made outside CodeAlta on the conversation its provider keeps are written
/// in its journal, the attachment that held the conversation as it was is retired, and the next prompt follows them.
/// </summary>
[TestClass]
public sealed class SessionCatchUpTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task CatchUp_WritesTheTurnsMadeElsewhere_RetiresTheAttachment_AndTheNextPromptFollowsThem()
    {
        using var temp = TempDirectory.Create();
        var provider = new ExternalProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Catch up")).SessionId;
        await SendAsync(host, sessionId, "one");
        Assert.IsNotNull(await host.RuntimeService.TryGetActiveSessionDescriptorAsync(sessionId), "The session is attached after its turn.");

        provider.External = state => provider.Turns(state, AgentExternalTurnOrigin.VsCode, "From VS Code.", "Answered there.");
        Assert.AreEqual("appended", await host.Commands.CatchUpExternalTurnsAsync(sessionId));

        Assert.IsNull(await host.RuntimeService.TryGetActiveSessionDescriptorAsync(sessionId),
            "The attachment held the conversation as it was: the next prompt attaches the session again.");
        var store = host.SessionViewCatalog.JournalStore.CreateSessionStore();
        var state = (await store.GetStateAsync(sessionId))!;
        Assert.AreEqual("""{"kind":"fake","turns":1,"users":1,"assistants":1}""", state.ProviderState!.Value.GetRawText(),
            "The state counts exactly what was written.");
        var turns = (await store.ReadTurnsAsync(sessionId)).Turns;
        Assert.AreEqual(2, turns.Count);
        Assert.IsTrue(turns[1].MadeElsewhere);
        Assert.AreEqual("From VS Code.", turns[1].PromptPreview);
        Assert.AreEqual("Answered there.", (await store.GetSessionSummaryAsync(sessionId))!.Summary, "The preview of the session follows the turns.");

        // Nothing new: nothing is written and the session stays attached.
        provider.External = static _ => AgentExternalTurns.Unchanged;
        await SendAsync(host, sessionId, "two");
        Assert.AreEqual("unchanged", await host.Commands.CatchUpExternalTurnsAsync(sessionId));
        Assert.IsNotNull(await host.RuntimeService.TryGetActiveSessionDescriptorAsync(sessionId));
        CollectionAssert.AreEqual(new[] { "one", "answer", "From VS Code.", "Answered there.", "two" }, provider.LastConversation(),
            "The next prompt follows the turns made elsewhere.");
    }

    [TestMethod]
    public async Task Send_CatchesUpFirst_SoThatThePromptFollowsTheTurnsMadeElsewhere()
    {
        using var temp = TempDirectory.Create();
        var provider = new ExternalProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Before a send")).SessionId;
        await SendAsync(host, sessionId, "one");

        provider.External = state => provider.Turns(state, AgentExternalTurnOrigin.Terminal, "From a terminal.", "Answered in the terminal.");
        await SendAsync(host, sessionId, "two");

        CollectionAssert.AreEqual(new[] { "one", "answer", "From a terminal.", "Answered in the terminal.", "two" }, provider.LastConversation());
        var turns = (await host.SessionViewCatalog.JournalStore.CreateSessionStore().ReadTurnsAsync(sessionId)).Turns;
        CollectionAssert.AreEqual(new[] { false, true, false }, turns.Select(static turn => turn.MadeElsewhere).ToArray());
        StringAssert.StartsWith(turns[1].PromptContentId, "external:terminal:");
    }

    [TestMethod]
    public async Task CatchUp_IsNotDoneWhileARunIsInProgress()
    {
        using var temp = TempDirectory.Create();
        var provider = new ExternalProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Busy")).SessionId;
        await SendAsync(host, sessionId, "one");

        provider.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, "two")).Receipt;
        Assert.IsNotNull(send);
        await provider.Started.Task.WaitAsync(Timeout);
        var reads = provider.Reads;
        try
        {
            provider.External = state => provider.Turns(state, AgentExternalTurnOrigin.VsCode, "Too early.", "Not written.");
            Assert.AreEqual("busy", await host.Commands.CatchUpExternalTurnsAsync(sessionId));
            Assert.AreEqual("busy", await host.RuntimeService.CatchUpExternalTurnsAsync(sessionId), "The runtime refuses it too.");
            Assert.AreEqual(reads, provider.Reads, "The conversation is not even read while the session works.");
        }
        finally { provider.Release.TrySetResult(); }

        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await send.Completion.WaitAsync(Timeout)).Outcome);
        Assert.IsFalse((await host.SessionViewCatalog.JournalStore.CreateSessionStore().ReadTurnsAsync(sessionId)).Turns.Any(static turn => turn.MadeElsewhere));
    }

    [TestMethod]
    public async Task CatchUp_ThatCannotFollow_WritesTheNotice_AndTheStateItFollowsFrom()
    {
        using var temp = TempDirectory.Create();
        var provider = new ExternalProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Rewound elsewhere")).SessionId;
        await SendAsync(host, sessionId, "one");

        provider.External = static _ => new AgentExternalTurns(AgentExternalTurnsStatus.CannotFollow, [],
            static (_, _, _) => JsonDocument.Parse("""{"kind":"fake","leaf":"elsewhere"}""").RootElement.Clone());
        Assert.AreEqual("cannot_follow", await host.Commands.CatchUpExternalTurnsAsync(sessionId));

        var store = host.SessionViewCatalog.JournalStore.CreateSessionStore();
        Assert.AreEqual("""{"kind":"fake","leaf":"elsewhere"}""", (await store.GetStateAsync(sessionId))!.ProviderState!.Value.GetRawText());
        var history = (await host.RuntimeService.TryReadStoredHistoryAsync(await ListedAsync(host, sessionId)))!;
        var notice = history.OfType<AgentSessionUpdateEvent>().Single(static update => update.Kind == AgentSessionUpdateKind.Warning);
        Assert.AreEqual("This conversation was changed outside CodeAlta in a way it cannot follow; the model has the full conversation.", notice.Message);
        Assert.AreEqual(1, (await store.ReadTurnsAsync(sessionId)).Turns.Count, "Nothing is appended as a turn.");
        Assert.IsNull(await host.RuntimeService.TryGetActiveSessionDescriptorAsync(sessionId), "The provider is resumed from the conversation as it is now.");
    }

    [TestMethod]
    public async Task CatchUp_ThatFails_DeletesTheImagesItSaved()
    {
        using var temp = TempDirectory.Create();
        var provider = new ExternalProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Images")).SessionId;
        await SendAsync(host, sessionId, "one");

        provider.External = static _ => new AgentExternalTurns(AgentExternalTurnsStatus.Read,
            [new AgentExternalTurn(
                [
                    new AgentConversationMessage(AgentConversationRole.User,
                        [new AgentMessagePart.Text("Look."), new AgentMessagePart.Data(Convert.ToBase64String([1, 2, 3]), "image/png")]),
                    new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("Seen.")]),
                ],
                AgentExternalTurnOrigin.VsCode, "claude-vscode", DateTimeOffset.UtcNow)],
            static (_, _, _) => throw new InvalidOperationException("The state cannot be made."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Commands.CatchUpExternalTurnsAsync(sessionId).WaitAsync(Timeout));

        Assert.AreEqual(0, Directory.EnumerateFiles(temp.Root, "*.png", SearchOption.AllDirectories).Count(), "No image is left without a turn that names it.");
        Assert.AreEqual(1, (await host.SessionViewCatalog.JournalStore.CreateSessionStore().ReadTurnsAsync(sessionId)).Turns.Count);
    }

    [TestMethod]
    public async Task Send_ThatJoinsACatchUpWhichFoundTheSessionChanged_CatchesUpAgain()
    {
        using var temp = TempDirectory.Create();
        var provider = new ExternalProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Joined")).SessionId;
        await SendAsync(host, sessionId, "one");

        provider.FoundOnce = false;
        provider.External = state => provider.Turns(state, AgentExternalTurnOrigin.VsCode, "From VS Code.", "Answered there.");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.ReadGate = gate;
        var catchUp = host.Commands.CatchUpExternalTurnsAsync(sessionId);
        await provider.ReadStarted.Task.WaitAsync(Timeout);
        // Written while the conversation is read: what the read found is stale.
        var store = host.SessionViewCatalog.JournalStore.CreateSessionStore();
        await store.UpsertStateAsync((await store.GetStateAsync(sessionId))! with { UpdatedAt = DateTimeOffset.UtcNow });

        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.RuntimeService.CatchUpJoined = id => { if (id == sessionId) joined.TrySetResult(); };
        var send = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, "two")).Receipt;
        Assert.IsNotNull(send);
        // The send joins the catch-up in progress, which then ends.
        await joined.Task.WaitAsync(Timeout);
        gate.TrySetResult();
        Assert.AreEqual("stale", await catchUp.WaitAsync(Timeout));

        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await send.Completion.WaitAsync(Timeout)).Outcome);
        CollectionAssert.AreEqual(new[] { "one", "answer", "From VS Code.", "Answered there.", "two" }, provider.LastConversation(),
            "The send read the conversation again: the turns made elsewhere are before its prompt.");
        Assert.AreEqual(2, provider.Reads, "Once by the catch-up that found the session changed, once by the send.");
    }

    [TestMethod]
    public async Task CatchUp_IsNotDoneWhileACompactionHoldsTheAttachment()
    {
        using var temp = TempDirectory.Create();
        var provider = new ExternalProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Compacting")).SessionId;
        await SendAsync(host, sessionId, "one");
        var current = await host.RuntimeService.GetCurrentStateAsync(sessionId);

        var compaction = host.RuntimeService.CompactOwnedCommandAsync(
            new OwnedCompactRequest("compact", sessionId, current.RuntimeInstanceId, current.Entry!.AttachmentGeneration), CancellationToken.None);
        await provider.CompactStarted.Task.WaitAsync(Timeout);
        var reads = provider.Reads;
        try
        {
            provider.External = state => provider.Turns(state, AgentExternalTurnOrigin.VsCode, "Too early.", "Not written.");
            Assert.AreEqual("busy", await host.RuntimeService.CatchUpExternalTurnsAsync(sessionId).WaitAsync(Timeout), "No run is active, but a compaction is.");
            Assert.AreEqual(reads, provider.Reads);
        }
        finally { provider.CompactRelease.TrySetResult(); }

        Assert.IsTrue((await compaction.WaitAsync(Timeout))!.Success);
        Assert.IsNotNull(await host.RuntimeService.TryGetActiveSessionDescriptorAsync(sessionId));
    }

    [TestMethod]
    public async Task CatchUp_IsNotDoneWhileRemoteControlIsOn()
    {
        using var temp = TempDirectory.Create();
        var provider = new ExternalProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Remote")).SessionId;
        await SendAsync(host, sessionId, "one");

        provider.RemoteControlChanged!(new AgentRemoteControl(AgentRemoteControlStatus.Connected, "https://claude.ai/code/remote"));
        var deadline = DateTime.UtcNow + Timeout;
        while ((await host.RuntimeService.GetCurrentStateAsync(sessionId)).Entry?.RemoteControl.Status != AgentRemoteControlStatus.Connected)
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "The remote control is observed.");
            await Task.Delay(10);
        }

        provider.External = state => provider.Turns(state, AgentExternalTurnOrigin.VsCode, "From VS Code.", "Answered there.");
        Assert.AreEqual("busy", await host.Commands.CatchUpExternalTurnsAsync(sessionId).WaitAsync(Timeout), "Retiring the attachment would end the remote control.");
        Assert.IsNotNull(await host.RuntimeService.TryGetActiveSessionDescriptorAsync(sessionId));
    }

    [TestMethod]
    public async Task CatchUp_OfAProviderThatSharesNoConversation_AsksNothing()
    {
        using var temp = TempDirectory.Create();
        var provider = new ExternalProvider(sharesConversations: false);
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Not shared")).SessionId;
        await SendAsync(host, sessionId, "one");

        Assert.AreEqual("none", await host.Commands.CatchUpExternalTurnsAsync(sessionId).WaitAsync(Timeout));
        Assert.AreEqual(0, provider.Reads);
    }

    [TestMethod]
    public async Task SharedConversation_OfAProviderThatSharesNone_IsUnsupported_AndItsProcessIsKept()
    {
        using var temp = TempDirectory.Create();
        var provider = new ExternalProvider(sharesConversations: false);
        await using var host = await CreateHostAsync(temp, provider);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Not shared")).SessionId;

        Assert.AreEqual(new OwnedSharedConversationResult("unsupported", null), await host.Commands.GetSharedConversationAsync(sessionId).WaitAsync(Timeout),
            "No turn yet, and no conversation to share whatever the turns.");
        await SendAsync(host, sessionId, "one");

        Assert.AreEqual(new OwnedSharedConversationResult("unsupported", null), await host.Commands.GetSharedConversationAsync(sessionId).WaitAsync(Timeout));
        Assert.AreEqual(new OwnedSharedConversationResult("unsupported", null), await host.Commands.ReleaseSharedConversationAsync(sessionId).WaitAsync(Timeout));
        Assert.IsNotNull(await host.RuntimeService.TryGetActiveSessionDescriptorAsync(sessionId), "Nothing is released for a provider that shares nothing.");
    }

    private static async Task SendAsync(CodeAltaHost host, string sessionId, string prompt)
    {
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, prompt)).Receipt;
        Assert.IsNotNull(receipt);
        var result = await receipt.Completion.WaitAsync(Timeout);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
    }

    private static async Task<SessionViewDescriptor> ListedAsync(CodeAltaHost host, string sessionId)
    {
        await foreach (var session in host.RuntimeService.ListRecoverableSessionsAsync(cancellationToken: CancellationToken.None))
        {
            if (session.SessionId == sessionId) return session;
        }

        Assert.Fail($"Session '{sessionId}' is not listed.");
        return null!;
    }

    private static Task<CodeAltaHost> CreateHostAsync(TempDirectory temp, ExternalProvider provider)
        => CodeAltaHost.CreateAsync(
            new CodeAltaHostOptions
            {
                GlobalRoot = temp.GlobalRoot,
                CurrentProjectPath = temp.ProjectRoot,
                IsHeadless = true,
                AutoApproveOwnedPermissions = true,
                OwnedCommandReceiptCapacity = 32,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
            },
            CancellationToken.None);

    // A provider that keeps a conversation other programs continue: what it reads of it is what the test says.
    private sealed class ExternalProvider(bool sharesConversations = true)
        : IAgentModelProviderRuntime, IModelProviderTurnExecutor, IAgentProviderExternalTurns, IAgentProviderCompaction, IAgentProviderRemoteControl
    {
        private readonly List<IReadOnlyList<AgentConversationMessage>> _conversations = [];
        private TaskCompletionSource? _readGate;
        private int _reads;

        public Func<AgentSessionState, AgentExternalTurns> External { get; set; } = static _ => AgentExternalTurns.Unchanged;

        // Whether a read is found once: the next one finds nothing new.
        public bool FoundOnce { get; set; } = true;

        // Holds the next read until it is completed; ReadStarted completes when that read began.
        public TaskCompletionSource? ReadGate { get => Volatile.Read(ref _readGate); set => Volatile.Write(ref _readGate, value); }

        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CompactStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CompactRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<AgentRemoteControl>? RemoteControlChanged { get; private set; }

        public TaskCompletionSource? Started { get; set; }

        public TaskCompletionSource? Release { get; set; }

        public int Reads => Volatile.Read(ref _reads);

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-external"), "Fake External")
        {
            DefaultModelId = "fake-model",
            SharesConversations = sharesConversations,
        };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "fake-external", DisplayName = "Fake External", TransportKind = AgentTransportKind.OpenAIResponses,
        };

        public IModelProviderModelCatalog? ModelCatalog => null;

        public AgentRuntimeProviderRegistration CreateProviderRegistration() => new() { Provider = RuntimeDescriptor, TurnExecutor = this };

        public IModelProviderTurnExecutor CreateTurnExecutor() => this;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Ready,
                Models = [new AgentModelInfo("fake-model", DisplayName: "Fake Model")],
                SelectedModelId = "fake-model",
            });

        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            lock (_conversations) _conversations.Add([.. request.Conversation]);
            Started?.TrySetResult();
            if (Release is { } release) await release.Task.WaitAsync(cancellationToken);
            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("answer")]),
                ProviderState = JsonDocument.Parse("""{"kind":"fake","turns":0,"users":0,"assistants":0}""").RootElement.Clone(),
            };
        }

        public async Task<AgentExternalTurns> ReadExternalTurnsAsync(AgentSessionState state, string workingDirectory, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            var read = External(state);
            if (FoundOnce) External = static _ => AgentExternalTurns.Unchanged;
            if (Interlocked.Exchange(ref _readGate, null) is { } gate)
            {
                ReadStarted.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken);
            }

            return read;
        }

        public async Task<AgentCompactionOutcome> CompactAsync(AgentTurnRequest request, CancellationToken cancellationToken)
        {
            CompactStarted.TrySetResult();
            await CompactRelease.Task.WaitAsync(cancellationToken);
            return new AgentCompactionOutcome(true, "Compacted.");
        }

        public IDisposable OnRemoteControlChanged(string sessionId, Action<AgentRemoteControl> handler)
        {
            RemoteControlChanged = handler;
            return new Registration(() => RemoteControlChanged = null);
        }

        public AgentRemoteControl GetRemoteControl(string sessionId) => AgentRemoteControl.Off;

        public Task<AgentRemoteControl> SetRemoteControlAsync(AgentTurnRequest request, bool enabled, string? name, CancellationToken cancellationToken)
            => Task.FromResult(AgentRemoteControl.Off);

        public AgentExternalTurns Turns(AgentSessionState state, AgentExternalTurnOrigin origin, string prompt, string answer)
            => new(AgentExternalTurnsStatus.Read,
                [new AgentExternalTurn(
                    [
                        new AgentConversationMessage(AgentConversationRole.User, [new AgentMessagePart.Text(prompt)]),
                        new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(answer)]),
                    ],
                    origin, origin is AgentExternalTurnOrigin.VsCode ? "claude-vscode" : "cli", DateTimeOffset.UtcNow)],
                static (turns, users, assistants) => JsonDocument.Parse($$"""{"kind":"fake","turns":{{turns}},"users":{{users}},"assistants":{{assistants}}}""").RootElement.Clone());

        public string[] LastConversation()
        {
            lock (_conversations)
            {
                return [.. _conversations[^1].Select(static message => string.Concat(message.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value)))];
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Registration(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string root)
        {
            Root = root;
            GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
            ProjectRoot = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        }

        public string Root { get; }

        public string GlobalRoot { get; }

        public string ProjectRoot { get; }

        public static TempDirectory Create() => new(Path.Combine(Path.GetTempPath(), $"CodeAlta.CatchUp.{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
