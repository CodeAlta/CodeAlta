using CodeAlta.Tui.App.Context;
using CodeAlta.Tui.App.State;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Shell;
using CodeAlta.Tui.ViewModels;
using XenoAtom.Terminal.UI.Controls;
using IntState = XenoAtom.Terminal.UI.State<int>;

namespace CodeAlta.Tui.App;

internal sealed class ShellStatusProjectionController
{
    private readonly CodeAltaShellViewModel _shellViewModel;
    private readonly SessionSelectionContext _sessionSelection;
    private readonly ShellWorkspaceContext _workspaceContext;
    private readonly IntState _viewRefreshState;

    public ShellStatusProjectionController(
        CodeAltaShellViewModel shellViewModel,
        SessionSelectionContext sessionSelection,
        ShellWorkspaceContext workspaceContext,
        IntState viewRefreshState)
    {
        ArgumentNullException.ThrowIfNull(shellViewModel);
        ArgumentNullException.ThrowIfNull(sessionSelection);
        ArgumentNullException.ThrowIfNull(workspaceContext);
        ArgumentNullException.ThrowIfNull(viewRefreshState);

        _shellViewModel = shellViewModel;
        _sessionSelection = sessionSelection;
        _workspaceContext = workspaceContext;
        _viewRefreshState = viewRefreshState;
    }

    public void SetProviderSessionLoadStatus(string? message)
    {
        _workspaceContext.DispatchToUi(
            () =>
            {
                _workspaceContext.VerifyBindableAccess();
                ShellViewModelProjection.ApplyProviderSessionLoadStatus(_shellViewModel, message);
                _workspaceContext.SyncActivePromptPanelProjection();
            });
    }

    public void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info)
        => SetStatus(message, showSpinner, tone, iconMarkup: null);

    public void SetStatus(string message, bool showSpinner, StatusTone tone, string? iconMarkup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        _workspaceContext.DispatchToUi(
            () =>
            {
                _workspaceContext.VerifyBindableAccess();
                ShellViewModelProjection.ApplyStatus(
                    _shellViewModel,
                    new ShellStatusSnapshot(message, showSpinner, tone, iconMarkup));
                _workspaceContext.SyncActivePromptPanelProjection();
            });
    }

    public void SetSessionStatus(
        OpenSessionState tab,
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

                if (_sessionSelection.IsSelectedSession(tab.SessionView.SessionId))
                {
                    _workspaceContext.ApplyPromptAvailabilityProjection();
                    SetReadyStatusForCurrentSelection();
                }

                if (changed)
                {
                    _workspaceContext.RefreshSidebarProjection();
                    _viewRefreshState.Value++;
                }
            });
    }

    public void ClearSessionStatus(OpenSessionState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        SetSessionStatus(
            tab,
            ShellTextFormatter.BuildReadyStatusText(tab.SessionView, _sessionSelection.GetSelectedProject(), globalScopeSelected: false),
            tone: StatusTone.Ready,
            hasCustomStatus: false);
    }

    public void RefreshRunningStatusElapsed(DateTimeOffset now)
    {
        _workspaceContext.DispatchToUi(
            () =>
            {
                _workspaceContext.VerifyBindableAccess();
                var selectedSession = _sessionSelection.GetSelectedSession();
                if (selectedSession is null)
                {
                    return;
                }

                var selectedTab = _sessionSelection.EnsureSessionTab(selectedSession);
                if (!selectedTab.HasCustomStatus ||
                    !selectedTab.StatusBusy ||
                    selectedTab.ActiveRunStartedAt is not { } startedAt ||
                    !StatusVisualFormatter.IsThinkingStatusText(selectedTab.StatusMessage))
                {
                    return;
                }

                var elapsed = now - startedAt;
                if (elapsed < TimeSpan.Zero)
                {
                    elapsed = TimeSpan.Zero;
                }

                var message = StatusVisualFormatter.BuildThinkingStatusText(elapsed);
                if (string.Equals(selectedTab.StatusMessage, message, StringComparison.Ordinal))
                {
                    return;
                }

                selectedTab.StatusMessage = message;
                ShellViewModelProjection.ApplyStatus(
                    _shellViewModel,
                    new ShellStatusSnapshot(message, selectedTab.StatusBusy, selectedTab.StatusTone));
                _workspaceContext.SyncActivePromptPanelProjection();
                _workspaceContext.RefreshSidebarProjection();
                _viewRefreshState.Value++;
            });
    }

    public void SetReadyStatusForCurrentSelection()
    {
        var selection = _sessionSelection.Selection;
        var selectedSession = selection.Target is WorkspaceTarget.Session ? _sessionSelection.GetSelectedSession() : null;
        var readyMessage = ShellTextFormatter.BuildReadyStatusText(
            selectedSession,
            _sessionSelection.GetSelectedProject(),
            selection.Target is WorkspaceTarget.Draft { IsGlobal: true });
        var promptUnavailable = _workspaceContext.GetPromptUnavailableStatus();
        if (selectedSession is not null)
        {
            var selectedTab = _sessionSelection.EnsureSessionTab(selectedSession);
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

        if (_workspaceContext.HasCurrentPromptDraft())
        {
            SetStatus(
                StatusVisualFormatter.BuildPromptEditedStatusText(),
                showSpinner: false,
                StatusTone.Info,
                StatusVisualFormatter.BuildPromptEditedIconMarkup());
            return;
        }

        SetStatus(readyMessage, tone: StatusTone.Ready);
    }
}
