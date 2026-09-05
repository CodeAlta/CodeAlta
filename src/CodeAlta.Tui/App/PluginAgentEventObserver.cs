using CodeAlta.Agent;
using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Tui.Views;
using XenoAtom.Logging;

namespace CodeAlta.Tui.App;

internal interface IPluginAgentEventObserver
{
    Task ObserveAgentEventAsync(SessionViewDescriptor session, AgentEvent agentEvent, CancellationToken cancellationToken = default);

    Task<SessionViewPluginDerivedEventProjectionResult> ProjectSessionEventsAsync(
        SessionViewDescriptor session,
        OpenSessionState tab,
        IReadOnlyList<AgentEvent> events,
        bool isReplay,
        CancellationToken cancellationToken = default);
}

internal sealed class PluginAgentEventObserver : IPluginAgentEventObserver
{
    private readonly PluginHostBridge? _pluginHostBridge;

    public PluginAgentEventObserver(PluginHostBridge? pluginHostBridge)
        => _pluginHostBridge = pluginHostBridge;

    public async Task ObserveAgentEventAsync(SessionViewDescriptor session, AgentEvent agentEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(agentEvent);

        if (_pluginHostBridge is null)
        {
            return;
        }

        try
        {
            await _pluginHostBridge.ObserveAgentEventAsync(session, agentEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            CodeAltaApp.UiLogger.Error(ex, $"Plugin agent event observer failed for session {session.SessionId}");
        }
    }

    public async Task<SessionViewPluginDerivedEventProjectionResult> ProjectSessionEventsAsync(
        SessionViewDescriptor session,
        OpenSessionState tab,
        IReadOnlyList<AgentEvent> events,
        bool isReplay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(events);

        if (_pluginHostBridge is null)
        {
            return new SessionViewPluginDerivedEventProjectionResult([], []);
        }

        try
        {
            return await _pluginHostBridge.ProjectSessionEventsAsync(session, tab, events, isReplay, cancellationToken);
        }
        catch (Exception ex)
        {
            CodeAltaApp.UiLogger.Error(ex, $"Plugin session event projection failed for session {session.SessionId}");

            return new SessionViewPluginDerivedEventProjectionResult([], []);
        }
    }
}
