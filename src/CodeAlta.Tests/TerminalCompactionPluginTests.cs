using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Tui.App;
using CodeAlta.Tui.App.State;
using CodeAlta.Tui.Presentation.Timeline;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tests;

/// <summary>The manual compaction of the terminal application runs the compaction hooks of plugins as CodeAlta Desktop does.</summary>
[TestClass]
public sealed class TerminalCompactionPluginTests
{
    private const string SummaryText = "## Objective\nKeep going.\n## Active User Request\nOne.\n## Constraints\nNone.\n## Progress\n### Done\nOne.\n### In Progress\nNone.\n### Blocked\nNone.\n## Decisions\nNone.\n## Next Steps\nNone.\n## Critical Context\nKEEP-THE-TODO-LIST\n## Relevant Files\nNone.";

    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task Compact_PluginInstructionsReachTheSummaryRequest_AndTheAfterHookSeesTheSummary()
    {
        await using var fixture = await Fixture.CreateAsync(new CompactionPlugin { Instructions = "Keep the list of TODO items verbatim." });
        var session = await fixture.PrepareAsync();

        var cancelReason = await fixture.CompactAsync(session);

        Assert.IsNull(cancelReason);
        Assert.AreEqual(1, fixture.Provider.SummaryRequests.Count);
        Assert.IsTrue(fixture.Provider.SummaryRequests.TryPeek(out var systemMessage));
        StringAssert.Contains(systemMessage, "Keep the list of TODO items verbatim.");
        // The instructions shape the summary only: the next turns of the session do not carry them.
        await fixture.SendAsync(session, "three");
        Assert.IsFalse(fixture.Provider.TurnInstructions.Last().Contains("Keep the list of TODO items verbatim.", StringComparison.Ordinal));
        var after = fixture.Plugin.After.Single();
        Assert.IsTrue(after.Succeeded);
        StringAssert.Contains(after.Summary, "KEEP-THE-TODO-LIST");
    }

    [TestMethod]
    public async Task Compact_AFailedCompaction_TellsTheAfterHookOnce()
    {
        await using var fixture = await Fixture.CreateAsync(new CompactionPlugin());
        var session = await fixture.PrepareAsync();
        fixture.Provider.FailSummaries = true;

        await Assert.ThrowsAsync<Exception>(() => fixture.CompactAsync(session));

        var after = fixture.Plugin.After.Single();
        Assert.IsFalse(after.Succeeded);
        StringAssert.Contains(after.Summary, "The summary model is unavailable.");
    }

