using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.Logging;
using XenoAtom.Terminal.UI.Controls;

internal sealed class ThreadCommandCoordinator
{
    private readonly WorkThreadRuntimeService _runtimeService;
    private readonly CatalogOptions _catalogOptions;
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates;
    private readonly Func<IUiDispatcher> _getUiDispatcher;
    private readonly Func<ChatPromptEditor?> _getThreadInput;
    private readonly Func<Select<ChatBackendOption>?> _getChatBackendSelect;
    private readonly Func<Select<ChatModelOption>?> _getChatModelSelect;
    private readonly Func<Select<ChatReasoningOption>?> _getChatReasoningSelect;
    private readonly Func<WorkThreadDescriptor?> _getSelectedThread;
    private readonly Func<ProjectDescriptor?> _getSelectedProject;
    private readonly Func<string?, ProjectDescriptor?> _getProjectById;
    private readonly Func<WorkThreadDescriptor, OpenThreadState> _ensureThreadTab;
    private readonly Func<string, OpenThreadState?> _findOpenThread;
    private readonly Func<WorkThreadDescriptor, CancellationToken, Task> _ensureThreadHistoryLoadedAsync;
    private readonly Func<bool> _getGlobalScopeSelected;
    private readonly Func<string?> _getSelectedProjectId;
    private readonly Func<AgentBackendId> _getPreferredBackendId;
    private readonly Func<bool> _trySetPromptUnavailableStatus;
    private readonly Func<Task<WorkThreadDescriptor?>> _createGlobalThreadAsync;
    private readonly Func<Task<WorkThreadDescriptor?>> _createProjectThreadAsync;
    private readonly Func<WorkThreadDescriptor, OpenThreadState, OpenThreadState> _registerDelegatedThread;
    private readonly Func<Task> _persistViewStateAsync;
    private readonly Func<bool> _getAutoApproveEnabled;
    private readonly Action<string, string?, AgentReasoningEffort?, bool, bool> _rememberThreadPreference;
    private readonly Action _setReadyStatusForCurrentSelection;
    private readonly Action _clearThreadInput;
    private readonly Action _refreshHeaderAndThreadWorkspace;
    private readonly Action _refreshCatalogAndThreadWorkspace;
    private readonly Action<string, bool, CodeAltaApp.StatusTone> _setShellStatus;
    private readonly Action<OpenThreadState, string, bool, CodeAltaApp.StatusTone> _setThreadStatus;
    private readonly Action<OpenThreadState, Action, string> _tryRenderInteraction;

    public ThreadCommandCoordinator(
        WorkThreadRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        Dictionary<string, ChatBackendState> chatBackendStates,
        Func<IUiDispatcher> getUiDispatcher,
        Func<ChatPromptEditor?> getThreadInput,
        Func<Select<ChatBackendOption>?> getChatBackendSelect,
        Func<Select<ChatModelOption>?> getChatModelSelect,
        Func<Select<ChatReasoningOption>?> getChatReasoningSelect,
        Func<WorkThreadDescriptor?> getSelectedThread,
        Func<ProjectDescriptor?> getSelectedProject,
        Func<string?, ProjectDescriptor?> getProjectById,
        Func<WorkThreadDescriptor, OpenThreadState> ensureThreadTab,
        Func<string, OpenThreadState?> findOpenThread,
        Func<WorkThreadDescriptor, CancellationToken, Task> ensureThreadHistoryLoadedAsync,
        Func<bool> getGlobalScopeSelected,
        Func<string?> getSelectedProjectId,
        Func<AgentBackendId> getPreferredBackendId,
        Func<bool> trySetPromptUnavailableStatus,
        Func<Task<WorkThreadDescriptor?>> createGlobalThreadAsync,
        Func<Task<WorkThreadDescriptor?>> createProjectThreadAsync,
        Func<WorkThreadDescriptor, OpenThreadState, OpenThreadState> registerDelegatedThread,
        Func<Task> persistViewStateAsync,
        Func<bool> getAutoApproveEnabled,
        Action<string, string?, AgentReasoningEffort?, bool, bool> rememberThreadPreference,
        Action setReadyStatusForCurrentSelection,
        Action clearThreadInput,
        Action refreshHeaderAndThreadWorkspace,
        Action refreshCatalogAndThreadWorkspace,
        Action<string, bool, CodeAltaApp.StatusTone> setShellStatus,
        Action<OpenThreadState, string, bool, CodeAltaApp.StatusTone> setThreadStatus,
        Action<OpenThreadState, Action, string> tryRenderInteraction)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(getUiDispatcher);
        ArgumentNullException.ThrowIfNull(getThreadInput);
        ArgumentNullException.ThrowIfNull(getChatBackendSelect);
        ArgumentNullException.ThrowIfNull(getChatModelSelect);
        ArgumentNullException.ThrowIfNull(getChatReasoningSelect);
        ArgumentNullException.ThrowIfNull(getSelectedThread);
        ArgumentNullException.ThrowIfNull(getSelectedProject);
        ArgumentNullException.ThrowIfNull(getProjectById);
        ArgumentNullException.ThrowIfNull(ensureThreadTab);
        ArgumentNullException.ThrowIfNull(findOpenThread);
        ArgumentNullException.ThrowIfNull(ensureThreadHistoryLoadedAsync);
        ArgumentNullException.ThrowIfNull(getGlobalScopeSelected);
        ArgumentNullException.ThrowIfNull(getSelectedProjectId);
        ArgumentNullException.ThrowIfNull(getPreferredBackendId);
        ArgumentNullException.ThrowIfNull(trySetPromptUnavailableStatus);
        ArgumentNullException.ThrowIfNull(createGlobalThreadAsync);
        ArgumentNullException.ThrowIfNull(createProjectThreadAsync);
        ArgumentNullException.ThrowIfNull(registerDelegatedThread);
        ArgumentNullException.ThrowIfNull(persistViewStateAsync);
        ArgumentNullException.ThrowIfNull(getAutoApproveEnabled);
        ArgumentNullException.ThrowIfNull(rememberThreadPreference);
        ArgumentNullException.ThrowIfNull(setReadyStatusForCurrentSelection);
        ArgumentNullException.ThrowIfNull(clearThreadInput);
        ArgumentNullException.ThrowIfNull(refreshHeaderAndThreadWorkspace);
        ArgumentNullException.ThrowIfNull(refreshCatalogAndThreadWorkspace);
        ArgumentNullException.ThrowIfNull(setShellStatus);
        ArgumentNullException.ThrowIfNull(setThreadStatus);
        ArgumentNullException.ThrowIfNull(tryRenderInteraction);

