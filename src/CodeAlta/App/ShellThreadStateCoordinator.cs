using CodeAlta.App.State;
using CodeAlta.App.Events;
using CodeAlta.Threading;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Shell;

namespace CodeAlta.App;

internal sealed class ShellThreadStateCoordinator
{
    internal sealed record InitialCatalogState(
        IReadOnlyList<ProjectDescriptor> Projects,
        IReadOnlyList<WorkThreadDescriptor> Threads,
        WorkThreadViewState ViewState);

    private readonly ShellSelectionCoordinator _selectionCoordinator = new();
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ShellStateStore _stateStore;
    private readonly FrontendEventPublisher? _frontendEvents;
    private readonly IThreadStateFrontendPort _frontendPort;
    private readonly ShellCatalogStateCoordinator _catalogStateCoordinator;
    private readonly OpenThreadStateStore _OpenThreadStateStore;
    private readonly ThreadViewStateCoordinator _viewStateCoordinator;

    public ShellThreadStateCoordinator(
        ProjectCatalog projectCatalog,
        WorkThreadCatalog threadCatalog,
        IUiDispatcher uiDispatcher,
        ShellStateStore stateStore,
        IThreadStateFrontendPort frontendPort,
        FrontendEventPublisher? frontendEvents = null)
    {
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(threadCatalog);
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(frontendPort);

        _uiDispatcher = uiDispatcher;
        _stateStore = stateStore;
        _frontendEvents = frontendEvents;
        _frontendPort = frontendPort;
        _viewStateCoordinator = new ThreadViewStateCoordinator(threadCatalog);
        var threadSessionFactory = new ThreadSessionFactory(
            uiDispatcher,
            new ThreadTimelineSurface(frontendPort.GetTimelineBounds),
            new ThreadPromptDraftService(frontendPort.LoadPromptDraft),
            new ThreadModelProviderPreferenceService(frontendPort.ApplyThreadPreference, frontendPort.RememberThreadPreference),
            new ThreadProjectRootResolver(GetSelectedProject, GetProjectById));
        _OpenThreadStateStore = new OpenThreadStateStore(threadSessionFactory);
        _catalogStateCoordinator = new ShellCatalogStateCoordinator(projectCatalog, threadCatalog, _viewStateCoordinator, _OpenThreadStateStore);
    }

    public IReadOnlyList<ProjectDescriptor> Projects => _catalogStateCoordinator.Projects;

    public IReadOnlyList<WorkThreadDescriptor> Threads => _catalogStateCoordinator.Threads;

    public WorkThreadViewState ViewState
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

    public string? SelectedThreadId
    {
        get => _selectionCoordinator.SelectedThreadId;
        set => _selectionCoordinator.SelectedThreadId = value;
    }

    public string? PendingStartupThreadRestoreId
    {
        get => _selectionCoordinator.PendingStartupThreadRestoreId;
        set => _selectionCoordinator.PendingStartupThreadRestoreId = value;
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
        _selectionCoordinator.ApplyInitialSelection(state.ViewState, Projects, Threads);
        SyncStateStore(catalogChanged: true, selectionChanged: true);
    }

    public async Task LoadCatalogStateAsync(CancellationToken cancellationToken)
    {
        ApplyInitialCatalogState(await LoadInitialCatalogStateAsync(cancellationToken));
    }

    public async Task PersistViewStateAsync()
        => await _viewStateCoordinator.PersistViewStateAsync(ViewState);

    public void ApplyRecoveredCatalogState(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads,
        bool pruneMissingThreads = true)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(threads);

        var previousPendingStartupThreadRestoreId = PendingStartupThreadRestoreId;
        var recovery = _catalogStateCoordinator.ApplyRecoveredCatalogState(
            projects,
            threads,
            ViewState,
            PendingStartupThreadRestoreId,
            pruneMissingThreads);

        _selectionCoordinator.ApplyInitialSelection(ViewState, Projects, Threads);
        PendingStartupThreadRestoreId = recovery.RestoredThreadId ??
            (!pruneMissingThreads && !string.IsNullOrWhiteSpace(previousPendingStartupThreadRestoreId)
                ? previousPendingStartupThreadRestoreId
                : PendingStartupThreadRestoreId);

