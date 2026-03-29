using CodeAlta.App.State;
using CodeAlta.Threading;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Shell;
using CodeAlta.Presentation.Timeline;
using CodeAlta.Views;
using XenoAtom.Logging;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.App;

internal sealed class ShellThreadStateCoordinator
{
    internal sealed record InitialCatalogState(
        IReadOnlyList<ProjectDescriptor> Projects,
        IReadOnlyList<WorkThreadDescriptor> Threads,
        WorkThreadViewState ViewState);

    private readonly ProjectCatalog _projectCatalog;
    private readonly WorkThreadCatalog _threadCatalog;
    private readonly ShellSelectionState _selection = new();
    private readonly Dictionary<string, OpenThreadState> _threadTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IUiDispatcher> _getUiDispatcher;
    private readonly Func<Rectangle?> _getTimelineBounds;
    private readonly Func<WorkThreadDescriptor, bool> _isBackendReady;
    private readonly Action<OpenThreadState> _applyThreadPreference;
    private readonly Action<string, string?, AgentReasoningEffort?, bool, bool> _rememberThreadPreference;
    private readonly Func<WorkThreadDescriptor, CancellationToken, Task> _ensureThreadHistoryLoadedAsync;
    private readonly Action _refreshSelectionAndThreadWorkspace;
    private readonly Action _refreshCatalogAndThreadWorkspace;
    private readonly Action _resetPendingThreadTabSelection;
    private readonly Action<string> _removeTabPage;
    private readonly Action<string, bool, StatusTone> _setStatus;
    private IReadOnlyList<ProjectDescriptor> _projects = [];
    private IReadOnlyList<WorkThreadDescriptor> _threads = [];

    public ShellThreadStateCoordinator(
        ProjectCatalog projectCatalog,
        WorkThreadCatalog threadCatalog,
        Func<IUiDispatcher> getUiDispatcher,
        Func<Rectangle?> getTimelineBounds,
        Func<WorkThreadDescriptor, bool> isBackendReady,
        Action<OpenThreadState> applyThreadPreference,
        Action<string, string?, AgentReasoningEffort?, bool, bool> rememberThreadPreference,
        Func<WorkThreadDescriptor, CancellationToken, Task> ensureThreadHistoryLoadedAsync,
        Action refreshSelectionAndThreadWorkspace,
        Action refreshCatalogAndThreadWorkspace,
        Action resetPendingThreadTabSelection,
        Action<string> removeTabPage,
        Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(projectCatalog);
        ArgumentNullException.ThrowIfNull(threadCatalog);
        ArgumentNullException.ThrowIfNull(getUiDispatcher);
        ArgumentNullException.ThrowIfNull(getTimelineBounds);
        ArgumentNullException.ThrowIfNull(isBackendReady);
        ArgumentNullException.ThrowIfNull(applyThreadPreference);
        ArgumentNullException.ThrowIfNull(rememberThreadPreference);
        ArgumentNullException.ThrowIfNull(ensureThreadHistoryLoadedAsync);
        ArgumentNullException.ThrowIfNull(refreshSelectionAndThreadWorkspace);
        ArgumentNullException.ThrowIfNull(refreshCatalogAndThreadWorkspace);
        ArgumentNullException.ThrowIfNull(resetPendingThreadTabSelection);
        ArgumentNullException.ThrowIfNull(removeTabPage);
        ArgumentNullException.ThrowIfNull(setStatus);

        _projectCatalog = projectCatalog;
        _threadCatalog = threadCatalog;
        _getUiDispatcher = getUiDispatcher;
        _getTimelineBounds = getTimelineBounds;
        _isBackendReady = isBackendReady;
        _applyThreadPreference = applyThreadPreference;
        _rememberThreadPreference = rememberThreadPreference;
        _ensureThreadHistoryLoadedAsync = ensureThreadHistoryLoadedAsync;
        _refreshSelectionAndThreadWorkspace = refreshSelectionAndThreadWorkspace;
        _refreshCatalogAndThreadWorkspace = refreshCatalogAndThreadWorkspace;
        _resetPendingThreadTabSelection = resetPendingThreadTabSelection;
        _removeTabPage = removeTabPage;
        _setStatus = setStatus;
    }

    public IReadOnlyList<ProjectDescriptor> Projects => _projects;

