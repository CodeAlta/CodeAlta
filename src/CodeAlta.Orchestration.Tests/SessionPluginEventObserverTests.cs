using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class SessionPluginEventObserverTests
{
    [TestMethod]
    public async Task ObserveAsync_DispatchesHeadlessAgentEventWithoutActivePlugins()
    {
        var observer = new SessionPluginEventObserver(new PluginOrchestrationBridge(
            new PluginContributionAdapterService(new PluginContributionRegistry()),
            static () => []));
        var context = new SessionCommandContext
        {
            ProjectId = "project-1",
            ProjectPath = "C:/project",
            PromptSessionId = "prompt-session-1",
            ModelProviderId = "provider-1",
            ModelId = "model-1",
            SessionId = "session-1",
        };
        var agentEvent = new AgentErrorEvent(
            new ModelProviderId("provider-1"),
            "session-1",
            DateTimeOffset.UtcNow,
            "diagnostic", (Exception?)null);

        var diagnostics = await observer.ObserveAsync(context, agentEvent);

        Assert.AreEqual(0, diagnostics.Count);
    }

    [TestMethod]
    public void ObserveAsync_RejectsMissingContext()
    {
        var observer = new SessionPluginEventObserver(new PluginOrchestrationBridge(
            new PluginContributionAdapterService(new PluginContributionRegistry()),
            static () => []));
        var agentEvent = new AgentErrorEvent(
            new ModelProviderId("provider-1"),
            "session-1",
            DateTimeOffset.UtcNow,
            "diagnostic", (Exception?)null);

        Assert.ThrowsExactly<ArgumentNullException>(() => observer.ObserveAsync(null!, agentEvent).AsTask().GetAwaiter().GetResult());
    }
}
