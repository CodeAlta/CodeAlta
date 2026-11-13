using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The model of a session the user writes in suggests the next prompt with its answer, whatever its provider: the
/// instructions ask for a <c>&lt;next_prompt&gt;</c> tag while the setting of the user says so, the tag is not shown,
/// and its content is the suggestion of the session once the turn ended.
/// </summary>
[TestClass]
public sealed class SessionPromptSuggestionTests
{
    private const string Instruction = "Next prompt suggestion:";
    private const string Reply = "Done.\n<next_prompt>run the tests</next_prompt>";

    [TestMethod]
    public async Task UserSession_IsAskedForASuggestion_AndOffersTheOneOfItsAnswer()
    {
        using var temp = TempDirectory.Create();
        var provider = new AnsweringProvider { Reply = Reply };
        await using var host = await CreateHostAsync(temp, provider, showsSuggestions: true);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Suggested")).SessionId;

        await SendAsync(host, sessionId);

        StringAssert.Contains(provider.Instructions[^1], Instruction, "On by default.");
        Assert.AreEqual("run the tests", await SuggestionAsync(host, sessionId));
        var history = await host.RuntimeService.GetHistoryAsync(sessionId, CancellationToken.None);
        var answer = history.OfType<AgentContentCompletedEvent>().Single(static content => content.Kind == AgentContentKind.Assistant);
        Assert.AreEqual("Done.", answer.Content.TrimEnd(), "The tag is not shown.");
        Assert.IsFalse(history.OfType<AgentPromptSuggestionEvent>().Any(), "A suggestion is not history.");

        // An answer without a tag suggests nothing: the suggestion of the turn before is gone with the run.
        provider.Reply = "Nothing obvious follows.";
        await SendAsync(host, sessionId, "two");
        Assert.IsNull(await SuggestionAsync(host, sessionId));
    }

