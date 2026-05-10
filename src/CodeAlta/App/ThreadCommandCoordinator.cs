using CodeAlta.App.State;
using CodeAlta.Threading;
using CodeAlta.Agent;
using CodeAlta.App.Context;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Shell;
using CodeAlta.Presentation.Prompting;
using CodeAlta.ViewModels;
using CodeAlta.Views;
using XenoAtom.Logging;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.App;

internal sealed class ThreadCommandCoordinator
{
    private readonly WorkThreadRuntimeService _runtimeService;
    private readonly IReadOnlyList<AgentBackendDescriptor> _backendDescriptors;
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates;
    private readonly ThreadSelectionContext _threadSelection;
    private readonly ModelProviderSelectorStateStore _selectorState;
    private readonly ThreadCommandContext _commandContext;
    private readonly ThreadPromptQueueCoordinator _queueCoordinator;
    private readonly PromptComposerViewModel _promptComposerViewModel;
    private readonly Func<bool> _getAlwaysEnqueue;
    private readonly ThreadExecutionOptionsFactory _executionOptionsFactory;
    private readonly ThreadPromptDispatchCoordinator _promptDispatchCoordinator;
    private readonly PluginHostBridge? _pluginHostBridge;

    public ThreadCommandCoordinator(
        WorkThreadRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        Dictionary<string, ChatBackendState> chatBackendStates,
        ThreadSelectionContext threadSelection,
        ModelProviderSelectorStateStore selectorState,
        ThreadCommandContext commandContext,
        ThreadPromptQueueCoordinator queueCoordinator,
        PromptComposerViewModel promptComposerViewModel,
        IProjectFileSearchService? projectFileSearchService = null,
        PluginHostBridge? pluginHostBridge = null,
        IServiceProvider? altaServices = null,
        IReadOnlySet<string>? altaToolBackendIds = null,
        Func<bool>? getAlwaysEnqueue = null)
        : this(
            runtimeService,
            catalogOptions,
            ChatBackendPresentation.CreateBackendStates().Values
                .Select(static state => new AgentBackendDescriptor(state.BackendId, state.DisplayName))
                .ToArray(),
            chatBackendStates,
            threadSelection,
            selectorState,
            commandContext,
            queueCoordinator,
            promptComposerViewModel,
            projectFileSearchService,
            pluginHostBridge,
            altaServices,
            altaToolBackendIds,
            getAlwaysEnqueue)
    {
    }

    public ThreadCommandCoordinator(
        WorkThreadRuntimeService runtimeService,
        CatalogOptions catalogOptions,
        IReadOnlyList<AgentBackendDescriptor> backendDescriptors,
        Dictionary<string, ChatBackendState> chatBackendStates,
        ThreadSelectionContext threadSelection,
        ModelProviderSelectorStateStore selectorState,
        ThreadCommandContext commandContext,
        ThreadPromptQueueCoordinator queueCoordinator,
        PromptComposerViewModel promptComposerViewModel,
        IProjectFileSearchService? projectFileSearchService = null,
        PluginHostBridge? pluginHostBridge = null,
        IServiceProvider? altaServices = null,
        IReadOnlySet<string>? altaToolBackendIds = null,
        Func<bool>? getAlwaysEnqueue = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(backendDescriptors);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(threadSelection);
        ArgumentNullException.ThrowIfNull(selectorState);
        ArgumentNullException.ThrowIfNull(commandContext);
        ArgumentNullException.ThrowIfNull(queueCoordinator);
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);

