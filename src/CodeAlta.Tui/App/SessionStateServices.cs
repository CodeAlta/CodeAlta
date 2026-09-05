using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Tui.Threading;

namespace CodeAlta.Tui.App;

internal interface ISessionModelProviderReadinessService
{
    bool IsModelProviderReady(SessionViewDescriptor session);
}

internal sealed class SessionModelProviderReadinessService : ISessionModelProviderReadinessService
{
    private readonly Func<SessionViewDescriptor, bool> _isModelProviderReady;

    public SessionModelProviderReadinessService(Func<SessionViewDescriptor, bool> isModelProviderReady)
    {
        ArgumentNullException.ThrowIfNull(isModelProviderReady);
        _isModelProviderReady = isModelProviderReady;
    }

    public bool IsModelProviderReady(SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _isModelProviderReady(session);
    }
}

internal interface ISessionHistoryLoaderService
{
    Task EnsureSessionHistoryLoadedAsync(SessionViewDescriptor session, CancellationToken cancellationToken);
}

internal sealed class SessionHistoryLoaderService : ISessionHistoryLoaderService
{
    private readonly Func<SessionViewDescriptor, CancellationToken, Task> _ensureSessionHistoryLoadedAsync;

    public SessionHistoryLoaderService(Func<SessionViewDescriptor, CancellationToken, Task> ensureSessionHistoryLoadedAsync)
    {
        ArgumentNullException.ThrowIfNull(ensureSessionHistoryLoadedAsync);
        _ensureSessionHistoryLoadedAsync = ensureSessionHistoryLoadedAsync;
    }

    public Task EnsureSessionHistoryLoadedAsync(SessionViewDescriptor session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _ensureSessionHistoryLoadedAsync(session, cancellationToken);
    }
}

internal interface ISessionStateTabLifecycleService
{
    IReadOnlyList<string> GetOpenSessionTabIds();

    void ResetPendingSessionTabSelection();

    void ReplaceDraftTabWithSession(string sessionId);

    void RemoveSessionTabPage(string sessionId, ShellTabCloseReason reason);
}

internal sealed class DraftTabReplacementPort
{
    private Func<string, bool> _replaceDraftTabWithSession = static _ => false;

    public void Bind(Func<string, bool> replaceDraftTabWithSession)
    {
        ArgumentNullException.ThrowIfNull(replaceDraftTabWithSession);
        _replaceDraftTabWithSession = replaceDraftTabWithSession;
    }

    public void ReplaceDraftTabWithSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _ = _replaceDraftTabWithSession(sessionId);
    }
}

internal sealed class SessionStateTabLifecycleService : ISessionStateTabLifecycleService
{
    private readonly Func<IReadOnlyList<string>> _getOpenSessionTabIds;
    private readonly Action _resetPendingSessionTabSelection;
    private readonly Action<string> _replaceDraftTabWithSession;
    private readonly Action<string, ShellTabCloseReason> _removeSessionTabPage;

    public SessionStateTabLifecycleService(
        Func<IReadOnlyList<string>> getOpenSessionTabIds,
        Action resetPendingSessionTabSelection,
        Action<string> replaceDraftTabWithSession,
        Action<string, ShellTabCloseReason> removeSessionTabPage)
    {
        ArgumentNullException.ThrowIfNull(getOpenSessionTabIds);
        ArgumentNullException.ThrowIfNull(resetPendingSessionTabSelection);
        ArgumentNullException.ThrowIfNull(replaceDraftTabWithSession);
        ArgumentNullException.ThrowIfNull(removeSessionTabPage);
        _getOpenSessionTabIds = getOpenSessionTabIds;
        _resetPendingSessionTabSelection = resetPendingSessionTabSelection;
        _replaceDraftTabWithSession = replaceDraftTabWithSession;
        _removeSessionTabPage = removeSessionTabPage;
    }

    public IReadOnlyList<string> GetOpenSessionTabIds()
        => _getOpenSessionTabIds();

    public void ResetPendingSessionTabSelection()
        => _resetPendingSessionTabSelection();

    public void ReplaceDraftTabWithSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _replaceDraftTabWithSession(sessionId);
    }

    public void RemoveSessionTabPage(string sessionId, ShellTabCloseReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _removeSessionTabPage(sessionId, reason);
    }
}
