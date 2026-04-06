using CodeAlta.App.State;
using CodeAlta.Threading;
using CodeAlta.Agent;
using CodeAlta.App.Context;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Shell;
using CodeAlta.Presentation.Prompting;
using CodeAlta.ViewModels;
using CodeAlta.Views;
using XenoAtom.Logging;
using XenoAtom.Terminal.UI.Controls;
using CodeAlta.Search;

namespace CodeAlta.App;

internal sealed class ThreadCommandCoordinator
{
    private readonly WorkThreadRuntimeService _runtimeService;
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates;
    private readonly ThreadSelectionContext _threadSelection;
    private readonly ChatSelectorStateContext _selectorState;
    private readonly ChatPreferenceContext _preferences;
    private readonly ThreadCommandContext _commandContext;
    private readonly ThreadPromptQueueCoordinator _queueCoordinator;
    private readonly PromptComposerViewModel _promptComposerViewModel;
    private readonly ThreadExecutionOptionsFactory _executionOptionsFactory;
    private readonly ThreadPromptDispatchCoordinator _promptDispatchCoordinator;

    public ThreadCommandCoordinator(
        WorkThreadRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        Dictionary<string, ChatBackendState> chatBackendStates,
        ThreadSelectionContext threadSelection,
        ChatSelectorStateContext selectorState,
        ChatPreferenceContext preferences,
        ThreadCommandContext commandContext,
        ThreadPromptQueueCoordinator queueCoordinator,
        PromptComposerViewModel promptComposerViewModel,
        IProjectFileSearchService? projectFileSearchService = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(threadSelection);
        ArgumentNullException.ThrowIfNull(selectorState);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(commandContext);
        ArgumentNullException.ThrowIfNull(queueCoordinator);
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);

