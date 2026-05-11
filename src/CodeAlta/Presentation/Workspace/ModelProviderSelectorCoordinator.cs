using CodeAlta.App.State;
using CodeAlta.Threading;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.Context;
using CodeAlta.App.Events;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Prompting;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Presentation.Workspace;

internal sealed class ModelProviderSelectorCoordinator : IPromptAvailabilityProjectionController
{
    private readonly IReadOnlyList<AgentBackendDescriptor> _backendDescriptors;
    private readonly ThreadWorkspaceViewModel _workspaceViewModel;
    private readonly PromptComposerViewModel _promptComposerViewModel;
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates;
    private readonly ModelProviderSelectorStateStore _selectorState;
    private readonly ThreadSelectionContext _threadSelection;
    private readonly IModelProviderPreferencePort _preferences;
    private readonly WorkspaceRefreshContext _workspaceRefresh;
    private readonly Func<string?, string?> _getEffectiveDefaultProviderKey;
    private readonly Action _syncModelProviderSelectorItems;
    private readonly Func<WorkThreadDescriptor, OpenThreadState, bool> _canSelectThreadBackend;
    private readonly Func<WorkThreadDescriptor, OpenThreadState, AgentBackendId, Task<bool>> _trySwitchThreadBackendAsync;
    private readonly Action _refreshSelectionAndThreadWorkspace;
    private readonly Func<IReadOnlyList<string>>? _getConfiguredProviderKeys;
    private bool _selectorsRefreshing;

    public ModelProviderSelectorCoordinator(
        ThreadWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        Dictionary<string, ChatBackendState> chatBackendStates,
        ModelProviderSelectorStateStore selectorState,
        ThreadSelectionContext threadSelection,
        IModelProviderPreferencePort preferences,
        WorkspaceRefreshContext workspaceRefresh,
        Func<string?, string?> getEffectiveDefaultProviderKey,
        Action syncModelProviderSelectorItems,
        Func<WorkThreadDescriptor, OpenThreadState, bool>? canSelectThreadBackend = null,
        Func<WorkThreadDescriptor, OpenThreadState, AgentBackendId, Task<bool>>? trySwitchThreadBackendAsync = null,
        Action? refreshSelectionAndThreadWorkspace = null,
        Func<IReadOnlyList<string>>? getConfiguredProviderKeys = null)
        : this(
            ChatBackendPresentation.CreateBackendStates().Values
                .Select(static state => new AgentBackendDescriptor(state.BackendId, state.DisplayName))
                .ToArray(),
            workspaceViewModel,
            promptComposerViewModel,
            chatBackendStates,
            selectorState,
            threadSelection,
            preferences,
            workspaceRefresh,
            getEffectiveDefaultProviderKey,
            syncModelProviderSelectorItems,
            canSelectThreadBackend,
            trySwitchThreadBackendAsync,
            refreshSelectionAndThreadWorkspace,
            getConfiguredProviderKeys)
    {
    }

    public ModelProviderSelectorCoordinator(
        IReadOnlyList<AgentBackendDescriptor> backendDescriptors,
        ThreadWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        Dictionary<string, ChatBackendState> chatBackendStates,
        ModelProviderSelectorStateStore selectorState,
        ThreadSelectionContext threadSelection,
        IModelProviderPreferencePort preferences,
        WorkspaceRefreshContext workspaceRefresh,
        Func<string?, string?> getEffectiveDefaultProviderKey,
        Action syncModelProviderSelectorItems,
        Func<WorkThreadDescriptor, OpenThreadState, bool>? canSelectThreadBackend = null,
        Func<WorkThreadDescriptor, OpenThreadState, AgentBackendId, Task<bool>>? trySwitchThreadBackendAsync = null,
        Action? refreshSelectionAndThreadWorkspace = null,
        Func<IReadOnlyList<string>>? getConfiguredProviderKeys = null)
    {
        ArgumentNullException.ThrowIfNull(backendDescriptors);
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(selectorState);
        ArgumentNullException.ThrowIfNull(threadSelection);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(workspaceRefresh);
        ArgumentNullException.ThrowIfNull(getEffectiveDefaultProviderKey);
        ArgumentNullException.ThrowIfNull(syncModelProviderSelectorItems);

        _backendDescriptors = backendDescriptors;
        _workspaceViewModel = workspaceViewModel;
        _promptComposerViewModel = promptComposerViewModel;
        _chatBackendStates = chatBackendStates;
        _selectorState = selectorState;
        _threadSelection = threadSelection;
        _preferences = preferences;
        _workspaceRefresh = workspaceRefresh;
        _getEffectiveDefaultProviderKey = getEffectiveDefaultProviderKey;
        _syncModelProviderSelectorItems = syncModelProviderSelectorItems;
        _canSelectThreadBackend = canSelectThreadBackend ?? ((_, _) => false);
        _trySwitchThreadBackendAsync = trySwitchThreadBackendAsync ?? ((_, _, _) => Task.FromResult(false));
        _refreshSelectionAndThreadWorkspace = refreshSelectionAndThreadWorkspace ?? (() => { });
        _getConfiguredProviderKeys = getConfiguredProviderKeys;
    }