    public IReadOnlyList<WorkThreadDescriptor> Threads => _threads;

    public WorkThreadViewState ViewState
    {
        get => _selection.ViewState;
        set => _selection.ViewState = value;
    }

    public bool DraftTabOpen
    {
        get => _selection.DraftTabOpen;
        set => _selection.DraftTabOpen = value;
    }

    public bool GlobalScopeSelected
    {
        get => _selection.GlobalScopeSelected;
        set => _selection.GlobalScopeSelected = value;
    }

    public string? SelectedProjectId
    {
        get => _selection.SelectedProjectId;
        set => _selection.SelectedProjectId = value;
    }

    public string? SelectedThreadId
    {
        get => _selection.SelectedThreadId;
        set => _selection.SelectedThreadId = value;
    }

    public string? PendingStartupThreadRestoreId
    {
        get => _selection.PendingStartupThreadRestoreId;
        set => _selection.PendingStartupThreadRestoreId = value;
    }

    public NavigatorSettings NavigatorSettings => ViewState.Navigator;

    public async Task<InitialCatalogState> LoadInitialCatalogStateAsync(CancellationToken cancellationToken)
    {
        var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var threads = await _threadCatalog.LoadInternalAsync(cancellationToken).ConfigureAwait(false);
        var viewState = await _threadCatalog.LoadViewStateAsync(cancellationToken).ConfigureAwait(false);
        ApplyThreadLocalState(threads, viewState);
        return new InitialCatalogState(projects, threads, viewState);
    }

    public void ApplyInitialCatalogState(InitialCatalogState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _projects = state.Projects;
        _threads = state.Threads;
        ViewState = state.ViewState;

        var desiredThreadId = ViewState.SelectedThreadId ?? ViewState.OpenThreadIds.FirstOrDefault();
        SelectedThreadId = string.IsNullOrWhiteSpace(desiredThreadId)
            ? null
            : _threads.FirstOrDefault(thread => string.Equals(thread.ThreadId, desiredThreadId, StringComparison.OrdinalIgnoreCase))?.ThreadId;
        DraftTabOpen = SelectedThreadId is null;
        PendingStartupThreadRestoreId = desiredThreadId;
        var selectedThread = GetSelectedThread();
        SelectedProjectId = selectedThread?.ProjectRef ?? _projects.FirstOrDefault()?.Id;
    }

