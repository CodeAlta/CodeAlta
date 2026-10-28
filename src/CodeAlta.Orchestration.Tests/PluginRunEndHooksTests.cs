using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Tests;

// The run-end hooks of plugins (≈ the Stop hooks of Codex): a run that ended normally may be continued with a prompt
// of a plugin, a bounded number of times in a row.
[TestClass]
public sealed class PluginRunEndHooksTests
{
    private static readonly ModelProviderId Provider = new("provider-1");

    [TestMethod]
    public async Task ARunThatEnded_IsContinuedWithThePromptOfThePlugin_AtMostThreeTimesInARow()
    {
        var (hooks, plugin, delivered, active) = await CreateAsync();
        plugin.Prompt = "Run the tests before you stop.";

        await hooks.Observe(Envelope(User("run-1", provenance: null)));
        await hooks.Observe(Envelope(Answer("run-1", "All done.")));
        await hooks.Observe(Envelope(Idle("run-1")));
        for (var run = 2; run <= 4; run++)
        {
            await hooks.Observe(Envelope(User($"run-{run}", AgentPromptProvenance.Plugin)));
            await hooks.Observe(Envelope(Idle($"run-{run}")));
        }

        // Three continuations, then the session stays idle: no loop.
        Assert.AreEqual(3, delivered.Count);
        Assert.IsTrue(delivered.All(static entry => entry == ("session-1", "Run the tests before you stop.")));
        CollectionAssert.AreEqual(new[] { "All done.:0:3", ":1:3", ":2:3" }, plugin.Asked.ToArray());

        // A prompt of the user starts the count again; the same run is never asked about twice.
        await hooks.Observe(Envelope(User("run-5", provenance: null)));
        await hooks.Observe(Envelope(Idle("run-5")));
        await hooks.Observe(Envelope(Idle("run-5")));
        Assert.AreEqual(4, delivered.Count);
        await active.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ARunThatFailedOrWasStopped_IsNotAsked_AndAHookThatSaysDoneLeavesTheSessionIdle()
    {
        var (hooks, plugin, delivered, active) = await CreateAsync();

        await hooks.Observe(Envelope(User("run-1", provenance: null)));
        // A run that fails or is stopped ends without its idle event.
        await hooks.Observe(Envelope(new AgentErrorEvent(Provider, "session-1", DateTimeOffset.UtcNow, "failed", (Exception?)null, new AgentRunId("run-1"))));
        Assert.AreEqual(0, plugin.Asked.Count);

        plugin.Prompt = null;
        await hooks.Observe(Envelope(Idle("run-2")));
        Assert.AreEqual(1, plugin.Asked.Count);
        Assert.AreEqual(0, delivered.Count);
        await active.DeactivateAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<(PluginRunEndHooks Hooks, ContinuePlugin Plugin, ConcurrentQueue<(string, string)> Delivered, ActivePluginInstance Active)> CreateAsync()
    {
        var registry = new PluginContributionRegistry();
        var activation = await new PluginRuntimeActivator(registry).ActivateAsync(
            new DiscoveredPluginType { Type = typeof(ContinuePlugin), Descriptor = PluginDescriptorFactory.FromType(typeof(ContinuePlugin)) }, null, null,
            new PluginActivationOptions
            {
                HostInfo = new PluginHostInfo
                {
                    ApplicationName = "CodeAlta.Orchestration.Tests",
                    Version = "1.0.0",
                    HostApiVersion = "1.0.0",
                    UserDataDirectory = Path.GetTempPath(),
                    IsHeadless = true,
                },
            });
        Assert.IsTrue(activation.Succeeded, string.Join(Environment.NewLine, activation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var active = activation.ActivePlugin!;
        var delivered = new ConcurrentQueue<(string, string)>();
        var hooks = new PluginRunEndHooks(new PluginContributionAdapterService(registry), () => [active], (session, prompt, _) =>
        {
            delivered.Enqueue((session, prompt));
            return Task.FromResult("queued");
        });
        return (hooks, (ContinuePlugin)active.Instance!, delivered, active);
    }

    private static RuntimePluginAgentEventEnvelope Envelope(AgentEvent agentEvent) => new(agentEvent, "session-1", null, Path.GetTempPath());

    private static AgentContentCompletedEvent User(string run, string? provenance)
        => new(Provider, "session-1", DateTimeOffset.UtcNow, new AgentRunId(run), AgentContentKind.User, run + ":user", null, "prompt", Provenance: provenance);

    private static AgentContentCompletedEvent Answer(string run, string text)
        => new(Provider, "session-1", DateTimeOffset.UtcNow, new AgentRunId(run), AgentContentKind.Assistant, run + ":answer", null, text);

    private static AgentSessionUpdateEvent Idle(string run)
        => new(Provider, "session-1", DateTimeOffset.UtcNow, new AgentRunId(run), AgentSessionUpdateKind.Idle, null);

    [Plugin("continue", DisplayName = "Continue")]
    public sealed class ContinuePlugin : PluginBase
    {
        public string? Prompt { get; set; } = "Go on.";

        public ConcurrentQueue<string> Asked { get; } = new();

        public override ValueTask<PluginAgentRunEndedResult?> OnAgentRunEndedAsync(PluginAgentRunEndedContext context, CancellationToken cancellationToken = default)
        {
            Asked.Enqueue($"{context.LastAssistantMessage}:{context.Continuations}:{context.MaxContinuations}");
            return ValueTask.FromResult<PluginAgentRunEndedResult?>(Prompt is { } prompt ? PluginAgentRunEndedResult.Continue(prompt) : PluginAgentRunEndedResult.Done);
        }
    }
}