    public void RefreshForDraftScope(AgentBackendId? preferredBackendId = null)
    {
        _selectorState.VerifyBindableAccess();
        _selectorsRefreshing = true;
        try
        {
            var backendOptions = ChatBackendPresentation.BuildBackendOptions(_backendDescriptors);
            _workspaceViewModel.ProviderSummaryMarkup = ChatBackendPresentation.BuildProviderSummaryMarkup(
                _chatBackendStates.Values,
                isInitializing: false,
                configuredProviderKeys: GetConfiguredProviderKeys());
            if (backendOptions.Count == 0)
            {
                _selectorState.SetModelProviderSelection([], -1);
                _selectorState.SetModelSelection([], -1);
                _selectorState.SetReasoningSelection([], -1);
                _workspaceViewModel.CanSelectModelProvider = false;
                _workspaceViewModel.CanSelectModel = false;
                _workspaceViewModel.CanSelectReasoning = false;
                _syncModelProviderSelectorItems();
                return;
            }

            var backendId = preferredBackendId ?? GetPreferredDraftBackendId(backendOptions);
            var backendIndex = Math.Max(0, backendOptions.FindIndex(option => string.Equals(option.BackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase)));
            _selectorState.SetModelProviderSelection(backendOptions, backendIndex);

            var backendState = _chatBackendStates[backendOptions[backendIndex].BackendId.Value];
            _preferences.ApplyDraftModelProviderState(backendState);
            var modelOptions = ChatBackendPresentation.BuildModelOptions(backendState);
            _selectorState.SetModelSelection(
                modelOptions,
                Math.Clamp(
                modelOptions.FindIndex(option => string.Equals(option.ModelId, backendState.SelectedModelId, StringComparison.Ordinal)),
                0,
                Math.Max(0, modelOptions.Count - 1)));

            var selectedModel = backendState.Models.FirstOrDefault(model => string.Equals(model.Id, backendState.SelectedModelId, StringComparison.Ordinal))
                                ?? ChatBackendPresentation.GetSelectedModel(backendState);
            var reasoningOptions = ChatBackendPresentation.BuildReasoningOptions(selectedModel);
            _selectorState.SetReasoningSelection(
                reasoningOptions,
                Math.Clamp(
                reasoningOptions.FindIndex(option => option.Effort == backendState.SelectedReasoningEffort),
                0,
                Math.Max(0, reasoningOptions.Count - 1)));
            _workspaceViewModel.CanSelectModelProvider = true;
            _workspaceViewModel.CanSelectModel = backendState.Availability == ChatBackendAvailability.Ready;
            _workspaceViewModel.CanSelectReasoning = backendState.Availability == ChatBackendAvailability.Ready;
            _syncModelProviderSelectorItems();
        }
        finally
        {
            _selectorsRefreshing = false;
        }
    }

