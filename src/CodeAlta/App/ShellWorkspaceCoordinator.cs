using CodeAlta.App.State;
using CodeAlta.Agent;
using CodeAlta.App.Context;
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
    private readonly ThreadWorkspaceViewModel _threadWorkspaceViewModel;
    private readonly SessionUsageViewModel _sessionUsageViewModel;
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates;
    private readonly ThreadSelectionContext _threadSelection;
    private readonly ShellWorkspaceContext _workspaceContext;
    private readonly State<float> _welcomeAnimationPhase01;
    private readonly State<int> _viewRefreshState = new(0);
    private readonly State<int> _usageRefreshState = new(0);

    public ShellWorkspaceCoordinator(
        CodeAltaShellViewModel shellViewModel,
        ThreadWorkspaceViewModel threadWorkspaceViewModel,
        SessionUsageViewModel sessionUsageViewModel,
        Dictionary<string, ChatBackendState> chatBackendStates,
        ThreadSelectionContext threadSelection,
        ShellWorkspaceContext workspaceContext,
        State<float> welcomeAnimationPhase01)
    {
        ArgumentNullException.ThrowIfNull(shellViewModel);
        ArgumentNullException.ThrowIfNull(threadWorkspaceViewModel);
        ArgumentNullException.ThrowIfNull(sessionUsageViewModel);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(threadSelection);
        ArgumentNullException.ThrowIfNull(workspaceContext);
        ArgumentNullException.ThrowIfNull(welcomeAnimationPhase01);

        _shellViewModel = shellViewModel;
        _threadWorkspaceViewModel = threadWorkspaceViewModel;
        _sessionUsageViewModel = sessionUsageViewModel;
        _chatBackendStates = chatBackendStates;
        _threadSelection = threadSelection;
        _workspaceContext = workspaceContext;
        _welcomeAnimationPhase01 = welcomeAnimationPhase01;
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
        => _workspaceContext.DispatchToUi(RefreshShellChromeCore);

    public void RefreshCatalogAndThreadWorkspace()
        => _workspaceContext.DispatchToUi(RefreshCatalogAndThreadWorkspaceCore);

    public void RefreshHeaderAndThreadWorkspace()
        => _workspaceContext.DispatchToUi(RefreshHeaderAndThreadWorkspaceCore);

    public void RefreshSelectionAndThreadWorkspace()
        => _workspaceContext.DispatchToUi(RefreshSelectionAndThreadWorkspaceCore);

    public void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info)
        => SetStatus(message, showSpinner, tone, iconMarkup: null);

    public void SetStatus(string message, bool showSpinner, StatusTone tone, string? iconMarkup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        _workspaceContext.DispatchToUi(
            () =>
            {
                _workspaceContext.VerifyBindableAccess();
                _shellViewModel.StatusText = message;
                _shellViewModel.StatusBusy = showSpinner;
                _shellViewModel.StatusTone = tone;
                _shellViewModel.StatusIconMarkup = iconMarkup ?? StatusVisualFormatter.BuildStatusIconMarkup(tone);
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

        _workspaceContext.DispatchToUi(
            () =>
            {
                _workspaceContext.VerifyBindableAccess();
                var changed =
                    !string.Equals(tab.StatusMessage, message, StringComparison.Ordinal) ||
                    tab.StatusBusy != showSpinner ||
                    tab.StatusTone != tone ||
                    tab.HasCustomStatus != hasCustomStatus;

                tab.StatusMessage = message;
                tab.StatusBusy = showSpinner;
                tab.StatusTone = tone;
                tab.HasCustomStatus = hasCustomStatus;

                if (_threadSelection.IsSelectedThread(tab.Thread.ThreadId))
                {
                    _workspaceContext.UpdatePromptAvailabilityUi();
                    SetReadyStatusForCurrentSelection();
                }

                if (changed)
                {
                    _workspaceContext.RefreshSidebarProjection();
                    _viewRefreshState.Value++;
                }
            });
    }

    public void ClearThreadStatus(OpenThreadState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        SetThreadStatus(
            tab,
            ShellTextFormatter.BuildReadyStatusText(tab.Thread, _threadSelection.GetSelectedProject(), globalScopeSelected: false),
            tone: StatusTone.Ready,
            hasCustomStatus: false);
    }

    public void InvalidateSelectedSessionUsage()
    {
        _workspaceContext.DispatchToUiDeferred(
            () =>
            {
                SyncSelectedSessionUsageViewModel();
                _usageRefreshState.Value++;
            });
    }

    public void InvalidateThreadChrome()
        => _workspaceContext.DispatchToUi(() => _viewRefreshState.Value++);

    public void SetReadyStatusForCurrentSelection()
    {
        var selection = _threadSelection.Selection;
        var selectedThread = selection.Target is WorkspaceTarget.Thread ? _threadSelection.GetSelectedThread() : null;
        var readyMessage = ShellTextFormatter.BuildReadyStatusText(
            selectedThread,
            _threadSelection.GetSelectedProject(),
            selection.Target is WorkspaceTarget.Draft { IsGlobal: true });
        var promptUnavailable = _workspaceContext.GetPromptUnavailableStatus();
        if (selectedThread is not null)
        {
            var selectedTab = _threadSelection.EnsureThreadTab(selectedThread);
            var snapshot = SelectionStatusResolver.Resolve(
                readyMessage,
                selectedTab.HasCustomStatus,
                selectedTab.ViewModel.StatusMessage,
                selectedTab.ViewModel.StatusBusy,
                selectedTab.ViewModel.StatusTone,
                selectedTab.HasPromptDraft,
                promptUnavailable.HasStatus,
                promptUnavailable.Message,
                promptUnavailable.Tone);
            SetStatus(snapshot.Message, snapshot.Busy, snapshot.Tone, snapshot.IconMarkup);
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
        => _workspaceContext.DispatchToUi(() => _shellViewModel.IsInitialized = isInitialized);

    private void RefreshHeaderAndThreadWorkspaceCore()
    {
        _workspaceContext.VerifyBindableAccess();
        _workspaceContext.EnsureSelectionDefaults();
        RefreshThreadWorkspaceCore();
    }

    private void RefreshShellChromeCore()
    {
        _workspaceContext.VerifyBindableAccess();
        _workspaceContext.EnsureSelectionDefaults();
        _workspaceContext.RefreshSidebarProjection();
    }

    private void RefreshCatalogAndThreadWorkspaceCore()
    {
        RefreshShellChromeCore();
        RefreshThreadWorkspaceCore();
    }

    private void RefreshSelectionAndThreadWorkspaceCore()
    {
        _workspaceContext.VerifyBindableAccess();
        _workspaceContext.EnsureSelectionDefaults();
        _workspaceContext.RefreshSidebarProjection();
        RefreshThreadWorkspaceCore();
    }

    private void RefreshThreadWorkspaceCore()
    {
        SyncSelectedSessionUsageViewModel();
        _threadWorkspaceViewModel.CanShowThreadInfo = _threadSelection.GetSelectedThread() is not null;
        _viewRefreshState.Value++;
        _usageRefreshState.Value++;
        RefreshThreadPaneContent();
    }

    private void RefreshThreadPaneContent()
    {
        if (!_workspaceContext.HasWorkspaceSurface())
        {
            return;
        }

        _workspaceContext.SyncThreadTabControl();

        if (_threadSelection.Selection.Target is not WorkspaceTarget.Thread)
        {
            _workspaceContext.RefreshQueuedPromptList();
            _workspaceContext.RefreshChatSelectorsForDraftScope();
            _workspaceContext.SyncPromptDraftText(session: null);
            _workspaceContext.UpdatePromptAvailabilityUi();
            _workspaceContext.SetThreadPaneContent(WelcomePaneFactory.Build(
                _threadSelection.GetSelectedProject(),
                _threadSelection.IsGlobalDraftSelected(),
                _welcomeAnimationPhase01));
            SetReadyStatusForCurrentSelection();
            return;
        }

        var selectedThread = _threadSelection.GetSelectedThread();
        if (selectedThread is null)
        {
            _workspaceContext.RefreshQueuedPromptList();
            _workspaceContext.RefreshChatSelectorsForDraftScope();
            _workspaceContext.SyncPromptDraftText(session: null);
            _workspaceContext.UpdatePromptAvailabilityUi();
            _workspaceContext.SetThreadPaneContent(WelcomePaneFactory.Build(
                _threadSelection.GetSelectedProject(),
                _threadSelection.IsGlobalDraftSelected(),
                _welcomeAnimationPhase01));
            SetReadyStatusForCurrentSelection();
            return;
        }

        var tab = _threadSelection.EnsureThreadTab(selectedThread);
        _workspaceContext.RefreshQueuedPromptList();
        _workspaceContext.RefreshChatSelectorsForThread(tab);
        _workspaceContext.SyncPromptDraftText(tab.Session);
        _workspaceContext.UpdatePromptAvailabilityUi();
        _workspaceContext.SetThreadPaneContent(tab.Timeline.Flow);
        SetReadyStatusForCurrentSelection();
    }

    private void SyncSelectedSessionUsageViewModel()
    {
        _workspaceContext.VerifyBindableAccess();
        if (_threadSelection.Selection.Target is WorkspaceTarget.Thread)
        {
            var selectedThread = _threadSelection.GetSelectedThread();
            if (selectedThread is null)
            {
                return;
            }

            var tab = _threadSelection.EnsureThreadTab(selectedThread);
            var backendState = _chatBackendStates[tab.BackendId.Value];
            _sessionUsageViewModel.Usage = tab.Usage;
            _sessionUsageViewModel.BackendName = backendState.DisplayName;
            _sessionUsageViewModel.ModelName = tab.ModelId ?? backendState.SelectedModelId;
            return;
        }

        var backendId = _workspaceContext.GetPreferredBackendId();
        var draftBackendState = _chatBackendStates[backendId.Value];
        _sessionUsageViewModel.Usage = null;
        _sessionUsageViewModel.BackendName = draftBackendState.DisplayName;
        _sessionUsageViewModel.ModelName = draftBackendState.SelectedModelId;
    }
}
