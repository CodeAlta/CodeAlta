using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Tests;

/// <summary>Sends of the host's command owner carry what the host's plugins add to a run.</summary>
[TestClass]
public sealed class OwnedSessionPluginTests
{
    [TestMethod]
    public async Task OwnedSend_CarriesPluginInstructionsAndTheToolsASessionActivated()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider();
        var plugin = new FixturePlugin();
        await using var host = await CodeAltaHost.CreateAsync(
            new CodeAltaHostOptions
            {
                GlobalRoot = temp.GlobalRoot,
                CurrentProjectPath = temp.ProjectRoot,
                IsHeadless = true,
                AutoApproveOwnedPermissions = true,
                OwnedCommandReceiptCapacity = 16,
                PluginBuiltIns = [new BuiltInPluginDefinition { Id = "fixture", DisplayName = "Fixture", PluginType = typeof(FixturePlugin), Factory = () => plugin }],
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
            },
            CancellationToken.None);

        // Creating the session runs nothing: plugins give it their standing contributions only.
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Plugins");
        Assert.AreEqual(0, plugin.BeforeRunSessions.Count);

        var first = await SendAsync(host, provider, session, "one");
        StringAssert.Contains(first.DeveloperInstructions, FixturePlugin.Guidance);
        CollectionAssert.DoesNotContain(first.Tools, FixturePlugin.ToolName);
        CollectionAssert.AreEqual(new[] { session.SessionId }, plugin.BeforeRunSessions.ToArray());

        // What a run of the session turned on (as "alta mcp activate" does) reaches the provider on the next send.
        plugin.Activated[session.SessionId] = true;
        var second = await SendAsync(host, provider, session, "two");
        CollectionAssert.Contains(second.Tools, FixturePlugin.ToolName);
        StringAssert.Contains(second.DeveloperInstructions, FixturePlugin.Guidance);
        // A plugin tool call knows the session and project it runs for; nothing is in scope outside one.
        Assert.IsTrue(plugin.ToolScopes.TryDequeue(out var scope));
        Assert.AreEqual(session.SessionId, scope!.SessionId);
        Assert.AreEqual(host.CurrentProject.Id, scope.ProjectId);
        Assert.AreEqual(temp.ProjectRoot, scope.ProjectPath);
        Assert.IsNull(PluginOrchestrationBridge.CurrentToolOperation);

