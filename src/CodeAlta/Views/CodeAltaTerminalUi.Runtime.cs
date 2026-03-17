using System.ComponentModel;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.Logging;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

internal sealed partial class CodeAltaTerminalUi
{
    private async Task LoadCatalogStateAsync(CancellationToken cancellationToken)
    {
        _projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        _threads = await _threadCatalog.LoadInternalAsync(cancellationToken).ConfigureAwait(false);
        _viewState = await _threadCatalog.LoadViewStateAsync(cancellationToken).ConfigureAwait(false);

        var desiredThreadId = _viewState.SelectedThreadId ?? _viewState.OpenThreadIds.FirstOrDefault();
        _selectedThreadId = string.IsNullOrWhiteSpace(desiredThreadId)
            ? null
            : _threads.FirstOrDefault(thread => string.Equals(thread.ThreadId, desiredThreadId, StringComparison.OrdinalIgnoreCase))?.ThreadId;
        _pendingStartupThreadRestoreId = desiredThreadId;
        var selectedThread = GetSelectedThread();
        _selectedProjectId = selectedThread?.ProjectRef ?? _projects.FirstOrDefault()?.Id;
    }

    internal static InitialThreadSelection ResolveInitialSelection(
        WorkThreadViewState viewState,
        IReadOnlyList<WorkThreadDescriptor> threads)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(threads);

        var selectedThreadId = viewState.SelectedThreadId ?? viewState.OpenThreadIds.FirstOrDefault();
        var selectedThread = string.IsNullOrWhiteSpace(selectedThreadId)
            ? null
            : threads.FirstOrDefault(thread => string.Equals(thread.ThreadId, selectedThreadId, StringComparison.OrdinalIgnoreCase));

