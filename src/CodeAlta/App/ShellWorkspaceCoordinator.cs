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
    private readonly SessionUsageViewModel _sessionUsageViewModel;
    private readonly Dictionary<string, ChatBackendState> _chatBackendStates;
    private readonly ThreadSelectionContext _threadSelection;
    private readonly ShellWorkspaceContext _workspaceContext;
    private readonly string _globalRoot;
    private readonly State<int> _viewRefreshState = new(0);
    private readonly State<int> _usageRefreshState = new(0);

    public ShellWorkspaceCoordinator(
        CodeAltaShellViewModel shellViewModel,
        SessionUsageViewModel sessionUsageViewModel,
        Dictionary<string, ChatBackendState> chatBackendStates,
        ThreadSelectionContext threadSelection,
        ShellWorkspaceContext workspaceContext,
        string globalRoot)
    {
        ArgumentNullException.ThrowIfNull(shellViewModel);
        ArgumentNullException.ThrowIfNull(sessionUsageViewModel);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(threadSelection);
        ArgumentNullException.ThrowIfNull(workspaceContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(globalRoot);

        _shellViewModel = shellViewModel;
        _sessionUsageViewModel = sessionUsageViewModel;
        _chatBackendStates = chatBackendStates;
        _threadSelection = threadSelection;
        _workspaceContext = workspaceContext;
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
        => _workspaceContext.DispatchToUi(RefreshShellChromeCore);

    public void RefreshCatalogAndThreadWorkspace()
        => _workspaceContext.DispatchToUi(RefreshCatalogAndThreadWorkspaceCore);

    public void RefreshHeaderAndThreadWorkspace()
        => _workspaceContext.DispatchToUi(RefreshHeaderAndThreadWorkspaceCore);

    public void RefreshSelectionAndThreadWorkspace()
        => _workspaceContext.DispatchToUi(RefreshSelectionAndThreadWorkspaceCore);

    public void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        _workspaceContext.DispatchToUi(
            () =>
            {
                _workspaceContext.VerifyBindableAccess();
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

        if (_threadSelection.IsSelectedThread(tab.Thread.ThreadId))
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
            ShellTextFormatter.BuildReadyStatusText(tab.Thread, _threadSelection.GetSelectedProject(), globalScopeSelected: false),
            tone: StatusTone.Ready,
            hasCustomStatus: false);
    }

    public void InvalidateSelectedSessionUsage()
    {
        _workspaceContext.DispatchToUi(
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
        var selectedThread = _threadSelection.GetSelectedThread();
        var readyMessage = ShellTextFormatter.BuildReadyStatusText(selectedThread, _threadSelection.GetSelectedProject(), _threadSelection.GlobalScopeSelected);
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
        => _workspaceContext.DispatchToUi(() => _shellViewModel.IsInitialized = isInitialized);

    public string BuildHeaderText()
    {
        return ShellTextFormatter.BuildHeaderText(
            _threadSelection.GetSelectedThread(),
            _threadSelection.GetSelectedProject(),
            _globalRoot,
            _workspaceContext.GetPreferredBackendId().Value,
            _threadSelection.GlobalScopeSelected);
    }

    private void RefreshHeaderAndThreadWorkspaceCore()
    {
        _workspaceContext.VerifyBindableAccess();
        _workspaceContext.EnsureSelectionDefaults();
        _shellViewModel.HeaderText = BuildHeaderText();
        RefreshThreadWorkspaceCore();
    }

    private void RefreshShellChromeCore()
    {
        _workspaceContext.VerifyBindableAccess();
        _workspaceContext.EnsureSelectionDefaults();
        _shellViewModel.HeaderText = BuildHeaderText();
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
        _shellViewModel.HeaderText = BuildHeaderText();
        _workspaceContext.SyncSidebarSelectionToCurrentState();
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
        if (_workspaceContext.GetThreadPaneLayout() is null ||
            _workspaceContext.GetThreadBodySplitter() is not { } threadBodySplitter ||
            _workspaceContext.GetThreadInput() is null)
        {
            return;
        }

        _workspaceContext.SyncThreadTabControl();

        var selectedThread = _threadSelection.GetSelectedThread();
        if (selectedThread is null)
        {
            _workspaceContext.RefreshQueuedPromptList();
            _workspaceContext.RefreshChatSelectorsForDraftScope();
            _workspaceContext.UpdatePromptAvailabilityUi();
            threadBodySplitter.First = WelcomePaneFactory.Build(_threadSelection.GetSelectedProject(), _threadSelection.GlobalScopeSelected);
            SetReadyStatusForCurrentSelection();
            return;
        }

        var tab = _threadSelection.EnsureThreadTab(selectedThread);
        _workspaceContext.RefreshQueuedPromptList();
        _workspaceContext.RefreshChatSelectorsForThread(tab);
        _workspaceContext.UpdatePromptAvailabilityUi();
        threadBodySplitter.First = tab.Timeline.Flow;
        SetReadyStatusForCurrentSelection();
    }

    private void SyncSelectedSessionUsageViewModel()
    {
        _workspaceContext.VerifyBindableAccess();
        var selectedThread = _threadSelection.GetSelectedThread();
        if (selectedThread is not null)
        {
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