    public void RefreshForThread(OpenThreadState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        _selectorState.VerifyBindableAccess();
        _selectorsRefreshing = true;
        try
        {
            var backendOptions = ChatBackendPresentation.BuildBackendOptions(_backendDescriptors);
            _workspaceViewModel.ProviderSummaryMarkup = ChatBackendPresentation.BuildProviderSummaryMarkup(
                _chatBackendStates.Values,
                isInitializing: false,
                configuredProviderKeys: GetConfiguredProviderKeys());
            if (backendOptions.Count == 0)
            {
                _selectorState.SetModelProviderSelection([], -1);
                _selectorState.SetModelSelection([], -1);
                _selectorState.SetReasoningSelection([], -1);
                _workspaceViewModel.CanSelectModelProvider = false;
                _workspaceViewModel.CanSelectModel = false;
                _workspaceViewModel.CanSelectReasoning = false;
                _syncModelProviderSelectorItems();
                return;
            }

            _selectorState.SetModelProviderSelection(
                backendOptions,
                Math.Clamp(
                backendOptions.FindIndex(option => string.Equals(option.BackendId.Value, tab.BackendId.Value, StringComparison.OrdinalIgnoreCase)),
                0,
                Math.Max(0, backendOptions.Count - 1)));

            var backendState = _chatBackendStates[tab.BackendId.Value];
            _preferences.ApplyThreadModelProviderState(tab);

            var modelOptions = ChatBackendPresentation.BuildModelOptions(backendState);
            _selectorState.SetModelSelection(
                modelOptions,
                Math.Clamp(
                modelOptions.FindIndex(option => string.Equals(option.ModelId, tab.ModelId ?? backendState.SelectedModelId, StringComparison.Ordinal)),
                0,
                Math.Max(0, modelOptions.Count - 1)));

            var selectedModel = backendState.Models.FirstOrDefault(model =>
                                    string.Equals(model.Id, tab.ModelId, StringComparison.Ordinal))
                                ?? ChatBackendPresentation.GetSelectedModel(backendState);
            var reasoningOptions = ChatBackendPresentation.BuildReasoningOptions(selectedModel);
            _selectorState.SetReasoningSelection(
                reasoningOptions,
                Math.Clamp(
                reasoningOptions.FindIndex(option => option.Effort == tab.ReasoningEffort),
                0,
                Math.Max(0, reasoningOptions.Count - 1)));
            _workspaceViewModel.CanSelectModelProvider = _canSelectThreadBackend(tab.Thread, tab);
            _workspaceViewModel.CanSelectModel = backendState.Availability == ChatBackendAvailability.Ready;
            _workspaceViewModel.CanSelectReasoning = backendState.Availability == ChatBackendAvailability.Ready;
            _syncModelProviderSelectorItems();
        }
        finally
        {
            _selectorsRefreshing = false;
        }
    }

