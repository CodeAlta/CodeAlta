using CodeAlta.App.State;
using CodeAlta.Threading;
using CodeAlta.Agent;
using CodeAlta.App.Context;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Formatting;
using CodeAlta.Presentation.Shell;
using CodeAlta.Views;
using XenoAtom.Logging;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.App;

internal sealed class ThreadCommandCoordinator
{
    private readonly WorkThreadRuntimeService _runtimeService;
    private readonly CatalogOptions _catalogOptions;
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates;
    private readonly ThreadSelectionContext _threadSelection;
    private readonly ChatSelectorUiContext _selectorUi;
    private readonly ChatPreferenceContext _preferences;
    private readonly ThreadCommandContext _commandContext;
    private readonly ThreadPromptQueueCoordinator _queueCoordinator;

    public ThreadCommandCoordinator(
        WorkThreadRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        Dictionary<string, ChatBackendState> chatBackendStates,
        ThreadSelectionContext threadSelection,
        ChatSelectorUiContext selectorUi,
        ChatPreferenceContext preferences,
        ThreadCommandContext commandContext,
        ThreadPromptQueueCoordinator queueCoordinator)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(threadSelection);
        ArgumentNullException.ThrowIfNull(selectorUi);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(commandContext);
        ArgumentNullException.ThrowIfNull(queueCoordinator);

