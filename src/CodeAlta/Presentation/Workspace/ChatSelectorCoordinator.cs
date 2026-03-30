using CodeAlta.App.State;
using CodeAlta.Threading;
using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.App.Context;
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
    private readonly ChatSelectorUiContext _uiContext;
    private readonly ThreadSelectionContext _threadSelection;
    private readonly ChatPreferenceContext _preferences;
    private readonly WorkspaceRefreshContext _workspaceRefresh;
    private bool _selectorsRefreshing;

    public ChatSelectorCoordinator(
        ThreadWorkspaceViewModel workspaceViewModel,
        PromptComposerViewModel promptComposerViewModel,
        Dictionary<string, ChatBackendState> chatBackendStates,
        ChatSelectorUiContext uiContext,
        ThreadSelectionContext threadSelection,
        ChatPreferenceContext preferences,
        WorkspaceRefreshContext workspaceRefresh)
    {
        ArgumentNullException.ThrowIfNull(workspaceViewModel);
        ArgumentNullException.ThrowIfNull(promptComposerViewModel);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(uiContext);
        ArgumentNullException.ThrowIfNull(threadSelection);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(workspaceRefresh);

        _workspaceViewModel = workspaceViewModel;
        _promptComposerViewModel = promptComposerViewModel;
        _chatBackendStates = chatBackendStates;
        _uiContext = uiContext;
        _threadSelection = threadSelection;
        _preferences = preferences;
        _workspaceRefresh = workspaceRefresh;
    }

    public void RefreshForDraftScope(AgentBackendId? preferredBackendId = null)
    {
        _uiContext.VerifyBindableAccess();
        _selectorsRefreshing = true;
        try
        {
            var backendSelect = _uiContext.GetChatBackendSelect()!;
            var modelSelect = _uiContext.GetChatModelSelect()!;
            var reasoningSelect = _uiContext.GetChatReasoningSelect()!;
            var backendOptions = ChatBackendPresentation.BuildBackendOptions();
            ChatBackendPresentation.ReplaceSelectItems(backendSelect, backendOptions);

            var backendId = preferredBackendId ?? GetPreferredDraftBackendId(backendOptions);
            var backendIndex = Math.Max(0, backendOptions.FindIndex(option => string.Equals(option.BackendId.Value, backendId.Value, StringComparison.OrdinalIgnoreCase)));
            backendSelect.SelectedIndex = backendIndex;
            backendSelect.IsEnabled = true;

            var backendState = _chatBackendStates[backendOptions[backendIndex].BackendId.Value];
            _preferences.ApplyDraftBackendPreference(backendState);
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
            _workspaceViewModel.AutoScroll = true;

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

        _uiContext.VerifyBindableAccess();
        _selectorsRefreshing = true;
        try
        {
            var backendSelect = _uiContext.GetChatBackendSelect()!;
            var modelSelect = _uiContext.GetChatModelSelect()!;
            var reasoningSelect = _uiContext.GetChatReasoningSelect()!;
            var backendOptions = ChatBackendPresentation.BuildBackendOptions();
            ChatBackendPresentation.ReplaceSelectItems(backendSelect, backendOptions);
            backendSelect.SelectedIndex = Math.Clamp(
                backendOptions.FindIndex(option => string.Equals(option.BackendId.Value, tab.BackendId.Value, StringComparison.OrdinalIgnoreCase)),
                0,
                Math.Max(0, backendOptions.Count - 1));

            var backendState = _chatBackendStates[tab.BackendId.Value];
            _preferences.ApplyThreadPreference(tab);

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
            _workspaceViewModel.AutoScroll = tab.AutoScroll;

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

        var thread = _threadSelection.GetSelectedThread();
        if (thread is null)
        {
            RefreshForDraftScope(options[newIndex].BackendId);
            _workspaceRefresh.InvalidateSelectedSessionUsage();
            return;
        }

        if (thread.IsBackendLocked)
        {
            return;
        }

        var tab = _threadSelection.EnsureThreadTab(thread);
        tab.BackendId = options[newIndex].BackendId;
        _workspaceRefresh.RefreshHeaderAndThreadWorkspace();
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
            _preferences.RememberGlobalBackendPreference(backendId, draftBackendState.SelectedModelId, draftBackendState.SelectedReasoningEffort);
            RefreshForDraftScope(backendId);
            _workspaceRefresh.InvalidateSelectedSessionUsage();
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
        var selectedModel = ChatBackendPreferenceCoordinator.FindModel(backendState.Models, tab.ModelId);
        tab.ReasoningEffort = ChatBackendPresentation.ResolvePreferredReasoningEffort(selectedModel, preferredReasoningEffort: null);
        _preferences.RememberThreadPreference(tab.Thread.ThreadId, tab.ModelId, tab.ReasoningEffort, tab.AutoScroll, true);
        backendState.SelectedModelId = tab.ModelId;
        backendState.SelectedReasoningEffort = tab.ReasoningEffort;
        _preferences.RememberGlobalBackendPreference(tab.BackendId, tab.ModelId, tab.ReasoningEffort);
        _workspaceRefresh.RefreshHeaderAndThreadWorkspace();
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
            var backendId = GetPreferredBackendId();
            var draftBackendState = _chatBackendStates[backendId.Value];
            var draftSelectedModel = draftBackendState.Models.FirstOrDefault(model => string.Equals(model.Id, draftBackendState.SelectedModelId, StringComparison.Ordinal));
            var draftOptions = ChatBackendPresentation.BuildReasoningOptions(draftSelectedModel);
            if ((uint)newIndex >= (uint)draftOptions.Count)
            {
                return;
            }

            draftBackendState.SelectedReasoningEffort = draftOptions[newIndex].Effort;
            _preferences.RememberGlobalBackendPreference(backendId, draftBackendState.SelectedModelId, draftBackendState.SelectedReasoningEffort);
            _workspaceRefresh.InvalidateSelectedSessionUsage();
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
        _preferences.RememberThreadPreference(tab.Thread.ThreadId, tab.ModelId, tab.ReasoningEffort, tab.AutoScroll, true);
        backendState.SelectedModelId = tab.ModelId;
        backendState.SelectedReasoningEffort = tab.ReasoningEffort;
        _preferences.RememberGlobalBackendPreference(tab.BackendId, tab.ModelId, tab.ReasoningEffort);
    }

    public void OnAutoScrollChanged()
    {
        if (_selectorsRefreshing)
        {
            return;
        }

        var thread = _threadSelection.GetSelectedThread();
        if (thread is null)
        {
            return;
        }

        var tab = _threadSelection.EnsureThreadTab(thread);
        if (tab.AutoScroll == _workspaceViewModel.AutoScroll)
        {
            return;
        }

        tab.AutoScroll = _workspaceViewModel.AutoScroll;
        _preferences.RememberThreadPreference(tab.Thread.ThreadId, tab.ModelId, tab.ReasoningEffort, tab.AutoScroll, true);
    }

    public AgentBackendId GetPreferredBackendId()
    {
        return UiDispatch.Invoke(
            _uiContext.GetUiDispatcher(),
            () =>
            {
                var options = ChatBackendPresentation.BuildBackendOptions();
                var chatBackendSelect = _uiContext.GetChatBackendSelect();
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
        _uiContext.VerifyBindableAccess();
        var projection = BuildPromptComposerProjection();
        _promptComposerViewModel.Placeholder = projection.Placeholder;
        _promptComposerViewModel.IsEnabled = projection.IsEnabled;
        _promptComposerViewModel.CanSend = projection.CanSend;
        _promptComposerViewModel.CanSteer = projection.CanSteer;
        _promptComposerViewModel.CanDelegate = projection.CanDelegate;
        _promptComposerViewModel.CanAbort = projection.CanAbort;
        _promptComposerViewModel.CanCompact = projection.CanCompact;
        _promptComposerViewModel.CanCloseTab = projection.CanCloseTab;
        _promptComposerViewModel.CanClearQueue = projection.CanClearQueue;
        _promptComposerViewModel.CanAlwaysEnqueue = projection.CanAlwaysEnqueue;
    }

    private AgentBackendId GetPreferredDraftBackendId(IReadOnlyList<ChatBackendOption> backendOptions)
    {
        var chatBackendSelect = _uiContext.GetChatBackendSelect();
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
        var selectedThread = _threadSelection.GetSelectedThread();
        var backendId = selectedThread is not null ? new AgentBackendId(selectedThread.BackendId) : GetPreferredBackendId();
        if (!_chatBackendStates.TryGetValue(backendId.Value, out var backendState) ||
            string.IsNullOrWhiteSpace(backendState.DisplayName))
        {
            backendState = _chatBackendStates[AgentBackendIds.Codex.Value];
        }

        return PromptComposerProjectionBuilder.Build(
            selectedThread,
            _threadSelection.GetSelectedProject(),
            _threadSelection.GlobalScopeSelected,
            backendState.DisplayName,
            backendState.Availability,
            HasAnyReadyChatBackend(),
            _threadSelection.DraftTabOpen,
            _threadSelection.SelectedThreadId,
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
}
