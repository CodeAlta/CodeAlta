using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Chat;
using CodeAlta.Presentation.Prompting;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Presentation.Workspace;

internal sealed class ChatSelectorCoordinator
{
    private readonly ThreadWorkspaceViewModel _workspaceViewModel;
    private readonly PromptComposerViewModel _promptComposerViewModel;
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates;
    private readonly Func<Select<ChatBackendOption>?> _getChatBackendSelect;
    private readonly Func<Select<ChatModelOption>?> _getChatModelSelect;
    private readonly Func<Select<ChatReasoningOption>?> _getChatReasoningSelect;
    private readonly Func<CheckBox?> _getChatAutoScrollCheckBox;
    private readonly Func<WorkThreadDescriptor?> _getSelectedThread;
    private readonly Func<ProjectDescriptor?> _getSelectedProject;
    private readonly Func<WorkThreadDescriptor, OpenThreadState> _ensureThreadTab;
    private readonly Func<bool> _getGlobalScopeSelected;
    private readonly Func<bool> _getDraftTabOpen;
    private readonly Func<string?> _getSelectedThreadId;
    private readonly Action<ChatBackendState> _applyDraftBackendPreference;
    private readonly Action<OpenThreadState> _applyThreadPreference;
    private readonly Action<AgentBackendId, string?, AgentReasoningEffort?> _rememberGlobalBackendPreference;
    private readonly Action<string, string?, AgentReasoningEffort?, bool, bool> _rememberThreadPreference;
    private readonly Action _invalidateSelectedSessionUsage;
    private readonly Action _refreshHeaderAndThreadWorkspace;
    private readonly Action _verifyBindableAccess;
    private readonly Func<IUiDispatcher> _getUiDispatcher;
    private bool _selectorsRefreshing;

    public ChatSelectorCoordinator(
        ThreadWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        Dictionary<string, ChatBackendState> chatBackendStates,
        Func<Select<ChatBackendOption>?> getChatBackendSelect,
        Func<Select<ChatModelOption>?> getChatModelSelect,
        Func<Select<ChatReasoningOption>?> getChatReasoningSelect,
        Func<CheckBox?> getChatAutoScrollCheckBox,
        Func<WorkThreadDescriptor?> getSelectedThread,
        Func<ProjectDescriptor?> getSelectedProject,
        Func<WorkThreadDescriptor, OpenThreadState> ensureThreadTab,
        Func<bool> getGlobalScopeSelected,
        Func<bool> getDraftTabOpen,
        Func<string?> getSelectedThreadId,
        Action<ChatBackendState> applyDraftBackendPreference,
        Action<OpenThreadState> applyThreadPreference,
        Action<AgentBackendId, string?, AgentReasoningEffort?> rememberGlobalBackendPreference,
        Action<string, string?, AgentReasoningEffort?, bool, bool> rememberThreadPreference,
        Action invalidateSelectedSessionUsage,
        Action refreshHeaderAndThreadWorkspace,
        Action verifyBindableAccess,
        Func<IUiDispatcher> getUiDispatcher)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(getChatBackendSelect);
        ArgumentNullException.ThrowIfNull(getChatModelSelect);
        ArgumentNullException.ThrowIfNull(getChatReasoningSelect);
        ArgumentNullException.ThrowIfNull(getChatAutoScrollCheckBox);
        ArgumentNullException.ThrowIfNull(getSelectedThread);
        ArgumentNullException.ThrowIfNull(getSelectedProject);
        ArgumentNullException.ThrowIfNull(ensureThreadTab);
        ArgumentNullException.ThrowIfNull(getGlobalScopeSelected);
        ArgumentNullException.ThrowIfNull(getDraftTabOpen);
        ArgumentNullException.ThrowIfNull(getSelectedThreadId);
        ArgumentNullException.ThrowIfNull(applyDraftBackendPreference);
        ArgumentNullException.ThrowIfNull(applyThreadPreference);
        ArgumentNullException.ThrowIfNull(rememberGlobalBackendPreference);
        ArgumentNullException.ThrowIfNull(rememberThreadPreference);
        ArgumentNullException.ThrowIfNull(invalidateSelectedSessionUsage);
        ArgumentNullException.ThrowIfNull(refreshHeaderAndThreadWorkspace);
        ArgumentNullException.ThrowIfNull(verifyBindableAccess);
        ArgumentNullException.ThrowIfNull(getUiDispatcher);