        _runtimeService = runtimeService;
        _catalogOptions = catalogOptions;
        _chatBackendStates = chatBackendStates;
        _threadSelection = threadSelection;
        _selectorUi = selectorUi;
        _preferences = preferences;
        _commandContext = commandContext;
        _queueCoordinator = queueCoordinator;
    }

    public async Task SendSelectedThreadPromptAsync(bool steer)
    {
        var thread = _threadSelection.GetSelectedThread();
        if (thread is null)
        {
            if (steer)
            {
                _commandContext.SetShellStatus("Start the thread before steering it.", false, StatusTone.Warning);
                return;
            }

            if (_commandContext.TrySetPromptUnavailableStatus())
            {
                return;
            }

            thread = _threadSelection.GlobalScopeSelected
                ? await _commandContext.CreateGlobalThreadAsync().ConfigureAwait(false)
                : await _commandContext.CreateProjectThreadAsync().ConfigureAwait(false);
            if (thread is null)
            {
                return;
            }
        }
        else if (!IsChatBackendReady(new AgentBackendId(thread.BackendId)))
        {
            _commandContext.SetReadyStatusForCurrentSelection();
            return;
        }

        var prompt = UiDispatch.Invoke(_selectorUi.GetUiDispatcher(), () => _selectorUi.GetThreadInput()?.Text?.Trim());
        if (string.IsNullOrWhiteSpace(prompt))
        {
            if (steer && thread is not null)
            {
                var queuedTab = _threadSelection.EnsureThreadTab(thread);
                await _queueCoordinator.ConvertNextQueuedPromptToSteerAsync(queuedTab, CancellationToken.None).ConfigureAwait(false);
            }

            return;
        }

        var tab = _threadSelection.EnsureThreadTab(thread);
        await _threadSelection.EnsureThreadHistoryLoadedAsync(thread, CancellationToken.None).ConfigureAwait(false);
        tab.Timeline.ReplaceTruncatedHistoryLoadButton();

        if (!steer && tab.StatusBusy)
        {
            _queueCoordinator.EnqueuePrompt(tab, prompt);
            _commandContext.ClearThreadInput();
            return;
        }

        _commandContext.ClearThreadInput();
        await DispatchPromptAsync(thread, tab, prompt, steer, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task DelegateSelectedThreadAsync()
    {
        var thread = _threadSelection.GetSelectedThread();
        if (thread is null)
        {
            _commandContext.SetShellStatus("Open a thread before delegating work.", false, StatusTone.Warning);
            return;
        }

        if (!IsChatBackendReady(new AgentBackendId(thread.BackendId)))
        {
            _commandContext.SetReadyStatusForCurrentSelection();
            return;
        }

        var tab = _threadSelection.EnsureThreadTab(thread);
        var prompt = UiDispatch.Invoke(_selectorUi.GetUiDispatcher(), () => _selectorUi.GetThreadInput()?.Text?.Trim());
        if (string.IsNullOrWhiteSpace(prompt))
        {
            _commandContext.SetShellStatus("Enter delegation instructions before creating an internal thread.", false, StatusTone.Warning);
            return;
        }

        var targetProject = _threadSelection.GetProjectById(thread.ProjectRef ?? _threadSelection.SelectedProjectId);
        if (targetProject is null)
        {
            _commandContext.SetShellStatus("Select a project before delegating internal work.", false, StatusTone.Warning);
            return;
        }

        try
        {
            _commandContext.SetThreadStatus(tab, $"Delegating internal work from '{thread.Title}'...", true, StatusTone.Info);
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
            _preferences.RememberThreadPreference(child.ThreadId, executionOptions.Model, executionOptions.ReasoningEffort, tab.AutoScroll, false);

            _ = _threadSelection.RegisterDelegatedThread(child, tab);

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

            _commandContext.ClearThreadInput();
            _commandContext.SetThreadStatus(tab, $"Delegation started · {child.Title}", false, StatusTone.Ready);
            await _commandContext.PersistViewStateAsync().ConfigureAwait(false);
            _commandContext.RefreshCatalogAndThreadWorkspace();
        }
        catch (Exception ex)
        {
            CodeAltaApp.UiLogger.Error(ex, "Failed to delegate internal thread.");
            _commandContext.SetThreadStatus(tab, $"Failed to delegate internal thread: {ex.Message}", false, StatusTone.Error);
        }
    }

    public async Task AbortSelectedThreadAsync()
    {
        var thread = _threadSelection.GetSelectedThread();
        if (thread is null)
        {
            return;
        }

        try
        {
            await _runtimeService.AbortAsync(thread.ThreadId).ConfigureAwait(false);
            var tab = _threadSelection.EnsureThreadTab(thread);
            _commandContext.SetThreadStatus(tab, $"Stopped · {thread.Title}", false, StatusTone.Warning);
        }
        catch (Exception ex)
        {
            var tab = _threadSelection.EnsureThreadTab(thread);
            _commandContext.SetThreadStatus(tab, $"Failed to abort '{thread.Title}': {ex.Message}", false, StatusTone.Error);
        }
    }

    public Task ClearSelectedThreadQueueAsync()
    {
        _queueCoordinator.ClearSelectedThreadQueue();
        return Task.CompletedTask;
    }

    public async Task ConvertSelectedThreadQueuedPromptToSteerAsync(string queuedPromptId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedPromptId);
        await _queueCoordinator.ConvertSelectedThreadQueuedPromptToSteerAsync(queuedPromptId, cancellationToken).ConfigureAwait(false);
    }

    public void DeleteSelectedThreadQueuedPrompt(string queuedPromptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedPromptId);
        _queueCoordinator.DeleteSelectedThreadQueuedPrompt(queuedPromptId);
    }

    public void UpdateSelectedThreadQueuedPromptCount(string queuedPromptId, int remainingCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedPromptId);
        _queueCoordinator.UpdateSelectedThreadQueuedPromptCount(queuedPromptId, remainingCount);
    }

    public void UpdateSelectedThreadQueuedPromptText(string queuedPromptId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedPromptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        _queueCoordinator.UpdateSelectedThreadQueuedPromptText(queuedPromptId, text);
    }

    public Task DrainQueuedPromptAsync(OpenThreadState tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return _queueCoordinator.DrainNextQueuedPromptAsync(tab, cancellationToken);
    }

    public Task DispatchQueuedPromptAsync(
        OpenThreadState tab,
        string prompt,
        bool steer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        return DispatchPromptAsync(tab.Thread, tab, prompt, steer, cancellationToken);
    }

    public WorkThreadExecutionOptions BuildPreferredExecutionOptions(
        AgentBackendId backendId,
        string workingDirectory,
        IReadOnlyList<string> projectRoots)
    {
        ArgumentNullException.ThrowIfNull(projectRoots);

        var backendState = _chatBackendStates[backendId.Value];
        var model = UiDispatch.Invoke(
            _selectorUi.GetUiDispatcher(),
            () =>
            {
                if (_selectorUi.GetChatBackendSelect() is not { } backendSelect || _selectorUi.GetChatModelSelect() is not { } modelSelect)
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
            _selectorUi.GetUiDispatcher(),
            () =>
            {
                if (_selectorUi.GetChatBackendSelect() is not { } backendSelect || _selectorUi.GetChatReasoningSelect() is not { } reasoningSelect)
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

        var autoApproveEnabled = _commandContext.GetAutoApproveEnabled();
        var decision = autoApproveEnabled
            ? new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)
            : new AgentPermissionDecision(AgentPermissionDecisionKind.Deny);

        if (ChatMarkdownFormatter.ShouldDisplayPermissionRequest(autoApproveEnabled) && _threadSelection.FindOpenThread(threadId) is { } tab)
        {
            _commandContext.TryRenderInteraction(
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

        var autoApproveEnabled = _commandContext.GetAutoApproveEnabled();
        var response = ChatPromptResponseBuilder.CreateResponse(request, autoApproveEnabled);
        if (_threadSelection.FindOpenThread(threadId) is { } tab)
        {
            _commandContext.TryRenderInteraction(
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

    private async Task DispatchPromptAsync(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        string prompt,
        bool steer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        try
        {
            _commandContext.SetThreadStatus(tab, StatusVisualFormatter.BuildThinkingStatusText(), true, StatusTone.Info);
            var executionOptions = BuildExecutionOptions(thread, tab);
            if (steer)
            {
                _ = await _runtimeService.SteerAsync(
                        thread,
                        executionOptions,
                        new AgentSteerOptions { Input = AgentInput.Text(prompt) },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                _ = await _runtimeService.SendAsync(
                        thread,
                        executionOptions,
                        new AgentSendOptions { Input = AgentInput.Text(prompt) },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            thread.MarkStarted(DateTimeOffset.UtcNow);
            tab.HistoryLoaded = true;
            _commandContext.RefreshHeaderAndThreadWorkspace();
        }
        catch (Exception ex)
        {
            if (LogManager.IsInitialized && CodeAltaApp.UiLogger.IsEnabled(LogLevel.Error))
            {
                CodeAltaApp.UiLogger.Error(ex, $"Failed to send prompt for thread {thread.ThreadId}");
            }

            tab.Timeline.RenderFailure($"Failed to send prompt: {ex.Message}");
            _commandContext.SetThreadStatus(tab, $"Failed to send prompt: {ex.Message}", false, StatusTone.Error);
        }
    }

    private bool IsChatBackendReady(AgentBackendId backendId)
    {
        return _chatBackendStates[backendId.Value].Availability == ChatBackendAvailability.Ready;
    }

    private string ResolveWorkingDirectory(WorkThreadDescriptor thread)
    {
        return thread.Kind switch
        {
            WorkThreadKind.GlobalThread => _catalogOptions.GlobalRoot,
            WorkThreadKind.ProjectThread or WorkThreadKind.InternalThread when _threadSelection.GetProjectById(thread.ProjectRef) is { } project => project.ProjectPath,
            _ => thread.WorkingDirectory,
        };
    }

    private IReadOnlyList<string> ResolveProjectRoots(WorkThreadDescriptor thread)
    {
        if (_threadSelection.GetProjectById(thread.ProjectRef) is { } project)
        {
            return [project.ProjectPath];
        }

        return [];
    }
}
