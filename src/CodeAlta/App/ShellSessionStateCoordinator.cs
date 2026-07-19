using CodeAlta.App.State;
using CodeAlta.App.Events;
using CodeAlta.Threading;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Shell;

namespace CodeAlta.App;

internal sealed partial class ShellSessionStateCoordinator
{
    internal sealed record InitialCatalogState(
        IReadOnlyList<ProjectDescriptor> Projects,
        IReadOnlyList<SessionViewDescriptor> Sessions,
        SessionViewViewState ViewState);

    private readonly ShellSelectionCoordinator _selectionCoordinator = new();
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ShellStateStore _stateStore;
    private readonly FrontendEventPublisher? _frontendEvents;
    private readonly ISessionPromptDraftService _promptDrafts;
    private readonly ISessionModelProviderReadinessService _modelProviderReadiness;
    private readonly ISessionHistoryLoaderService _historyLoader;
    private readonly ISessionStateTabLifecycleService _tabLifecycle;
    private readonly ShellCatalogStateCoordinator _catalogStateCoordinator;
    private readonly OpenSessionStateStore _openSessionStateStore;
    private readonly SessionViewStateCoordinator _viewStateCoordinator;
    private readonly HashSet<string> _locallyRegisteredSessionIds = new(StringComparer.OrdinalIgnoreCase);

    public ShellSessionStateCoordinator(
        ProjectCatalog projectCatalog,
        SessionViewCatalog sessionCatalog,
        IUiDispatcher uiDispatcher,
        ShellStateStore stateStore,
        ISessionTimelineSurface timelineSurface,
        ISessionPromptDraftService promptDrafts,
        ISessionModelProviderPreferenceService modelProviderPreferences,
        ISessionModelProviderReadinessService modelProviderReadiness,
        ISessionHistoryLoaderService historyLoader,
        ISessionStateTabLifecycleService tabLifecycle,
        FrontendEventPublisher? frontendEvents = null,
        ProjectDescriptor? currentProject = null)
    {
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(sessionCatalog);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(timelineSurface);
        ArgumentNullException.ThrowIfNull(promptDrafts);
        ArgumentNullException.ThrowIfNull(modelProviderPreferences);
        ArgumentNullException.ThrowIfNull(modelProviderReadiness);
        ArgumentNullException.ThrowIfNull(historyLoader);
        ArgumentNullException.ThrowIfNull(tabLifecycle);

        _uiDispatcher = uiDispatcher;
        _stateStore = stateStore;
        _frontendEvents = frontendEvents;
        _promptDrafts = promptDrafts;
        _modelProviderReadiness = modelProviderReadiness;
        _historyLoader = historyLoader;
        _tabLifecycle = tabLifecycle;
        _viewStateCoordinator = new SessionViewStateCoordinator(sessionCatalog);
        var sessionStateFactory = new SessionStateFactory(
            uiDispatcher,
            timelineSurface,
            promptDrafts,
            modelProviderPreferences,
            new SessionProjectRootResolver(GetSelectedProject, GetProjectById));
        _openSessionStateStore = new OpenSessionStateStore(sessionStateFactory);
        _catalogStateCoordinator = new ShellCatalogStateCoordinator(projectCatalog, sessionCatalog, _viewStateCoordinator, _openSessionStateStore, currentProject);
    }

    public IReadOnlyList<ProjectDescriptor> Projects => _catalogStateCoordinator.Projects;

    public IReadOnlyList<SessionViewDescriptor> Sessions => _catalogStateCoordinator.Sessions;

    public SessionViewViewState ViewState
    {
        get => _selectionCoordinator.ViewState;
        set => _selectionCoordinator.ViewState = value;
    }

    public bool DraftTabOpen
    {
        get => _selectionCoordinator.DraftTabOpen;
        set => _selectionCoordinator.DraftTabOpen = value;
    }

    public bool GlobalScopeSelected
    {
        get => _selectionCoordinator.GlobalScopeSelected;
        set => _selectionCoordinator.GlobalScopeSelected = value;
    }

    public string? SelectedProjectId
    {
        get => _selectionCoordinator.SelectedProjectId;
        set => _selectionCoordinator.SelectedProjectId = value;
    }

