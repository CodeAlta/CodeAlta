using CodeAlta.App.State;
using CodeAlta.App.Events;
using CodeAlta.Threading;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Shell;

namespace CodeAlta.App;

internal sealed partial class ShellThreadStateCoordinator
{
    internal sealed record InitialCatalogState(
        IReadOnlyList<ProjectDescriptor> Projects,
        IReadOnlyList<WorkThreadDescriptor> Threads,
        WorkThreadViewState ViewState);

    private readonly ShellSelectionCoordinator _selectionCoordinator = new();
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ShellStateStore _stateStore;
    private readonly FrontendEventPublisher? _frontendEvents;
    private readonly IThreadPromptDraftService _promptDrafts;
    private readonly IThreadModelProviderReadinessService _modelProviderReadiness;
    private readonly IThreadHistoryLoaderService _historyLoader;
    private readonly IThreadStateTabLifecycleService _tabLifecycle;
    private readonly ShellCatalogStateCoordinator _catalogStateCoordinator;
    private readonly OpenThreadStateStore _OpenThreadStateStore;
    private readonly ThreadViewStateCoordinator _viewStateCoordinator;

    public ShellThreadStateCoordinator(
        ProjectCatalog projectCatalog,
        WorkThreadCatalog threadCatalog,
        IUiDispatcher uiDispatcher,
        ShellStateStore stateStore,
        IThreadTimelineSurface timelineSurface,
        IThreadPromptDraftService promptDrafts,
        IThreadModelProviderPreferenceService modelProviderPreferences,
        IThreadModelProviderReadinessService modelProviderReadiness,
        IThreadHistoryLoaderService historyLoader,
        IThreadStateTabLifecycleService tabLifecycle,
        FrontendEventPublisher? frontendEvents = null)
    {
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(threadCatalog);
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
        _viewStateCoordinator = new ThreadViewStateCoordinator(threadCatalog);
        var threadSessionFactory = new ThreadSessionFactory(
            uiDispatcher,
            timelineSurface,
            promptDrafts,
            modelProviderPreferences,
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
        RestoreStartupOpenThreadSelection(state.ViewState, Threads);
        _selectionCoordinator.ApplyInitialSelection(state.ViewState, Projects, Threads);
        SyncStateStore(catalogChanged: true, selectionChanged: true);
    }

    public async Task LoadCatalogStateAsync(CancellationToken cancellationToken)
    {
        ApplyInitialCatalogState(await LoadInitialCatalogStateAsync(cancellationToken));
    }

    public async Task PersistViewStateAsync()
        => await _viewStateCoordinator.PersistViewStateAsync(ViewState);

    private static void RestoreStartupOpenThreadSelection(
        WorkThreadViewState viewState,
        IReadOnlyList<WorkThreadDescriptor> threads)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(threads);

        if (viewState.Selection.Surface == WorkThreadSelectionSurface.Thread ||
            viewState.OpenThreadIds.Count == 0)
        {
            return;
        }

        var selectedThread = ResolveStartupOpenThread(viewState, threads);
        if (selectedThread is null)
        {
            return;
        }

        viewState.Selection = WorkThreadSelectionState.Thread(selectedThread.ThreadId, selectedThread.ProjectRef);
        viewState.SelectedThreadId = selectedThread.ThreadId;
    }

    private static WorkThreadDescriptor? ResolveStartupOpenThread(
        WorkThreadViewState viewState,
        IReadOnlyList<WorkThreadDescriptor> threads)
    {
        var preferredProjectId = viewState.Selection.ProjectId;
        foreach (var threadId in Enumerable.Reverse(viewState.OpenThreadIds))
        {
            var thread = threads.FirstOrDefault(candidate =>
                string.Equals(candidate.ThreadId, threadId, StringComparison.OrdinalIgnoreCase));
            if (thread is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(preferredProjectId) ||
                string.Equals(thread.ProjectRef, preferredProjectId, StringComparison.OrdinalIgnoreCase))
            {
                return thread;
            }
        }

        return null;
    }

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

    public void UpsertRuntimeThread(WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        _viewStateCoordinator.ApplyThreadLocalState([thread], ViewState, readJournal: false);
        var localState = _viewStateCoordinator.RememberThreadLocalState(ViewState, thread);
        global::CodeAlta.CodeAltaTaskMonitor.Observe(
            _viewStateCoordinator.PersistThreadLocalStateSnapshotAsync(thread, localState),
            $"Persist local state for thread {thread.ThreadId}");
        _catalogStateCoordinator.UpsertThread(thread);
        SyncStateStore(catalogChanged: true);
    }

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
        _tabLifecycle.RemoveThreadTabPage(oldThreadId, ShellTabCloseReason.Replaced);

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
        if (thread is null || !_modelProviderReadiness.IsModelProviderReady(thread))
        {
            return;
        }

