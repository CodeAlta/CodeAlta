using CodeAlta.App.State;
using CodeAlta.Catalog;

namespace CodeAlta.App;

internal sealed class OpenSessionStateStore
{
    private readonly Dictionary<string, OpenSessionState> _sessionTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SessionStateFactory _sessionStateFactory;

    public OpenSessionStateStore(SessionStateFactory sessionStateFactory)
    {
        ArgumentNullException.ThrowIfNull(sessionStateFactory);
        _sessionStateFactory = sessionStateFactory;
    }

    public OpenSessionState EnsureSessionTab(SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (_sessionTabs.TryGetValue(session.SessionId, out var existing))
        {
            _sessionStateFactory.UpdateOpenSession(existing, session);
            return existing;
        }

        var state = _sessionStateFactory.CreateOpenSession(session);
        _sessionTabs[session.SessionId] = state;
        return state;
    }

    public void ResetSessionTab(OpenSessionState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        PluginDynamicProjectionSubscription[] pluginSubscriptions;
        lock (tab.Session.PluginProjectionSyncRoot)
        {
            tab.PluginTransientEvents.Clear();
            Interlocked.Increment(ref tab.Session.PluginProjectionVersion);
            pluginSubscriptions = tab.Session.PluginDynamicProjectionSubscriptions.Values.ToArray();
            tab.Session.PluginDynamicProjectionSubscriptions.Clear();
        }

        foreach (var subscription in pluginSubscriptions)
        {
            subscription.Dispose();
        }

        tab.Timeline.Reset();
        tab.PermissionRequests.Clear();
        tab.UserInputRequests.Clear();
        tab.Session.LastRenderedSystemPromptEvent = null;
        tab.RenderedHistoryEvents.Clear();
    }

    public OpenSessionState? FindOpenSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _sessionTabs.GetValueOrDefault(sessionId);
    }

    public void RekeySessionTab(string oldSessionId, SessionViewDescriptor session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldSessionId);
        ArgumentNullException.ThrowIfNull(session);

        if (!_sessionTabs.TryGetValue(oldSessionId, out var state))
        {
            return;
        }

        _sessionTabs.Remove(oldSessionId);
        _sessionTabs.Remove(session.SessionId);

        _sessionStateFactory.UpdateOpenSession(state, session);
        _sessionTabs[session.SessionId] = state;
    }

    public void RemoveSessionTab(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_sessionTabs.Remove(sessionId, out var tab))
        {
            ResetSessionTab(tab);
        }
    }

    public void PruneRetainedSessionState(IReadOnlyList<SessionViewDescriptor> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var knownSessionIds = sessions
            .Select(static session => session.SessionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var sessionId in _sessionTabs.Keys.ToArray())
        {
            if (!knownSessionIds.Contains(sessionId))
            {
                RemoveSessionTab(sessionId);
            }
        }
    }

}