    public string? SelectedSessionId
    {
        get => _selectionCoordinator.SelectedSessionId;
        set
        {
            _selectionCoordinator.SelectedSessionId = value;
            if (string.IsNullOrWhiteSpace(value))
            {
                PendingStartupSessionRestoreId = null;
            }
        }
    }

    public string? PendingStartupSessionRestoreId
    {
        get => _selectionCoordinator.PendingStartupSessionRestoreId;
        set => _selectionCoordinator.PendingStartupSessionRestoreId = value;
    }

    public ShellSelection Selection => _selectionCoordinator.Selection;

    public NavigatorSettings NavigatorSettings => ViewState.Navigator;

    public async Task<InitialCatalogState> LoadInitialCatalogStateAsync(CancellationToken cancellationToken)
    {
        return await _catalogStateCoordinator.LoadInitialCatalogStateAsync(cancellationToken);
    }

    public void ApplyInitialCatalogState(InitialCatalogState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _catalogStateCoordinator.ApplyInitialCatalogState(state);
        _selectionCoordinator.ApplyInitialSelection(state.ViewState, Projects, Sessions);
        SyncStateStore(catalogChanged: true, selectionChanged: true);
    }

    public async Task LoadCatalogStateAsync(CancellationToken cancellationToken)
    {
        ApplyInitialCatalogState(await LoadInitialCatalogStateAsync(cancellationToken));
    }

    public Task<NavigatorSettings> LoadNavigatorSettingsAsync(CancellationToken cancellationToken)
        => _viewStateCoordinator.LoadNavigatorSettingsAsync(cancellationToken);

    public async Task PersistViewStateAsync()
        => await _viewStateCoordinator.PersistViewStateAsync(ViewState);

    public void ApplyRecoveredCatalogState(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<SessionViewDescriptor> sessions,
        bool pruneMissingSessions = true)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(sessions);

        sessions = PreserveLocallyRegisteredSessions(sessions);
        var previousPendingStartupSessionRestoreId = PendingStartupSessionRestoreId;
        var recovery = _catalogStateCoordinator.ApplyRecoveredCatalogState(
            projects,
            sessions,
            ViewState,
            PendingStartupSessionRestoreId,
            pruneMissingSessions);

        _selectionCoordinator.ApplyInitialSelection(ViewState, Projects, Sessions);
        PendingStartupSessionRestoreId = recovery.RestoredSessionId ??
            (!pruneMissingSessions && !string.IsNullOrWhiteSpace(previousPendingStartupSessionRestoreId)
                ? previousPendingStartupSessionRestoreId
                : PendingStartupSessionRestoreId);

