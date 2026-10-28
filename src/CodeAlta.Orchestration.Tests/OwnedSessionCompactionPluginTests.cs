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

/// <summary>A manual compaction of the host's command owner (the one of CodeAlta Desktop) runs the compaction hooks of plugins.</summary>
[TestClass]
public sealed class OwnedSessionCompactionPluginTests
{
    private const string SummaryText = "## Objective\nKeep going.\n## Active User Request\nOne.\n## Constraints\nNone.\n## Progress\n### Done\nOne.\n### In Progress\nNone.\n### Blocked\nNone.\n## Decisions\nNone.\n## Next Steps\nNone.\n## Critical Context\nKEEP-THE-TODO-LIST\n## Relevant Files\nNone.";

    [TestMethod]
    public async Task OwnedCompact_APluginThatCancels_SettlesWithItsCodeAndRecordsItsReason()
    {
        await using var fixture = await Fixture.CreateAsync(new CompactionPlugin { CancelReason = "The nightly report needs the full history." });
        var session = await fixture.PrepareAsync();

        var result = await fixture.CompactAsync(session);

        Assert.AreEqual(OwnedSessionCommandOutcome.Failed, result.Outcome);
        Assert.AreEqual("compact_plugin_cancelled", result.Code);
        Assert.AreEqual(0, fixture.Provider.SummaryRequests.Count, "A cancelled compaction asks the model nothing.");
        Assert.AreEqual(1, fixture.Plugin.Before.Count);
        Assert.AreEqual(0, fixture.Plugin.InstructionCalls, "Instructions are asked only of a compaction no plugin cancelled.");
        Assert.AreEqual(0, fixture.Plugin.After.Count, "A compaction that did not happen has no after-hook.");
        // The timeline of the window reads the journal of the session.
        var history = await fixture.Host.SessionViewCatalog.JournalStore.CreateSessionStore().ReadEventsAsync(session.SessionId);
        Assert.IsTrue(history.OfType<AgentSessionUpdateEvent>().Any(update => update.Kind == AgentSessionUpdateKind.Warning
            && update.Message == "A plugin cancelled the compaction: The nightly report needs the full history."));
    }

    [TestMethod]
    public async Task OwnedCompact_PluginInstructionsReachTheSummaryRequest_AndTheAfterHookSeesTheSummary()
    {
        await using var fixture = await Fixture.CreateAsync(new CompactionPlugin { Instructions = "Keep the list of TODO items verbatim." });
        var session = await fixture.PrepareAsync();

        var result = await fixture.CompactAsync(session);

        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
        Assert.IsTrue(fixture.Provider.SummaryRequests.TryPeek(out var systemMessage));
        StringAssert.Contains(systemMessage, "Keep the list of TODO items verbatim.");
        var before = fixture.Plugin.Before.Single();
        Assert.AreEqual(session.SessionId, before.SessionId);
        Assert.AreEqual(fixture.Host.CurrentProject.Id, before.ProjectId);
        Assert.AreEqual("fake-compaction", before.ProviderId);
        Assert.AreEqual("Compacted", before.Metadata["SessionTitle"]);
        Assert.IsFalse(string.IsNullOrEmpty(before.CompactionId));
        var after = fixture.Plugin.After.Single();
        Assert.IsTrue(after.Succeeded);
        StringAssert.Contains(after.Summary, "KEEP-THE-TODO-LIST");
        Assert.AreEqual(before.CompactionId, after.CompactionId);
    }

    [TestMethod]
    public async Task OwnedCompact_AFailedCompaction_TellsTheAfterHook()
    {
        await using var fixture = await Fixture.CreateAsync(new CompactionPlugin());
        var session = await fixture.PrepareAsync();
        fixture.Provider.FailSummaries = true;

        var result = await fixture.CompactAsync(session);

        Assert.AreEqual(OwnedSessionCommandOutcome.Failed, result.Outcome);
        Assert.AreEqual("compact_failed", result.Code);
        var after = fixture.Plugin.After.Single();
        Assert.IsFalse(after.Succeeded);
        StringAssert.Contains(after.Summary, "The summary model is unavailable.");
    }