    public async Task LoadCatalogStateAsync(CancellationToken cancellationToken)
    {
        ApplyInitialCatalogState(await LoadInitialCatalogStateAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task PersistViewStateAsync()
    {
        try
        {
            await _threadCatalog.SaveViewStateAsync(ViewState, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && CodeAltaApp.UiLogger.IsEnabled(LogLevel.Error))
            {
                CodeAltaApp.UiLogger.Error(ex, "Failed to persist thread view state.");
            }
        }
    }

    public void ApplyRecoveredCatalogState(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(threads);

        _projects = projects;
        _threads = ApplyThreadLocalState(threads, ViewState);

        ViewState.OpenThreadIds.RemoveAll(id => _threads.All(thread => !string.Equals(thread.ThreadId, id, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(ViewState.SelectedThreadId) &&
            ViewState.OpenThreadIds.All(id => !string.Equals(id, ViewState.SelectedThreadId, StringComparison.OrdinalIgnoreCase)))
        {
            ViewState.SelectedThreadId = null;
        }

        if (string.IsNullOrWhiteSpace(SelectedThreadId) &&
            !string.IsNullOrWhiteSpace(PendingStartupThreadRestoreId) &&
            FindThread(PendingStartupThreadRestoreId) is { } restoredThread)
        {
            if (!ViewState.OpenThreadIds.Contains(restoredThread.ThreadId, StringComparer.OrdinalIgnoreCase))
            {
                ViewState.OpenThreadIds.Insert(0, restoredThread.ThreadId);
            }

            ViewState.SelectedThreadId = restoredThread.ThreadId;
            SelectedThreadId = restoredThread.ThreadId;
            SelectedProjectId = restoredThread.ProjectRef ?? SelectedProjectId;
            DraftTabOpen = false;
        }

        EnsureSelectionDefaults();
        _refreshCatalogAndThreadWorkspace();
    }

    public async Task PersistThreadLocalStateAsync(WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        ViewState.ThreadStates[thread.ThreadId] = new WorkThreadLocalState
        {
            Archived = thread.Status == WorkThreadStatus.Archived,
            MessageCount = thread.MessageCount,
        };
        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync().ConfigureAwait(false);
    }

    public NavigatorSettings GetNavigatorSettingsSnapshot()
    {
        return new NavigatorSettings
        {
            SortMode = ViewState.Navigator.SortMode,
            RecentThreadsPerProject = ViewState.Navigator.RecentThreadsPerProject,
        };
    }

    public async Task SaveNavigatorSettingsAsync(NavigatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        ViewState.Navigator = new NavigatorSettings
        {
            SortMode = settings.SortMode,
            RecentThreadsPerProject = settings.RecentThreadsPerProject,
        };
        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync().ConfigureAwait(false);
    }

    public void TrySchedulePendingStartupThreadRestore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(PendingStartupThreadRestoreId))
        {
            return;
        }

        var thread = FindThread(PendingStartupThreadRestoreId);
        if (thread is null || !_isBackendReady(thread))
        {
            return;
        }

        var threadId = PendingStartupThreadRestoreId;
        PendingStartupThreadRestoreId = null;
        _ = RestoreStartupThreadHistoryAsync(threadId, cancellationToken);
    }

    public void SelectGlobalScope()
    {
        _resetPendingThreadTabSelection();
        DraftTabOpen = true;
        GlobalScopeSelected = true;
        SelectedThreadId = null;
        ViewState.SelectedThreadId = null;
        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        _ = PersistViewStateAsync();
        _refreshSelectionAndThreadWorkspace();
    }

    public void SelectProjectScope(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        _resetPendingThreadTabSelection();
        DraftTabOpen = true;
        GlobalScopeSelected = false;
        SelectedProjectId = projectId;
        SelectedThreadId = null;
        ViewState.SelectedThreadId = null;
        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        _ = PersistViewStateAsync();
        _refreshSelectionAndThreadWorkspace();
    }

    public void EnsureSelectionDefaults()
    {
        if (!string.IsNullOrWhiteSpace(SelectedThreadId) &&
            _threads.All(thread => !string.Equals(thread.ThreadId, SelectedThreadId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedThreadId = null;
        }

        if (string.IsNullOrWhiteSpace(SelectedProjectId) ||
            _projects.All(project => !string.Equals(project.Id, SelectedProjectId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedProjectId = _projects.FirstOrDefault()?.Id;
        }

        if (!GlobalScopeSelected && SelectedProjectId is null)
        {
            GlobalScopeSelected = true;
        }
    }

    public async Task RegisterCreatedThreadAsync(WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        var threads = _threads.ToList();
        threads.RemoveAll(existing => string.Equals(existing.ThreadId, thread.ThreadId, StringComparison.OrdinalIgnoreCase));
        threads.Add(thread);
        _threads = threads
            .OrderByDescending(static item => item.LastActiveAt)
            .ToArray();

        DraftTabOpen = false;
        OpenThread(thread.ThreadId);
        await _ensureThreadHistoryLoadedAsync(thread, CancellationToken.None).ConfigureAwait(false);
    }

    public OpenThreadState RegisterDelegatedThread(WorkThreadDescriptor child, OpenThreadState sourceTab)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(sourceTab);

        _threads = _threads
            .Where(existing => !string.Equals(existing.ThreadId, child.ThreadId, StringComparison.OrdinalIgnoreCase))
            .Append(child)
            .OrderByDescending(static item => item.LastActiveAt)
            .ToArray();

        if (!ViewState.OpenThreadIds.Contains(child.ThreadId, StringComparer.OrdinalIgnoreCase))
        {
            ViewState.OpenThreadIds.Add(child.ThreadId);
            ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var childTab = EnsureThreadTab(child);
        childTab.BackendId = sourceTab.BackendId;
        childTab.ModelId = sourceTab.ModelId;
        childTab.ReasoningEffort = sourceTab.ReasoningEffort;
        childTab.AutoScroll = sourceTab.AutoScroll;
        return childTab;
    }

    public void OpenThread(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var thread = FindThread(threadId);
        if (thread is null)
        {
            _setStatus($"Thread '{threadId}' was not found.", false, StatusTone.Warning);
            return;
        }

        _resetPendingThreadTabSelection();
        EnsureThreadTab(thread);
        if (!ViewState.OpenThreadIds.Contains(threadId, StringComparer.OrdinalIgnoreCase))
        {
            ViewState.OpenThreadIds.Add(threadId);
        }

        ViewState.SelectedThreadId = threadId;
        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        SelectedThreadId = threadId;
        if (ThreadHistoryCoordinator.CanLoadThreadHistory(thread) && !_isBackendReady(thread))
        {
            PendingStartupThreadRestoreId = thread.ThreadId;
        }

        _ = PersistViewStateAsync();
        _refreshSelectionAndThreadWorkspace();
        _ = _ensureThreadHistoryLoadedAsync(thread, CancellationToken.None);
    }

    public async Task CloseSelectedThreadAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedThreadId))
        {
            return;
        }

        await CloseThreadAsync(SelectedThreadId).ConfigureAwait(false);
    }

    public async Task CloseThreadAsync(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        _resetPendingThreadTabSelection();
        ViewState.OpenThreadIds.RemoveAll(id => string.Equals(id, threadId, StringComparison.OrdinalIgnoreCase));
        _removeTabPage(threadId);
        _threadTabs.Remove(threadId);
        if (string.Equals(SelectedThreadId, threadId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedThreadId = ViewState.OpenThreadIds.FirstOrDefault();
            ViewState.SelectedThreadId = SelectedThreadId;
        }

        ViewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync().ConfigureAwait(false);
        _refreshSelectionAndThreadWorkspace();
    }

    public OpenThreadState EnsureThreadTab(WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        if (_threadTabs.TryGetValue(thread.ThreadId, out var existing))
        {
            existing.Thread = thread;
            existing.ViewModel.ThreadId = thread.ThreadId;
            existing.ViewModel.Title = thread.Title;
            return existing;
        }

        OpenThreadState? state = null;
        var timeline = new ThreadTimelinePresenter(
            _getUiDispatcher(),
            () => state!.AutoScroll,
            _getTimelineBounds);
        state = new OpenThreadState(thread, timeline);
        state.BackendId = new AgentBackendId(thread.BackendId);
        state.ViewModel.Title = thread.Title;
        state.StatusMessage = ShellTextFormatter.BuildReadyStatusText(thread, GetSelectedProject(), globalScopeSelected: false);

        _applyThreadPreference(state);
        _rememberThreadPreference(thread.ThreadId, state.ModelId, state.ReasoningEffort, state.AutoScroll, false);

        _threadTabs[thread.ThreadId] = state;
        return state;
    }

    public void ResetThreadTab(OpenThreadState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        tab.Timeline.Reset();
        tab.PermissionRequests.Clear();
        tab.UserInputRequests.Clear();
    }

    public OpenThreadState? FindOpenThread(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return _threadTabs.GetValueOrDefault(threadId);
    }

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

        return _projects.FirstOrDefault(project => string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase));
    }

    public WorkThreadDescriptor? GetSelectedThread()
        => FindThread(SelectedThreadId);

    public WorkThreadDescriptor? FindThread(string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        return _threads.FirstOrDefault(thread => string.Equals(thread.ThreadId, threadId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task RestoreStartupThreadHistoryAsync(string? threadId, CancellationToken cancellationToken)
    {
        var thread = FindThread(threadId);
        if (thread is null)
        {
            return;
        }

        await _ensureThreadHistoryLoadedAsync(thread, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<WorkThreadDescriptor> ApplyThreadLocalState(
        IReadOnlyList<WorkThreadDescriptor> threads,
        WorkThreadViewState viewState)
    {
        ArgumentNullException.ThrowIfNull(threads);
        ArgumentNullException.ThrowIfNull(viewState);

        foreach (var thread in threads)
        {
            if (!viewState.ThreadStates.TryGetValue(thread.ThreadId, out var localState))
            {
                continue;
            }

            if (localState.Archived)
            {
                thread.Status = WorkThreadStatus.Archived;
            }

            if (localState.MessageCount is { } messageCount)
            {
                thread.MessageCount = messageCount;
            }
        }

        return threads;
    }
}