        EnsureSelectionDefaults();
        SyncStateStore(catalogChanged: true, selectionChanged: true);
    }

    public async Task PersistThreadLocalStateAsync(WorkThreadDescriptor thread)
        => await _viewStateCoordinator.PersistThreadLocalStateAsync(ViewState, thread);

    public void UpsertProject(ProjectDescriptor project)
    {
        ArgumentNullException.ThrowIfNull(project);

        _catalogStateCoordinator.UpsertProject(project);
        SyncStateStore(catalogChanged: true);
    }

    public void RekeyThreadIdentity(string oldThreadId, WorkThreadDescriptor thread)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldThreadId);
        ArgumentNullException.ThrowIfNull(thread);

        if (string.Equals(oldThreadId, thread.ThreadId, StringComparison.OrdinalIgnoreCase))
        {
            _catalogStateCoordinator.UpsertThread(thread);
            SyncStateStore(catalogChanged: true);
            return;
        }

        _OpenThreadStateStore.RekeyThreadTab(oldThreadId, thread);
        _frontendPort.RemoveThreadTabPage(oldThreadId, ShellTabCloseReason.Replaced);

        for (var index = 0; index < ViewState.OpenThreadIds.Count; index++)
        {
            if (string.Equals(ViewState.OpenThreadIds[index], oldThreadId, StringComparison.OrdinalIgnoreCase))
            {
                ViewState.OpenThreadIds[index] = thread.ThreadId;
            }
        }

        ViewState.OpenThreadIds = ViewState.OpenThreadIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ViewState.ThreadStates.Remove(oldThreadId, out var localState))
        {
            ViewState.ThreadStates[thread.ThreadId] = localState;
        }

        if (ViewState.ThreadPreferences.Remove(oldThreadId, out var preference))
        {
            ViewState.ThreadPreferences[thread.ThreadId] = preference;
        }

        if (string.Equals(PendingStartupThreadRestoreId, oldThreadId, StringComparison.OrdinalIgnoreCase))
        {
            PendingStartupThreadRestoreId = thread.ThreadId;
        }

        if (string.Equals(ViewState.SelectedThreadId, oldThreadId, StringComparison.OrdinalIgnoreCase))
        {
            ViewState.SelectedThreadId = thread.ThreadId;
        }

        if (ViewState.Selection.Surface == WorkThreadSelectionSurface.Thread &&
            string.Equals(ViewState.Selection.ThreadId, oldThreadId, StringComparison.OrdinalIgnoreCase))
        {
            ViewState.Selection = WorkThreadSelectionState.Thread(thread.ThreadId, thread.ProjectRef);
            ViewState.SelectedThreadId = thread.ThreadId;
            SelectedThreadId = thread.ThreadId;
        }

        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        _catalogStateCoordinator.UpsertThread(thread);
        SyncStateStore(catalogChanged: true, selectionChanged: true);
    }

    public NavigatorSettings GetNavigatorSettingsSnapshot()
        => _viewStateCoordinator.GetNavigatorSettingsSnapshot(ViewState);

    public async Task SaveNavigatorSettingsAsync(NavigatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        await _viewStateCoordinator.SaveNavigatorSettingsAsync(ViewState, settings);
        SyncStateStore(selectionChanged: true);
    }

    public void TrySchedulePendingStartupThreadRestore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(PendingStartupThreadRestoreId))
        {
            return;
        }

        var thread = FindThread(PendingStartupThreadRestoreId);
        if (thread is null || !_frontendPort.IsModelProviderReady(thread))
        {
            return;
        }

        var threadId = PendingStartupThreadRestoreId;
        PendingStartupThreadRestoreId = null;
        _ = RestoreStartupThreadHistoryAsync(threadId, cancellationToken);
    }

    public void SelectGlobalScope()
    {
        _frontendPort.ResetPendingThreadTabSelection();
        _selectionCoordinator.SelectGlobalScope(Projects);
        ViewState.SelectedThreadId = null;
        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        _ = PersistViewStateAsync();
        SyncStateStore(selectionChanged: true);
    }

    public SelectionChangeResult SelectProjectScope(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var previousSelection = Selection;
        _frontendPort.ResetPendingThreadTabSelection();
        _selectionCoordinator.SelectProjectScope(projectId, Projects);
        ViewState.SelectedThreadId = null;
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
        _selectionCoordinator.EnsureSelectionDefaults(Projects, Threads);
        if (Selection != previousSelection)
        {
            SyncStateStore(selectionChanged: true);
        }
    }

    public async Task RegisterCreatedThreadAsync(WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        UiDispatch.Invoke(
            _uiDispatcher,
            () =>
            {
                _catalogStateCoordinator.UpsertThread(thread);
                OpenThread(thread.ThreadId);
            });

        await _frontendPort.EnsureThreadHistoryLoadedAsync(thread, CancellationToken.None);
    }

    public OpenThreadResult OpenThread(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var thread = FindThread(threadId);
        if (thread is null)
        {
            return OpenThreadResult.NotFound;
        }

        var alreadyOpen = ViewState.OpenThreadIds.Contains(threadId, StringComparer.OrdinalIgnoreCase);
        _frontendPort.ResetPendingThreadTabSelection();
        EnsureThreadTab(thread);
        if (!alreadyOpen)
        {
            ViewState.OpenThreadIds.Add(threadId);
        }

        ViewState.SelectedThreadId = threadId;
        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        _selectionCoordinator.SelectThread(thread);
        if (ThreadHistoryCoordinator.CanLoadThreadHistory(thread) && !_frontendPort.IsModelProviderReady(thread))
        {
            PendingStartupThreadRestoreId = thread.ThreadId;
        }

        _ = PersistViewStateAsync();
        SyncStateStore(selectionChanged: true);
        _ = _frontendPort.EnsureThreadHistoryLoadedAsync(thread, CancellationToken.None);
        return alreadyOpen
            ? OpenThreadResult.AlreadyOpen
            : OpenThreadResult.Opened;
    }

    public async Task<TabCloseResult> CloseSelectedThreadAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedThreadId))
        {
            return TabCloseResult.NotOpen;
        }

        return await CloseThreadTabAsync(SelectedThreadId);
    }

    public async Task<TabCloseResult> CloseThreadTabAsync(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var wasOpen = ViewState.OpenThreadIds.Contains(threadId, StringComparer.OrdinalIgnoreCase);
        _frontendPort.ResetPendingThreadTabSelection();
        var removedSelectedThread = string.Equals(SelectedThreadId, threadId, StringComparison.OrdinalIgnoreCase);
        var removedThread = FindThread(threadId);
        ViewState.OpenThreadIds.RemoveAll(id => string.Equals(id, threadId, StringComparison.OrdinalIgnoreCase));
        _frontendPort.RemoveThreadTabPage(threadId, ShellTabCloseReason.UserDetached);
        if (removedSelectedThread)
        {
            var nextThreadId = ViewState.OpenThreadIds.FirstOrDefault();
            ViewState.SelectedThreadId = nextThreadId;
            _selectionCoordinator.ApplyThreadRemovalFallback(nextThreadId, removedThread?.ProjectRef, Projects, Threads);
        }

        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync();
        SyncStateStore(selectionChanged: true);
        return wasOpen
            ? TabCloseResult.Closed
            : TabCloseResult.NotOpen;
    }

    public async Task RemoveDeletedThreadArtifactsAsync(IReadOnlyList<string> threadIds)
    {
        ArgumentNullException.ThrowIfNull(threadIds);

        var removedAnyState = false;
        foreach (var threadId in threadIds)
        {
            if (string.IsNullOrWhiteSpace(threadId))
            {
                continue;
            }

            _frontendPort.DeletePromptDraft(threadId);
            PendingStartupThreadRestoreId = string.Equals(PendingStartupThreadRestoreId, threadId, StringComparison.OrdinalIgnoreCase)
                ? null
                : PendingStartupThreadRestoreId;
            removedAnyState |= ViewState.ThreadStates.Remove(threadId);
        }

        if (!removedAnyState)
        {
            return;
        }

        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync();
    }

    public void RemoveDeletedThread(string threadId, string? fallbackProjectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        _frontendPort.ResetPendingThreadTabSelection();
        var removedSelectedThread = string.Equals(SelectedThreadId, threadId, StringComparison.OrdinalIgnoreCase);
        ViewState.OpenThreadIds.RemoveAll(id => string.Equals(id, threadId, StringComparison.OrdinalIgnoreCase));
        _frontendPort.RemoveThreadTabPage(threadId, ShellTabCloseReason.ThreadDeleted);
        _OpenThreadStateStore.RemoveThreadTab(threadId);
        _frontendPort.DeletePromptDraft(threadId);

        if (string.Equals(ViewState.SelectedThreadId, threadId, StringComparison.OrdinalIgnoreCase))
        {
            ViewState.SelectedThreadId = null;
        }

        if (removedSelectedThread)
        {
            _selectionCoordinator.ApplyThreadRemovalFallback(nextSelectedThreadId: null, fallbackProjectId, Projects, Threads);
        }

        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        _ = PersistViewStateAsync();
        SyncStateStore(catalogChanged: true, selectionChanged: true);
    }

    public void RemoveDeletedProject(string projectId, IReadOnlyList<string> deletedThreadIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(deletedThreadIds);

        _frontendPort.ResetPendingThreadTabSelection();
        var removedSelectedThread = deletedThreadIds.Contains(SelectedThreadId, StringComparer.OrdinalIgnoreCase);
        var removedSelectedProject = !GlobalScopeSelected &&
            string.Equals(SelectedProjectId, projectId, StringComparison.OrdinalIgnoreCase);

        foreach (var threadId in deletedThreadIds)
        {
            ViewState.OpenThreadIds.RemoveAll(id => string.Equals(id, threadId, StringComparison.OrdinalIgnoreCase));
            _frontendPort.RemoveThreadTabPage(threadId, ShellTabCloseReason.ProjectClosed);
            _OpenThreadStateStore.RemoveThreadTab(threadId);
            _frontendPort.DeletePromptDraft(threadId);

            if (string.Equals(ViewState.SelectedThreadId, threadId, StringComparison.OrdinalIgnoreCase))
            {
                ViewState.SelectedThreadId = null;
            }
        }

        if (removedSelectedThread)
        {
            _selectionCoordinator.ApplyThreadRemovalFallback(nextSelectedThreadId: null, fallbackProjectId: null, Projects, Threads);
        }
        else if (removedSelectedProject && string.IsNullOrWhiteSpace(SelectedThreadId))
        {
            _selectionCoordinator.SelectGlobalScope(Projects);
        }

        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        _ = PersistViewStateAsync();
        SyncStateStore(catalogChanged: true, selectionChanged: true);
    }

    public OpenThreadState EnsureThreadTab(WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        return _OpenThreadStateStore.EnsureThreadTab(thread);
    }

    public void ResetThreadTab(OpenThreadState tab)
        => _OpenThreadStateStore.ResetThreadTab(tab);

    private void SyncStateStore(bool catalogChanged = false, bool selectionChanged = false)
    {
        _stateStore.Mutate(snapshot => snapshot
            .SetCatalog(Projects, Threads)
            .SetSelection(Selection, ViewState.OpenThreadIds, NavigatorSettings));

        if (catalogChanged)
        {
            _frontendEvents?.Publish(new CatalogChangedEvent());
        }

        if (selectionChanged)
        {
            _frontendEvents?.Publish(new SelectionChangedEvent(_stateStore.Snapshot));
        }
    }

    public OpenThreadState? FindOpenThread(string threadId)
        => _OpenThreadStateStore.FindOpenThread(threadId);

    public ProjectDescriptor? GetSelectedProject()
    {
        var selectedThread = GetSelectedThread();
        return selectedThread?.ProjectRef is { } projectId
            ? GetProjectById(projectId)
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

    public WorkThreadDescriptor? GetSelectedThread()
        => FindThread(SelectedThreadId);

    public WorkThreadDescriptor? FindThread(string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        return _catalogStateCoordinator.FindThread(threadId);
    }

    public async Task RestoreStartupThreadHistoryAsync(string? threadId, CancellationToken cancellationToken)
    {
        var thread = FindThread(threadId);
        if (thread is null)
        {
            return;
        }

        await _frontendPort.EnsureThreadHistoryLoadedAsync(thread, cancellationToken);
    }

}
