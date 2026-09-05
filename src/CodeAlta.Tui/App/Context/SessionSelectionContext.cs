using CodeAlta.Tui.App.State;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;

namespace CodeAlta.Tui.App.Context;

internal sealed class SessionSelectionContext
{
    private readonly ShellSessionStateCoordinator _sessionStateCoordinator;
    private readonly Func<SessionViewDescriptor, CancellationToken, Task> _ensureSessionHistoryLoadedAsync;
    private readonly Func<string, bool> _isSelectedSession;

    public SessionSelectionContext(
        ShellSessionStateCoordinator sessionStateCoordinator,
        Func<SessionViewDescriptor, CancellationToken, Task> ensureSessionHistoryLoadedAsync,
        Func<string, bool> isSelectedSession)
    {
        ArgumentNullException.ThrowIfNull(sessionStateCoordinator);
        ArgumentNullException.ThrowIfNull(ensureSessionHistoryLoadedAsync);
        ArgumentNullException.ThrowIfNull(isSelectedSession);

        _sessionStateCoordinator = sessionStateCoordinator;
        _ensureSessionHistoryLoadedAsync = ensureSessionHistoryLoadedAsync;
        _isSelectedSession = isSelectedSession;
    }

    public IReadOnlyList<ProjectDescriptor> Projects => _sessionStateCoordinator.Projects;

    public IReadOnlyList<SessionViewDescriptor> Sessions => _sessionStateCoordinator.Sessions;

    public IReadOnlyList<string> OpenSessionIds => _sessionStateCoordinator.ViewState.OpenSessionIds;

    public ShellSelection Selection => _sessionStateCoordinator.Selection;

    public WorkspaceTarget Target => Selection.Target;

    public OpenSessionState EnsureSessionTab(SessionViewDescriptor session)
        => _sessionStateCoordinator.EnsureSessionTab(session);

    public OpenSessionState? FindOpenSession(string sessionId)
        => _sessionStateCoordinator.FindOpenSession(sessionId);

    public ProjectDescriptor? GetSelectedProject()
        => _sessionStateCoordinator.GetSelectedProject();

    public ProjectDescriptor? GetProjectById(string? projectId)
        => _sessionStateCoordinator.GetProjectById(projectId);

    public SessionViewDescriptor? GetSelectedSession()
        => _sessionStateCoordinator.GetSelectedSession();

    public SessionViewDescriptor? FindSession(string? sessionId)
        => _sessionStateCoordinator.FindSession(sessionId);

    public Task EnsureSessionHistoryLoadedAsync(
        SessionViewDescriptor session,
        CancellationToken cancellationToken = default)
        => _ensureSessionHistoryLoadedAsync(session, cancellationToken);

    public bool IsGlobalDraftSelected()
        => Target is WorkspaceTarget.Draft { IsGlobal: true };

    public bool IsDraftSelected()
        => Target is WorkspaceTarget.Draft;

    public bool HasOpenDraftTab()
        => _sessionStateCoordinator.DraftTabOpen;

    public string? GetSelectedProjectId()
        => Selection.SelectedProjectId;

    public string? GetSelectedSessionId()
        => Selection.SelectedSessionId;

    public bool IsSelectedSession(string sessionId)
        => _isSelectedSession(sessionId);
}
