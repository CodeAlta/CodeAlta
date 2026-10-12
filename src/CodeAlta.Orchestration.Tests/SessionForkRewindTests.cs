using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// A session is forked at a turn and rewound to one through the runtime, and what its provider is sent afterwards is
/// what the kept turns sent: the same conversation, the same instructions and the same prompt-cache key.
/// </summary>
[TestClass]
public sealed class SessionForkRewindTests
{
    [TestMethod]
    public async Task Fork_ContinuesFromThePoint_WithTheRequestOfItsSource()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var options = Options(temp, provider);
        var source = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Review", CancellationToken.None);
        foreach (var prompt in new[] { "one", "two", "three" }) await SendAsync(host, source, options, prompt);
        var sourceRequest = provider.Requests.Last(request => request.SessionId == source.SessionId);

        var turns = await host.RuntimeService.ReadSessionTurnsAsync(source.SessionId);
        Assert.IsNotNull(turns);
        CollectionAssert.AreEqual(new[] { "one", "two", "three" }, turns.Turns.Turns.Select(static turn => turn.PromptPreview).ToArray());
        Assert.IsNull(turns.ActiveRunId);

        var outcome = await host.RuntimeService.ForkSessionAsync(new SessionForkRequest { SessionId = source.SessionId, BeforeRunId = turns.Turns.Turns[2].RunId });

        Assert.AreEqual("ok", outcome.Status);
        var fork = outcome.Session!;
        Assert.AreNotEqual(source.SessionId, fork.SessionId);
        Assert.AreEqual("Review (fork)", fork.Title);
        Assert.AreEqual(2, outcome.KeptTurnCount);
        Assert.AreEqual("three", outcome.NextTurn!.PromptPreview);
        Assert.IsNull(fork.ParentSessionId, "A fork is not a sub-agent of its source.");
        Assert.AreEqual(source.SessionId, fork.ForkedFrom!.SessionId);
        Assert.AreEqual(source.SessionId, fork.ForkedFrom.RootSessionId);
        Assert.AreEqual(AgentSessionForkKind.Fork, fork.ForkedFrom.Kind);
        Assert.AreEqual(2, fork.ForkedFrom.TurnCount);

        // The fork is a stored session like another: listed, and attached when it is first used.
        var listed = await ListedAsync(host, fork.SessionId);
        Assert.AreEqual("Review (fork)", listed.Title);
        Assert.AreEqual(source.SessionId, listed.ForkedFrom!.SessionId);
        Assert.AreEqual(source.ProjectRef, listed.ProjectRef);
        await SendAsync(host, listed, options, "four");

        var forkRequest = provider.Requests.Last(request => request.SessionId == fork.SessionId);
        CollectionAssert.AreEqual(new[] { "one", "two", "four" }, UserTexts(forkRequest));
        Assert.AreEqual(source.SessionId, forkRequest.PromptCacheKey, "The fork reads the prompt cache of its source.");
        Assert.AreEqual(sourceRequest.SystemMessage, forkRequest.SystemMessage);
        Assert.AreEqual(sourceRequest.DeveloperInstructions, forkRequest.DeveloperInstructions, "The instructions are the same text, scratchpad file included.");
        CollectionAssert.AreEqual(sourceRequest.Tools.Select(static tool => tool.Spec.Name).ToArray(), forkRequest.Tools.Select(static tool => tool.Spec.Name).ToArray());
        // What the two requests start with is the same, message for message: that is what a provider caches.
        CollectionAssert.AreEqual(
            sourceRequest.Conversation.Take(4).Select(static message => message.ToJson()).ToArray(),
            forkRequest.Conversation.Take(4).Select(static message => message.ToJson()).ToArray());

        // The source goes on as it was.
        await SendAsync(host, source, options, "five");
        var after = provider.Requests.Last(request => request.SessionId == source.SessionId);
        CollectionAssert.AreEqual(new[] { "one", "two", "three", "five" }, UserTexts(after));
        Assert.IsNull(after.PromptCacheKey);

