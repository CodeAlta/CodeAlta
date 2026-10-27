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

        await host.RuntimeService.SendAsync(child, Options(provider, temp), new AgentSendOptions { Input = AgentInput.Text("one") }).WaitAsync(TestDeadlines.Hang);

        Assert.AreEqual(1, provider.Instructions.Count);
        Assert.IsFalse(provider.Instructions[^1]!.Contains(Instruction, StringComparison.Ordinal));
        Assert.IsNull(await SuggestionAsync(host, child.SessionId));
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
        => (await host.RuntimeService.GetCurrentStateAsync(sessionId).WaitAsync(TestDeadlines.Hang)).Entry?.PromptSuggestion;

    private static async Task SendAsync(CodeAltaHost host, string sessionId, string prompt = "one")
    {
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), sessionId, prompt)).Receipt;
        Assert.IsNotNull(receipt);
        var result = await receipt.Completion.WaitAsync(TestDeadlines.Hang);
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
