using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Shell;
using CodeAlta.ViewModels;
using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.App;

internal sealed class ShellWorkspaceCoordinator
{
    private readonly CodeAltaShellViewModel _shellViewModel;
    private readonly SessionUsageViewModel _sessionUsageViewModel;
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates;
    private readonly Func<WorkThreadDescriptor?> _getSelectedThread;
    private readonly Func<ProjectDescriptor?> _getSelectedProject;
    private readonly Func<WorkThreadDescriptor, OpenThreadState> _ensureThreadTab;
    private readonly Func<bool> _getGlobalScopeSelected;
    private readonly Func<AgentBackendId> _getPreferredBackendId;
    private readonly Func<(bool HasStatus, string Message, StatusTone Tone)> _getPromptUnavailableStatus;
    private readonly Func<string, bool> _isSelectedThread;
    private readonly Func<Visual?> _getThreadPaneLayout;
    private readonly Func<VSplitter?> _getThreadBodySplitter;
    private readonly Func<ChatPromptEditor?> _getThreadInput;
    private readonly Action _ensureSelectionDefaults;
    private readonly Action _refreshSidebarProjection;
    private readonly Action _syncSidebarSelectionToCurrentState;
    private readonly Action _refreshChatSelectorsForDraftScope;
    private readonly Action<OpenThreadState> _refreshChatSelectorsForThread;
    private readonly Action _updatePromptAvailabilityUi;
    private readonly Action _syncThreadTabControl;
    private readonly Action<Action> _dispatchToUi;
    private readonly Action _verifyBindableAccess;
    private readonly string _globalRoot;
    private readonly State<int> _viewRefreshState = new(0);
    private readonly State<int> _usageRefreshState = new(0);

    public ShellWorkspaceCoordinator(
        CodeAltaShellViewModel shellViewModel,
        SessionUsageViewModel sessionUsageViewModel,
        Dictionary<string, ChatBackendState> chatBackendStates,
        Func<WorkThreadDescriptor?> getSelectedThread,
        Func<ProjectDescriptor?> getSelectedProject,
        Func<WorkThreadDescriptor, OpenThreadState> ensureThreadTab,
        Func<bool> getGlobalScopeSelected,
        Func<AgentBackendId> getPreferredBackendId,
        Func<(bool HasStatus, string Message, StatusTone Tone)> getPromptUnavailableStatus,
        Func<string, bool> isSelectedThread,
        Func<Visual?> getThreadPaneLayout,
        Func<VSplitter?> getThreadBodySplitter,
        Func<ChatPromptEditor?> getThreadInput,
        Action ensureSelectionDefaults,
        Action refreshSidebarProjection,
        Action syncSidebarSelectionToCurrentState,
        Action refreshChatSelectorsForDraftScope,
        Action<OpenThreadState> refreshChatSelectorsForThread,
        Action updatePromptAvailabilityUi,
        Action syncThreadTabControl,
        Action<Action> dispatchToUi,
        Action verifyBindableAccess,
        string globalRoot)
    {
        ArgumentNullException.ThrowIfNull(shellViewModel);
        ArgumentNullException.ThrowIfNull(sessionUsageViewModel);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(getSelectedThread);
        ArgumentNullException.ThrowIfNull(getSelectedProject);
        ArgumentNullException.ThrowIfNull(ensureThreadTab);
        ArgumentNullException.ThrowIfNull(getGlobalScopeSelected);
        ArgumentNullException.ThrowIfNull(getPreferredBackendId);
        ArgumentNullException.ThrowIfNull(getPromptUnavailableStatus);
        ArgumentNullException.ThrowIfNull(isSelectedThread);
        ArgumentNullException.ThrowIfNull(getThreadPaneLayout);
        ArgumentNullException.ThrowIfNull(getThreadBodySplitter);
        ArgumentNullException.ThrowIfNull(getThreadInput);
        ArgumentNullException.ThrowIfNull(ensureSelectionDefaults);
        ArgumentNullException.ThrowIfNull(refreshSidebarProjection);
        ArgumentNullException.ThrowIfNull(syncSidebarSelectionToCurrentState);
        ArgumentNullException.ThrowIfNull(refreshChatSelectorsForDraftScope);
        ArgumentNullException.ThrowIfNull(refreshChatSelectorsForThread);
        ArgumentNullException.ThrowIfNull(updatePromptAvailabilityUi);
        ArgumentNullException.ThrowIfNull(syncThreadTabControl);
        ArgumentNullException.ThrowIfNull(dispatchToUi);
        ArgumentNullException.ThrowIfNull(verifyBindableAccess);
        ArgumentException.ThrowIfNullOrWhiteSpace(globalRoot);

        _shellViewModel = shellViewModel;
        _sessionUsageViewModel = sessionUsageViewModel;
        _chatBackendStates = chatBackendStates;
        _getSelectedThread = getSelectedThread;
        _getSelectedProject = getSelectedProject;
        _ensureThreadTab = ensureThreadTab;
        _getGlobalScopeSelected = getGlobalScopeSelected;
        _getPreferredBackendId = getPreferredBackendId;
        _getPromptUnavailableStatus = getPromptUnavailableStatus;
        _isSelectedThread = isSelectedThread;
        _getThreadPaneLayout = getThreadPaneLayout;
        _getThreadBodySplitter = getThreadBodySplitter;
        _getThreadInput = getThreadInput;
        _ensureSelectionDefaults = ensureSelectionDefaults;
        _refreshSidebarProjection = refreshSidebarProjection;
        _syncSidebarSelectionToCurrentState = syncSidebarSelectionToCurrentState;
        _refreshChatSelectorsForDraftScope = refreshChatSelectorsForDraftScope;
        _refreshChatSelectorsForThread = refreshChatSelectorsForThread;
        _updatePromptAvailabilityUi = updatePromptAvailabilityUi;
        _syncThreadTabControl = syncThreadTabControl;
        _dispatchToUi = dispatchToUi;
        _verifyBindableAccess = verifyBindableAccess;
        _globalRoot = globalRoot;
    }