    [TestMethod]
    public async Task OwnedCompact_WithoutCompactionHooks_CompactsAsBefore()
    {
        await using var fixture = await Fixture.CreateAsync(plugin: null);
        var session = await fixture.PrepareAsync();

        var result = await fixture.CompactAsync(session);

        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
        Assert.IsTrue(fixture.Provider.SummaryRequests.TryPeek(out var systemMessage));
        Assert.IsFalse(systemMessage.Contains("Also follow these instructions", StringComparison.Ordinal));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory _temp;

        private Fixture(TempDirectory temp, CodeAltaHost host, SummaryProvider provider, CompactionPlugin plugin)
            => (_temp, Host, Provider, Plugin) = (temp, host, provider, plugin);

        public CodeAltaHost Host { get; }

        public SummaryProvider Provider { get; }

        public CompactionPlugin Plugin { get; }

        public static async Task<Fixture> CreateAsync(CompactionPlugin? plugin)
        {
            var temp = TempDirectory.Create();
            var provider = new SummaryProvider();
            var hooks = plugin ?? new CompactionPlugin();
            var host = await CodeAltaHost.CreateAsync(
                new CodeAltaHostOptions
                {
                    GlobalRoot = temp.GlobalRoot,
                    CurrentProjectPath = temp.ProjectRoot,
                    IsHeadless = true,
                    AutoApproveOwnedPermissions = true,
                    OwnedCommandReceiptCapacity = 16,
                    PluginBuiltIns = plugin is null ? [] :
                        [new BuiltInPluginDefinition { Id = "compaction", DisplayName = "Compaction", PluginType = typeof(CompactionPlugin), Factory = () => plugin }],
                    ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
                },
                CancellationToken.None);
            return new Fixture(temp, host, provider, hooks);
        }

        // A session with a conversation to compact, idle.
        public async Task<SessionViewDescriptor> PrepareAsync()
        {
            var session = await Host.Commands.CreateDraftSessionAsync(Host.CurrentProject, Provider.Descriptor, "Compacted");
            foreach (var text in new[] { "one", "two" })
            {
                var receipt = Host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), session.SessionId, text)).Receipt;
                Assert.IsNotNull(receipt);
                var sent = await receipt.Completion.WaitAsync(TestDeadlines.Hang);
                Assert.AreEqual(OwnedSessionCommandOutcome.Completed, sent.Outcome, sent.Code);
                using var timeout = new CancellationTokenSource(TestDeadlines.Hang);
                await Provider.Turns.Reader.ReadAsync(timeout.Token);
                while (await Host.RuntimeService.HasActiveRunAsync(session, timeout.Token)) await Task.Delay(20, timeout.Token);
            }

            return session;
        }

        public async Task<OwnedSessionCommandResult> CompactAsync(SessionViewDescriptor session)
        {
            var state = await Host.RuntimeService.GetCurrentStateAsync(session.SessionId).WaitAsync(TestDeadlines.Hang);
            Assert.IsNotNull(state.Entry);
            var admission = Host.Commands.AdmitCompact(new("compact", session.SessionId, state.RuntimeInstanceId, state.Entry.AttachmentGeneration));
            Assert.AreEqual(OwnedSessionCommandAdmissionKind.Accepted, admission.Kind);
            return await admission.Receipt!.Completion.WaitAsync(TestDeadlines.Hang);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            _temp.Dispose();
        }
    }

    public sealed record AfterCall(bool Succeeded, string? Summary, string? CompactionId);

    public sealed class CompactionPlugin : PluginBase
    {
        public string? CancelReason { get; init; }

        public string? Instructions { get; init; }

        public ConcurrentQueue<PluginBeforeCompactionContext> Before { get; } = new();

        public int InstructionCalls;

        public ConcurrentQueue<AfterCall> After { get; } = new();

        public override IEnumerable<PluginCompactionContribution> GetCompactionContributions()
        {
            yield return new PluginCompactionContribution
            {
                Capabilities = PluginCompactionCapabilities.Cancel | PluginCompactionCapabilities.Instructions | PluginCompactionCapabilities.Observe,
                BeforeCompaction = (context, _) =>
                {
                    Before.Enqueue(context);
                    return ValueTask.FromResult(CancelReason is null ? PluginBeforeCompactionResult.Continue : PluginBeforeCompactionResult.CancelWithReason(CancelReason));
                },
                Instructions = (_, _) =>
                {
                    Interlocked.Increment(ref InstructionCalls);
                    return ValueTask.FromResult(new PluginCompactionInstructionResult { Instructions = Instructions });
                },
                AfterCompaction = (context, _) =>
                {
                    After.Enqueue(new(context.Succeeded, context.Summary, context.CompactionId));
                    return ValueTask.CompletedTask;
                },
            };
        }
    }

    public sealed class SummaryProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public Channel<string> Turns { get; } = Channel.CreateUnbounded<string>();

        // The system message of each summary request of a compaction.
        public ConcurrentQueue<string> SummaryRequests { get; } = new();

        public volatile bool FailSummaries;

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-compaction"), "Fake Compaction") { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "fake-compaction", DisplayName = "Fake Compaction", TransportKind = AgentTransportKind.OpenAIResponses,
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
                Turns.Writer.TryWrite(request.RunId.Value);
            }

            return Task.FromResult(new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text(text)]),
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

        public static TempDirectory Create() => new(Path.Combine(Path.GetTempPath(), $"CodeAlta.OwnedCompaction.{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