        EnsureSelectionDefaults();
        SyncStateStore(catalogChanged: true, selectionChanged: true);
    }

    public async Task PersistSessionLocalStateAsync(SessionViewDescriptor session)
        => await _viewStateCoordinator.PersistSessionLocalStateAsync(ViewState, session);

    public void UpsertRuntimeSession(SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);

        RememberLocallyRegisteredSession(session);
        if (!Sessions.Any(existingSession => ReferenceEquals(existingSession, session)))
        {
            _viewStateCoordinator.ApplySessionLocalState([session], ViewState, readJournal: false);
        }

        var localState = _viewStateCoordinator.RememberSessionLocalState(ViewState, session);
        global::CodeAlta.CodeAltaTaskMonitor.Observe(
            _viewStateCoordinator.PersistSessionLocalStateSnapshotAsync(session, localState),
            $"Persist local state for session {session.SessionId}");
        _catalogStateCoordinator.UpsertSession(session);
        SyncStateStore(catalogChanged: true);
    }

    public void UpsertProject(ProjectDescriptor project)
    {
        ArgumentNullException.ThrowIfNull(project);

        _catalogStateCoordinator.UpsertProject(project);
        SyncStateStore(catalogChanged: true);
    }

    public void RekeySessionIdentity(string oldSessionId, SessionViewDescriptor session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldSessionId);
        ArgumentNullException.ThrowIfNull(session);

        if (string.Equals(oldSessionId, session.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            _catalogStateCoordinator.UpsertSession(session);
            SyncStateStore(catalogChanged: true);
            return;
        }

        _openSessionStateStore.RekeySessionTab(oldSessionId, session);
        _tabLifecycle.RemoveSessionTabPage(oldSessionId, ShellTabCloseReason.Replaced);

        for (var index = 0; index < ViewState.OpenSessionIds.Count; index++)
        {
            if (string.Equals(ViewState.OpenSessionIds[index], oldSessionId, StringComparison.OrdinalIgnoreCase))
            {
                ViewState.OpenSessionIds[index] = session.SessionId;
            }
        }

        ViewState.OpenSessionIds = ViewState.OpenSessionIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ViewState.SessionStates.Remove(oldSessionId, out var localState))
        {
            ViewState.SessionStates[session.SessionId] = localState;
        }

        if (ViewState.SessionPreferences.Remove(oldSessionId, out var preference))
        {
            ViewState.SessionPreferences[session.SessionId] = preference;
        }

        if (string.Equals(PendingStartupSessionRestoreId, oldSessionId, StringComparison.OrdinalIgnoreCase))
        {
            PendingStartupSessionRestoreId = session.SessionId;
        }

        if (string.Equals(ViewState.SelectedSessionId, oldSessionId, StringComparison.OrdinalIgnoreCase))
        {
            ViewState.SelectedSessionId = session.SessionId;
        }

        if (ViewState.Selection.Surface == SessionViewSelectionSurface.Session &&
            string.Equals(ViewState.Selection.SessionId, oldSessionId, StringComparison.OrdinalIgnoreCase))
        {
            ViewState.Selection = SessionViewSelectionState.Session(session.SessionId, session.ProjectRef);
            ViewState.SelectedSessionId = session.SessionId;
            SelectedSessionId = session.SessionId;
        }

        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        _catalogStateCoordinator.UpsertSession(session);
        SyncStateStore(catalogChanged: true, selectionChanged: true);
    }

    public NavigatorSettings GetNavigatorSettingsSnapshot()
        => _viewStateCoordinator.GetNavigatorSettingsSnapshot(ViewState);

    public void ApplyNavigatorSettingsSnapshot(NavigatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        ViewState.Navigator = SessionViewStateCoordinator.CloneNavigatorSettings(settings);
        SyncStateStore(selectionChanged: true);
    }

    public async Task SaveNavigatorSettingsAsync(NavigatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        await _viewStateCoordinator.SaveNavigatorSettingsAsync(ViewState, settings);
        SyncStateStore(selectionChanged: true);
    }

    public void TrySchedulePendingStartupSessionRestore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(PendingStartupSessionRestoreId))
        {
            return;
        }

        var session = FindSession(PendingStartupSessionRestoreId);
        if (session is null || !_modelProviderReadiness.IsModelProviderReady(session))
        {
            return;
        }

        var sessionId = PendingStartupSessionRestoreId;
        PendingStartupSessionRestoreId = null;
        _ = RestoreStartupSessionHistoryAsync(sessionId, cancellationToken);
    }

    public void SelectGlobalScope()
    {
        _tabLifecycle.ResetPendingSessionTabSelection();
        PendingStartupSessionRestoreId = null;
        PruneOpenSessionIdsToCurrentSessionTabs();
        _selectionCoordinator.SelectGlobalScope(Projects);
        ViewState.SelectedSessionId = null;
        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        _ = PersistViewStateAsync();
        SyncStateStore(selectionChanged: true);
    }

    public SelectionChangeResult SelectProjectScope(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var previousSelection = Selection;
        _tabLifecycle.ResetPendingSessionTabSelection();
        PendingStartupSessionRestoreId = null;
        PruneOpenSessionIdsToCurrentSessionTabs();
        _selectionCoordinator.SelectProjectScope(projectId, Projects);
        ViewState.SelectedSessionId = null;
        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        _ = PersistViewStateAsync();
        SyncStateStore(selectionChanged: true);
        return Selection == previousSelection
            ? SelectionChangeResult.Unchanged
            : SelectionChangeResult.Changed;
    }

    public void EnsureSelectionDefaults()
    {
        var previousSelection = Selection;
        _selectionCoordinator.EnsureSelectionDefaults(Projects, Sessions);
        if (Selection != previousSelection)
        {
            SyncStateStore(selectionChanged: true);
        }
    }

    public async Task RegisterCreatedSessionAsync(SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);

        UiDispatch.Invoke(
            _uiDispatcher,
            () =>
            {
                RememberLocallyRegisteredSession(session);
                _catalogStateCoordinator.UpsertSession(session);
                OpenSessionCore(session.SessionId, persistViewState: false, publishSelectionChanged: false, loadHistory: false);
                _tabLifecycle.ReplaceDraftTabWithSession(session.SessionId);
                SyncStateStore(catalogChanged: true, selectionChanged: true);
            });

        await PersistViewStateAsync();
        await _historyLoader.EnsureSessionHistoryLoadedAsync(session, CancellationToken.None);
    }

    public OpenSessionResult OpenSession(string sessionId)
        => OpenSessionCore(sessionId, persistViewState: true, publishSelectionChanged: true, loadHistory: true);

    private OpenSessionResult OpenSessionCore(
        string sessionId,
        bool persistViewState,
        bool publishSelectionChanged,
        bool loadHistory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var session = FindSession(sessionId);
        if (session is null)
        {
            return OpenSessionResult.NotFound;
        }

        var alreadyOpen = ViewState.OpenSessionIds.Contains(sessionId, StringComparer.OrdinalIgnoreCase);
        _tabLifecycle.ResetPendingSessionTabSelection();
        EnsureSessionTab(session);
        if (!alreadyOpen)
        {
            ViewState.OpenSessionIds.Add(sessionId);
        }

        ViewState.SelectedSessionId = sessionId;
        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        _selectionCoordinator.SelectSession(session);
        PendingStartupSessionRestoreId = null;
        if (SessionHistoryCoordinator.CanLoadSessionHistory(session) && !_modelProviderReadiness.IsModelProviderReady(session))
        {
            PendingStartupSessionRestoreId = session.SessionId;
        }

        if (persistViewState)
        {
            _ = PersistViewStateAsync();
        }

        SyncStateStore(selectionChanged: publishSelectionChanged);
        if (loadHistory)
        {
            _ = _historyLoader.EnsureSessionHistoryLoadedAsync(session, CancellationToken.None);
        }

        return alreadyOpen
            ? OpenSessionResult.AlreadyOpen
            : OpenSessionResult.Opened;
    }

    public async Task<TabCloseResult> CloseSessionTabAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var wasOpen = ViewState.OpenSessionIds.Contains(sessionId, StringComparer.OrdinalIgnoreCase);
        _tabLifecycle.ResetPendingSessionTabSelection();
        ClearPendingStartupSessionRestore(sessionId);
        var removedSelectedSession = string.Equals(SelectedSessionId, sessionId, StringComparison.OrdinalIgnoreCase);
        var removedSession = FindSession(sessionId);
        var openSessionTabIds = removedSelectedSession
            ? _tabLifecycle.GetOpenSessionTabIds()
            : [];
        ViewState.OpenSessionIds.RemoveAll(id =>
            string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase) ||
            (removedSelectedSession && !openSessionTabIds.Contains(id, StringComparer.OrdinalIgnoreCase)));
        if (removedSelectedSession)
        {
            var nextSessionId = GetNextOpenSessionTabId(sessionId, openSessionTabIds);
            ViewState.SelectedSessionId = nextSessionId;
            _selectionCoordinator.ApplySessionRemovalFallback(nextSessionId, removedSession?.ProjectRef, Projects, Sessions);
            if (!string.IsNullOrWhiteSpace(nextSessionId) && FindSession(nextSessionId) is { } nextSession)
            {
                EnsureSessionTab(nextSession);
                _ = _historyLoader.EnsureSessionHistoryLoadedAsync(nextSession, CancellationToken.None);
            }
        }

        _openSessionStateStore.RemoveSessionTab(sessionId);
        _tabLifecycle.RemoveSessionTabPage(sessionId, ShellTabCloseReason.UserDetached);
        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync();
        SyncStateStore(selectionChanged: true);
        return wasOpen
            ? TabCloseResult.Closed
            : TabCloseResult.NotOpen;
    }

    private string? GetNextOpenSessionTabId(string closingSessionId, IReadOnlyList<string> openSessionTabIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(closingSessionId);
        ArgumentNullException.ThrowIfNull(openSessionTabIds);

        foreach (var sessionId in openSessionTabIds)
        {
            if (string.IsNullOrWhiteSpace(sessionId) ||
                string.Equals(sessionId, closingSessionId, StringComparison.OrdinalIgnoreCase) ||
                !ViewState.OpenSessionIds.Contains(sessionId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            return sessionId;
        }

        return null;
    }

    public async Task RemoveDeletedSessionArtifactsAsync(IReadOnlyList<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);

        var removedAnyState = false;
        foreach (var sessionId in sessionIds)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                continue;
            }

            _locallyRegisteredSessionIds.Remove(sessionId);
            _promptDrafts.DeletePromptDraft(sessionId);
            PendingStartupSessionRestoreId = string.Equals(PendingStartupSessionRestoreId, sessionId, StringComparison.OrdinalIgnoreCase)
                ? null
                : PendingStartupSessionRestoreId;
            removedAnyState |= ViewState.SessionStates.Remove(sessionId);
        }

        if (!removedAnyState)
        {
            return;
        }

        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync();
    }

    public void RemoveDeletedSession(string sessionId, string? fallbackProjectId)
        => RemoveDeletedSessions([sessionId], fallbackProjectId);

    public void RemoveDeletedSessions(IReadOnlyList<string> sessionIds, string? fallbackProjectId)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);

        var deletedSessionIds = sessionIds
            .Where(static sessionId => !string.IsNullOrWhiteSpace(sessionId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (deletedSessionIds.Length == 0)
        {
            return;
        }

        _tabLifecycle.ResetPendingSessionTabSelection();
        var removedSelectedSession = deletedSessionIds.Contains(SelectedSessionId, StringComparer.OrdinalIgnoreCase);
        _catalogStateCoordinator.RemoveSessions(deletedSessionIds);

        foreach (var sessionId in deletedSessionIds)
        {
            _locallyRegisteredSessionIds.Remove(sessionId);
            ViewState.OpenSessionIds.RemoveAll(id => string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase));
            _tabLifecycle.RemoveSessionTabPage(sessionId, ShellTabCloseReason.SessionDeleted);
            _openSessionStateStore.RemoveSessionTab(sessionId);
            _promptDrafts.DeletePromptDraft(sessionId);
            ClearPendingStartupSessionRestore(sessionId);

            if (string.Equals(ViewState.SelectedSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                ViewState.SelectedSessionId = null;
            }
        }

        if (removedSelectedSession)
        {
            _selectionCoordinator.ApplySessionRemovalFallback(nextSelectedSessionId: null, fallbackProjectId, Projects, Sessions);
        }

        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        _ = PersistViewStateAsync();
        SyncStateStore(catalogChanged: true, selectionChanged: true);
    }

    public void RemoveDeletedProject(ProjectDescriptor project, IReadOnlyList<string> deletedSessionIds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(deletedSessionIds);

        _catalogStateCoordinator.UpsertProject(project);
        RemoveDeletedProject(project.Id, deletedSessionIds);
    }

    public void RemoveDeletedProject(string projectId, IReadOnlyList<string> deletedSessionIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(deletedSessionIds);

        _tabLifecycle.ResetPendingSessionTabSelection();
        var removedSelectedSession = deletedSessionIds.Contains(SelectedSessionId, StringComparer.OrdinalIgnoreCase);
        var removedSelectedProject = !GlobalScopeSelected &&
            string.Equals(SelectedProjectId, projectId, StringComparison.OrdinalIgnoreCase);
        _catalogStateCoordinator.RemoveProject(projectId);
        _catalogStateCoordinator.RemoveSessions(deletedSessionIds.ToArray());

        foreach (var sessionId in deletedSessionIds)
        {
            _locallyRegisteredSessionIds.Remove(sessionId);
            ViewState.OpenSessionIds.RemoveAll(id => string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase));
            _tabLifecycle.RemoveSessionTabPage(sessionId, ShellTabCloseReason.ProjectClosed);
            _openSessionStateStore.RemoveSessionTab(sessionId);
            _promptDrafts.DeletePromptDraft(sessionId);
            ClearPendingStartupSessionRestore(sessionId);

            if (string.Equals(ViewState.SelectedSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                ViewState.SelectedSessionId = null;
            }
        }

        if (removedSelectedSession)
        {
            _selectionCoordinator.ApplySessionRemovalFallback(nextSelectedSessionId: null, fallbackProjectId: null, Projects, Sessions);
        }
        else if (removedSelectedProject && string.IsNullOrWhiteSpace(SelectedSessionId))
        {
            _selectionCoordinator.SelectGlobalScope(Projects);
        }

        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        _ = PersistViewStateAsync();
        SyncStateStore(catalogChanged: true, selectionChanged: true);
    }

    public OpenSessionState EnsureSessionTab(SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return _openSessionStateStore.EnsureSessionTab(session);
    }

    public void ResetSessionTab(OpenSessionState tab)
        => _openSessionStateStore.ResetSessionTab(tab);

    private void SyncStateStore(bool catalogChanged = false, bool selectionChanged = false)
    {
        _stateStore.Mutate(snapshot => snapshot
            .SetCatalog(Projects, Sessions)
            .SetSelection(Selection, ViewState.OpenSessionIds, NavigatorSettings));

        if (catalogChanged)
        {
            _frontendEvents?.Publish(new CatalogChangedEvent());
        }

        if (selectionChanged)
        {
            _frontendEvents?.Publish(new SelectionChangedEvent(_stateStore.Snapshot));
        }
    }

    public OpenSessionState? FindOpenSession(string sessionId)
        => _openSessionStateStore.FindOpenSession(sessionId);

    private void ClearPendingStartupSessionRestore(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (string.Equals(PendingStartupSessionRestoreId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            PendingStartupSessionRestoreId = null;
        }
    }

    private void PruneOpenSessionIdsToCurrentSessionTabs()
    {
        var openSessionTabIds = _tabLifecycle.GetOpenSessionTabIds();
        ViewState.OpenSessionIds.RemoveAll(sessionId => !openSessionTabIds.Contains(sessionId, StringComparer.OrdinalIgnoreCase));
    }

    private void RememberLocallyRegisteredSession(SessionViewDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!string.IsNullOrWhiteSpace(session.SessionId))
        {
            _locallyRegisteredSessionIds.Add(session.SessionId);
        }
    }

    private IReadOnlyList<SessionViewDescriptor> PreserveLocallyRegisteredSessions(IReadOnlyList<SessionViewDescriptor> recoveredSessions)
    {
        ArgumentNullException.ThrowIfNull(recoveredSessions);
        if (_locallyRegisteredSessionIds.Count == 0)
        {
            return recoveredSessions;
        }

        var recoveredSessionIds = recoveredSessions
            .Where(static session => !string.IsNullOrWhiteSpace(session.SessionId))
            .Select(static session => session.SessionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _locallyRegisteredSessionIds.ExceptWith(recoveredSessionIds);
        if (_locallyRegisteredSessionIds.Count == 0)
        {
            return recoveredSessions;
        }

        List<SessionViewDescriptor>? mergedSessions = null;
        foreach (var session in Sessions)
        {
            if (string.IsNullOrWhiteSpace(session.SessionId) ||
                recoveredSessionIds.Contains(session.SessionId) ||
                !_locallyRegisteredSessionIds.Contains(session.SessionId))
            {
                continue;
            }

            mergedSessions ??= [.. recoveredSessions];
            mergedSessions.Add(session);
            recoveredSessionIds.Add(session.SessionId);
        }

        return mergedSessions is null
            ? recoveredSessions
            : mergedSessions
                .OrderByDescending(static session => session.LastActiveAt)
                .ThenBy(static session => session.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static session => session.SessionId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    public ProjectDescriptor? GetSelectedProject()
    {
        var selectedSession = GetSelectedSession();
        if (selectedSession is not null)
        {
            return selectedSession.ProjectRef is { } projectId
                ? GetProjectById(projectId)
                : null;
        }

        return Selection.Target is WorkspaceTarget.Draft { IsGlobal: true }
            ? null
            : GetProjectById(SelectedProjectId);
    }

    public ProjectDescriptor? GetProjectById(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        return _catalogStateCoordinator.GetProjectById(projectId);
    }

    public SessionViewDescriptor? GetSelectedSession()
        => FindSession(SelectedSessionId);

    public SessionViewDescriptor? FindSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return _catalogStateCoordinator.FindSession(sessionId);
    }

    public async Task RestoreStartupSessionHistoryAsync(string? sessionId, CancellationToken cancellationToken)
    {
        var session = FindSession(sessionId);
        if (session is null)
        {
            return;
        }

        await _historyLoader.EnsureSessionHistoryLoadedAsync(session, cancellationToken);
    }

}