    public ComputedVisual CreateComputedVisual(Func<Visual> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return new ComputedVisual(
            () =>
            {
                var _ = _viewRefreshState.Value;
                return build();
            });
    }

    public ComputedVisual CreateUsageComputedVisual(Func<Visual> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return new ComputedVisual(
            () =>
            {
                var _ = _usageRefreshState.Value;
                return build();
            });
    }

    public void RefreshShellChrome()
        => _dispatchToUi(RefreshShellChromeCore);

    public void RefreshCatalogAndThreadWorkspace()
        => _dispatchToUi(RefreshCatalogAndThreadWorkspaceCore);

    public void RefreshHeaderAndThreadWorkspace()
        => _dispatchToUi(RefreshHeaderAndThreadWorkspaceCore);

    public void RefreshSelectionAndThreadWorkspace()
        => _dispatchToUi(RefreshSelectionAndThreadWorkspaceCore);

    public void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        _dispatchToUi(
            () =>
            {
                _verifyBindableAccess();
                _shellViewModel.StatusText = message;
                _shellViewModel.StatusBusy = showSpinner;
                _shellViewModel.StatusTone = tone;
                _shellViewModel.StatusIconMarkup = StatusVisualFormatter.BuildStatusIconMarkup(tone);
            });
    }

    public void SetThreadStatus(
        OpenThreadState tab,
        string message,
        bool showSpinner = false,
        StatusTone tone = StatusTone.Info,
        bool hasCustomStatus = true)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var changed =
            !string.Equals(tab.StatusMessage, message, StringComparison.Ordinal) ||
            tab.StatusBusy != showSpinner ||
            tab.StatusTone != tone ||
            tab.HasCustomStatus != hasCustomStatus;

        tab.StatusMessage = message;
        tab.StatusBusy = showSpinner;
        tab.StatusTone = tone;
        tab.HasCustomStatus = hasCustomStatus;

        if (_isSelectedThread(tab.Thread.ThreadId))
        {
            SetReadyStatusForCurrentSelection();
        }

        if (changed)
        {
            InvalidateThreadChrome();
        }
    }

    public void ClearThreadStatus(OpenThreadState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        SetThreadStatus(
            tab,
            ShellTextFormatter.BuildReadyStatusText(tab.Thread, _getSelectedProject(), globalScopeSelected: false),
            tone: StatusTone.Ready,
            hasCustomStatus: false);
    }

    public void InvalidateSelectedSessionUsage()
    {
        _dispatchToUi(
            () =>
            {
                SyncSelectedSessionUsageViewModel();
                _usageRefreshState.Value++;
            });
    }

    public void InvalidateThreadChrome()
        => _dispatchToUi(() => _viewRefreshState.Value++);

    public void SetReadyStatusForCurrentSelection()
    {
        var selectedThread = _getSelectedThread();
        var readyMessage = ShellTextFormatter.BuildReadyStatusText(selectedThread, _getSelectedProject(), _getGlobalScopeSelected());
        var promptUnavailable = _getPromptUnavailableStatus();
        if (selectedThread is not null)
        {
            var selectedTab = _ensureThreadTab(selectedThread);
            var snapshot = SelectionStatusResolver.Resolve(
                readyMessage,
                selectedTab.HasCustomStatus,
                selectedTab.ViewModel.StatusMessage,
                selectedTab.ViewModel.StatusBusy,
                selectedTab.ViewModel.StatusTone,
                promptUnavailable.HasStatus,
                promptUnavailable.Message,
                promptUnavailable.Tone);
            SetStatus(snapshot.Message, snapshot.Busy, snapshot.Tone);
            return;
        }

        if (promptUnavailable.HasStatus)
        {
            SetStatus(promptUnavailable.Message, tone: promptUnavailable.Tone);
            return;
        }

        SetStatus(readyMessage, tone: StatusTone.Ready);
    }

    public void SetShellInitialized(bool isInitialized)
        => _dispatchToUi(() => _shellViewModel.IsInitialized = isInitialized);

    public string BuildHeaderText()
    {
        return ShellTextFormatter.BuildHeaderText(
            _getSelectedThread(),
            _getSelectedProject(),
            _globalRoot,
            _getPreferredBackendId().Value,
            _getGlobalScopeSelected());
    }

    private void RefreshHeaderAndThreadWorkspaceCore()
    {
        _verifyBindableAccess();
        _ensureSelectionDefaults();
        _shellViewModel.HeaderText = BuildHeaderText();
        RefreshThreadWorkspaceCore();
    }

    private void RefreshShellChromeCore()
    {
        _verifyBindableAccess();
        _ensureSelectionDefaults();
        _shellViewModel.HeaderText = BuildHeaderText();
        _refreshSidebarProjection();
    }

    private void RefreshCatalogAndThreadWorkspaceCore()
    {
        RefreshShellChromeCore();
        RefreshThreadWorkspaceCore();
    }

    private void RefreshSelectionAndThreadWorkspaceCore()
    {
        _verifyBindableAccess();
        _ensureSelectionDefaults();
        _shellViewModel.HeaderText = BuildHeaderText();
        _syncSidebarSelectionToCurrentState();
        RefreshThreadWorkspaceCore();
    }

    private void RefreshThreadWorkspaceCore()
    {
        SyncSelectedSessionUsageViewModel();
        _viewRefreshState.Value++;
        _usageRefreshState.Value++;
        RefreshThreadPaneContent();
    }

    private void RefreshThreadPaneContent()
    {
        if (_getThreadPaneLayout() is null || _getThreadBodySplitter() is not { } threadBodySplitter || _getThreadInput() is null)
        {
            return;
        }

        _syncThreadTabControl();

        var selectedThread = _getSelectedThread();
        if (selectedThread is null)
        {
            _refreshChatSelectorsForDraftScope();
            _updatePromptAvailabilityUi();
            threadBodySplitter.First = WelcomePaneFactory.Build(_getSelectedProject(), _getGlobalScopeSelected());
            SetReadyStatusForCurrentSelection();
            return;
        }

        var tab = _ensureThreadTab(selectedThread);
        _refreshChatSelectorsForThread(tab);
        _updatePromptAvailabilityUi();
        threadBodySplitter.First = tab.Timeline.Flow;
        SetReadyStatusForCurrentSelection();
    }

    private void SyncSelectedSessionUsageViewModel()
    {
        _verifyBindableAccess();
        var selectedThread = _getSelectedThread();
        if (selectedThread is not null)
        {
            var tab = _ensureThreadTab(selectedThread);
            var backendState = _chatBackendStates[tab.BackendId.Value];
            _sessionUsageViewModel.Usage = tab.Usage;
            _sessionUsageViewModel.BackendName = backendState.DisplayName;
            _sessionUsageViewModel.ModelName = tab.ModelId ?? backendState.SelectedModelId;
            return;
        }

        var backendId = _getPreferredBackendId();
        var draftBackendState = _chatBackendStates[backendId.Value];
        _sessionUsageViewModel.Usage = null;
        _sessionUsageViewModel.BackendName = draftBackendState.DisplayName;
        _sessionUsageViewModel.ModelName = draftBackendState.SelectedModelId;
    }
}