        _runtimeService = runtimeService;
        _backendDescriptors = backendDescriptors;
        _chatBackendStates = chatBackendStates;
        _threadSelection = threadSelection;
        _selectorState = selectorState;
        _commandContext = commandContext;
        _queueCoordinator = queueCoordinator;
        _promptComposerViewModel = promptComposerViewModel;
        _getAlwaysEnqueue = getAlwaysEnqueue ?? (() => _promptComposerViewModel.AlwaysEnqueue);
        _pluginHostBridge = pluginHostBridge;
        var permissionRequests = new ThreadPermissionRequestCoordinator(threadSelection, commandContext);
        var userInputRequests = new ThreadUserInputRequestCoordinator(threadSelection, commandContext);
        _executionOptionsFactory = new ThreadExecutionOptionsFactory(catalogOptions, backendDescriptors, chatBackendStates, threadSelection, selectorState, permissionRequests, userInputRequests, altaServices, altaToolBackendIds);
        _promptDispatchCoordinator = new ThreadPromptDispatchCoordinator(
            runtimeService,
            _executionOptionsFactory,
            queueCoordinator,
            commandContext,
            catalogOptions,
            projectFileSearchService ?? NullProjectFileSearchService.Instance,
            pluginHostBridge,
            new RuntimeWorkThreadOrchestratorAdapter(runtimeService, threadSelection.FindThread));
    }

    public async Task SendPromptAsync(
        string? promptText,
        bool steer,
        CancellationToken cancellationToken = default)
    {
        var thread = _threadSelection.GetSelectedThread();
        var hadExistingThread = thread is not null;
        var prompt = _commandContext.CaptureThreadInput(promptText);
        if (prompt.Images.Count > 0 && thread is null && !CurrentPromptModelSupportsImages(thread, null))
        {
            _commandContext.SetShellStatus("The selected model does not support image input; remove the prompt images or choose a vision-capable model.", false, StatusTone.Warning);
            return;
        }

        if (thread is null)
        {
            if (steer)
            {
                _commandContext.SetShellStatus("Start the thread before steering it.", false, StatusTone.Warning);
                return;
            }

            if (!prompt.HasContent)
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
        else if (!IsModelProviderReady(new AgentBackendId(thread.BackendId)))
        {
            _commandContext.SetReadyStatusForCurrentSelection();
            return;
        }
        if (!prompt.HasContent)
        {
            if (steer && thread is not null)
            {
                var queuedTab = _threadSelection.EnsureThreadTab(thread);
                await _queueCoordinator.ConvertNextQueuedPromptToSteerAsync(queuedTab, cancellationToken);
            }

            return;
        }

        var tab = _threadSelection.EnsureThreadTab(thread);
        if (prompt.Images.Count > 0 && !CurrentPromptModelSupportsImages(thread, tab))
        {
            _commandContext.SetShellStatus("The selected model does not support image input; remove the prompt images or choose a vision-capable model.", false, StatusTone.Warning);
            return;
        }

        await _threadSelection.EnsureThreadHistoryLoadedAsync(thread, cancellationToken);
        tab.Timeline.ReplaceTruncatedHistoryLoadButton();

        var alwaysEnqueue = hadExistingThread &&
            UiDispatch.Invoke(_selectorState.GetUiDispatcher(), _getAlwaysEnqueue);
        if (!steer && (tab.StatusBusy || alwaysEnqueue))
        {
            _queueCoordinator.EnqueuePrompt(tab, prompt);
            _commandContext.ClearThreadInput();
            return;
        }

        _commandContext.ClearThreadInput();
        await _promptDispatchCoordinator.DispatchPromptAsync(thread, tab, prompt, steer, cancellationToken);
    }

    public bool IsCurrentPromptEmpty()
        => _commandContext.IsThreadInputEmpty();

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

        if (!IsModelProviderReady(new AgentBackendId(thread.BackendId)))
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
            var options = BuildExecutionOptions(thread, tab);
            var augmentation = _pluginHostBridge is null
                ? new PluginCompactionAugmentation()
                : await _pluginHostBridge.BeforeCompactionAsync(thread, tab, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(augmentation.CancelReason))
            {
                tab.PendingManualCompaction = false;
                _commandContext.SetThreadStatus(tab, $"Compaction cancelled by plugin: {augmentation.CancelReason}", false, StatusTone.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(augmentation.AdditionalDeveloperInstructions))
            {
                options = _promptDispatchCoordinator.AppendAdditionalDeveloperInstructions(options, augmentation.AdditionalDeveloperInstructions);
            }

            await _runtimeService.CompactAsync(thread, options, CancellationToken.None);
            if (_pluginHostBridge is not null)
            {
                await _pluginHostBridge.AfterCompactionAsync(thread, tab, succeeded: true, summary: null, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            tab.PendingManualCompaction = false;
            if (_pluginHostBridge is not null)
            {
                await _pluginHostBridge.AfterCompactionAsync(thread, tab, succeeded: false, summary: ex.Message, CancellationToken.None);
            }

            if (LogManager.IsInitialized && CodeAltaApp.UiLogger.IsEnabled(LogLevel.Error))
            {
                CodeAltaApp.UiLogger.Error(ex, $"Failed to compact thread {thread.ThreadId}");
            }

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
        ArgumentNullException.ThrowIfNull(text);
        _queueCoordinator.UpdateSelectedThreadQueuedPromptText(queuedPromptId, text);
    }

    public Task DrainQueuedPromptAsync(OpenThreadState tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return _queueCoordinator.DrainNextQueuedPromptAsync(tab, cancellationToken);
    }

    public Task DispatchQueuedPromptAsync(
        OpenThreadState tab,
        PromptSubmission prompt,
        bool steer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(prompt);
        if (!prompt.HasContent)
        {
            throw new ArgumentException("Prompt text or image attachments are required.", nameof(prompt));
        }

        if (prompt.Images.Count > 0 && !CurrentPromptModelSupportsImages(tab.Thread, tab))
        {
            _commandContext.SetThreadStatus(tab, "The selected model does not support image input; the queued prompt was left in the queue.", false, StatusTone.Warning);
            throw new InvalidOperationException("The selected model does not support image input.");
        }

        return _promptDispatchCoordinator.DispatchPromptAsync(tab.Thread, tab, prompt, steer, cancellationToken);
    }

    public WorkThreadExecutionOptions BuildPreferredExecutionOptions(
        AgentBackendId backendId,
        string workingDirectory,
        IReadOnlyList<string> projectRoots)
        => _promptDispatchCoordinator.BuildPreferredExecutionOptions(backendId, workingDirectory, projectRoots);

    public WorkThreadExecutionOptions BuildExecutionOptions(WorkThreadDescriptor thread, OpenThreadState tab)
        => _promptDispatchCoordinator.BuildExecutionOptions(thread, tab);

    public async Task ActivateSelectedSkillAsync(string skillName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);

        var thread = _threadSelection.GetSelectedThread();
        if (thread is null)
        {
            _commandContext.SetShellStatus("Open a local/raw backend thread before activating a CodeAlta-managed skill.", false, StatusTone.Warning);
            return;
        }

        var backendId = new AgentBackendId(thread.BackendId);
        if (backendId == AgentBackendIds.Codex || backendId == AgentBackendIds.Copilot)
        {
            _commandContext.SetShellStatus(
                "Codex and Copilot manage their own native skills; CodeAlta-managed skill activation is unavailable for this thread.",
                false,
                StatusTone.Warning);
            return;
        }

        if (!IsModelProviderReady(backendId))
        {
            _commandContext.SetReadyStatusForCurrentSelection();
            return;
        }

        var tab = _threadSelection.EnsureThreadTab(thread);
        if (tab.StatusBusy)
        {
            _commandContext.SetShellStatus($"Wait for '{thread.Title}' to become idle before activating a skill.", false, StatusTone.Warning);
            return;
        }

        try
        {
            await _threadSelection.EnsureThreadHistoryLoadedAsync(thread, cancellationToken);
            tab.Timeline.ReplaceTruncatedHistoryLoadButton();
            _commandContext.SetThreadStatus(tab, $"Activating skill '{skillName}'...", true, StatusTone.Info);
            _ = await _runtimeService.ActivateSkillAsync(
                    thread,
                    BuildExecutionOptions(thread, tab),
                    skillName,
                    cancellationToken)
                ;
            _commandContext.SetThreadStatus(tab, $"Activated skill '{skillName}'.", false, StatusTone.Ready);
        }
        catch (Exception ex)
        {
            CodeAltaApp.UiLogger.Error(ex, $"Failed to activate skill {skillName}.");
            _commandContext.SetThreadStatus(tab, $"Failed to activate skill '{skillName}': {ex.Message}", false, StatusTone.Error);
        }
    }

    private bool IsModelProviderReady(AgentBackendId backendId)
    {
        return _chatBackendStates.TryGetValue(backendId.Value, out var state) &&
               state.Availability == ChatBackendAvailability.Ready;
    }

    private bool CurrentPromptModelSupportsImages(WorkThreadDescriptor? thread, OpenThreadState? tab)
    {
        var backendId = tab?.BackendId ?? (thread is not null ? new AgentBackendId(thread.BackendId) : ResolveSelectedBackendId());
        if (!_chatBackendStates.TryGetValue(backendId.Value, out var backendState))
        {
            return false;
        }

        var modelId = tab?.ModelId ?? backendState.SelectedModelId;
        var model = !string.IsNullOrWhiteSpace(modelId)
            ? backendState.Models.FirstOrDefault(candidate => string.Equals(candidate.Id, modelId, StringComparison.Ordinal))
            : null;
        model ??= ChatBackendPresentation.GetSelectedModel(backendState) ??
            (backendState.Models.Count == 1 ? backendState.Models[0] : null);
        return AgentModelCapabilityHelper.SupportsImageInput(backendId, model);
    }

    private AgentBackendId ResolveSelectedBackendId()
    {
        var backendIndex = UiDispatch.Invoke(_selectorState.GetUiDispatcher(), () => _selectorState.GetSelectedModelProviderIndex());
        var backendOptions = ChatBackendPresentation.BuildBackendOptions(_backendDescriptors);
        if (backendIndex is { } index && (uint)index < (uint)backendOptions.Count)
        {
            return backendOptions[index].BackendId;
        }

        return _chatBackendStates.Values.FirstOrDefault(static state => state.Availability == ChatBackendAvailability.Ready)?.BackendId ??
            AgentBackendIds.Codex;
    }

}