        // And stays for the sends after it, in the same session only.
        var third = await SendAsync(host, provider, session, "three");
        CollectionAssert.Contains(third.Tools, FixturePlugin.ToolName);
        var other = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Other");
        CollectionAssert.DoesNotContain((await SendAsync(host, provider, other, "one")).Tools, FixturePlugin.ToolName);
    }

    [TestMethod]
    public async Task OwnedSend_APluginThatCancelsTheRun_FailsTheSendBeforeTheProvider()
    {
        using var temp = TempDirectory.Create();
        var provider = new RecordingProvider();
        var plugin = new FixturePlugin();
        await using var host = await CodeAltaHost.CreateAsync(
            new CodeAltaHostOptions
            {
                GlobalRoot = temp.GlobalRoot,
                CurrentProjectPath = temp.ProjectRoot,
                IsHeadless = true,
                AutoApproveOwnedPermissions = true,
                OwnedCommandReceiptCapacity = 16,
                PluginBuiltIns = [new BuiltInPluginDefinition { Id = "fixture", DisplayName = "Fixture", PluginType = typeof(FixturePlugin), Factory = () => plugin }],
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => provider),
            },
            CancellationToken.None);
        var session = await host.Commands.CreateDraftSessionAsync(host.CurrentProject, provider.Descriptor, "Cancelled");
        plugin.CancelRuns = true;

        var receipt = host.Commands.AdmitSend(new("cancelled", session.SessionId, "one")).Receipt;
        Assert.IsNotNull(receipt);
        var result = await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(OwnedSessionCommandOutcome.Failed, result.Outcome);
        Assert.AreEqual("preparation_failed", result.Code);
        Assert.IsFalse(provider.Turns.Reader.TryRead(out _));
    }

    private static async Task<RecordedTurn> SendAsync(CodeAltaHost host, RecordingProvider provider, SessionViewDescriptor session, string text)
    {
        var receipt = host.Commands.AdmitSend(new(Guid.NewGuid().ToString("N"), session.SessionId, text)).Receipt;
        Assert.IsNotNull(receipt);
        var result = await receipt.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, result.Outcome, result.Code);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var turn = await provider.Turns.Reader.ReadAsync(timeout.Token);
        // The next send may change the session's tools, which the runtime only does for an idle session.
        while (await host.RuntimeService.HasActiveRunAsync(session, timeout.Token)) await Task.Delay(20, timeout.Token);
        return turn;
    }

    private sealed record RecordedTurn(string[] Tools, string DeveloperInstructions);

    public sealed class FixturePlugin : PluginBase
    {
        public const string ToolName = "fixture_tool";
        public const string Guidance = "Fixture plugin guidance.";

        public ConcurrentDictionary<string, bool> Activated { get; } = new(StringComparer.Ordinal);

        public ConcurrentQueue<string?> BeforeRunSessions { get; } = new();

        public ConcurrentQueue<PluginAdapterOperationOptions?> ToolScopes { get; } = new();

        public bool CancelRuns { get; set; }

        public override IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions()
        {
            yield return Prompt.Static(PluginPromptChannel.Developer, Guidance);
        }

        public override ValueTask<PluginBeforeAgentRunResult?> OnBeforeAgentRunAsync(PluginBeforeAgentRunContext context, CancellationToken cancellationToken = default)
        {
            BeforeRunSessions.Enqueue(context.SessionId);
            if (CancelRuns) return new(new PluginBeforeAgentRunResult { Cancel = true, CancelReason = "Not now." });
            if (context.SessionId is null || !Activated.ContainsKey(context.SessionId)) return new((PluginBeforeAgentRunResult?)null);
            using var schema = JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""");
            return new(new PluginBeforeAgentRunResult
            {
                AdditionalTools =
                [
                    new AgentToolDefinition(new AgentToolSpec(ToolName, "Fixture tool.", schema.RootElement.Clone()), (_, _) =>
                    {
                        ToolScopes.Enqueue(PluginOrchestrationBridge.CurrentToolOperation);
                        return Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("ok")]));
                    }),
                ],
            });
        }
    }

    private sealed class RecordingProvider : IAgentModelProviderRuntime, IModelProviderTurnExecutor
    {
        public Channel<RecordedTurn> Turns { get; } = Channel.CreateUnbounded<RecordedTurn>();

        public ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("fake-plugins"), "Fake Plugins") { DefaultModelId = "fake-model" };

        public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; } = new()
        {
            ProtocolFamily = "test", ProviderKey = "fake-plugins", DisplayName = "Fake Plugins", TransportKind = AgentTransportKind.OpenAIResponses,
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
            // The model "calls" the plugin tool once per turn that offers it.
            if (request.Tools.FirstOrDefault(static tool => tool.Spec.Name == FixturePlugin.ToolName) is { } tool)
            {
                using var arguments = JsonDocument.Parse("{}");
                await tool.Handler(new AgentToolInvocation(request.ProviderId, request.SessionId, "call", tool.Spec.Name, arguments.RootElement.Clone()), cancellationToken);
            }

            Turns.Writer.TryWrite(new([.. request.Tools.Select(static tool => tool.Spec.Name)], request.DeveloperInstructions ?? string.Empty));
            return new AgentTurnResponse
            {
                AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant, [new AgentMessagePart.Text("done")]),
            };
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

        public static TempDirectory Create() => new(Path.Combine(Path.GetTempPath(), $"CodeAlta.OwnedPlugins.{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