        _workspaceViewModel = workspaceViewModel;
        _promptComposerViewModel = promptComposerViewModel;
        _chatBackendStates = chatBackendStates;
        _getChatBackendSelect = getChatBackendSelect;
        _getChatModelSelect = getChatModelSelect;
        _getChatReasoningSelect = getChatReasoningSelect;
        _getChatAutoScrollCheckBox = getChatAutoScrollCheckBox;
        _getSelectedThread = getSelectedThread;
        _getSelectedProject = getSelectedProject;
        _ensureThreadTab = ensureThreadTab;
        _getGlobalScopeSelected = getGlobalScopeSelected;
        _getDraftTabOpen = getDraftTabOpen;
        _getSelectedThreadId = getSelectedThreadId;
        _applyDraftBackendPreference = applyDraftBackendPreference;
        _applyThreadPreference = applyThreadPreference;
        _rememberGlobalBackendPreference = rememberGlobalBackendPreference;
        _rememberThreadPreference = rememberThreadPreference;
        _invalidateSelectedSessionUsage = invalidateSelectedSessionUsage;
        _refreshHeaderAndThreadWorkspace = refreshHeaderAndThreadWorkspace;
        _verifyBindableAccess = verifyBindableAccess;
        _getUiDispatcher = getUiDispatcher;
    }

    public void RefreshForDraftScope(AgentBackendId? preferredBackendId = null)
    {
        _verifyBindableAccess();
        _selectorsRefreshing = true;
        try
        {
            var backendSelect = _getChatBackendSelect()!;
            var modelSelect = _getChatModelSelect()!;
            var reasoningSelect = _getChatReasoningSelect()!;
            var autoScrollCheckBox = _getChatAutoScrollCheckBox()!;
            var backendOptions = ChatBackendPresentation.BuildBackendOptions();
            ChatBackendPresentation.ReplaceSelectItems(backendSelect, backendOptions);

            var backendId = preferredBackendId ?? GetPreferredDraftBackendId(backendOptions);
            var backendIndex = Math.Max(0, backendOptions.FindIndex(option => string.Equals(option.BackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase)));
            backendSelect.SelectedIndex = backendIndex;
            backendSelect.IsEnabled = true;

            var backendState = _chatBackendStates[backendOptions[backendIndex].BackendId.Value];
            _applyDraftBackendPreference(backendState);
            var modelOptions = ChatBackendPresentation.BuildModelOptions(backendState);
            ChatBackendPresentation.ReplaceSelectItems(modelSelect, modelOptions);
            modelSelect.SelectedIndex = Math.Clamp(
                modelOptions.FindIndex(option => string.Equals(option.ModelId, backendState.SelectedModelId, StringComparison.Ordinal)),
                0,
                Math.Max(0, modelOptions.Count - 1));
            modelSelect.IsEnabled = backendState.Availability == ChatBackendAvailability.Ready;

            var selectedModel = backendState.Models.FirstOrDefault(model => string.Equals(model.Id, backendState.SelectedModelId, StringComparison.Ordinal))
                                ?? ChatBackendPresentation.GetSelectedModel(backendState);
            var reasoningOptions = ChatBackendPresentation.BuildReasoningOptions(selectedModel);
            ChatBackendPresentation.ReplaceSelectItems(reasoningSelect, reasoningOptions);
            reasoningSelect.SelectedIndex = Math.Clamp(
                reasoningOptions.FindIndex(option => option.Effort == backendState.SelectedReasoningEffort),
                0,
                Math.Max(0, reasoningOptions.Count - 1));
            reasoningSelect.IsEnabled = backendState.Availability == ChatBackendAvailability.Ready;
            autoScrollCheckBox.IsChecked = true;
            autoScrollCheckBox.IsEnabled = false;

            _workspaceViewModel.BackendStatusMarkup = ChatBackendPresentation.BuildBackendStatusMarkup(_chatBackendStates.Values, backendOptions[backendIndex].BackendId, isInitializing: false);
            _workspaceViewModel.CanSelectBackend = true;
            _workspaceViewModel.CanSelectModel = backendState.Availability == ChatBackendAvailability.Ready;
            _workspaceViewModel.CanSelectReasoning = backendState.Availability == ChatBackendAvailability.Ready;
            _workspaceViewModel.CanToggleAutoScroll = false;
        }
        finally
        {
            _selectorsRefreshing = false;
        }
    }

    public void RefreshForThread(OpenThreadState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        _verifyBindableAccess();
        _selectorsRefreshing = true;
        try
        {
            var backendSelect = _getChatBackendSelect()!;
            var modelSelect = _getChatModelSelect()!;
            var reasoningSelect = _getChatReasoningSelect()!;
            var autoScrollCheckBox = _getChatAutoScrollCheckBox()!;
            var backendOptions = ChatBackendPresentation.BuildBackendOptions();
            ChatBackendPresentation.ReplaceSelectItems(backendSelect, backendOptions);
            backendSelect.SelectedIndex = Math.Clamp(
                backendOptions.FindIndex(option => string.Equals(option.BackendId.Value, tab.BackendId.Value, StringComparison.OrdinalIgnoreCase)),
                0,
                Math.Max(0, backendOptions.Count - 1));

            var backendState = _chatBackendStates[tab.BackendId.Value];
            _applyThreadPreference(tab);

            var modelOptions = ChatBackendPresentation.BuildModelOptions(backendState);
            ChatBackendPresentation.ReplaceSelectItems(modelSelect, modelOptions);
            modelSelect.SelectedIndex = Math.Clamp(
                modelOptions.FindIndex(option => string.Equals(option.ModelId, tab.ModelId ?? backendState.SelectedModelId, StringComparison.Ordinal)),
                0,
                Math.Max(0, modelOptions.Count - 1));
            modelSelect.IsEnabled = backendState.Availability == ChatBackendAvailability.Ready;

            var selectedModel = backendState.Models.FirstOrDefault(model =>
                                    string.Equals(model.Id, tab.ModelId, StringComparison.Ordinal))
                                ?? ChatBackendPresentation.GetSelectedModel(backendState);
            var reasoningOptions = ChatBackendPresentation.BuildReasoningOptions(selectedModel);
            ChatBackendPresentation.ReplaceSelectItems(reasoningSelect, reasoningOptions);
            reasoningSelect.SelectedIndex = Math.Clamp(
                reasoningOptions.FindIndex(option => option.Effort == tab.ReasoningEffort),
                0,
                Math.Max(0, reasoningOptions.Count - 1));
            reasoningSelect.IsEnabled = backendState.Availability == ChatBackendAvailability.Ready;
            autoScrollCheckBox.IsChecked = tab.AutoScroll;
            autoScrollCheckBox.IsEnabled = true;

            backendSelect.IsEnabled = false;
            _workspaceViewModel.BackendStatusMarkup = ChatBackendPresentation.BuildBackendStatusMarkup(_chatBackendStates.Values, tab.BackendId, isInitializing: false);
            _workspaceViewModel.CanSelectBackend = false;
            _workspaceViewModel.CanSelectModel = backendState.Availability == ChatBackendAvailability.Ready;
            _workspaceViewModel.CanSelectReasoning = backendState.Availability == ChatBackendAvailability.Ready;
            _workspaceViewModel.CanToggleAutoScroll = true;
        }
        finally
        {
            _selectorsRefreshing = false;
        }
    }

    public void OnBackendSelectionChanged(int newIndex)
    {
        if (_selectorsRefreshing)
        {
            return;
        }

        var options = ChatBackendPresentation.BuildBackendOptions();
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        var thread = _getSelectedThread();
        if (thread is null)
        {
            RefreshForDraftScope(options[newIndex].BackendId);
            _invalidateSelectedSessionUsage();
            return;
        }

        if (thread.IsBackendLocked)
        {
            return;
        }

        var tab = _ensureThreadTab(thread);
        tab.BackendId = options[newIndex].BackendId;
        _refreshHeaderAndThreadWorkspace();
    }

    public void OnModelSelectionChanged(int newIndex)
    {
        if (_selectorsRefreshing)
        {
            return;
        }

        var thread = _getSelectedThread();
        if (thread is null)
        {
            var backendId = GetPreferredBackendId();
            var draftBackendState = _chatBackendStates[backendId.Value];
            var draftOptions = ChatBackendPresentation.BuildModelOptions(draftBackendState);
            if ((uint)newIndex >= (uint)draftOptions.Count)
            {
                return;
            }

            draftBackendState.SelectedModelId = draftOptions[newIndex].ModelId;
            var preferredModel = ChatBackendPreferenceCoordinator.FindModel(draftBackendState.Models, draftBackendState.SelectedModelId);
            draftBackendState.SelectedReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(preferredModel, preferredReasoningEffort: null);
            _rememberGlobalBackendPreference(backendId, draftBackendState.SelectedModelId, draftBackendState.SelectedReasoningEffort);
            RefreshForDraftScope(backendId);
            _invalidateSelectedSessionUsage();
            return;
        }

        var tab = _ensureThreadTab(thread);
        var backendState = _chatBackendStates[tab.BackendId.Value];
        var options = ChatBackendPresentation.BuildModelOptions(backendState);
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        tab.ModelId = options[newIndex].ModelId;
        var selectedModel = ChatBackendPreferenceCoordinator.FindModel(backendState.Models, tab.ModelId);
        tab.ReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(selectedModel, preferredReasoningEffort: null);
        _rememberThreadPreference(tab.Thread.ThreadId, tab.ModelId, tab.ReasoningEffort, tab.AutoScroll, true);
        backendState.SelectedModelId = tab.ModelId;
        backendState.SelectedReasoningEffort = tab.ReasoningEffort;
        _rememberGlobalBackendPreference(tab.BackendId, tab.ModelId, tab.ReasoningEffort);
        _refreshHeaderAndThreadWorkspace();
    }

    public void OnReasoningSelectionChanged(int newIndex)
    {
        if (_selectorsRefreshing)
        {
            return;
        }

        var thread = _getSelectedThread();
        if (thread is null)
        {
            var backendId = GetPreferredBackendId();
            var draftBackendState = _chatBackendStates[backendId.Value];
            var draftSelectedModel = draftBackendState.Models.FirstOrDefault(model => string.Equals(model.Id, draftBackendState.SelectedModelId, StringComparison.Ordinal));
            var draftOptions = ChatBackendPresentation.BuildReasoningOptions(draftSelectedModel);
            if ((uint)newIndex >= (uint)draftOptions.Count)
            {
                return;
            }

            draftBackendState.SelectedReasoningEffort = draftOptions[newIndex].Effort;
            _rememberGlobalBackendPreference(backendId, draftBackendState.SelectedModelId, draftBackendState.SelectedReasoningEffort);
            _invalidateSelectedSessionUsage();
            return;
        }

        var tab = _ensureThreadTab(thread);
        var backendState = _chatBackendStates[tab.BackendId.Value];
        var selectedModel = backendState.Models.FirstOrDefault(model => string.Equals(model.Id, tab.ModelId, StringComparison.Ordinal));
        var options = ChatBackendPresentation.BuildReasoningOptions(selectedModel);
        if ((uint)newIndex >= (uint)options.Count)
        {
            return;
        }

        tab.ReasoningEffort = options[newIndex].Effort;
        _rememberThreadPreference(tab.Thread.ThreadId, tab.ModelId, tab.ReasoningEffort, tab.AutoScroll, true);
        backendState.SelectedModelId = tab.ModelId;
        backendState.SelectedReasoningEffort = tab.ReasoningEffort;
        _rememberGlobalBackendPreference(tab.BackendId, tab.ModelId, tab.ReasoningEffort);
    }

    public void OnAutoScrollChanged()
    {
        if (_selectorsRefreshing)
        {
            return;
        }

        var autoScrollCheckBox = _getChatAutoScrollCheckBox();
        if (autoScrollCheckBox is null)
        {
            return;
        }

        var thread = _getSelectedThread();
        if (thread is null)
        {
            return;
        }

        var tab = _ensureThreadTab(thread);
        if (tab.AutoScroll == autoScrollCheckBox.IsChecked)
        {
            return;
        }

        tab.AutoScroll = autoScrollCheckBox.IsChecked;
        _rememberThreadPreference(tab.Thread.ThreadId, tab.ModelId, tab.ReasoningEffort, tab.AutoScroll, true);
    }

    public AgentBackendId GetPreferredBackendId()
    {
        return UiDispatch.Invoke(
            _getUiDispatcher(),
            () =>
            {
                var options = ChatBackendPresentation.BuildBackendOptions();
                var chatBackendSelect = _getChatBackendSelect();
                if (chatBackendSelect is not null &&
                    (uint)chatBackendSelect.SelectedIndex < (uint)options.Count)
                {
                    return options[chatBackendSelect.SelectedIndex].BackendId;
                }

                var readyBackend = options.FirstOrDefault(option => IsChatBackendReady(option.BackendId));
                if (readyBackend is not null)
                {
                    return readyBackend.BackendId;
                }

                return AgentBackendIds.Codex;
            });
    }

    public bool IsChatBackendReady(AgentBackendId backendId)
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

    public void UpdatePromptAvailabilityUi()
    {
        _verifyBindableAccess();
        var projection = BuildPromptComposerProjection();
        _promptComposerViewModel.Placeholder = projection.Placeholder;
        _promptComposerViewModel.IsEnabled = projection.IsEnabled;
        _promptComposerViewModel.CanSend = projection.CanSend;
        _promptComposerViewModel.CanSteer = projection.CanSteer;
        _promptComposerViewModel.CanDelegate = projection.CanDelegate;
        _promptComposerViewModel.CanAbort = projection.CanAbort;
        _promptComposerViewModel.CanCloseTab = projection.CanCloseTab;
    }

    private AgentBackendId GetPreferredDraftBackendId(IReadOnlyList<ChatBackendOption> backendOptions)
    {
        var chatBackendSelect = _getChatBackendSelect();
        if (chatBackendSelect is not null &&
            (uint)chatBackendSelect.SelectedIndex < (uint)backendOptions.Count)
        {
            var current = backendOptions[chatBackendSelect.SelectedIndex].BackendId;
            if (IsChatBackendReady(current))
            {
                return current;
            }
        }

        var readyBackend = backendOptions.FirstOrDefault(option => IsChatBackendReady(option.BackendId));
        if (readyBackend is not null)
        {
            return readyBackend.BackendId;
        }

        return backendOptions.FirstOrDefault()?.BackendId ?? AgentBackendIds.Codex;
    }

    private bool HasAnyReadyChatBackend()
    {
        return _chatBackendStates.Values.Any(static state => state.Availability == ChatBackendAvailability.Ready);
    }

    private PromptComposerProjection BuildPromptComposerProjection()
    {
        var selectedThread = _getSelectedThread();
        var backendId = selectedThread is not null ? new AgentBackendId(selectedThread.BackendId) : GetPreferredBackendId();
        if (!_chatBackendStates.TryGetValue(backendId.Value, out var backendState) ||
            string.IsNullOrWhiteSpace(backendState.DisplayName))
        {
            backendState = _chatBackendStates[AgentBackendIds.Codex.Value];
        }

        return PromptComposerProjectionBuilder.Build(
            selectedThread,
            _getSelectedProject(),
            _getGlobalScopeSelected(),
            backendState.DisplayName,
            backendState.Availability,
            HasAnyReadyChatBackend(),
            _getDraftTabOpen(),
            _getSelectedThreadId());
    }
}