        _runtimeService = runtimeService;
        _catalogOptions = catalogOptions;
        _chatBackendStates = chatBackendStates;
        _getUiDispatcher = getUiDispatcher;
        _getThreadInput = getThreadInput;
        _getChatBackendSelect = getChatBackendSelect;
        _getChatModelSelect = getChatModelSelect;
        _getChatReasoningSelect = getChatReasoningSelect;
        _getSelectedThread = getSelectedThread;
        _getSelectedProject = getSelectedProject;
        _getProjectById = getProjectById;
        _ensureThreadTab = ensureThreadTab;
        _findOpenThread = findOpenThread;
        _ensureThreadHistoryLoadedAsync = ensureThreadHistoryLoadedAsync;
        _getGlobalScopeSelected = getGlobalScopeSelected;
        _getSelectedProjectId = getSelectedProjectId;
        _getPreferredBackendId = getPreferredBackendId;
        _trySetPromptUnavailableStatus = trySetPromptUnavailableStatus;
        _createGlobalThreadAsync = createGlobalThreadAsync;
        _createProjectThreadAsync = createProjectThreadAsync;
        _registerDelegatedThread = registerDelegatedThread;
        _persistViewStateAsync = persistViewStateAsync;
        _getAutoApproveEnabled = getAutoApproveEnabled;
        _rememberThreadPreference = rememberThreadPreference;
        _setReadyStatusForCurrentSelection = setReadyStatusForCurrentSelection;
        _clearThreadInput = clearThreadInput;
        _refreshHeaderAndThreadWorkspace = refreshHeaderAndThreadWorkspace;
        _refreshCatalogAndThreadWorkspace = refreshCatalogAndThreadWorkspace;
        _setShellStatus = setShellStatus;
        _setThreadStatus = setThreadStatus;
        _tryRenderInteraction = tryRenderInteraction;
    }

    public async Task SendSelectedThreadPromptAsync(bool steer)
    {
        var thread = _getSelectedThread();
        if (thread is null)
        {
            if (steer)
            {
                _setShellStatus("Start the thread before steering it.", false, CodeAltaApp.StatusTone.Warning);
                return;
            }

            if (_trySetPromptUnavailableStatus())
            {
                return;
            }

            thread = _getGlobalScopeSelected()
                ? await _createGlobalThreadAsync().ConfigureAwait(false)
                : await _createProjectThreadAsync().ConfigureAwait(false);
            if (thread is null)
            {
                return;
            }
        }
        else if (!IsChatBackendReady(new AgentBackendId(thread.BackendId)))
        {
            _setReadyStatusForCurrentSelection();
            return;
        }

        var prompt = UiDispatch.Invoke(_getUiDispatcher(), () => _getThreadInput()?.Text?.Trim());
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        var tab = _ensureThreadTab(thread);
        await _ensureThreadHistoryLoadedAsync(thread, CancellationToken.None).ConfigureAwait(false);
        tab.Timeline.ReplaceTruncatedHistoryLoadButton();
        _clearThreadInput();
        try
        {
            _setThreadStatus(tab, StatusVisualFormatter.BuildThinkingStatusText(), true, CodeAltaApp.StatusTone.Info);
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
            _refreshHeaderAndThreadWorkspace();
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && CodeAltaApp.UiLogger.IsEnabled(LogLevel.Error))
            {
                CodeAltaApp.UiLogger.Error(ex, $"Failed to send prompt for thread {thread.ThreadId}");
            }

            tab.Timeline.RenderFailure($"Failed to send prompt: {ex.Message}");
            _setThreadStatus(tab, $"Failed to send prompt: {ex.Message}", false, CodeAltaApp.StatusTone.Error);
        }
    }

    public async Task DelegateSelectedThreadAsync()
    {
        var thread = _getSelectedThread();
        if (thread is null)
        {
            _setShellStatus("Open a thread before delegating work.", false, CodeAltaApp.StatusTone.Warning);
            return;
        }

        if (!IsChatBackendReady(new AgentBackendId(thread.BackendId)))
        {
            _setReadyStatusForCurrentSelection();
            return;
        }

        var tab = _ensureThreadTab(thread);
        var prompt = UiDispatch.Invoke(_getUiDispatcher(), () => _getThreadInput()?.Text?.Trim());
        if (string.IsNullOrWhiteSpace(prompt))
        {
            _setShellStatus("Enter delegation instructions before creating an internal thread.", false, CodeAltaApp.StatusTone.Warning);
            return;
        }

        var targetProject = _getProjectById(thread.ProjectRef ?? _getSelectedProjectId());
        if (targetProject is null)
        {
            _setShellStatus("Select a project before delegating internal work.", false, CodeAltaApp.StatusTone.Warning);
            return;
        }

        try
        {
            _setThreadStatus(tab, $"Delegating internal work from '{thread.Title}'...", true, CodeAltaApp.StatusTone.Info);
            var transientThreadKey = CreateTransientThreadKey(tab.BackendId, targetProject.ProjectPath);
            var executionOptions = new WorkThreadExecutionOptions
            {
                BackendId = tab.BackendId,
                WorkingDirectory = targetProject.ProjectPath,
                ProjectRoots = [targetProject.ProjectPath],
                Model = tab.ModelId,
                ReasoningEffort = tab.ReasoningEffort,
                OnPermissionRequest = (request, cancellationToken) => HandleThreadPermissionRequestAsync(transientThreadKey, request, cancellationToken),
                OnUserInputRequest = (request, cancellationToken) => HandleThreadUserInputRequestAsync(transientThreadKey, request, cancellationToken),
            };

            var child = await _runtimeService.CreateInternalThreadAsync(
                    thread,
                    targetProject,
                    executionOptions,
                    title: ThreadRuntimeEventCoordinator.SummarizeContent(prompt),
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            _rememberThreadPreference(child.ThreadId, executionOptions.Model, executionOptions.ReasoningEffort, tab.AutoScroll, false);

            _ = _registerDelegatedThread(child, tab);

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

            _clearThreadInput();
            _setThreadStatus(tab, $"Delegation started · {child.Title}", false, CodeAltaApp.StatusTone.Ready);
            await _persistViewStateAsync().ConfigureAwait(false);
            _refreshCatalogAndThreadWorkspace();
        }
        catch (Exception ex)
        {
            CodeAltaApp.UiLogger.Error(ex, "Failed to delegate internal thread.");
            _setThreadStatus(tab, $"Failed to delegate internal thread: {ex.Message}", false, CodeAltaApp.StatusTone.Error);
        }
    }

    public async Task AbortSelectedThreadAsync()
    {
        var thread = _getSelectedThread();
        if (thread is null)
        {
            return;
        }

        try
        {
            await _runtimeService.AbortAsync(thread.ThreadId).ConfigureAwait(false);
            var tab = _ensureThreadTab(thread);
            _setThreadStatus(tab, $"Stopped · {thread.Title}", false, CodeAltaApp.StatusTone.Warning);
        }
        catch (Exception ex)
        {
            var tab = _ensureThreadTab(thread);
            _setThreadStatus(tab, $"Failed to abort '{thread.Title}': {ex.Message}", false, CodeAltaApp.StatusTone.Error);
        }
    }

    public WorkThreadExecutionOptions BuildPreferredExecutionOptions(
        AgentBackendId backendId,
        string workingDirectory,
        IReadOnlyList<string> projectRoots)
    {
        ArgumentNullException.ThrowIfNull(projectRoots);

        var backendState = _chatBackendStates[backendId.Value];
        var model = UiDispatch.Invoke(
            _getUiDispatcher(),
            () =>
            {
                if (_getChatBackendSelect() is not { } backendSelect || _getChatModelSelect() is not { } modelSelect)
                {
                    return backendState.SelectedModelId;
                }

                var backendOptions = ChatBackendPresentation.BuildBackendOptions();
                if ((uint)backendSelect.SelectedIndex < (uint)backendOptions.Count &&
                    string.Equals(backendOptions[backendSelect.SelectedIndex].BackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase))
                {
                    var modelOptions = ChatBackendPresentation.BuildModelOptions(backendState);
                    if ((uint)modelSelect.SelectedIndex < (uint)modelOptions.Count)
                    {
                        return modelOptions[modelSelect.SelectedIndex].ModelId;
                    }
                }

                return backendState.SelectedModelId;
            });

        var reasoning = UiDispatch.Invoke(
            _getUiDispatcher(),
            () =>
            {
                if (_getChatBackendSelect() is not { } backendSelect || _getChatReasoningSelect() is not { } reasoningSelect)
                {
                    return backendState.SelectedReasoningEffort;
                }

                var backendOptions = ChatBackendPresentation.BuildBackendOptions();
                if ((uint)backendSelect.SelectedIndex < (uint)backendOptions.Count &&
                    string.Equals(backendOptions[backendSelect.SelectedIndex].BackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase))
                {
                    var selectedModel = backendState.Models.FirstOrDefault(candidate => string.Equals(candidate.Id, model, StringComparison.Ordinal));
                    var reasoningOptions = ChatBackendPresentation.BuildReasoningOptions(selectedModel);
                    if ((uint)reasoningSelect.SelectedIndex < (uint)reasoningOptions.Count)
                    {
                        return reasoningOptions[reasoningSelect.SelectedIndex].Effort;
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

    private async Task<AgentPermissionDecision> HandleThreadPermissionRequestAsync(
        string threadId,
        AgentPermissionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var autoApproveEnabled = _getAutoApproveEnabled();
        var decision = autoApproveEnabled
            ? new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)
            : new AgentPermissionDecision(AgentPermissionDecisionKind.Deny);

        if (ChatMarkdownFormatter.ShouldDisplayPermissionRequest(autoApproveEnabled) && _findOpenThread(threadId) is { } tab)
        {
            _tryRenderInteraction(
                tab,
                () =>
                {
                    tab.Timeline.UpsertInteraction(
                        request.InteractionId,
                        request.Timestamp,
                        ChatMarkdownFormatter.FormatChatPermissionRequestMarkdown(request),
                        ChatMarkdownFormatter.FormatChatImmediatePermissionDecisionMarkdown(decision, autoApproveEnabled),
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

        var autoApproveEnabled = _getAutoApproveEnabled();
        var response = ChatPromptResponseBuilder.CreateResponse(request, autoApproveEnabled);
        if (_findOpenThread(threadId) is { } tab)
        {
            _tryRenderInteraction(
                tab,
                () =>
                {
                    tab.Timeline.UpsertInteraction(
                        request.InteractionId,
                        request.Timestamp,
                        ChatMarkdownFormatter.FormatChatUserInputRequestMarkdown(request, autoApproveEnabled),
                        ChatMarkdownFormatter.FormatChatImmediateUserInputResponseMarkdown(response, autoApproveEnabled),
                        ChatTimelineTone.Interaction,
                        "Action Required",
                        "User Input Request");
                },
                "user input request");
        }

        return response;
    }

    public WorkThreadExecutionOptions BuildExecutionOptions(WorkThreadDescriptor thread, OpenThreadState tab)
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

    private static string CreateTransientThreadKey(AgentBackendId backendId, string workingDirectory)
        => $"{backendId.Value}:{workingDirectory}";

    private bool IsChatBackendReady(AgentBackendId backendId)
    {
        return _chatBackendStates[backendId.Value].Availability == ChatBackendAvailability.Ready;
    }

    private string ResolveWorkingDirectory(WorkThreadDescriptor thread)
    {
        return thread.Kind switch
        {
            WorkThreadKind.GlobalThread => _catalogOptions.GlobalRoot,
            WorkThreadKind.ProjectThread or WorkThreadKind.InternalThread when _getProjectById(thread.ProjectRef) is { } project => project.ProjectPath,
            _ => thread.WorkingDirectory,
        };
    }

    private IReadOnlyList<string> ResolveProjectRoots(WorkThreadDescriptor thread)
    {
        if (_getProjectById(thread.ProjectRef) is { } project)
        {
            return [project.ProjectPath];
        }

        return [];
    }
}