    [TestMethod]
    public async Task SettingOff_ReachesTheSessionWithItsNextPrompt()
    {
        using var temp = TempDirectory.Create();
        var provider = new AnsweringProvider { Reply = Reply };
        await using var host = await CreateHostAsync(temp, provider, showsSuggestions: true);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Suggested")).SessionId;
        await SendAsync(host, sessionId);
        Assert.AreEqual("run the tests", await SuggestionAsync(host, sessionId));

        var settings = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.GlobalRoot });
        settings.SaveGlobalPromptSuggestions(false);
        await SendAsync(host, sessionId, "two");

        Assert.IsFalse(provider.Instructions[^1]!.Contains(Instruction, StringComparison.Ordinal));
        Assert.IsNull(await SuggestionAsync(host, sessionId), "A tag the model writes all the same is not offered.");
        Assert.IsFalse((await host.RuntimeService.GetHistoryAsync(sessionId, CancellationToken.None)).OfType<AgentContentCompletedEvent>()
            .Any(static content => content.Content.Contains("next_prompt", StringComparison.Ordinal)), "Nor shown.");

        settings.SaveGlobalPromptSuggestions(true);
        await SendAsync(host, sessionId, "three");
        StringAssert.Contains(provider.Instructions[^1], Instruction);
        Assert.AreEqual("run the tests", await SuggestionAsync(host, sessionId));
    }

    [TestMethod]
    public async Task SessionAnAgentDrives_IsNotAskedForSuggestions()
    {
        using var temp = TempDirectory.Create();
        var provider = new AnsweringProvider { Reply = Reply };
        await using var host = await CreateHostAsync(temp, provider, showsSuggestions: true);
        var parentId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Parent")).SessionId;
        var child = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, Options(provider, temp), "Child", parentId, null, CancellationToken.None);

        await host.RuntimeService.SendAsync(child, Options(provider, temp), new AgentSendOptions { Input = AgentInput.Text("one") }).WithinAsync(TestDeadlines.Hang);

        Assert.AreEqual(1, provider.Instructions.Count);
        Assert.IsFalse(provider.Instructions[^1]!.Contains(Instruction, StringComparison.Ordinal));
        Assert.IsNull(await SuggestionAsync(host, child.SessionId));
        Assert.IsTrue(await host.RuntimeService.DetachRuntimeSessionAsync(child.SessionId).WithinAsync(TestDeadlines.Hang));
        Assert.IsNull((await ObserveAsync(host, child)).PromptSuggestion, "Nor stored for it.");
    }

    [TestMethod]
    public async Task HostThatShowsNoSuggestion_DoesNotAskForThem()
    {
        // The terminal UI shows none: its sessions are not asked, and a tag is still never shown.
        using var temp = TempDirectory.Create();
        var provider = new AnsweringProvider { Reply = Reply };
        await using var host = await CreateHostAsync(temp, provider, showsSuggestions: false);
        var sessionId = (await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Plain")).SessionId;

        await SendAsync(host, sessionId);

        Assert.IsFalse(provider.Instructions[^1]!.Contains(Instruction, StringComparison.Ordinal));
        Assert.IsNull(await SuggestionAsync(host, sessionId));
        Assert.AreEqual("Done.", (await host.RuntimeService.GetHistoryAsync(sessionId, CancellationToken.None))
            .OfType<AgentContentCompletedEvent>().Single(static content => content.Kind == AgentContentKind.Assistant).Content.TrimEnd());
    }

    [TestMethod]
    public async Task Suggestion_OutlivesItsAttachment_AndARestart_UntilTheNextRun()
    {
        using var temp = TempDirectory.Create();
        var provider = new AnsweringProvider { Reply = Reply };
        SessionViewDescriptor session;
        string? run;
        await using (var host = await CreateHostAsync(temp, provider, showsSuggestions: true))
        {
            session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Suggested");
            await SendAsync(host, session.SessionId);
            var entry = (await host.RuntimeService.GetCurrentStateAsync(session.SessionId).WithinAsync(TestDeadlines.Hang)).Entry!;
            run = entry.PromptSuggestionRunId;
            Assert.AreEqual("run the tests", entry.PromptSuggestion);
            Assert.IsNotNull(run, "The suggestion names the run it answers.");

            // Detached, as a session nobody shows is once it has nothing to do: it has no entry and keeps its suggestion.
            Assert.IsTrue(await host.RuntimeService.DetachRuntimeSessionAsync(session.SessionId).WithinAsync(TestDeadlines.Hang));
            var detached = await ObserveAsync(host, session);
            Assert.IsNull(detached.Entry);
            Assert.AreEqual(("run the tests", run), (detached.PromptSuggestion, detached.PromptSuggestionRunId));

            // Attached again without a run, its entry offers it again.
            var resumable = await host.RuntimeService.TryGetResumableSessionAsync(session.SessionId).WithinAsync(TestDeadlines.Hang);
            await host.RuntimeService.EnsureCoordinatorSessionAsync(resumable!, Options(provider, temp)).WithinAsync(TestDeadlines.Hang);
            var seeded = (await host.RuntimeService.GetCurrentStateAsync(session.SessionId).WithinAsync(TestDeadlines.Hang)).Entry!;
            Assert.AreEqual(("run the tests", run), (seeded.PromptSuggestion, seeded.PromptSuggestionRunId));
        }

        // A host that starts again reads it from the journal of the session.
        await using (var host = await CreateHostAsync(temp, provider, showsSuggestions: true))
        {
            var restarted = await ObserveAsync(host, session);
            Assert.IsNull(restarted.Entry);
            Assert.AreEqual(("run the tests", run), (restarted.PromptSuggestion, restarted.PromptSuggestionRunId));
            // The state the panel of the session reads says it too; the bare runtime facts read no journal.
            var shown = await host.RuntimeService.GetShownStateAsync(session.SessionId).WithinAsync(TestDeadlines.Hang);
            Assert.AreEqual(("run the tests", run), (shown.PromptSuggestion, shown.PromptSuggestionRunId));
            Assert.IsNull((await host.RuntimeService.GetCurrentStateAsync(session.SessionId).WithinAsync(TestDeadlines.Hang)).PromptSuggestion);

            // The next run ends it, live and stored.
            provider.Reply = "Nothing obvious follows.";
            await SendAsync(host, session.SessionId, "two");
            Assert.IsNull(await SuggestionAsync(host, session.SessionId));
            Assert.IsTrue(await host.RuntimeService.DetachRuntimeSessionAsync(session.SessionId).WithinAsync(TestDeadlines.Hang));
            Assert.IsNull((await ObserveAsync(host, session)).PromptSuggestion);
        }
    }

    [TestMethod]
    public async Task StoredSuggestion_IsNotOfferedAgain_OnceTheSettingIsOff()
    {
        using var temp = TempDirectory.Create();
        var provider = new AnsweringProvider { Reply = Reply };
        await using var host = await CreateHostAsync(temp, provider, showsSuggestions: true);
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Suggested");
        await SendAsync(host, session.SessionId);
        Assert.IsTrue(await host.RuntimeService.DetachRuntimeSessionAsync(session.SessionId).WithinAsync(TestDeadlines.Hang));
        Assert.AreEqual("run the tests", (await ObserveAsync(host, session)).PromptSuggestion);

        var settings = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.GlobalRoot });
        settings.SaveGlobalPromptSuggestions(false);
        Assert.IsNull((await ObserveAsync(host, session)).PromptSuggestion, "Not for a session without entry.");
        Assert.IsNull((await host.RuntimeService.GetShownStateAsync(session.SessionId).WithinAsync(TestDeadlines.Hang)).PromptSuggestion);
        var resumable = await host.RuntimeService.TryGetResumableSessionAsync(session.SessionId).WithinAsync(TestDeadlines.Hang);
        await host.RuntimeService.EnsureCoordinatorSessionAsync(resumable!, Options(provider, temp)).WithinAsync(TestDeadlines.Hang);
        Assert.IsNull(await SuggestionAsync(host, session.SessionId), "Nor for one attached again.");

        // Turned on again, the one the journal still keeps is offered once the session has no entry.
        settings.SaveGlobalPromptSuggestions(true);
        Assert.IsTrue(await host.RuntimeService.DetachRuntimeSessionAsync(session.SessionId).WithinAsync(TestDeadlines.Hang));
        Assert.AreEqual("run the tests", (await ObserveAsync(host, session)).PromptSuggestion);
    }

    [TestMethod]
    public async Task ForkAndRewind_DoNotCarryTheSuggestion()
    {
        using var temp = TempDirectory.Create();
        var provider = new AnsweringProvider { Reply = Reply };
        await using var host = await CreateHostAsync(temp, provider, showsSuggestions: true);
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Suggested");
        await SendAsync(host, session.SessionId);
        await SendAsync(host, session.SessionId, "two");
        Assert.AreEqual("run the tests", await SuggestionAsync(host, session.SessionId));

        var fork = await host.RuntimeService.ForkSessionAsync(new SessionForkRequest { SessionId = session.SessionId }).WithinAsync(TestDeadlines.Hang);
        Assert.AreEqual("ok", fork.Status);
        Assert.IsNull((await ObserveAsync(host, fork.Session!)).PromptSuggestion, "A fork does not continue the turn the suggestion answered.");

        var turns = await host.RuntimeService.ReadSessionTurnsAsync(session.SessionId).WithinAsync(TestDeadlines.Hang);
        var rewind = await host.RuntimeService.RewindSessionAsync(new SessionRewindRequest { SessionId = session.SessionId, BeforeRunId = turns!.Turns.Turns[1].RunId })
            .WithinAsync(TestDeadlines.Hang);
        Assert.AreEqual("ok", rewind.Status);
        var rewound = await ObserveAsync(host, rewind.Session!);
        Assert.IsNull(rewound.Entry);
        Assert.IsNull(rewound.PromptSuggestion, "Nor does a rewound session.");
    }

    // The state the desktop reads of a session it shows, also when the session has no entry.
    private static async Task<SessionRuntimeCurrentState> ObserveAsync(CodeAltaHost host, SessionViewDescriptor session)
    {
        var observation = await host.RuntimeService.ObserveOwnedStateAsync(session.SessionId, session.CreatedAt, "project", session.ProjectRef, session.WorkingDirectory)
            .WithinAsync(TestDeadlines.Hang);
        Assert.AreEqual("ok", observation.Status);
        return observation.State!;
    }

    private static SessionExecutionOptions Options(AnsweringProvider provider, TempDirectory temp) => new()
    {
        ProviderId = provider.Descriptor.ProviderId,
        ProviderKey = provider.Descriptor.ProviderId.Value,
        WorkingDirectory = temp.ProjectRoot,
        ProjectRoots = [temp.ProjectRoot],
        Model = "fake-model",
        OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
    };

    private static async Task<string?> SuggestionAsync(CodeAltaHost host, string sessionId)
        => (await host.RuntimeService.GetCurrentStateAsync(sessionId).WithinAsync(TestDeadlines.Hang)).Entry?.PromptSuggestion;

    private static async Task SendAsync(CodeAltaHost host, string sessionId, string prompt = "one")
    {
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, prompt)).Receipt;
        Assert.IsNotNull(receipt);
        var result = await receipt.Completion.WithinAsync(TestDeadlines.Hang);
        if (result.Outcome == OwnedSessionCommandOutcome.Completed) return;
        var errors = (await host.RuntimeService.GetHistoryAsync(sessionId, CancellationToken.None)).OfType<AgentErrorEvent>().Select(static error => error.Message);
        Assert.Fail($"{result.Outcome} {result.Code}: {string.Join(" | ", errors)}");
    }

    private static Task<CodeAltaHost> CreateHostAsync(TempDirectory temp, AnsweringProvider provider, bool showsSuggestions)
        => CodeAltaHost.CreateAsync(
            new CodeAltaHostOptions
            {
                GlobalRoot = temp.GlobalRoot,
                CurrentProjectPath = temp.ProjectRoot,
                IsHeadless = true,
                AutoApproveOwnedPermissions = true,
                OwnedCommandReceiptCapacity = 32,
                ShowsPromptSuggestions = showsSuggestions,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
            },
            CancellationToken.None);

    private sealed class AnsweringProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        /// <summary>The developer instructions of each turn, in order.</summary>
        public List<string?> Instructions { get; } = [];

        public string Reply { get; set; } = "An answer.";

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-suggestions"), "Fake Suggestions") { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "fake-suggestions", DisplayName = "Fake Suggestions", TransportKind = AgentTransportKind.OpenAIResponses,
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

        public Task<AgentTurnResponse> ExecuteTurnAsync(AgentTurnRequest request, Func<AgentTurnDelta, CancellationToken, ValueTask> onUpdate, CancellationToken cancellationToken = default)
        {
            lock (Instructions) Instructions.Add(AgentInstructionText.DeveloperAndSession(request));
            return Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(Reply)]),
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

        public static TempDirectory Create() => new(Path.Combine(Path.GetTempPath(), $"CodeAlta.Suggestions.{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