        _runtimeService = runtimeService;
        _chatBackendStates = chatBackendStates;
        _threadSelection = threadSelection;
        _selectorState = selectorState;
        _preferences = preferences;
        _commandContext = commandContext;
        _queueCoordinator = queueCoordinator;
        _promptComposerViewModel = promptComposerViewModel;
        var permissionRequests = new ThreadPermissionRequestCoordinator(threadSelection, commandContext);
        var userInputRequests = new ThreadUserInputRequestCoordinator(threadSelection, commandContext);
        _executionOptionsFactory = new ThreadExecutionOptionsFactory(catalogOptions, chatBackendStates, threadSelection, selectorState, permissionRequests, userInputRequests);
        _promptDispatchCoordinator = new ThreadPromptDispatchCoordinator(
            runtimeService,
            _executionOptionsFactory,
            queueCoordinator,
            commandContext,
            projectFileSearchService ?? NullProjectFileSearchService.Instance);
    }

    public async Task SendPromptAsync(
        string? promptText,
        bool steer,
        CancellationToken cancellationToken = default)
    {
        var thread = _threadSelection.GetSelectedThread();
        var hadExistingThread = thread is not null;
        var prompt = promptText?.Trim();
        if (thread is null)
        {
            if (steer)
            {
                _commandContext.SetShellStatus("Start the thread before steering it.", false, StatusTone.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                return;
            }

            if (_commandContext.TrySetPromptUnavailableStatus())
            {
                return;
            }

            var initialThreadTitle = ThreadPromptDispatchCoordinator.CreateInitialThreadTitle(prompt);
            thread = _threadSelection.Selection.Target is WorkspaceTarget.Draft { IsGlobal: true }
                ? await _commandContext.CreateGlobalThreadAsync(initialThreadTitle)
                : await _commandContext.CreateProjectThreadAsync(initialThreadTitle);
            if (thread is null)
            {
                return;
            }

            _commandContext.ClearDraftInput();
        }
        else if (!IsChatBackendReady(new AgentBackendId(thread.BackendId)))
        {
            _commandContext.SetReadyStatusForCurrentSelection();
            return;
        }
        if (string.IsNullOrWhiteSpace(prompt))
        {
            if (steer && thread is not null)
            {
                var queuedTab = _threadSelection.EnsureThreadTab(thread);
                await _queueCoordinator.ConvertNextQueuedPromptToSteerAsync(queuedTab, cancellationToken);
            }

            return;
        }

        var tab = _threadSelection.EnsureThreadTab(thread);
        await _threadSelection.EnsureThreadHistoryLoadedAsync(thread, cancellationToken);
        tab.Timeline.ReplaceTruncatedHistoryLoadButton();

        var alwaysEnqueue = hadExistingThread &&
            UiDispatch.Invoke(_selectorState.GetUiDispatcher(), () => _promptComposerViewModel.AlwaysEnqueue);
        if (!steer && (tab.StatusBusy || alwaysEnqueue))
        {
            _queueCoordinator.EnqueuePrompt(tab, prompt);
            _commandContext.ClearThreadInput();
            return;
        }

        _commandContext.ClearThreadInput();
        await _promptDispatchCoordinator.DispatchPromptAsync(thread, tab, prompt, steer, cancellationToken);
    }

    public async Task DelegateThreadAsync(
        string? promptText,
        CancellationToken cancellationToken = default)
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
        var prompt = promptText?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            _commandContext.SetShellStatus("Enter delegation instructions before creating an internal thread.", false, StatusTone.Warning);
            return;
        }

        var targetProject = _threadSelection.GetProjectById(thread.ProjectRef ?? _threadSelection.GetSelectedProjectId());
        if (targetProject is null)
        {
            _commandContext.SetShellStatus("Select a project before delegating internal work.", false, StatusTone.Warning);
            return;
        }

        try
        {
            _commandContext.SetThreadStatus(tab, $"Delegating internal work from '{thread.Title}'...", true, StatusTone.Info);
            var transientThreadKey = ThreadExecutionOptionsFactory.CreateTransientThreadKey(tab.BackendId, targetProject.ProjectPath);
            var executionOptions = _promptDispatchCoordinator.BuildDelegationExecutionOptions(
                transientThreadKey,
                tab,
                targetProject.ProjectPath,
                [targetProject.ProjectPath]);

            var child = await _runtimeService.CreateInternalThreadAsync(
                    thread,
                    targetProject,
                    executionOptions,
                    title: ThreadRuntimeEventCoordinator.SummarizeContent(prompt),
                    cancellationToken: cancellationToken)
                ;
            _preferences.RememberThreadPreference(child.ThreadId, executionOptions.Model, executionOptions.ReasoningEffort, tab.AutoScroll, false);

            _ = _threadSelection.RegisterDelegatedThread(child, tab);

            _ = await _runtimeService.SendAsync(
                    child,
                    _promptDispatchCoordinator.BuildDelegationExecutionOptions(
                        child.ThreadId,
                        tab,
                        targetProject.ProjectPath,
                        [targetProject.ProjectPath]),
                    new AgentSendOptions
                    {
                        Input = AgentInput.Text(
                            $"Delegated from thread '{thread.Title}' ({thread.ThreadId}): {prompt}")
                    },
                    cancellationToken)
                ;

            _commandContext.ClearThreadInput();
            _commandContext.SetThreadStatus(tab, $"Delegation started · {child.Title}", false, StatusTone.Ready);
            await _commandContext.PersistViewStateAsync();
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
            await _runtimeService.AbortAsync(thread.ThreadId);
            var tab = _threadSelection.EnsureThreadTab(thread);
            _commandContext.SetThreadStatus(tab, $"Stopped · {thread.Title}", false, StatusTone.Warning);
        }
        catch (Exception ex)
        {
            var tab = _threadSelection.EnsureThreadTab(thread);
            _commandContext.SetThreadStatus(tab, $"Failed to abort '{thread.Title}': {ex.Message}", false, StatusTone.Error);
        }
    }

    public async Task CompactSelectedThreadAsync()
    {
        var thread = _threadSelection.GetSelectedThread();
        if (thread is null)
        {
            _commandContext.SetShellStatus("Open a thread before compacting it.", false, StatusTone.Warning);
            return;
        }

        if (!IsChatBackendReady(new AgentBackendId(thread.BackendId)))
        {
            _commandContext.SetReadyStatusForCurrentSelection();
            return;
        }

        var tab = _threadSelection.EnsureThreadTab(thread);
        if (tab.StatusBusy)
        {
            _commandContext.SetShellStatus($"Wait for '{thread.Title}' to become idle before compacting it.", false, StatusTone.Warning);
            return;
        }

        if (thread.StartedAt is null)
        {
            _commandContext.SetThreadStatus(tab, "Compaction is available after the thread has completed at least one run.", false, StatusTone.Warning);
            return;
        }

        try
        {
            tab.PendingManualCompaction = true;
            _commandContext.SetThreadStatus(tab, $"Compacting '{thread.Title}'...", true, StatusTone.Info);
            await _runtimeService.CompactAsync(thread, BuildExecutionOptions(thread, tab), CancellationToken.None);
        }
        catch (Exception ex)
        {
            tab.PendingManualCompaction = false;
            _commandContext.SetThreadStatus(tab, $"Failed to compact '{thread.Title}': {ex.Message}", false, StatusTone.Error);
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
        await _queueCoordinator.ConvertSelectedThreadQueuedPromptToSteerAsync(queuedPromptId, cancellationToken);
    }

    public void DeleteSelectedThreadQueuedPrompt(string queuedPromptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedPromptId);
        _queueCoordinator.DeleteSelectedThreadQueuedPrompt(queuedPromptId);
    }

    public void DeleteSelectedThreadPendingSteer(string pendingSteerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingSteerId);
        _queueCoordinator.DeleteSelectedThreadPendingSteer(pendingSteerId);
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

        return _promptDispatchCoordinator.DispatchPromptAsync(tab.Thread, tab, prompt, steer, cancellationToken);
    }

    public WorkThreadExecutionOptions BuildPreferredExecutionOptions(
        AgentBackendId backendId,
        string workingDirectory,
        IReadOnlyList<string> projectRoots)
        => _promptDispatchCoordinator.BuildPreferredExecutionOptions(backendId, workingDirectory, projectRoots);

    public WorkThreadExecutionOptions BuildExecutionOptions(WorkThreadDescriptor thread, OpenThreadState tab)
        => _promptDispatchCoordinator.BuildExecutionOptions(thread, tab);

    private bool IsChatBackendReady(AgentBackendId backendId)
    {
        return _chatBackendStates[backendId.Value].Availability == ChatBackendAvailability.Ready;
    }

}
