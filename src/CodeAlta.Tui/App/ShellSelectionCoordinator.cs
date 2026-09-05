using CodeAlta.Catalog;
using CodeAlta.Tui.Models;
using CodeAlta.Orchestration;

namespace CodeAlta.Tui.App;

internal sealed class ShellSelectionCoordinator
{
    private readonly ShellSelectionState _state = new();

    public SessionViewViewState ViewState
    {
        get => _state.ViewState;
        set => _state.ViewState = value;
    }

    public ShellSelection Selection
    {
        get => _state.Selection;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _state.Selection = value;
            ViewState.Selection = ToPersistedSelection(value);
            ViewState.SelectedSessionId = value.SelectedSessionId;
        }
    }

    public string? PendingStartupSessionRestoreId
    {
        get => _state.PendingStartupSessionRestoreId;
        set => _state.PendingStartupSessionRestoreId = value;
    }

    public bool DraftTabOpen
    {
        get => Selection.DraftTabOpen;
        set
        {
            if (!value || Selection.DraftTabOpen)
            {
                return;
            }

            Selection = Selection.GlobalScopeSelected
                ? ShellSelection.GlobalDraft(Selection.SelectedProjectId)
                : Selection.SelectedProjectId is { } projectId
                    ? ShellSelection.ProjectDraft(projectId)
                    : ShellSelection.GlobalDraft();
        }
    }

    public bool GlobalScopeSelected
    {
        get => Selection.GlobalScopeSelected;
        set
        {
            if (!Selection.DraftTabOpen)
            {
                return;
            }

            Selection = new ShellSelection(
                ShellSurface.DraftWorkspace,
                new WorkspaceTarget.Draft(Selection.SelectedProjectId, value));
        }
    }

    public string? SelectedProjectId
    {
        get => Selection.SelectedProjectId;
        set
        {
            Selection = Selection switch
            {
                { Target: WorkspaceTarget.Draft draft } => new ShellSelection(
                    ShellSurface.DraftWorkspace,
                    new WorkspaceTarget.Draft(value, draft.IsGlobal)),
                { Target: WorkspaceTarget.Session session } => session.SessionId is { Length: > 0 } sessionId
                    ? ShellSelection.Session(sessionId, value)
                    : Selection,
                _ => Selection,
            };
        }
    }

    public string? SelectedSessionId
    {
        get => Selection.SelectedSessionId;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Selection = Selection.GlobalScopeSelected
                    ? ShellSelection.GlobalDraft(Selection.SelectedProjectId)
                    : Selection.SelectedProjectId is { } projectId
                        ? ShellSelection.ProjectDraft(projectId)
                        : ShellSelection.GlobalDraft();
                return;
            }

            Selection = ShellSelection.Session(value, Selection.SelectedProjectId);
        }
    }

    public void ApplyInitialSelection(
        SessionViewViewState viewState,
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<SessionViewDescriptor> sessions)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(sessions);

        ViewState = viewState;
        var persistedSelection = NormalizePersistedSelection(ViewState.Selection, ViewState.SelectedSessionId, ViewState.OpenSessionIds);
        PendingStartupSessionRestoreId = persistedSelection.Surface == SessionViewSelectionSurface.Session
            ? persistedSelection.SessionId
            : null;

        if (persistedSelection.Surface == SessionViewSelectionSurface.Session &&
            persistedSelection.SessionId is { Length: > 0 } desiredSessionId &&
            FindSession(sessions, desiredSessionId) is { } session)
        {
            Selection = ShellSelection.Session(session.SessionId, session.ProjectRef);
            return;
        }

        var preferredProjectId = NormalizeProjectId(projects, persistedSelection.ProjectId);
        Selection = persistedSelection.DraftScope == SessionViewDraftScope.Global
            ? ShellSelection.GlobalDraft(preferredProjectId)
            : (preferredProjectId ?? GetDefaultProjectId(projects)) is { } projectId
                ? ShellSelection.ProjectDraft(projectId)
                : ShellSelection.GlobalDraft();
    }

    public void EnsureSelectionDefaults(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<SessionViewDescriptor> sessions)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(sessions);

        if (Selection.SelectedSessionId is { } selectedSessionId &&
            FindSession(sessions, selectedSessionId) is not { } session)
        {
            Selection = BuildDraftFallback(projects, Selection.SelectedProjectId);
            return;
        }

        if (Selection.Target is WorkspaceTarget.Session currentSession &&
            FindSession(sessions, currentSession.SessionId) is { } selectedSession)
        {
            Selection = ShellSelection.Session(selectedSession.SessionId, NormalizeProjectId(projects, selectedSession.ProjectRef));
            return;
        }

        var normalizedProjectId = NormalizeProjectId(projects, Selection.SelectedProjectId);
        Selection = Selection.GlobalScopeSelected
            ? ShellSelection.GlobalDraft(normalizedProjectId)
            : normalizedProjectId is { } projectId
                ? ShellSelection.ProjectDraft(projectId)
                : ShellSelection.GlobalDraft();
    }

    public void SelectGlobalScope(IReadOnlyList<ProjectDescriptor> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        Selection = ShellSelection.GlobalDraft(NormalizeProjectId(projects, Selection.SelectedProjectId));
    }

    public void SelectProjectScope(string projectId, IReadOnlyList<ProjectDescriptor> projects)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(projects);
        Selection = NormalizeProjectId(projects, projectId) is { } normalizedProjectId
            ? ShellSelection.ProjectDraft(normalizedProjectId)
            : ShellSelection.GlobalDraft();
    }

    public void SelectSession(SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Selection = ShellSelection.Session(session.SessionId, session.ProjectRef);
    }

    public void ApplySessionRemovalFallback(
        string? nextSelectedSessionId,
        string? fallbackProjectId,
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<SessionViewDescriptor> sessions)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(sessions);

        if (!string.IsNullOrWhiteSpace(nextSelectedSessionId) &&
            FindSession(sessions, nextSelectedSessionId) is { } nextSession)
        {
            Selection = ShellSelection.Session(nextSession.SessionId, nextSession.ProjectRef);
            return;
        }

        Selection = BuildDraftFallback(projects, fallbackProjectId ?? Selection.SelectedProjectId);
    }

    private static ShellSelection BuildDraftFallback(
        IReadOnlyList<ProjectDescriptor> projects,
        string? preferredProjectId)
    {
        var projectId = NormalizeProjectId(projects, preferredProjectId);
        return preferredProjectId is { Length: > 0 } && projectId is not null
            ? ShellSelection.ProjectDraft(projectId)
            : ShellSelection.GlobalDraft(projectId);
    }

    private static string? NormalizeProjectId(IReadOnlyList<ProjectDescriptor> projects, string? projectId)
    {
        ArgumentNullException.ThrowIfNull(projects);

        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        return projects.FirstOrDefault(project =>
            string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static string? GetDefaultProjectId(IReadOnlyList<ProjectDescriptor> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        return projects.FirstOrDefault()?.Id;
    }

    private static SessionViewDescriptor? FindSession(IReadOnlyList<SessionViewDescriptor> sessions, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return sessions.FirstOrDefault(session => string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
    }

    private static SessionViewSelectionState NormalizePersistedSelection(
        SessionViewSelectionState? selection,
        string? legacySelectedSessionId,
        IReadOnlyList<string> openSessionIds)
    {
        if (selection is not null)
        {
            return selection;
        }

        if (!string.IsNullOrWhiteSpace(legacySelectedSessionId))
        {
            return SessionViewSelectionState.Session(legacySelectedSessionId, projectId: null);
        }

        if (openSessionIds.FirstOrDefault(static sessionId => !string.IsNullOrWhiteSpace(sessionId)) is { } firstOpenSessionId)
        {
            return SessionViewSelectionState.Session(firstOpenSessionId, projectId: null);
        }

        return SessionViewSelectionState.GlobalDraft();
    }

    private static SessionViewSelectionState ToPersistedSelection(ShellSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        return selection.Target switch
        {
            WorkspaceTarget.Draft { IsGlobal: true } draft => SessionViewSelectionState.GlobalDraft(draft.ProjectId),
            WorkspaceTarget.Draft draft => draft.ProjectId is { Length: > 0 } projectId
                ? SessionViewSelectionState.ProjectDraft(projectId)
                : SessionViewSelectionState.GlobalDraft(),
            WorkspaceTarget.Session session => SessionViewSelectionState.Session(session.SessionId, session.ProjectId),
            _ => SessionViewSelectionState.GlobalDraft(selection.SelectedProjectId),
        };
    }
}