    [TestMethod]
    public async Task Compact_APluginThatCancels_CompactsNothing()
    {
        await using var fixture = await Fixture.CreateAsync(new CompactionPlugin { CancelReason = "The nightly report needs the full history." });
        var session = await fixture.PrepareAsync();

        var cancelReason = await fixture.CompactAsync(session);

        Assert.AreEqual("The nightly report needs the full history.", cancelReason);
        Assert.AreEqual(0, fixture.Provider.SummaryRequests.Count);
        Assert.AreEqual(0, fixture.Plugin.After.Count);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;

        private Fixture(string root, CodeAltaHost host, SummaryProvider provider, CompactionPlugin plugin)
            => (_root, Host, Provider, Plugin) = (root, host, provider, plugin);

        public CodeAltaHost Host { get; }

        public SummaryProvider Provider { get; }

        public CompactionPlugin Plugin { get; }

        public static async Task<Fixture> CreateAsync(CompactionPlugin plugin)
        {
            var root = Directory.CreateTempSubdirectory("CodeAlta-terminal-compaction-").FullName;
            var provider = new SummaryProvider();
            var host = await CodeAltaHost.CreateAsync(
                new CodeAltaHostOptions
                {
                    GlobalRoot = Path.Combine(root, "global"),
                    CurrentProjectPath = Directory.CreateDirectory(Path.Combine(root, "project")).FullName,
                    IsHeadless = true,
                    PluginFrontend = PluginFrontends.Terminal,
                    PluginBuiltIns = [new BuiltInPluginDefinition { Id = "compaction", DisplayName = "Compaction", PluginType = typeof(CompactionPlugin), Factory = () => plugin }],
                    ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
                },
                CancellationToken.None);
            return new Fixture(root, host, provider, plugin);
        }

        public SessionExecutionOptions Options => new()
        {
            ProviderId = Provider.Descriptor.ProviderId,
            WorkingDirectory = Host.CurrentProject.ProjectPath,
            ProjectRoots = [Host.CurrentProject.ProjectPath],
            Model = "fake-model",
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
        };

        // A session with a conversation to compact, idle.
        public async Task<SessionViewDescriptor> PrepareAsync()
        {
            var session = await Host.RuntimeService.CreateProjectSessionAsync(Host.CurrentProject, Options, "Compacted");
            await SendAsync(session, "one");
            await SendAsync(session, "two");
            return session;
        }

        public async Task SendAsync(SessionViewDescriptor session, string text)
        {
            using var timeout = new CancellationTokenSource(Hang);
            var turns = Provider.TurnInstructions.Count;
            await Host.RuntimeService.SendAsync(session, Options, new AgentSendOptions { Input = AgentInput.Text(text) }, timeout.Token);
            while (Provider.TurnInstructions.Count == turns || await Host.RuntimeService.HasActiveRunAsync(session, timeout.Token))
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        // What the /compact command of the terminal application does once it checked that the session is idle.
        public async Task<string?> CompactAsync(SessionViewDescriptor session)
        {
            var bridge = new PluginHostBridge(Host.PluginRuntime, () => Host.CurrentProject);
            var tab = new OpenSessionState(session, new SessionTimelinePresenter(new InlineUiDispatcher(), static () => null));
            using var timeout = new CancellationTokenSource(Hang);
            return await SessionCommandCoordinator.CompactWithPluginHooksAsync(Host.RuntimeService, bridge, session, tab, Options, timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    public sealed record AfterCall(bool Succeeded, string? Summary);

    public sealed class CompactionPlugin : PluginBase
    {
        public string? CancelReason { get; init; }

        public string? Instructions { get; init; }

        public ConcurrentQueue<AfterCall> After { get; } = new();

        public override IEnumerable<PluginCompactionContribution> GetCompactionContributions()
        {
            yield return new PluginCompactionContribution
            {
                Capabilities = PluginCompactionCapabilities.Cancel | PluginCompactionCapabilities.Instructions | PluginCompactionCapabilities.Observe,
                BeforeCompaction = (_, _) => ValueTask.FromResult(CancelReason is null
                    ? PluginBeforeCompactionResult.Continue
                    : PluginBeforeCompactionResult.CancelWithReason(CancelReason)),
                Instructions = (_, _) => ValueTask.FromResult(new PluginCompactionInstructionResult { Instructions = Instructions }),
                AfterCompaction = (context, _) =>
                {
                    After.Enqueue(new(context.Succeeded, context.Summary));
                    return ValueTask.CompletedTask;
                },
            };
        }
    }

    public sealed class SummaryProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        // The system message of each summary request of a compaction.
        public ConcurrentQueue<string> SummaryRequests { get; } = new();

        // The developer instructions of each turn of the conversation.
        public ConcurrentQueue<string> TurnInstructions { get; } = new();

        public volatile bool FailSummaries;

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-terminal-compaction"), "Fake Compaction") { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "fake-terminal-compaction", DisplayName = "Fake Compaction", TransportKind = AgentTransportKind.OpenAIResponses,
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
            var text = "done";
            if (request.IsStandalone)
            {
                SummaryRequests.Enqueue(request.SystemMessage ?? string.Empty);
                if (FailSummaries) throw new InvalidOperationException("The summary model is unavailable.");
                text = SummaryText;
            }
            else
            {
                TurnInstructions.Enqueue($"{request.SystemMessage}\n{request.DeveloperInstructions}");
            }

            return Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(text)]),
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
        }

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
    }
}