    public async Task OnModelProviderSelectionChangedAsync(int newIndex)
    {
        if (_selectorsRefreshing)
        {
            return;
        }

        var options = ChatBackendPresentation.BuildBackendOptions(_backendDescriptors);
        if (options.Count == 0)
        {
            return;
        }

        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        _selectorState.SetSelectedModelProviderIndex(newIndex);

        var thread = _threadSelection.GetSelectedThread();
        if (thread is null)
        {
            RefreshForDraftScope(options[newIndex].BackendId);
            _workspaceRefresh.ApplySessionUsageProjection();
            return;
        }

        var tab = _threadSelection.EnsureThreadTab(thread);
        var targetBackendId = options[newIndex].BackendId;
        if (string.Equals(tab.BackendId.Value, targetBackendId.Value, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_canSelectThreadBackend(thread, tab))
        {
            RefreshForThread(tab);
            return;
        }

        if (await _trySwitchThreadBackendAsync(thread, tab, targetBackendId))
        {
            _refreshSelectionAndThreadWorkspace();
            return;
        }

        RefreshForThread(tab);
    }

    public void OnModelSelectionChanged(int newIndex)
    {
        if (_selectorsRefreshing)
        {
            return;
        }

        var thread = _threadSelection.GetSelectedThread();
        if (thread is null)
        {
            var backendId = GetPreferredModelProviderId();
            var draftBackendState = _chatBackendStates[backendId.Value];
            var draftOptions = ChatBackendPresentation.BuildModelOptions(draftBackendState);
            if ((uint)newIndex >= (uint)draftOptions.Count)
            {
                return;
            }

            draftBackendState.SelectedModelId = draftOptions[newIndex].ModelId;
            var preferredModel = ModelProviderPreferenceCoordinator.FindModel(draftBackendState.Models, draftBackendState.SelectedModelId);
            draftBackendState.SelectedReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(preferredModel, preferredReasoningEffort: null);
            UpdateModelSelectorState(draftOptions, newIndex, preferredModel, draftBackendState.SelectedReasoningEffort);
            _preferences.RememberGlobalPreference(CreatePreference(backendId, draftBackendState.SelectedModelId, draftBackendState.SelectedReasoningEffort));
            _workspaceRefresh.ApplySessionUsageProjection();
            return;
        }

        var tab = _threadSelection.EnsureThreadTab(thread);
        var backendState = _chatBackendStates[tab.BackendId.Value];
        var options = ChatBackendPresentation.BuildModelOptions(backendState);
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        tab.ModelId = options[newIndex].ModelId;
        var selectedModel = ModelProviderPreferenceCoordinator.FindModel(backendState.Models, tab.ModelId);
        tab.ReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(selectedModel, preferredReasoningEffort: null);
        UpdateModelSelectorState(options, newIndex, selectedModel, tab.ReasoningEffort);
        _preferences.RememberThreadPreference(tab.Thread.ThreadId, CreatePreference(tab.BackendId, tab.ModelId, tab.ReasoningEffort), true);
        backendState.SelectedModelId = tab.ModelId;
        backendState.SelectedReasoningEffort = tab.ReasoningEffort;
        _preferences.RememberGlobalPreference(CreatePreference(tab.BackendId, tab.ModelId, tab.ReasoningEffort));
        _workspaceRefresh.ApplyHeaderProjection();
    }

    public void OnReasoningSelectionChanged(int newIndex)
    {
        if (_selectorsRefreshing)
        {
            return;
        }

        var thread = _threadSelection.GetSelectedThread();
        if (thread is null)
        {
            var backendId = GetPreferredModelProviderId();
            var draftBackendState = _chatBackendStates[backendId.Value];
            var draftSelectedModel = draftBackendState.Models.FirstOrDefault(model => string.Equals(model.Id, draftBackendState.SelectedModelId, StringComparison.Ordinal));
            var draftOptions = ChatBackendPresentation.BuildReasoningOptions(draftSelectedModel);
            if ((uint)newIndex >= (uint)draftOptions.Count)
            {
                return;
            }

            draftBackendState.SelectedReasoningEffort = draftOptions[newIndex].Effort;
            UpdateReasoningSelectorState(draftOptions, newIndex);
            _preferences.RememberGlobalPreference(CreatePreference(backendId, draftBackendState.SelectedModelId, draftBackendState.SelectedReasoningEffort));
            _workspaceRefresh.ApplySessionUsageProjection();
            return;
        }

        var tab = _threadSelection.EnsureThreadTab(thread);
        var backendState = _chatBackendStates[tab.BackendId.Value];
        var selectedModel = backendState.Models.FirstOrDefault(model => string.Equals(model.Id, tab.ModelId, StringComparison.Ordinal));
        var options = ChatBackendPresentation.BuildReasoningOptions(selectedModel);
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        tab.ReasoningEffort = options[newIndex].Effort;
        UpdateReasoningSelectorState(options, newIndex);
        _preferences.RememberThreadPreference(tab.Thread.ThreadId, CreatePreference(tab.BackendId, tab.ModelId, tab.ReasoningEffort), true);
        backendState.SelectedModelId = tab.ModelId;
        backendState.SelectedReasoningEffort = tab.ReasoningEffort;
        _preferences.RememberGlobalPreference(CreatePreference(tab.BackendId, tab.ModelId, tab.ReasoningEffort));
    }

    public AgentBackendId GetPreferredModelProviderId()
    {
        return UiDispatch.Invoke(
            _selectorState.GetUiDispatcher(),
            () =>
            {
                var options = ChatBackendPresentation.BuildBackendOptions(_backendDescriptors);
                if (options.Count == 0)
                {
                    return GetDefaultBackendId();
                }

                if (_selectorState.GetSelectedModelProviderIndex() is { } backendIndex &&
                    (uint)backendIndex < (uint)options.Count)
                {
                    return options[backendIndex].BackendId;
                }

                if (ResolveConfiguredDefaultBackendId(options) is { } configuredDefaultBackendId)
                {
                    return configuredDefaultBackendId;
                }

                var readyBackend = options.FirstOrDefault(option => IsModelProviderReady(option.BackendId));
                if (readyBackend is not null)
                {
                    return readyBackend.BackendId;
                }

                return GetDefaultBackendId();
            });
    }

    public bool IsModelProviderReady(AgentBackendId backendId)
    {
        return _chatBackendStates.TryGetValue(backendId.Value, out var state) &&
               state.Availability == ChatBackendAvailability.Ready;
    }

    public bool TryGetPromptUnavailableStatus(out string message, out StatusTone tone)
    {
        var projection = BuildPromptComposerProjection();
        if (!projection.HasUnavailableStatus)
        {
            message = string.Empty;
            tone = StatusTone.Ready;
            return false;
        }

        message = projection.UnavailableStatusMessage!;
        tone = projection.UnavailableStatusTone;
        return true;
    }

    public void ApplyPromptAvailabilityProjection()
    {
        _selectorState.VerifyBindableAccess();
        var projection = BuildPromptComposerProjection();
        _promptComposerViewModel.Placeholder = projection.Placeholder;
        _promptComposerViewModel.IsEnabled = projection.IsEnabled;
        _promptComposerViewModel.CanSend = projection.CanSend;
        _promptComposerViewModel.CanSteer = projection.CanSteer;
        _promptComposerViewModel.CanAbort = projection.CanAbort;
        _promptComposerViewModel.CanCompact = projection.CanCompact;
        _promptComposerViewModel.CanCloseTab = projection.CanCloseTab;
        _promptComposerViewModel.CanClearQueue = projection.CanClearQueue;
        _promptComposerViewModel.CanAlwaysEnqueue = projection.CanAlwaysEnqueue;
        UpdateThreadModelProviderSelectionAvailability();
    }

    private void UpdateThreadModelProviderSelectionAvailability()
    {
        if (_threadSelection.Selection.Target is not WorkspaceTarget.Thread)
        {
            return;
        }

        var selectedThread = _threadSelection.GetSelectedThread();
        if (selectedThread is null ||
            _threadSelection.FindOpenThread(selectedThread.ThreadId) is not { } selectedTab)
        {
            return;
        }

        _workspaceViewModel.CanSelectModelProvider = _workspaceViewModel.ModelProviderOptions.Count > 0 &&
            _canSelectThreadBackend(selectedThread, selectedTab);
    }

    private AgentBackendId GetPreferredDraftBackendId(IReadOnlyList<ChatBackendOption> backendOptions)
    {
        if (_selectorState.GetSelectedModelProviderIndex() is { } backendIndex &&
            (uint)backendIndex < (uint)backendOptions.Count)
        {
            var current = backendOptions[backendIndex].BackendId;
            if (IsModelProviderReady(current))
            {
                return current;
            }
        }

        if (ResolveConfiguredDefaultBackendId(backendOptions) is { } configuredDefaultBackendId)
        {
            return configuredDefaultBackendId;
        }

        var readyBackend = backendOptions.FirstOrDefault(option => IsModelProviderReady(option.BackendId));
        if (readyBackend is not null)
        {
            return readyBackend.BackendId;
        }

        return backendOptions.FirstOrDefault()?.BackendId ?? GetDefaultBackendId();
    }

    private bool HasAnyReadyChatBackend()
    {
        return _chatBackendStates.Values.Any(static state => state.Availability == ChatBackendAvailability.Ready);
    }

    private void UpdateModelSelectorState(
        IReadOnlyList<ChatModelOption> modelOptions,
        int selectedModelIndex,
        AgentModelInfo? selectedModel,
        AgentReasoningEffort? selectedReasoningEffort)
    {
        ArgumentNullException.ThrowIfNull(modelOptions);

        var reasoningOptions = ChatBackendPresentation.BuildReasoningOptions(selectedModel);
        var selectedReasoningIndex = Math.Clamp(
            reasoningOptions.FindIndex(option => option.Effort == selectedReasoningEffort),
            0,
            Math.Max(0, reasoningOptions.Count - 1));

        var wasRefreshing = _selectorsRefreshing;
        _selectorsRefreshing = true;
        try
        {
            _selectorState.SetModelSelection(modelOptions, selectedModelIndex);
            _selectorState.SetReasoningSelection(reasoningOptions, selectedReasoningIndex);
            _syncModelProviderSelectorItems();
        }
        finally
        {
            _selectorsRefreshing = wasRefreshing;
        }
    }

    private void UpdateReasoningSelectorState(
        IReadOnlyList<ChatReasoningOption> reasoningOptions,
        int selectedReasoningIndex)
    {
        ArgumentNullException.ThrowIfNull(reasoningOptions);

        var wasRefreshing = _selectorsRefreshing;
        _selectorsRefreshing = true;
        try
        {
            _selectorState.SetReasoningSelection(reasoningOptions, selectedReasoningIndex);
            _syncModelProviderSelectorItems();
        }
        finally
        {
            _selectorsRefreshing = wasRefreshing;
        }
    }

    private PromptComposerProjection BuildPromptComposerProjection()
    {
        var selection = _threadSelection.Selection;
        var selectedThread = selection.Target is WorkspaceTarget.Thread ? _threadSelection.GetSelectedThread() : null;
        var backendId = selectedThread is not null ? new AgentBackendId(selectedThread.BackendId) : GetPreferredModelProviderId();
        if (!_chatBackendStates.TryGetValue(backendId.Value, out var backendState) ||
            string.IsNullOrWhiteSpace(backendState.DisplayName))
        {
            backendState = _chatBackendStates.Values.FirstOrDefault()
                ?? new ChatBackendState(GetDefaultBackendId(), GetDefaultBackendId().Value);
        }

        return PromptComposerProjectionBuilder.Build(
            selectedThread,
            _threadSelection.GetSelectedProject(),
            selection.Target is WorkspaceTarget.Draft { IsGlobal: true },
            backendState.DisplayName,
            backendState.Availability,
            HasAnyReadyChatBackend(),
            _threadSelection.HasOpenDraftTab(),
            _threadSelection.OpenThreadIds.Count + (_threadSelection.HasOpenDraftTab() ? 1 : 0),
            selectedThread?.ThreadId,
            selectedThread is not null &&
            _threadSelection.FindOpenThread(selectedThread.ThreadId) is { } selectedTab &&
            selectedTab.QueuedPrompts.Count > 0,
            selectedThread is not null,
            selectedThread is not null &&
            _threadSelection.FindOpenThread(selectedThread.ThreadId) is { } selectedThreadTab &&
            selectedThread.StartedAt is not null &&
            !selectedThreadTab.StatusBusy,
            selectedThread is not null &&
            _threadSelection.FindOpenThread(selectedThread.ThreadId) is { } selectedAbortTab &&
            selectedAbortTab.StatusBusy);
    }

    private AgentBackendId GetDefaultBackendId()
    {
        return _backendDescriptors.FirstOrDefault()?.BackendId ?? AgentBackendIds.Codex;
    }

    private static ModelProviderPreference CreatePreference(
        AgentBackendId modelProviderId,
        string? modelId,
        AgentReasoningEffort? reasoningEffort)
        => new(new ModelProviderId(modelProviderId.Value), modelId, reasoningEffort);

    private IReadOnlyList<string>? GetConfiguredProviderKeys()
        => _getConfiguredProviderKeys?.Invoke();

    private AgentBackendId? ResolveConfiguredDefaultBackendId(IReadOnlyList<ChatBackendOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var projectRoot = _threadSelection.GetSelectedProject()?.ProjectPath;
        var configuredProviderKey = _getEffectiveDefaultProviderKey(projectRoot);
        if (string.IsNullOrWhiteSpace(configuredProviderKey))
        {
            return null;
        }

        return options.FirstOrDefault(option =>
                string.Equals(option.BackendId.Value, configuredProviderKey, StringComparison.OrdinalIgnoreCase))
            ?.BackendId;
    }
}