        var threadId = PendingStartupThreadRestoreId;
        PendingStartupThreadRestoreId = null;
        _ = RestoreStartupThreadHistoryAsync(threadId, cancellationToken);
    }

    public void SelectGlobalScope()
    {
        _tabLifecycle.ResetPendingThreadTabSelection();
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
        _tabLifecycle.ResetPendingThreadTabSelection();
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
                _tabLifecycle.ReplaceDraftTabWithThread(thread.ThreadId);
            });

        await _historyLoader.EnsureThreadHistoryLoadedAsync(thread, CancellationToken.None);
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
        _tabLifecycle.ResetPendingThreadTabSelection();
        EnsureThreadTab(thread);
        if (!alreadyOpen)
        {
            ViewState.OpenThreadIds.Add(threadId);
        }

        ViewState.SelectedThreadId = threadId;
        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        _selectionCoordinator.SelectThread(thread);
        if (ThreadHistoryCoordinator.CanLoadThreadHistory(thread) && !_modelProviderReadiness.IsModelProviderReady(thread))
        {
            PendingStartupThreadRestoreId = thread.ThreadId;
        }

        _ = PersistViewStateAsync();
        SyncStateStore(selectionChanged: true);
        _ = _historyLoader.EnsureThreadHistoryLoadedAsync(thread, CancellationToken.None);
        return alreadyOpen
            ? OpenThreadResult.AlreadyOpen
            : OpenThreadResult.Opened;
    }

    public async Task<TabCloseResult> CloseThreadTabAsync(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var wasOpen = ViewState.OpenThreadIds.Contains(threadId, StringComparer.OrdinalIgnoreCase);
        _tabLifecycle.ResetPendingThreadTabSelection();
        var removedSelectedThread = string.Equals(SelectedThreadId, threadId, StringComparison.OrdinalIgnoreCase);
        var removedThread = FindThread(threadId);
        var openThreadTabIds = removedSelectedThread
            ? _tabLifecycle.GetOpenThreadTabIds()
            : [];
        ViewState.OpenThreadIds.RemoveAll(id =>
            string.Equals(id, threadId, StringComparison.OrdinalIgnoreCase) ||
            (removedSelectedThread && !openThreadTabIds.Contains(id, StringComparer.OrdinalIgnoreCase)));
        if (removedSelectedThread)
        {
            var nextThreadId = GetNextOpenThreadTabId(threadId, openThreadTabIds);
            ViewState.SelectedThreadId = nextThreadId;
            _selectionCoordinator.ApplyThreadRemovalFallback(nextThreadId, removedThread?.ProjectRef, Projects, Threads);
            if (!string.IsNullOrWhiteSpace(nextThreadId) && FindThread(nextThreadId) is { } nextThread)
            {
                EnsureThreadTab(nextThread);
                _ = _historyLoader.EnsureThreadHistoryLoadedAsync(nextThread, CancellationToken.None);
            }
        }

        _tabLifecycle.RemoveThreadTabPage(threadId, ShellTabCloseReason.UserDetached);
        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync();
        SyncStateStore(selectionChanged: true);
        return wasOpen
            ? TabCloseResult.Closed
            : TabCloseResult.NotOpen;
    }

    private string? GetNextOpenThreadTabId(string closingThreadId, IReadOnlyList<string> openThreadTabIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(closingThreadId);
        ArgumentNullException.ThrowIfNull(openThreadTabIds);

        foreach (var threadId in openThreadTabIds)
        {
            if (string.IsNullOrWhiteSpace(threadId) ||
                string.Equals(threadId, closingThreadId, StringComparison.OrdinalIgnoreCase) ||
                !ViewState.OpenThreadIds.Contains(threadId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            return threadId;
        }

        return null;
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

            _promptDrafts.DeletePromptDraft(threadId);
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

        _tabLifecycle.ResetPendingThreadTabSelection();
        var removedSelectedThread = string.Equals(SelectedThreadId, threadId, StringComparison.OrdinalIgnoreCase);
        ViewState.OpenThreadIds.RemoveAll(id => string.Equals(id, threadId, StringComparison.OrdinalIgnoreCase));
        _tabLifecycle.RemoveThreadTabPage(threadId, ShellTabCloseReason.ThreadDeleted);
        _OpenThreadStateStore.RemoveThreadTab(threadId);
        _promptDrafts.DeletePromptDraft(threadId);

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

        _tabLifecycle.ResetPendingThreadTabSelection();
        var removedSelectedThread = deletedThreadIds.Contains(SelectedThreadId, StringComparer.OrdinalIgnoreCase);
        var removedSelectedProject = !GlobalScopeSelected &&
            string.Equals(SelectedProjectId, projectId, StringComparison.OrdinalIgnoreCase);

        foreach (var threadId in deletedThreadIds)
        {
            ViewState.OpenThreadIds.RemoveAll(id => string.Equals(id, threadId, StringComparison.OrdinalIgnoreCase));
            _tabLifecycle.RemoveThreadTabPage(threadId, ShellTabCloseReason.ProjectClosed);
            _OpenThreadStateStore.RemoveThreadTab(threadId);
            _promptDrafts.DeletePromptDraft(threadId);

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
        if (selectedThread is not null)
        {
            return selectedThread.ProjectRef is { } projectId
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

        await _historyLoader.EnsureThreadHistoryLoadedAsync(thread, cancellationToken);
    }

}
