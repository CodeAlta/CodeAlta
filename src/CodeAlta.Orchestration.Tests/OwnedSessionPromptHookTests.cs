using System.Collections.Concurrent;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Tests;

/// <summary>The prompt-submit hooks of plugins see the prompts the user sends through the host's command owner.</summary>
[TestClass]
public sealed class OwnedSessionPromptHookTests
{
    [TestMethod]
    public async Task OwnedSend_APluginThatReplacesThePrompt_SendsItsTextAndTheAttachmentsItKept()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Plugin.Handler = context => PluginPromptResult.Replace(context.Text.ToUpperInvariant(), [context.Attachments[1]]);

        var result = await fixture.SendAsync(new("replace", fixture.Session.SessionId, "make it loud")
        {
            Attachments = [new AgentInputItem.Text("first block"), new AgentInputItem.Text("second block")],
        });

        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
        Assert.IsNotNull(result.RunId);
        var turn = await fixture.NextTurnAsync();
        StringAssert.Contains(turn.UserText, "MAKE IT LOUD");
        StringAssert.Contains(turn.UserText, "second block");
        Assert.IsFalse(turn.UserText.Contains("first block", StringComparison.Ordinal), turn.UserText);
        Assert.IsFalse(turn.UserText.Contains("make it loud", StringComparison.Ordinal), turn.UserText);
        // The hook saw the prompt as typed, and each attachment with an identity it can return.
        Assert.IsTrue(fixture.Plugin.Seen.TryDequeue(out var seen));
        Assert.AreEqual("make it loud", seen!.Text);
        CollectionAssert.AreEqual(new[] { "attachment-1", "attachment-2" }, seen.Attachments.Select(static item => item.Metadata["id"]).ToArray());
        Assert.AreEqual(PluginPromptAttachmentKind.Text, seen.Attachments[0].Kind);
        Assert.AreEqual(fixture.Session.SessionId, seen.SessionId);
    }

    [TestMethod]
    public async Task OwnedSend_APluginThatHandlesThePrompt_SendsNothingAndCompletesWithItsMessage()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Plugin.Handler = _ => PluginPromptResult.Handled("Opened the ticket.");

        var result = await fixture.SendAsync(new("handled", fixture.Session.SessionId, "/ticket 42"));

        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome);
        Assert.AreEqual(OwnedSessionCommandService.PluginHandledCode, result.Code);
        Assert.AreEqual("Opened the ticket.", result.Message);
        Assert.IsNull(result.RunId);
        Assert.IsFalse(fixture.Provider.Turns.Reader.TryRead(out _));
        Assert.AreEqual(0, fixture.Plugin.BeforeRuns);
        Assert.IsFalse(await fixture.Host.RuntimeService.HasActiveRunAsync(fixture.Session, CancellationToken.None));
    }

    [TestMethod]
    public async Task OwnedSend_APluginThatCancelsThePrompt_SendsNothingAndIsCancelledWithItsMessage()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Plugin.Handler = _ => PluginPromptResult.Cancel("  Secrets are not sent.  ");

        var result = await fixture.SendAsync(new("cancelled", fixture.Session.SessionId, "my password is hunter2"));

        Assert.AreEqual(OwnedSessionCommandOutcome.Cancelled, result.Outcome);
        Assert.AreEqual(OwnedSessionCommandService.PluginCancelledCode, result.Code);
        Assert.AreEqual("Secrets are not sent.", result.Message);
        Assert.IsNull(result.RunId);
        Assert.IsFalse(fixture.Provider.Turns.Reader.TryRead(out _));
        Assert.AreEqual(0, fixture.Plugin.BeforeRuns);

        // Without a message, the receipt says nothing of its own: the window says what happened.
        fixture.Plugin.Handler = _ => PluginPromptResult.Cancel();
        var silent = await fixture.SendAsync(new("cancelled-silently", fixture.Session.SessionId, "again"));
        Assert.AreEqual(OwnedSessionCommandService.PluginCancelledCode, silent.Code);
        Assert.IsNull(silent.Message);
    }

    [TestMethod]
    public async Task OwnedSend_APromptOfTheHost_DoesNotGoThroughTheHooks()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Plugin.Handler = _ => PluginPromptResult.Cancel("Never.");

        var result = await fixture.SendAsync(new("reminder", fixture.Session.SessionId, "Check the build.") { Provenance = AgentPromptProvenance.Reminder });

        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
        StringAssert.Contains((await fixture.NextTurnAsync()).UserText, "Check the build.");
        Assert.IsTrue(fixture.Plugin.Seen.IsEmpty);
    }

    [TestMethod]
    public async Task OwnedSend_ThePromptPartsOfAHook_ReachTheInstructionsOfThatRunOnly()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string Part = "Answer this one in French.";
        fixture.Plugin.Handler = context => context.Text == "first"
            ? PluginPromptResult.Continue with { TemporaryPromptContributions = [Prompt.Static(PluginPromptChannel.Developer, Part)] }
            : null;

        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await fixture.SendAsync(new("first", fixture.Session.SessionId, "first"))).Outcome);
        var first = await fixture.NextTurnAsync();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await fixture.SendAsync(new("second", fixture.Session.SessionId, "second"))).Outcome);
        var second = await fixture.NextTurnAsync();

        StringAssert.Contains(first.DeveloperInstructions, Part);
        StringAssert.Contains(first.DeveloperInstructions, PromptHookPlugin.Guidance);
        Assert.IsFalse(second.DeveloperInstructions.Contains(Part, StringComparison.Ordinal), second.DeveloperInstructions);
        StringAssert.Contains(second.DeveloperInstructions, PromptHookPlugin.Guidance);
    }

    [TestMethod]
    public async Task HeadlessRun_APromptAPluginAnswers_EndsWithWhatThePluginSaid()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var driver = new HeadlessSessionDriver(fixture.Host);
        var opened = await driver.OpenAsync(new HeadlessSessionRequest { ProjectFolder = fixture.ProjectRoot, ModelRef = "fake-prompt-hooks:fake-model" });
        Assert.AreEqual(HeadlessOpenStatus.Created, opened.Status, opened.Message);

        fixture.Plugin.Handler = _ => PluginPromptResult.Handled("Opened the ticket.");
        var handled = await driver.RunAsync(opened.Session!, new HeadlessPrompt("/ticket 42")).WaitAsync(TestDeadlines.Hang);
        Assert.AreEqual(HeadlessRunStatus.Completed, handled.Status, handled.FailureMessage);
        Assert.AreEqual("Opened the ticket.", handled.FinalMessage);
        Assert.IsNull(handled.RunId);

        fixture.Plugin.Handler = _ => PluginPromptResult.Cancel("Secrets are not sent.");
        var cancelled = await driver.RunAsync(opened.Session!, new HeadlessPrompt("my password")).WaitAsync(TestDeadlines.Hang);
        Assert.AreEqual(HeadlessRunStatus.Aborted, cancelled.Status);
        Assert.AreEqual("plugin", cancelled.AbortReason);
        Assert.AreEqual("Secrets are not sent.", cancelled.FailureMessage);
        Assert.IsFalse(fixture.Provider.Turns.Reader.TryRead(out _));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory _temp;

        private Fixture(TempDirectory temp, CodeAltaHost host, RecordingProvider provider, PromptHookPlugin plugin, SessionViewDescriptor session)
            => (_temp, Host, Provider, Plugin, Session) = (temp, host, provider, plugin, session);

        public CodeAltaHost Host { get; }

        public RecordingProvider Provider { get; }

        public PromptHookPlugin Plugin { get; }

        public SessionViewDescriptor Session { get; }

        public string ProjectRoot => _temp.ProjectRoot;

        public static async Task<Fixture> CreateAsync()
        {
            var temp = TempDirectory.Create();
            var provider = new RecordingProvider();
            var plugin = new PromptHookPlugin();
            var host = await CodeAltaHost.CreateAsync(
                new CodeAltaHostOptions
                {
                    GlobalRoot = temp.GlobalRoot,
                    CurrentProjectPath = temp.ProjectRoot,
                    IsHeadless = true,
                    AutoApproveOwnedPermissions = true,
                    OwnedCommandReceiptCapacity = 16,
                    PluginBuiltIns = [new BuiltInPluginDefinition { Id = "prompt-hook", DisplayName = "Prompt hook", PluginType = typeof(PromptHookPlugin), Factory = () => plugin }],
                    ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
                },
                CancellationToken.None);
            var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Prompt hooks");
            return new(temp, host, provider, plugin, session);
        }

        public async Task<OwnedSessionCommandResult> SendAsync(OwnedTextSendRequest request)
        {
            var receipt = Host.Commands.AdmitSend(request).Receipt;
            Assert.IsNotNull(receipt);
            return await receipt.Completion.WaitAsync(TestDeadlines.Hang);
        }

        public async Task<RecordedTurn> NextTurnAsync()
        {
            using var timeout = new CancellationTokenSource(TestDeadlines.Hang);
            var turn = await Provider.Turns.Reader.ReadAsync(timeout.Token);
            // The next send may change the session's instructions, which the runtime only does for an idle session.
            while (await Host.RuntimeService.HasActiveRunAsync(Session, timeout.Token)) await Task.Delay(20, timeout.Token);
            return turn;
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            _temp.Dispose();
        }
    }

    private sealed record RecordedTurn(string UserText, string DeveloperInstructions);

    public sealed class PromptHookPlugin : PluginBase
    {
        public const string Guidance = "Prompt hook plugin guidance.";

        private int _beforeRuns;

        public Func<PluginPromptSubmittingContext, PluginPromptResult?>? Handler { get; set; }

        public ConcurrentQueue<PluginPromptSubmittingContext> Seen { get; } = new();

        public int BeforeRuns => Volatile.Read(ref _beforeRuns);

        public override IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions()
        {
            yield return Prompt.Static(PluginPromptChannel.Developer, Guidance);
        }

        public override ValueTask<PluginPromptResult?> OnPromptSubmittingAsync(PluginPromptSubmittingContext context, CancellationToken cancellationToken = default)
        {
            Seen.Enqueue(context);
            return new(Handler?.Invoke(context));
        }

        public override ValueTask<PluginBeforeAgentRunResult?> OnBeforeAgentRunAsync(PluginBeforeAgentRunContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _beforeRuns);
            return new((PluginBeforeAgentRunResult?)null);
        }
    }

    private sealed class RecordingProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public Channel<RecordedTurn> Turns { get; } = Channel.CreateUnbounded<RecordedTurn>();

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-prompt-hooks"), "Fake Prompt Hooks") { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "fake-prompt-hooks", DisplayName = "Fake Prompt Hooks", TransportKind = AgentTransportKind.OpenAIResponses,
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
            var user = request.Conversation.Last(static message => message.Role == AgentConversationRole.User);
            Turns.Writer.TryWrite(new(string.Join("\n", user.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value)),
                request.DeveloperInstructions ?? string.Empty));
            return Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("done")]),
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

        public static TempDirectory Create() => new(Path.Combine(Path.GetTempPath(), $"CodeAlta.OwnedPromptHooks.{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
