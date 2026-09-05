using CodeAlta.Catalog;

namespace CodeAlta.Tui.App;

internal sealed class ShellCatalogStateCoordinator
{
    internal readonly record struct CatalogRecoveryResult(string? RestoredSessionId);

    private readonly ProjectCatalog _projectCatalog;
    private readonly SessionViewCatalog _sessionCatalog;
    private readonly SessionViewStateCoordinator _viewStateCoordinator;
    private readonly OpenSessionStateStore _openSessionStateStore;
    private readonly ProjectDescriptor? _currentProject;
    private bool _suppressCurrentProject;
    private IReadOnlyList<ProjectDescriptor> _projects = [];
    private IReadOnlyList<SessionViewDescriptor> _sessions = [];

    public ShellCatalogStateCoordinator(
        ProjectCatalog projectCatalog,
        SessionViewCatalog sessionCatalog,
        SessionViewStateCoordinator viewStateCoordinator,
        OpenSessionStateStore OpenSessionStateStore,
        ProjectDescriptor? currentProject = null)
    {
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(sessionCatalog);
        ArgumentNullException.ThrowIfNull(viewStateCoordinator);
        ArgumentNullException.ThrowIfNull(OpenSessionStateStore);

        _projectCatalog = projectCatalog;
        _sessionCatalog = sessionCatalog;
        _viewStateCoordinator = viewStateCoordinator;
        _openSessionStateStore = OpenSessionStateStore;
        _currentProject = currentProject;
    }

    public IReadOnlyList<ProjectDescriptor> Projects => _projects;

    public IReadOnlyList<SessionViewDescriptor> Sessions => _sessions;

    public async Task<ShellSessionStateCoordinator.InitialCatalogState> LoadInitialCatalogStateAsync(CancellationToken cancellationToken)
    {
        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var sessions = await _sessionCatalog.LoadInternalAsync(cancellationToken).ConfigureAwait(false);
        var viewState = await _viewStateCoordinator.LoadViewStateAsync(cancellationToken).ConfigureAwait(false);
        await _viewStateCoordinator.ApplySessionLocalStateAsync(sessions, viewState, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new ShellSessionStateCoordinator.InitialCatalogState(IncludeCurrentProject(projects), sessions, viewState);
    }

    public void ApplyInitialCatalogState(ShellSessionStateCoordinator.InitialCatalogState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _projects = IncludeCurrentProject(state.Projects);
        _sessions = state.Sessions;
    }

    public CatalogRecoveryResult ApplyRecoveredCatalogState(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<SessionViewDescriptor> sessions,
        SessionViewViewState viewState,
        string? pendingStartupSessionRestoreId,
        bool pruneMissingSessions = true)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(viewState);

        _projects = IncludeCurrentProject(projects);
        _sessions = _viewStateCoordinator.ApplySessionLocalState(sessions, viewState, readJournal: false);
        if (pruneMissingSessions)
        {
            _openSessionStateStore.PruneRetainedSessionState(_sessions);
        }
        viewState.Selection ??= SessionViewSelectionState.GlobalDraft();

        if (pruneMissingSessions)
        {
            viewState.OpenSessionIds.RemoveAll(id => _sessions.All(session => !string.Equals(session.SessionId, id, StringComparison.OrdinalIgnoreCase)));
        }
        if (viewState.Selection.Surface == SessionViewSelectionSurface.Session &&
            (!viewState.OpenSessionIds.Contains(viewState.Selection.SessionId, StringComparer.OrdinalIgnoreCase) ||
             FindSession(viewState.Selection.SessionId) is null))
        {
            viewState.Selection = viewState.Selection.ProjectId is { Length: > 0 } projectId
                ? SessionViewSelectionState.ProjectDraft(projectId)
                : SessionViewSelectionState.GlobalDraft(viewState.Selection.ProjectId);
            viewState.SelectedSessionId = null;
        }
        else
        {
            viewState.SelectedSessionId = viewState.Selection.Surface == SessionViewSelectionSurface.Session
                ? viewState.Selection.SessionId
                : null;
        }

        string? restoredSessionId = null;
        if (viewState.Selection.Surface != SessionViewSelectionSurface.Session &&
            !string.IsNullOrWhiteSpace(pendingStartupSessionRestoreId) &&
            FindSession(pendingStartupSessionRestoreId) is { } restoredSession)
        {
            if (!viewState.OpenSessionIds.Contains(restoredSession.SessionId, StringComparer.OrdinalIgnoreCase))
            {
                viewState.OpenSessionIds.Insert(0, restoredSession.SessionId);
            }

            viewState.Selection = SessionViewSelectionState.Session(restoredSession.SessionId, restoredSession.ProjectRef);
            viewState.SelectedSessionId = restoredSession.SessionId;
            restoredSessionId = restoredSession.SessionId;
        }

        return new CatalogRecoveryResult(restoredSessionId);
    }

    public void UpsertSession(SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _sessions = _sessions
            .Where(existing => !string.Equals(existing.SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase))
            .Append(session)
            .OrderByDescending(static item => item.LastActiveAt)
            .ToArray();
    }

    public void UpsertProject(ProjectDescriptor project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (_currentProject is not null &&
            (string.Equals(_currentProject.Id, project.Id, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(NormalizePath(_currentProject.ProjectPath), NormalizePath(project.ProjectPath), StringComparison.OrdinalIgnoreCase)))
        {
            _suppressCurrentProject = false;
        }

        _projects = _projects
            .Where(existing =>
                !string.Equals(existing.Id, project.Id, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(NormalizePath(existing.ProjectPath), NormalizePath(project.ProjectPath), StringComparison.OrdinalIgnoreCase))
            .Append(project)
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void RemoveProject(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        if (_currentProject is not null && string.Equals(_currentProject.Id, projectId, StringComparison.OrdinalIgnoreCase))
        {
            _suppressCurrentProject = true;
        }

        _projects = _projects
            .Where(project => !string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public void RemoveSessions(IReadOnlyCollection<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);

        if (sessionIds.Count == 0)
        {
            return;
        }

        var removedSessionIds = new HashSet<string>(sessionIds, StringComparer.OrdinalIgnoreCase);
        _sessions = _sessions
            .Where(session => !removedSessionIds.Contains(session.SessionId))
            .ToArray();
    }

    public ProjectDescriptor? GetProjectById(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        return _projects.FirstOrDefault(project => string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase));
    }

    public SessionViewDescriptor? FindSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return _sessions.FirstOrDefault(session => string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<ProjectDescriptor> IncludeCurrentProject(IReadOnlyList<ProjectDescriptor> projects)
    {
        if (_currentProject is null || _currentProject.Archived || _suppressCurrentProject)
        {
            return projects;
        }

        if (projects.Any(project =>
                string.Equals(project.Id, _currentProject.Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizePath(project.ProjectPath), NormalizePath(_currentProject.ProjectPath), StringComparison.OrdinalIgnoreCase)))
        {
            return projects
                .Select(project =>
                    string.Equals(project.Id, _currentProject.Id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizePath(project.ProjectPath), NormalizePath(_currentProject.ProjectPath), StringComparison.OrdinalIgnoreCase)
                        ? _currentProject
                        : project)
                .OrderBy(static project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return projects
            .Append(_currentProject)
            .OrderBy(static project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