        return new InitialThreadSelection(
            selectedThread?.ThreadId,
            selectedThread?.ThreadId);
    }

    private void StartStartupRefresh(CancellationToken cancellationToken)
    {
        if (_startupRefreshTask is not null)
        {
            return;
        }

        _startupRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runtimeEventsCts.Token);
        _startupRefreshTask = Task.Run(
            () => RunStartupRefreshAsync(_startupRefreshCts.Token),
            CancellationToken.None);
    }

    private async Task RunStartupRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await InitializeChatBackendsAsync(cancellationToken).ConfigureAwait(false);
            await RefreshCatalogFromBackendsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            PostToUi(
                () =>
                {
                    RefreshView();
                    SetReadyStatusForCurrentSelection();
                });

            TrySchedulePendingStartupThreadRestore(CancellationToken.None);
        }
    }

    private async Task InitializeChatBackendsAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
                RefreshChatBackendStateAsync(AgentBackendIds.Codex, cancellationToken),
                RefreshChatBackendStateAsync(AgentBackendIds.Copilot, cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task RefreshChatBackendStateAsync(AgentBackendId backendId, CancellationToken cancellationToken)
    {
        var state = _chatBackendStates[backendId.Value];
        PostToUi(
            () =>
            {
                state.Availability = ChatBackendAvailability.Connecting;
                state.StatusMessage = "Detecting backend...";
                RefreshView();
            });

        try
        {
            var models = await _agentHub.ListModelsAsync(backendId, cancellationToken).ConfigureAwait(false);
            PostToUi(
                () =>
                {
                    state.Models.Clear();
                    state.Models.AddRange(models);
                    state.SelectedModelId = models.FirstOrDefault()?.Id;
                    state.SelectedReasoningEffort = NormalizeReasoningEffort(state.SelectedReasoningEffort, GetSelectedModel(state));
                    state.Availability = ChatBackendAvailability.Ready;
                    state.StatusMessage = BuildReadyStatusMessage(state);
                    RefreshView();
                });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var (availability, statusMessage) = ClassifyBackendInitializationFailure(state, ex);
            PostToUi(
                () =>
                {
                    state.Models.Clear();
                    state.SelectedModelId = null;
                    state.SelectedReasoningEffort = null;
                    state.Availability = availability;
                    state.StatusMessage = statusMessage;
                    RefreshView();
                });
        }
    }

    private async Task RefreshCatalogFromBackendsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await TerminalHost.ImportKnownProjectsFromBackendsAsync(_agentHub, _projectCatalog, cancellationToken).ConfigureAwait(false);
            var projects = await _projectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
            var threads = await _runtimeService.ListRecoverableThreadsAsync(cancellationToken).ConfigureAwait(false);
            PostToUi(() => ApplyRecoveredCatalogState(projects, threads));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && UiLogger.IsEnabled(LogLevel.Error))
            {
                UiLogger.Error(ex, "Failed to refresh backend startup state.");
            }
        }
    }

    private void ApplyRecoveredCatalogState(
        IReadOnlyList<ProjectDescriptor> projects,
        IReadOnlyList<WorkThreadDescriptor> threads)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(threads);

        _projects = projects;
        _threads = threads;

        _viewState.OpenThreadIds.RemoveAll(id => _threads.All(thread => !string.Equals(thread.ThreadId, id, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(_viewState.SelectedThreadId) &&
            _viewState.OpenThreadIds.All(id => !string.Equals(id, _viewState.SelectedThreadId, StringComparison.OrdinalIgnoreCase)))
        {
            _viewState.SelectedThreadId = null;
        }

        if (string.IsNullOrWhiteSpace(_selectedThreadId) &&
            !string.IsNullOrWhiteSpace(_pendingStartupThreadRestoreId) &&
            FindThread(_pendingStartupThreadRestoreId) is { } restoredThread)
        {
            if (!_viewState.OpenThreadIds.Contains(restoredThread.ThreadId, StringComparer.OrdinalIgnoreCase))
            {
                _viewState.OpenThreadIds.Insert(0, restoredThread.ThreadId);
            }

            _viewState.SelectedThreadId = restoredThread.ThreadId;
            _selectedThreadId = restoredThread.ThreadId;
            _globalScopeSelected = restoredThread.Kind == WorkThreadKind.GlobalThread;
            _selectedProjectId = restoredThread.ProjectRef ?? _selectedProjectId;
        }

        EnsureSelectionDefaults();
        RefreshView();
    }

    internal static (ChatBackendAvailability Availability, string StatusMessage) ClassifyBackendInitializationFailure(
        ChatBackendState state,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(exception);

        var root = exception.GetBaseException();
        if (root is FileNotFoundException or DirectoryNotFoundException)
        {
            return (ChatBackendAvailability.Unsupported, BuildUnsupportedBackendMessage(state, root.Message));
        }

        if (root is Win32Exception win32Exception && win32Exception.NativeErrorCode == 2)
        {
            return (ChatBackendAvailability.Unsupported, BuildUnsupportedBackendMessage(state, root.Message));
        }

        var message = root.Message.Trim();
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            return (ChatBackendAvailability.Unsupported, BuildUnsupportedBackendMessage(state, message));
        }

        return (ChatBackendAvailability.Failed, BuildFailedBackendMessage(state, message));
    }

    private async Task PumpRuntimeEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var runtimeEvent in _runtimeService.StreamEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                HandleRuntimeEvent(runtimeEvent);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void TrySchedulePendingStartupThreadRestore(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_pendingStartupThreadRestoreId))
        {
            return;
        }

        var thread = FindThread(_pendingStartupThreadRestoreId);
        if (thread is null || !IsChatBackendReady(new AgentBackendId(thread.BackendId)))
        {
            return;
        }

        var threadId = _pendingStartupThreadRestoreId;
        _pendingStartupThreadRestoreId = null;
        _ = RestoreStartupThreadHistoryAsync(threadId, cancellationToken);
    }

    private async Task RestoreStartupThreadHistoryAsync(string? threadId, CancellationToken cancellationToken)
    {
        var thread = FindThread(threadId);
        if (thread is null)
        {
            return;
        }

        await EnsureThreadHistoryLoadedAsync(thread, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReloadCatalogAsync()
    {
        try
        {
            SetStatus("Refreshing project and thread catalog...", showSpinner: true);
            await TerminalHost.ImportKnownProjectsFromBackendsAsync(_agentHub, _projectCatalog, CancellationToken.None).ConfigureAwait(false);
            var projects = await _projectCatalog.LoadAsync().ConfigureAwait(false);
            var threads = await _runtimeService.ListRecoverableThreadsAsync().ConfigureAwait(false);
            PostToUi(() => ApplyRecoveredCatalogState(projects, threads));
            SetReadyStatusForCurrentSelection();
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to refresh catalog: {ex.Message}", tone: StatusTone.Error);
        }
    }

    private async Task<WorkThreadDescriptor?> CreateGlobalThreadAsync()
    {
        try
        {
            SetStatus("Creating global thread...", showSpinner: true);
            var title = ReadUiValue(() => _viewModel.DraftThreadTitle?.Trim());
            var executionOptions = BuildPreferredExecutionOptions(
                GetPreferredBackendId(),
                _catalogOptions.GlobalRoot,
                []);
            var thread = await _runtimeService.CreateGlobalThreadAsync(executionOptions, title).ConfigureAwait(false);
            await RegisterCreatedThreadAsync(thread).ConfigureAwait(false);
            ClearThreadTitleDraft();
            SetStatus($"Prompt ready · {thread.Title}", tone: StatusTone.Ready);
            return thread;
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to create global thread: {ex.Message}", tone: StatusTone.Error);
            return null;
        }
    }

    private async Task<WorkThreadDescriptor?> CreateProjectThreadAsync()
    {
        var project = GetSelectedProject();
        if (project is null)
        {
            SetStatus("Select a project before creating a project thread.", tone: StatusTone.Warning);
            return null;
        }

        try
        {
            SetStatus($"Creating thread for '{project.DisplayName}'...", showSpinner: true);
            var title = ReadUiValue(() => _viewModel.DraftThreadTitle?.Trim());
            var executionOptions = BuildPreferredExecutionOptions(
                GetPreferredBackendId(),
                project.ProjectPath,
                [project.ProjectPath]);
            var thread = await _runtimeService.CreateProjectThreadAsync(project, executionOptions, title).ConfigureAwait(false);
            await RegisterCreatedThreadAsync(thread).ConfigureAwait(false);
            ClearThreadTitleDraft();
            SetStatus($"Prompt ready · {thread.Title}", tone: StatusTone.Ready);
            return thread;
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to create project thread: {ex.Message}", tone: StatusTone.Error);
            return null;
        }
    }

    private async Task RegisterCreatedThreadAsync(WorkThreadDescriptor thread)
    {
        var threads = _threads.ToList();
        threads.RemoveAll(existing => string.Equals(existing.ThreadId, thread.ThreadId, StringComparison.OrdinalIgnoreCase));
        threads.Add(thread);
        _threads = threads
            .OrderByDescending(static item => item.LastActiveAt)
            .ToArray();

        OpenThread(thread.ThreadId);
        await EnsureThreadHistoryLoadedAsync(thread).ConfigureAwait(false);
    }

    private void OpenThread(string threadId)
    {
        var thread = FindThread(threadId);
        if (thread is null)
        {
            SetStatus($"Thread '{threadId}' was not found.", tone: StatusTone.Warning);
            return;
        }

        EnsureThreadTab(thread);
        if (!_viewState.OpenThreadIds.Contains(threadId, StringComparer.OrdinalIgnoreCase))
        {
            _viewState.OpenThreadIds.Add(threadId);
        }

        _viewState.SelectedThreadId = threadId;
        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        _selectedThreadId = threadId;
        _globalScopeSelected = thread.Kind == WorkThreadKind.GlobalThread;
        _selectedProjectId = thread.ProjectRef ?? _selectedProjectId;
        if (CanLoadThreadHistory(thread) && !IsChatBackendReady(new AgentBackendId(thread.BackendId)))
        {
            _pendingStartupThreadRestoreId = thread.ThreadId;
        }

        _ = PersistViewStateAsync();
        RefreshView();
        _ = EnsureThreadHistoryLoadedAsync(thread);
    }

    private async Task CloseSelectedThreadAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedThreadId))
        {
            return;
        }

        await CloseThreadAsync(_selectedThreadId).ConfigureAwait(false);
    }

    private async Task CloseThreadAsync(string threadId)
    {
        _viewState.OpenThreadIds.RemoveAll(id => string.Equals(id, threadId, StringComparison.OrdinalIgnoreCase));
        _threadTabs.Remove(threadId);
        if (string.Equals(_selectedThreadId, threadId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedThreadId = _viewState.OpenThreadIds.FirstOrDefault();
            _viewState.SelectedThreadId = _selectedThreadId;
        }

        _viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistViewStateAsync().ConfigureAwait(false);
        RefreshView();
    }

    private async Task EnsureThreadHistoryLoadedAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken = default)
    {
        if (!CanLoadThreadHistory(thread) ||
            !IsChatBackendReady(new AgentBackendId(thread.BackendId)))
        {
            return;
        }

        var tab = EnsureThreadTab(thread);
        var loadTask = GetOrStartThreadHistoryLoadTask(
            tab,
            thread,
            static (self, currentThread, currentTab, token) => self.LoadThreadHistoryCoreAsync(currentThread, currentTab, token),
            cancellationToken);
        await loadTask.ConfigureAwait(false);
    }

    internal static bool CanLoadThreadHistory(WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        if (thread.StartedAt is not null)
        {
            return true;
        }

        return thread.Status != WorkThreadStatus.Draft &&
               !string.IsNullOrWhiteSpace(thread.BackendSessionId);
    }

    private Task GetOrStartThreadHistoryLoadTask(
        ThreadTabState tab,
        WorkThreadDescriptor thread,
        Func<CodeAltaTerminalUi, WorkThreadDescriptor, ThreadTabState, CancellationToken, Task> loadAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(loadAsync);

        if (tab.HistoryLoaded)
        {
            return Task.CompletedTask;
        }

        if (tab.HistoryLoadTask is { } existingTask)
        {
            return existingTask.WaitAsync(cancellationToken);
        }

        var loadTask = loadAsync(this, thread, tab, cancellationToken);
        tab.HistoryLoadTask = loadTask;
        return loadTask.WaitAsync(cancellationToken);
    }

    private async Task LoadThreadHistoryCoreAsync(
        WorkThreadDescriptor thread,
        ThreadTabState tab,
        CancellationToken cancellationToken)
    {
        tab.HistoryLoading = true;
        try
        {
            SetStatus($"Loading thread '{thread.Title}'...", showSpinner: true);
            var executionOptions = BuildExecutionOptions(thread, tab);
            await _runtimeService.EnsureCoordinatorSessionAsync(thread, executionOptions, cancellationToken).ConfigureAwait(false);
            var history = await _runtimeService.GetHistoryAsync(thread.ThreadId, cancellationToken).ConfigureAwait(false);
            ResetThreadTab(tab);
            foreach (var @event in history)
            {
                HandleAgentEvent(thread, tab, @event);
            }

            tab.HistoryLoaded = true;
            SetStatus($"Prompt ready · {thread.Title}", tone: StatusTone.Ready);
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && UiLogger.IsEnabled(LogLevel.Error))
            {
                UiLogger.Error(ex, $"Failed to load history for thread {thread.ThreadId}");
            }

            RenderThreadFailure(tab, $"Failed to load history: {ex.Message}");
            SetStatus($"Failed to load '{thread.Title}': {ex.Message}", tone: StatusTone.Error);
        }
        finally
        {
            tab.HistoryLoading = false;
            tab.HistoryLoadTask = null;
        }
    }

    private async Task SendSelectedThreadPromptAsync(bool steer)
    {
        var thread = GetSelectedThread();
        if (thread is null)
        {
            if (steer)
            {
                SetStatus("Start the thread before steering it.", tone: StatusTone.Warning);
                return;
            }

            if (TrySetPromptUnavailableStatus())
            {
                return;
            }

            thread = _globalScopeSelected
                ? await CreateGlobalThreadAsync().ConfigureAwait(false)
                : await CreateProjectThreadAsync().ConfigureAwait(false);
            if (thread is null)
            {
                return;
            }
        }
        else if (!IsChatBackendReady(new AgentBackendId(thread.BackendId)))
        {
            SetReadyStatusForCurrentSelection();
            return;
        }

        var prompt = ReadUiValue(() => _threadInput?.Text?.Trim());
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        var tab = EnsureThreadTab(thread);
        await EnsureThreadHistoryLoadedAsync(thread).ConfigureAwait(false);
        ClearThreadInput();
        try
        {
            SetStatus($"Running '{thread.Title}'...", showSpinner: true);
            var executionOptions = BuildExecutionOptions(thread, tab);
            if (steer)
            {
                _ = await _runtimeService.SteerAsync(
                        thread,
                        executionOptions,
                        new AgentSteerOptions { Input = AgentInput.Text(prompt) })
                    .ConfigureAwait(false);
            }
            else
            {
                _ = await _runtimeService.SendAsync(
                        thread,
                        executionOptions,
                        new AgentSendOptions { Input = AgentInput.Text(prompt) })
                    .ConfigureAwait(false);
            }

            thread.MarkStarted(DateTimeOffset.UtcNow);
            tab.HistoryLoaded = true;
            RefreshView();
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && UiLogger.IsEnabled(LogLevel.Error))
            {
                UiLogger.Error(ex, $"Failed to send prompt for thread {thread.ThreadId}");
            }

            RenderThreadFailure(tab, $"Failed to send prompt: {ex.Message}");
            SetStatus($"Failed to send prompt: {ex.Message}", tone: StatusTone.Error);
        }
    }

    private async Task DelegateSelectedThreadAsync()
    {
        var thread = GetSelectedThread();
        if (thread is null)
        {
            SetStatus("Open a thread before delegating work.", tone: StatusTone.Warning);
            return;
        }

        if (!IsChatBackendReady(new AgentBackendId(thread.BackendId)))
        {
            SetReadyStatusForCurrentSelection();
            return;
        }

        var tab = EnsureThreadTab(thread);
        var prompt = ReadUiValue(() => _threadInput?.Text?.Trim());
        if (string.IsNullOrWhiteSpace(prompt))
        {
            SetStatus("Enter delegation instructions before creating an internal thread.", tone: StatusTone.Warning);
            return;
        }

        var targetProject = GetProjectById(thread.ProjectRef ?? _selectedProjectId);
        if (targetProject is null)
        {
            SetStatus("Select a project before delegating internal work.", tone: StatusTone.Warning);
            return;
        }

        try
        {
            SetStatus($"Delegating internal work from '{thread.Title}'...", showSpinner: true);
            var executionOptions = new WorkThreadExecutionOptions
            {
                BackendId = tab.BackendId,
                WorkingDirectory = targetProject.ProjectPath,
                ProjectRoots = [targetProject.ProjectPath],
                Model = tab.ModelId,
                ReasoningEffort = tab.ReasoningEffort,
                OnPermissionRequest = (request, cancellationToken) => HandleThreadPermissionRequestAsync(CreateTransientThreadKey(tab.BackendId, targetProject.ProjectPath), request, cancellationToken),
                OnUserInputRequest = (request, cancellationToken) => HandleThreadUserInputRequestAsync(CreateTransientThreadKey(tab.BackendId, targetProject.ProjectPath), request, cancellationToken),
            };

            var child = await _runtimeService.CreateInternalThreadAsync(
                thread,
                targetProject,
                executionOptions,
                title: SummarizeThreadContent(prompt),
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            _threads = _threads
                .Where(existing => !string.Equals(existing.ThreadId, child.ThreadId, StringComparison.OrdinalIgnoreCase))
                .Append(child)
                .OrderByDescending(static item => item.LastActiveAt)
                .ToArray();

            EnsureThreadTab(child);
            if (!_viewState.OpenThreadIds.Contains(child.ThreadId, StringComparer.OrdinalIgnoreCase))
            {
                _viewState.OpenThreadIds.Add(child.ThreadId);
                _viewState.UpdatedAt = DateTimeOffset.UtcNow;
            }

            var childTab = EnsureThreadTab(child);
            childTab.BackendId = tab.BackendId;
            childTab.ModelId = tab.ModelId;
            childTab.ReasoningEffort = tab.ReasoningEffort;

            _ = await _runtimeService.SendAsync(
                    child,
                    new WorkThreadExecutionOptions
                    {
                        BackendId = tab.BackendId,
                        WorkingDirectory = targetProject.ProjectPath,
                        ProjectRoots = [targetProject.ProjectPath],
                        Model = tab.ModelId,
                        ReasoningEffort = tab.ReasoningEffort,
                        OnPermissionRequest = (request, cancellationToken) => HandleThreadPermissionRequestAsync(child.ThreadId, request, cancellationToken),
                        OnUserInputRequest = (request, cancellationToken) => HandleThreadUserInputRequestAsync(child.ThreadId, request, cancellationToken),
                    },
                    new AgentSendOptions
                    {
                        Input = AgentInput.Text(
                            $"Delegated from thread '{thread.Title}' ({thread.ThreadId}): {prompt}")
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);

            ClearThreadInput();
            SetStatus($"Delegation started · {child.Title}", tone: StatusTone.Ready);
            await PersistViewStateAsync().ConfigureAwait(false);
            RefreshView();
        }
        catch (Exception ex)
        {
            UiLogger.Error(ex, "Failed to delegate internal thread.");
            SetStatus($"Failed to delegate internal thread: {ex.Message}", tone: StatusTone.Error);
        }
    }

    private async Task AbortSelectedThreadAsync()
    {
        var thread = GetSelectedThread();
        if (thread is null)
        {
            return;
        }

        try
        {
            await _runtimeService.AbortAsync(thread.ThreadId).ConfigureAwait(false);
            SetStatus($"Stopped · {thread.Title}", tone: StatusTone.Warning);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to abort '{thread.Title}': {ex.Message}", tone: StatusTone.Error);
        }
    }

    private void HandleRuntimeEvent(WorkThreadRuntimeEvent runtimeEvent)
    {
        var thread = FindThread(runtimeEvent.ThreadId);
        if (thread is null)
        {
            return;
        }

        switch (runtimeEvent)
        {
            case WorkThreadAgentEvent agentEvent:
                UpdateThreadFromAgentEvent(thread, agentEvent.Event);
                if (_threadTabs.TryGetValue(thread.ThreadId, out var tab))
                {
                    TryRenderThreadInteraction(tab, () => HandleAgentEvent(thread, tab, agentEvent.Event), "agent event");
                }

                break;

            case WorkThreadHostEvent hostEvent:
                UpdateThreadSummary(thread, hostEvent.Message, hostEvent.Timestamp);
                if (_threadTabs.TryGetValue(thread.ThreadId, out var hostTab))
                {
                    TryRenderThreadInteraction(
                        hostTab,
                        () => UpsertThreadStatus(
                            hostTab,
                            dictionary: null,
                            key: null,
                            hostEvent.Timestamp,
                            markdown: hostEvent.Message,
                            tone: ChatTimelineTone.Notice,
                            headerOverride: "Notice",
                            headerSecondary: GetSessionUpdateHeader(hostEvent.Kind)),
                        "host event");
                }

                break;
        }

        RefreshView();
    }

    private void HandleAgentEvent(WorkThreadDescriptor thread, ThreadTabState tab, AgentEvent @event)
    {
        switch (@event)
        {
            case AgentContentDeltaEvent delta:
                if (TryHandleToolTimelineContent(tab, delta))
                {
                    break;
                }

                if (!ShouldDisplayContentDelta(delta))
                {
                    break;
                }

                AppendThreadContent(tab, delta);
                break;

            case AgentContentCompletedEvent completed:
                if (TryHandleToolTimelineContent(tab, completed))
                {
                    break;
                }

                if (ShouldSkipEmptyAssistantCompletion(tab, completed))
                {
                    break;
                }

                if (!ShouldDisplayCompletedContent(completed))
                {
                    break;
                }

                FinalizeThreadContent(tab, completed);
                if (completed.Kind == AgentContentKind.Assistant && !string.IsNullOrWhiteSpace(completed.Content))
                {
                    thread.LatestSummary = SummarizeThreadContent(completed.Content);
                }

                break;

            case AgentPlanSnapshotEvent planEvent:
                UpsertThreadStatus(
                    tab,
                    tab.PlanStates,
                    "plan",
                    planEvent.Timestamp,
                    FormatChatPlanMarkdown(planEvent.Snapshot),
                    ChatTimelineTone.Notice,
                    headerOverride: "Plan");
                break;

            case AgentActivityEvent activity:
                if (TryHandleToolTimelineActivity(tab, activity))
                {
                    break;
                }

                if (!ShouldDisplayActivity(activity))
                {
                    break;
                }

                UpsertThreadStatus(
                    tab,
                    tab.ActivityStates,
                    activity.ActivityId,
                    activity.Timestamp,
                    FormatChatActivityMarkdown(activity),
                    ChatTimelineTone.Activity,
                    headerOverride: GetActivityHeadline(activity.Kind, activity.Phase));
                break;

            case AgentRawEvent raw:
                if (!ShouldDisplayRawEvent(raw))
                {
                    break;
                }

                UpsertThreadStatus(
                    tab,
                    dictionary: null,
                    key: null,
                    raw.Timestamp,
                    markdown: FormatChatRawEventMarkdown(raw),
                    tone: ChatTimelineTone.Activity,
                    headerOverride: "Raw Event");
                break;

            case AgentPermissionRequest permissionRequest:
                if (!ShouldDisplayPermissionRequest(GetAutoApproveEnabled()))
                {
                    break;
                }

                tab.PermissionRequests[permissionRequest.InteractionId] = permissionRequest;
                UpsertThreadInteraction(
                    tab,
                    permissionRequest.InteractionId,
                    permissionRequest.Timestamp,
                    FormatChatPermissionRequestMarkdown(permissionRequest),
                    null,
                    ChatTimelineTone.Interaction,
                    "Action Required",
                    "Permission Request");
                break;

            case AgentUserInputRequest userInputRequest:
                var autoApproveEnabled = GetAutoApproveEnabled();
                tab.UserInputRequests[userInputRequest.InteractionId] = userInputRequest;
                UpsertThreadInteraction(
                    tab,
                    userInputRequest.InteractionId,
                    userInputRequest.Timestamp,
                    FormatChatUserInputRequestMarkdown(userInputRequest, autoApproveEnabled),
                    null,
                    ChatTimelineTone.Interaction,
                    "Action Required",
                    "User Input Request");
                break;

            case AgentInteractionEvent interaction:
                if (!ShouldDisplayInteraction(interaction, GetAutoApproveEnabled()))
                {
                    tab.PermissionRequests.Remove(interaction.InteractionId);
                    tab.UserInputRequests.Remove(interaction.InteractionId);
                    break;
                }

                UpsertThreadInteraction(
                    tab,
                    interaction.InteractionId,
                    interaction.Timestamp,
                    null,
                    FormatChatInteractionResolutionMarkdown(interaction, includeHeading: false),
                    ChatTimelineTone.Interaction);
                tab.PermissionRequests.Remove(interaction.InteractionId);
                tab.UserInputRequests.Remove(interaction.InteractionId);
                break;

            case AgentSessionUpdateEvent update:
                if (update.Kind == AgentSessionUpdateKind.Idle)
                {
                    SetStatus($"Prompt ready · {thread.Title}", tone: StatusTone.Ready);
                    break;
                }

                if (!ShouldDisplaySessionUpdate(update))
                {
                    break;
                }

                UpsertThreadStatus(
                    tab,
                    dictionary: null,
                    key: null,
                    update.Timestamp,
                    markdown: FormatChatSessionUpdateMarkdown(update),
                    tone: update.Kind == AgentSessionUpdateKind.Warning ? ChatTimelineTone.Interaction : ChatTimelineTone.Notice,
                    headerOverride: "Notice",
                    headerSecondary: GetSessionUpdateHeader(update.Kind));
                if (!string.IsNullOrWhiteSpace(update.Message))
                {
                    thread.LatestSummary = SummarizeThreadContent(update.Message);
                }

                break;

            case AgentErrorEvent error:
                RenderThreadError(tab, error.Message, error.Timestamp);
                thread.LatestSummary = SummarizeThreadContent(error.Message);
                break;
        }
    }

    private void AppendThreadContent(ThreadTabState tab, AgentContentDeltaEvent delta)
    {
        if (string.IsNullOrEmpty(delta.Delta))
        {
            return;
        }

        var state = GetOrCreateThreadContentState(tab, delta.Kind, delta.ContentId, delta.Timestamp);
        state.Buffer.Append(delta.Delta);
        var content = state.Buffer.ToString();
        var markdown = FormatChatContentMarkdown(delta.Kind, content);
        var headerSecondary = GetChatContentHeaderSecondary(delta.Kind, content);
        PostToUi(() =>
        {
            ApplyChatCardHeader(state.HeaderText, GetContentTone(delta.Kind), GetContentHeader(delta.Kind), headerSecondary);
            state.Markdown.Markdown = markdown;
            tab.Flow.ScrollToTail();
        });
    }

    private void FinalizeThreadContent(ThreadTabState tab, AgentContentCompletedEvent completed)
    {
        var state = GetOrCreateThreadContentState(tab, completed.Kind, completed.ContentId, completed.Timestamp);
        var content = ResolveCompletedThreadContent(completed.Content, state.Buffer);
        state.Buffer.Clear();
        state.Buffer.Append(content);
        var markdown = FormatChatContentMarkdown(completed.Kind, content);
        var headerSecondary = GetChatContentHeaderSecondary(completed.Kind, content);
        PostToUi(() =>
        {
            ApplyChatCardHeader(state.HeaderText, GetContentTone(completed.Kind), GetContentHeader(completed.Kind), headerSecondary);
            state.Markdown.Markdown = markdown;
            tab.Flow.ScrollToTail();
        });
    }

    internal static string ResolveCompletedThreadContent(string completedContent, System.Text.StringBuilder bufferedContent)
    {
        ArgumentNullException.ThrowIfNull(bufferedContent);

        return completedContent.Length > 0
            ? completedContent
            : bufferedContent.ToString();
    }

    private static bool ShouldSkipEmptyAssistantCompletion(ThreadTabState tab, AgentContentCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(completed);

        if (completed.Kind != AgentContentKind.Assistant || !string.IsNullOrWhiteSpace(completed.Content))
        {
            return false;
        }

        var key = CreateChatContentKey(completed.Kind, completed.ContentId);
        if (tab.ContentStates.TryGetValue(key, out var existing))
        {
            return existing.Buffer.Length == 0;
        }

        return true;
    }

    private ChatContentState GetOrCreateThreadContentState(ThreadTabState tab, AgentContentKind kind, string contentId, DateTimeOffset timestamp)
    {
        var key = CreateChatContentKey(kind, contentId);
        if (tab.ContentStates.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (kind == AgentContentKind.Assistant && tab.PendingAssistant is { ContentId: null } pending)
        {
            pending.ContentId = contentId;
            ApplyChatCardTimestamp(pending.TimestampText, timestamp);
            tab.PendingAssistant = null;
            var pendingState = new ChatContentState(pending.Item, pending.Markdown, pending.TimestampText, pending.HeaderText, pending.Buffer, kind);
            tab.ContentStates[key] = pendingState;
            return pendingState;
        }

        var entry = CreateChatMarkdownItem(
            FormatChatContentMarkdown(kind, string.Empty),
            GetContentTone(kind),
            headerOverride: GetContentHeader(kind),
            headerSecondary: GetChatContentHeaderSecondary(kind, string.Empty));
        ApplyChatCardTimestamp(entry.TimestampText, timestamp);
        var state = new ChatContentState(entry.Item, entry.Markdown, entry.TimestampText, entry.HeaderText, new System.Text.StringBuilder(), kind);
        tab.ContentStates[key] = state;
        if (kind == AgentContentKind.User)
        {
            foreach (var item in BuildUserPromptTimelineItems(entry.Item, tab.HasSeenUserPrompt))
            {
                AppendThreadTimelineItem(tab, item);
            }

            tab.HasSeenUserPrompt = true;
            return state;
        }

        AppendThreadTimelineItem(tab, entry.Item);
        return state;
    }

    private void UpsertThreadStatus(
        ThreadTabState tab,
        Dictionary<string, ChatStatusState>? dictionary,
        string? key,
        DateTimeOffset timestamp,
        string markdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null)
    {
        if (dictionary is null || key is null)
        {
            var state = CreateChatStatusState(markdown, tone, timestamp, headerOverride, headerSecondary);
            AppendThreadTimelineItem(tab, state.Item);
            return;
        }

        if (!dictionary.TryGetValue(key, out var stateEntry))
        {
            stateEntry = CreateChatStatusState(markdown, tone, timestamp, headerOverride, headerSecondary);
            dictionary[key] = stateEntry;
            AppendThreadTimelineItem(tab, stateEntry.Item);
        }

        stateEntry.BaseMarkdown = markdown;
        PostToUi(() =>
        {
            ApplyChatCardTimestamp(stateEntry.TimestampText, timestamp);
            stateEntry.Markdown.Markdown = stateEntry.MarkdownValue;
            tab.Flow.ScrollToTail();
        });
    }

    private void UpsertThreadInteraction(
        ThreadTabState tab,
        string interactionId,
        DateTimeOffset timestamp,
        string? baseMarkdown,
        string? statusMarkdown,
        ChatTimelineTone tone,
        string? headerOverride = null,
        string? headerSecondary = null)
    {
        if (!tab.InteractionStates.TryGetValue(interactionId, out var state))
        {
            state = CreateChatStatusState(baseMarkdown ?? statusMarkdown ?? string.Empty, tone, timestamp, headerOverride, headerSecondary);
            tab.InteractionStates[interactionId] = state;
            AppendThreadTimelineItem(tab, state.Item);
        }

        if (!string.IsNullOrWhiteSpace(baseMarkdown))
        {
            state.BaseMarkdown = baseMarkdown;
        }

        if (!string.IsNullOrWhiteSpace(statusMarkdown))
        {
            state.StatusMarkdown = statusMarkdown;
        }

        PostToUi(() =>
        {
            ApplyChatCardTimestamp(state.TimestampText, timestamp);
            state.Markdown.Markdown = state.MarkdownValue;
            tab.Flow.ScrollToTail();
        });
    }

    private static ChatStatusState CreateChatStatusState(
        string markdown,
        ChatTimelineTone tone,
        DateTimeOffset timestamp,
        string? headerOverride = null,
        string? headerSecondary = null)
    {
        var entry = CreateChatMarkdownItem(markdown, tone, headerOverride, headerSecondary);
        ApplyChatCardTimestamp(entry.TimestampText, timestamp);
        return new ChatStatusState(entry.Item, entry.Markdown, entry.TimestampText)
        {
            BaseMarkdown = markdown,
        };
    }

    private static void ReplaceEmptyPendingAssistantPlaceholder(ThreadTabState tab)
    {
        var pendingAssistant = tab.PendingAssistant;
        if (pendingAssistant is null || pendingAssistant.Buffer.Length > 0)
        {
            return;
        }

        RunOnUiThread(
            static state =>
            {
                state.pendingAssistant.Markdown.Markdown = "_No assistant content was returned._";
                return 0;
            },
            new { pendingAssistant });
        tab.PendingAssistant = null;
    }

    private static void RenderThreadError(ThreadTabState tab, string message, DateTimeOffset timestamp)
    {
        var pendingAssistant = tab.PendingAssistant;
        if (pendingAssistant is not null)
        {
            pendingAssistant.Buffer.Append(message);
            RunOnUiThread(
                static state =>
                {
                    state.pendingAssistant.Markdown.Markdown = state.message;
                    ApplyChatCardTimestamp(state.pendingAssistant.TimestampText, state.timestamp);
                    return 0;
                },
                (pendingAssistant: pendingAssistant, message, timestamp));
            tab.PendingAssistant = null;
            return;
        }

        var entry = CreateChatMarkdownItem(message, ChatTimelineTone.Interaction, headerOverride: "Error");
        ApplyChatCardTimestamp(entry.TimestampText, timestamp);
        RunOnUiThread(
            static state =>
            {
                state.tab.Flow.Items.Add(state.entry.Item);
                return 0;
            },
            (tab: tab, entry));
    }

    private static void RenderThreadFailure(ThreadTabState tab, string markdown)
    {
        var pendingAssistant = tab.PendingAssistant;
        if (pendingAssistant is not null)
        {
            pendingAssistant.Buffer.Append(markdown);
            RunOnUiThread(
                static state =>
                {
                    state.pendingAssistant.Markdown.Markdown = state.markdown;
                    return 0;
                },
                (pendingAssistant: pendingAssistant, markdown));
            tab.PendingAssistant = null;
            return;
        }

        var entry = CreateChatMarkdownItem(markdown, ChatTimelineTone.Interaction, headerOverride: "Error");
        RunOnUiThread(
            static state =>
            {
                state.tab.Flow.Items.Add(state.entry.Item);
                return 0;
            },
            (tab: tab, entry));
    }

    private void TryRenderThreadInteraction(ThreadTabState tab, Action action, string context)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && UiLogger.IsEnabled(LogLevel.Error))
            {
                UiLogger.Error(ex, $"Failed to render thread {context}");
            }

            SetStatus($"Failed to render thread {context}: {ex.Message}", tone: StatusTone.Error);
            tab.PendingAssistant = null;
        }
    }

    private async Task<AgentPermissionDecision> HandleThreadPermissionRequestAsync(
        string threadId,
        AgentPermissionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var autoApproveEnabled = GetAutoApproveEnabled();
        var decision = autoApproveEnabled
            ? new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)
            : new AgentPermissionDecision(AgentPermissionDecisionKind.Deny);

        if (ShouldDisplayPermissionRequest(autoApproveEnabled) && _threadTabs.TryGetValue(threadId, out var tab))
        {
            TryRenderThreadInteraction(
                tab,
                () =>
                {
                    UpsertThreadInteraction(
                        tab,
                        request.InteractionId,
                        request.Timestamp,
                        FormatChatPermissionRequestMarkdown(request),
                        FormatChatImmediatePermissionDecisionMarkdown(decision, autoApproveEnabled),
                        ChatTimelineTone.Interaction,
                        "Action Required",
                        "Permission Request");
                },
                "permission request");
        }

        return decision;
    }

    private async Task<AgentUserInputResponse> HandleThreadUserInputRequestAsync(
        string threadId,
        AgentUserInputRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var autoApproveEnabled = GetAutoApproveEnabled();
        var response = CreateChatUserInputResponse(request, autoApproveEnabled);
        if (_threadTabs.TryGetValue(threadId, out var tab))
        {
            TryRenderThreadInteraction(
                tab,
                () =>
                {
                    UpsertThreadInteraction(
                        tab,
                        request.InteractionId,
                        request.Timestamp,
                        FormatChatUserInputRequestMarkdown(request, autoApproveEnabled),
                        FormatChatImmediateUserInputResponseMarkdown(response, autoApproveEnabled),
                        ChatTimelineTone.Interaction,
                        "Action Required",
                        "User Input Request");
                },
                "user input request");
        }

        return response;
    }

    private WorkThreadExecutionOptions BuildExecutionOptions(WorkThreadDescriptor thread, ThreadTabState tab)
    {
        var workingDirectory = ResolveWorkingDirectory(thread);
        var projectRoots = ResolveProjectRoots(thread);
        return new WorkThreadExecutionOptions
        {
            BackendId = new AgentBackendId(thread.BackendId),
            WorkingDirectory = workingDirectory,
            ProjectRoots = projectRoots,
            Model = tab.ModelId,
            ReasoningEffort = tab.ReasoningEffort,
            OnPermissionRequest = (request, cancellationToken) => HandleThreadPermissionRequestAsync(thread.ThreadId, request, cancellationToken),
            OnUserInputRequest = (request, cancellationToken) => HandleThreadUserInputRequestAsync(thread.ThreadId, request, cancellationToken),
        };
    }

    private WorkThreadExecutionOptions BuildPreferredExecutionOptions(
        AgentBackendId backendId,
        string workingDirectory,
        IReadOnlyList<string> projectRoots)
    {
        var backendState = _chatBackendStates[backendId.Value];
        var model = ReadUiValue(
            () =>
            {
                if (_chatBackendSelect is null || _chatModelSelect is null)
                {
                    return backendState.SelectedModelId;
                }

                var backendOptions = BuildChatBackendOptions();
                if ((uint)_chatBackendSelect.SelectedIndex < (uint)backendOptions.Count &&
                    string.Equals(backendOptions[_chatBackendSelect.SelectedIndex].BackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase))
                {
                    var modelOptions = BuildChatModelOptions(backendState);
                    if ((uint)_chatModelSelect.SelectedIndex < (uint)modelOptions.Count)
                    {
                        return modelOptions[_chatModelSelect.SelectedIndex].ModelId;
                    }
                }

                return backendState.SelectedModelId;
            });

        var reasoning = ReadUiValue(
            () =>
            {
                if (_chatBackendSelect is null || _chatReasoningSelect is null)
                {
                    return backendState.SelectedReasoningEffort;
                }

                var backendOptions = BuildChatBackendOptions();
                if ((uint)_chatBackendSelect.SelectedIndex < (uint)backendOptions.Count &&
                    string.Equals(backendOptions[_chatBackendSelect.SelectedIndex].BackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase))
                {
                    var selectedModel = backendState.Models.FirstOrDefault(candidate => string.Equals(candidate.Id, model, StringComparison.Ordinal));
                    var reasoningOptions = BuildChatReasoningOptions(selectedModel);
                    if ((uint)_chatReasoningSelect.SelectedIndex < (uint)reasoningOptions.Count)
                    {
                        return reasoningOptions[_chatReasoningSelect.SelectedIndex].Effort;
                    }
                }

                return backendState.SelectedReasoningEffort;
            });

        return new WorkThreadExecutionOptions
        {
            BackendId = backendId,
            WorkingDirectory = workingDirectory,
            ProjectRoots = projectRoots,
            Model = model,
            ReasoningEffort = reasoning,
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            OnUserInputRequest = (request, cancellationToken) => HandleThreadUserInputRequestAsync(CreateTransientThreadKey(backendId, workingDirectory), request, cancellationToken),
        };
    }

    private static string CreateTransientThreadKey(AgentBackendId backendId, string workingDirectory)
        => $"{backendId.Value}:{workingDirectory}";

    private string ResolveWorkingDirectory(WorkThreadDescriptor thread)
    {
        return thread.Kind switch
        {
            WorkThreadKind.GlobalThread => _catalogOptions.GlobalRoot,
            WorkThreadKind.ProjectThread or WorkThreadKind.InternalThread when GetProjectById(thread.ProjectRef) is { } project => project.ProjectPath,
            _ => thread.WorkingDirectory,
        };
    }

    private IReadOnlyList<string> ResolveProjectRoots(WorkThreadDescriptor thread)
    {
        if (GetProjectById(thread.ProjectRef) is { } project)
        {
            return [project.ProjectPath];
        }

        return [];
    }

    private void UpdateThreadFromAgentEvent(WorkThreadDescriptor thread, AgentEvent @event)
    {
        thread.UpdatedAt = @event.Timestamp;
        thread.LastActiveAt = @event.Timestamp;

        switch (@event)
        {
            case AgentContentCompletedEvent { Kind: AgentContentKind.Assistant } completed when !string.IsNullOrWhiteSpace(completed.Content):
                thread.LatestSummary = SummarizeThreadContent(completed.Content);
                break;
            case AgentSessionUpdateEvent update when !string.IsNullOrWhiteSpace(update.Message):
                thread.LatestSummary = SummarizeThreadContent(update.Message);
                break;
            case AgentErrorEvent error when !string.IsNullOrWhiteSpace(error.Message):
                thread.LatestSummary = SummarizeThreadContent(error.Message);
                break;
        }
    }

    private void UpdateThreadSummary(WorkThreadDescriptor thread, string message, DateTimeOffset timestamp)
    {
        thread.UpdatedAt = timestamp;
        thread.LastActiveAt = timestamp;
        thread.LatestSummary = SummarizeThreadContent(message);
    }

    private static string SummarizeThreadContent(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalized.Length <= 120)
        {
            return normalized;
        }

        return normalized[..117].TrimEnd() + "...";
    }

    private ThreadTabState EnsureThreadTab(WorkThreadDescriptor thread)
    {
        if (_threadTabs.TryGetValue(thread.ThreadId, out var existing))
        {
            existing.Thread = thread;
            return existing;
        }

        var flow = RunOnUiThread(
            static () => new DocumentFlow
            {
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
                ItemPadding = new Thickness(1, 0, 0, 0),
                ItemSpacing = 0,
            });

        var state = new ThreadTabState(thread, flow)
        {
            BackendId = new AgentBackendId(thread.BackendId),
        };

        if (_chatBackendStates.TryGetValue(thread.BackendId, out var backendState))
        {
            state.ModelId = backendState.SelectedModelId;
            state.ReasoningEffort = backendState.SelectedReasoningEffort;
        }

        _threadTabs[thread.ThreadId] = state;
        return state;
    }

    private void ResetThreadTab(ThreadTabState tab)
    {
        CloseToolCallDialogs(tab);
        PostToUi(() => tab.Flow.Items.Clear());
        tab.ContentStates.Clear();
        tab.ActivityStates.Clear();
        tab.InteractionStates.Clear();
        tab.PlanStates.Clear();
        tab.ToolCallStates.Clear();
        tab.PermissionRequests.Clear();
        tab.UserInputRequests.Clear();
        tab.PendingAssistant = null;
        tab.ActiveToolCallGroup = null;
        tab.HasSeenUserPrompt = false;
    }

    private void AppendThreadTimelineItem(ThreadTabState tab, DocumentFlowItem item)
    {
        tab.ActiveToolCallGroup = null;
        PostToUi(() =>
        {
            tab.Flow.Items.Add(item);
            tab.Flow.ScrollToTail();
        });
    }

    private ProjectDescriptor? GetSelectedProject()
        => GetProjectById(_selectedProjectId);

    private ProjectDescriptor? GetProjectById(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        return _projects.FirstOrDefault(project => string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase));
    }

    private WorkThreadDescriptor? GetSelectedThread()
        => FindThread(_selectedThreadId);

    private WorkThreadDescriptor? FindThread(string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        return _threads.FirstOrDefault(thread => string.Equals(thread.ThreadId, threadId, StringComparison.OrdinalIgnoreCase));
    }

    private WorkThreadDescriptor[] GetThreadsForProject(string projectId, bool includeInternal)
    {
        return _threads
            .Where(thread => string.Equals(thread.ProjectRef, projectId, StringComparison.OrdinalIgnoreCase))
            .Where(thread => includeInternal || thread.Kind == WorkThreadKind.ProjectThread)
            .OrderByDescending(static thread => thread.LastActiveAt)
            .ToArray();
    }

    internal static string BuildThreadScopeSummary(
        WorkThreadDescriptor thread,
        IReadOnlyList<ProjectDescriptor> projects,
        string globalRoot)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(projects);

        return thread.Kind switch
        {
            WorkThreadKind.GlobalThread => $"Global thread · {globalRoot}",
            WorkThreadKind.ProjectThread when projects.FirstOrDefault(project => string.Equals(project.Id, thread.ProjectRef, StringComparison.OrdinalIgnoreCase)) is { } project
                => $"{project.DisplayName} · {project.ProjectPath}",
            WorkThreadKind.InternalThread when projects.FirstOrDefault(project => string.Equals(project.Id, thread.ProjectRef, StringComparison.OrdinalIgnoreCase)) is { } internalProject
                => $"Internal · {internalProject.DisplayName}",
            WorkThreadKind.InternalThread => "Internal delegated thread",
            _ => thread.WorkingDirectory,
        };
    }

    internal static IReadOnlyList<WorkThreadDescriptor> FilterThreadsForProject(
        IReadOnlyList<WorkThreadDescriptor> threads,
        string? projectId,
        bool includeInternal)
    {
        ArgumentNullException.ThrowIfNull(threads);

        return threads
            .Where(thread => string.Equals(thread.ProjectRef, projectId, StringComparison.OrdinalIgnoreCase))
            .Where(thread => includeInternal || thread.Kind == WorkThreadKind.ProjectThread)
            .OrderByDescending(static thread => thread.LastActiveAt)
            .ToArray();
    }

}