        // A second fork of the same session is told apart from the first.
        var sibling = await host.RuntimeService.ForkSessionAsync(new SessionForkRequest { SessionId = source.SessionId });
        Assert.AreEqual("Review (fork 2)", sibling.Session!.Title);

        // A fork of the fork keeps the key of the first session, and is named after the fork.
        var second = await host.RuntimeService.ForkSessionAsync(new SessionForkRequest { SessionId = fork.SessionId });
        Assert.AreEqual("ok", second.Status);
        Assert.AreEqual("Review (fork 3)", second.Session!.Title);
        Assert.AreEqual(fork.SessionId, second.Session.ForkedFrom!.SessionId);
        Assert.AreEqual(source.SessionId, second.Session.ForkedFrom.RootSessionId);
        Assert.AreEqual(3, second.KeptTurnCount);
        await SendAsync(host, await ListedAsync(host, second.Session.SessionId), options, "six");
        var secondRequest = provider.Requests.Last(request => request.SessionId == second.Session.SessionId);
        CollectionAssert.AreEqual(new[] { "one", "two", "four", "six" }, UserTexts(secondRequest));
        Assert.AreEqual(source.SessionId, secondRequest.PromptCacheKey);
        Assert.AreEqual(sourceRequest.DeveloperInstructions, secondRequest.DeveloperInstructions);
    }

    [TestMethod]
    public async Task Fork_OfASessionThatRuns_LeavesOutTheTurnInProgress()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var options = Options(temp, provider);
        var source = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Long work", CancellationToken.None);
        await SendAsync(host, source, options, "one");
        provider.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = host.RuntimeService.SendAsync(source, options, new AgentSendOptions { Input = AgentInput.Text("two") }, CancellationToken.None);
        try
        {
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var turns = await host.RuntimeService.ReadSessionTurnsAsync(source.SessionId);
            Assert.AreEqual(2, turns!.Turns.Turns.Count);
            Assert.AreEqual(turns.Turns.Turns[1].RunId, turns.ActiveRunId);
            var fork = await host.RuntimeService.ForkSessionAsync(new SessionForkRequest { SessionId = source.SessionId });
            var rewind = await host.RuntimeService.RewindSessionAsync(new SessionRewindRequest { SessionId = source.SessionId, BeforeRunId = turns.Turns.Turns[1].RunId });
            var later = await host.RuntimeService.ForkSessionAsync(new SessionForkRequest { SessionId = source.SessionId, BeforeRunId = "no-such-run" });

            Assert.AreEqual("ok", fork.Status);
            Assert.AreEqual(1, fork.KeptTurnCount);
            Assert.AreEqual("session_in_use", rewind.Status, "A session that runs is not rewound.");
            Assert.AreEqual("turn_not_found", later.Status);
            provider.Hold.TrySetResult();
            await running;
            await WaitIdleAsync(host, source);
            await SendAsync(host, await ListedAsync(host, fork.Session!.SessionId), options, "three");
            CollectionAssert.AreEqual(new[] { "one", "three" }, UserTexts(provider.Requests.Last(request => request.SessionId == fork.Session.SessionId)));
        }
        finally
        {
            provider.Hold.TrySetResult();
        }
    }

    [TestMethod]
    public async Task Rewind_RemovesTheTurnAndWhatFollows_AndKeepsThemInASessionOfTheirOwn()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var options = Options(temp, provider);
        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Refactoring", CancellationToken.None);
        foreach (var prompt in new[] { "one", "two", "three" }) await SendAsync(host, session, options, prompt);
        var turns = await host.RuntimeService.ReadSessionTurnsAsync(session.SessionId);
        Assert.IsFalse(host.RuntimeService.Display.GetSnapshot().Sessions.Single(value => value.SessionId == session.SessionId).Text.IsEmpty);

        var outcome = await host.RuntimeService.RewindSessionAsync(new SessionRewindRequest { SessionId = session.SessionId, BeforeRunId = turns!.Turns.Turns[1].RunId });

        Assert.AreEqual("ok", outcome.Status);
        Assert.AreEqual(1, outcome.KeptTurnCount);
        // A view shows a live text until the journal holds it: those of the removed turns would never leave it.
        var live = host.RuntimeService.Display.GetSnapshot().Sessions.Single(value => value.SessionId == session.SessionId);
        Assert.IsTrue(live.Text.IsEmpty && live.ToolActivities.IsEmpty, "The live window of a rewound session holds nothing of what was removed.");
        Assert.AreEqual("two", outcome.NextTurn!.PromptPreview, "The prompt of the first removed turn is what a frontend puts back in the composer.");
        Assert.AreEqual(session.SessionId, outcome.Session!.SessionId);
        Assert.AreEqual("Refactoring", outcome.Session.Title);
        Assert.AreEqual(1, outcome.Session.RewindCount);
        CollectionAssert.AreEqual(new[] { "one" }, (await host.RuntimeService.ReadSessionTurnsAsync(session.SessionId))!.Turns.Turns.Select(static turn => turn.PromptPreview).ToArray());

        // What was removed is a session of its own, with every turn; its own records are those of the removed turns.
        var kept = outcome.RemovedTurns!;
        Assert.AreEqual("Refactoring (before rewind)", kept.Title);
        Assert.AreEqual(AgentSessionForkKind.Rewind, kept.ForkedFrom!.Kind);
        Assert.AreEqual(session.SessionId, kept.ForkedFrom.SessionId);
        Assert.AreEqual(turns.Turns.Turns[1].StartedAt, kept.ForkedFrom.CountedFrom);
        CollectionAssert.AreEqual(new[] { "one", "two", "three" },
            (await host.RuntimeService.ReadSessionTurnsAsync(kept.SessionId))!.Turns.Turns.Select(static turn => turn.PromptPreview).ToArray());

        // The next prompt starts from the shortened conversation, under the key the session always had.
        await SendAsync(host, await ListedAsync(host, session.SessionId), options, "four");
        var request = provider.Requests.Last(value => value.SessionId == session.SessionId);
        CollectionAssert.AreEqual(new[] { "one", "four" }, UserTexts(request));
        Assert.IsNull(request.PromptCacheKey);
        // The provider is told once that the session continues from an earlier point: its first answer ends that.
        Assert.AreEqual(new AgentProviderBranch(AgentProviderBranchKind.Rewind, AtEnd: false), request.State.ProviderBranch);
        await SendAsync(host, await ListedAsync(host, session.SessionId), options, "four again");
        Assert.IsNull(provider.Requests.Last(value => value.SessionId == session.SessionId).State.ProviderBranch);
        await SendAsync(host, await ListedAsync(host, kept.SessionId), options, "five");
        CollectionAssert.AreEqual(new[] { "one", "two", "three", "five" }, UserTexts(provider.Requests.Last(value => value.SessionId == kept.SessionId)));

        // Without keeping them, the removed turns are gone.
        turns = await host.RuntimeService.ReadSessionTurnsAsync(session.SessionId);
        var discarded = await host.RuntimeService.RewindSessionAsync(new SessionRewindRequest
        {
            SessionId = session.SessionId,
            BeforeRunId = turns!.Turns.Turns[0].RunId,
            KeepRemovedTurns = false,
        });
        Assert.AreEqual("ok", discarded.Status);
        Assert.IsNull(discarded.RemovedTurns);
        Assert.AreEqual(0, discarded.KeptTurnCount);
        Assert.AreEqual(2, discarded.Session!.RewindCount);
        await SendAsync(host, await ListedAsync(host, session.SessionId), options, "six");
        CollectionAssert.AreEqual(new[] { "six" }, UserTexts(provider.Requests.Last(value => value.SessionId == session.SessionId)));
        Assert.AreEqual("session_missing", (await host.RuntimeService.RewindSessionAsync(new SessionRewindRequest { SessionId = "no-such-session", BeforeRunId = "run" })).Status);
    }

    [TestMethod]
    public async Task ForkAnAgentCreates_IsNotASubAgentOfThatAgent_UnlessItIsGivenAParent()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var options = Options(temp, provider);
        var agent = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Coordinator", CancellationToken.None);
        var source = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Review", CancellationToken.None);
        await SendAsync(host, source, options, "one");
        var by = new AltaActorProvenance { Kind = "agent", SourceSessionId = agent.SessionId };

        var alone = await host.RuntimeService.ForkSessionAsync(new SessionForkRequest { SessionId = source.SessionId, CreatedBy = by });
        var child = await host.RuntimeService.ForkSessionAsync(new SessionForkRequest { SessionId = source.SessionId, CreatedBy = by, ParentSessionId = agent.SessionId });

        // A session an agent creates is its sub-agent; a fork continues another session and reports to none.
        var listedAlone = await ListedAsync(host, alone.Session!.SessionId);
        Assert.IsNull(listedAlone.ParentSessionId);
        Assert.AreEqual(agent.SessionId, listedAlone.CreatedBy?.SourceSessionId, "Who made the fork is still recorded.");
        Assert.AreEqual(agent.SessionId, (await ListedAsync(host, child.Session!.SessionId)).ParentSessionId);

        // Its instructions are those of its source: no parent to report to, and the prompt cache is read.
        var sourceRequest = provider.Requests.Last(request => request.SessionId == source.SessionId);
        await SendAsync(host, listedAlone, options, "two");
        var aloneRequest = provider.Requests.Last(request => request.SessionId == alone.Session.SessionId);
        Assert.AreEqual(sourceRequest.DeveloperInstructions, aloneRequest.DeveloperInstructions);
        await SendAsync(host, await ListedAsync(host, child.Session.SessionId), options, "two");
        var childRequest = provider.Requests.Last(request => request.SessionId == child.Session.SessionId);
        Assert.AreNotEqual(sourceRequest.DeveloperInstructions, childRequest.DeveloperInstructions, "A fork with a parent is told about it.");
        StringAssert.Contains(childRequest.DeveloperInstructions, agent.SessionId);
    }

    [TestMethod]
    public async Task RewindOfAFork_ToBeforeThePointItWasForkedAt_KeepsOnlyItsOwnTurnsAsItsOwn()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider();
        await using var host = await CreateHostAsync(temp, provider);
        var options = Options(temp, provider);
        var source = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Review", CancellationToken.None);
        foreach (var prompt in new[] { "one", "two" }) await SendAsync(host, source, options, prompt);
        var fork = (await host.RuntimeService.ForkSessionAsync(new SessionForkRequest { SessionId = source.SessionId })).Session!;
        await SendAsync(host, await ListedAsync(host, fork.SessionId), options, "three");
        var turns = (await host.RuntimeService.ReadSessionTurnsAsync(fork.SessionId))!.Turns.Turns;

        // The fork is rewound to before a turn it inherited from its source.
        var outcome = await host.RuntimeService.RewindSessionAsync(new SessionRewindRequest { SessionId = fork.SessionId, BeforeRunId = turns[1].RunId });

        Assert.AreEqual("ok", outcome.Status);
        var kept = outcome.RemovedTurns!.ForkedFrom!;
        // The inherited turn is counted in the first session, not in the fork and not in what its rewind keeps.
        Assert.AreEqual(fork.ForkedFrom!.CountedFrom, kept.CountedFrom);
        Assert.IsTrue(kept.CountedFrom > turns[1].StartedAt);
        Assert.AreEqual(source.SessionId, kept.RootSessionId);
        Assert.AreEqual(fork.SessionId, kept.SessionId);
    }

    [TestMethod]
    public void BranchTitle_CountsTheForksOfAFork()
    {
        Assert.AreEqual("Review (fork)", SessionRuntimeService.BranchTitle("Review", "fork"));
        Assert.AreEqual("Review (fork 2)", SessionRuntimeService.BranchTitle("Review (fork)", "fork"));
        Assert.AreEqual("Review (fork 3)", SessionRuntimeService.BranchTitle(" Review (fork 2) ", "fork"));
        Assert.AreEqual("Review (forked) (fork)", SessionRuntimeService.BranchTitle("Review (forked)", "fork"));
        Assert.AreEqual("Review (fork) (before rewind)", SessionRuntimeService.BranchTitle("Review (fork)", "before rewind"));
    }

    private static string[] UserTexts(AgentTurnRequest request)
        => request.Conversation.Where(static message => message.Role == AgentConversationRole.User)
            .Select(static message => string.Concat(message.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value))).ToArray();

    private static async Task SendAsync(CodeAltaHost host, SessionViewDescriptor session, SessionExecutionOptions options, string prompt)
    {
        await host.RuntimeService.SendAsync(session, options, new AgentSendOptions { Input = AgentInput.Text(prompt) }, CancellationToken.None);
        await WaitIdleAsync(host, session);
    }

    private static async Task WaitIdleAsync(CodeAltaHost host, SessionViewDescriptor session)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (await host.RuntimeService.HasActiveRunAsync(session, CancellationToken.None))
        {
            Assert.IsTrue(DateTimeOffset.UtcNow < deadline, "The run did not end.");
            await Task.Delay(10);
        }
    }

    private static async Task<SessionViewDescriptor> ListedAsync(CodeAltaHost host, string sessionId)
    {
        await foreach (var session in host.RuntimeService.ListRecoverableSessionsAsync())
        {
            if (session.SessionId == sessionId) return session;
        }

        Assert.Fail($"Session {sessionId} is not listed.");
        return null!;
    }

    private static SessionExecutionOptions Options(TempDirectory temp, RecordingProvider provider) => new()
    {
        ProviderId = provider.Descriptor.ProviderId,
        ProviderKey = provider.Descriptor.ProviderId.Value,
        WorkingDirectory = temp.ProjectRoot,
        ProjectRoots = [temp.ProjectRoot],
        Model = "fake-model",
        OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.Deny)),
    };

    private static Task<CodeAltaHost> CreateHostAsync(TempDirectory temp, RecordingProvider provider)
        => CodeAltaHost.CreateAsync(
            new CodeAltaHostOptions
            {
                GlobalRoot = temp.GlobalRoot,
                CurrentProjectPath = temp.ProjectRoot,
                IsHeadless = true,
                HasInteractiveUi = false,
                StartPlugins = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
            },
            CancellationToken.None);

    // Answers every prompt and keeps the requests it was sent.
    private sealed class RecordingProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public ConcurrentQueue<AgentTurnRequest> Requests { get; } = new();

        public TaskCompletionSource? Hold { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-branch"), "Fake Branch") { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test",
            ProviderKey = "fake-branch",
            DisplayName = "Fake Branch",
            TransportKind = AgentTransportKind.OpenAIResponses,
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate,
            CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            var contentId = "content-" + Guid.NewGuid().ToString("N");
            await onUpdate(new AgentTurnDelta { Kind = AgentContentKind.Assistant, ContentId = contentId, Text = "answer" }, cancellationToken).ConfigureAwait(false);
            if (Hold is { } hold)
            {
                Started.TrySetResult();
                await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("answer")]),
                AssistantPartContentIds = [contentId],
                ProviderSessionId = "response-" + Requests.Count,
            };
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string root)
        {
            Root = root;
            GlobalRoot = Path.Combine(root, "global");
            ProjectRoot = Path.Combine(root, "project");
            Directory.CreateDirectory(GlobalRoot);
            Directory.CreateDirectory(ProjectRoot);
        }

        public string Root { get; }

        public string GlobalRoot { get; }

        public string ProjectRoot { get; }

        public static TempDirectory Create()
            => new(Path.Combine(Path.GetTempPath(), $"CodeAlta.ForkRewind.{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
