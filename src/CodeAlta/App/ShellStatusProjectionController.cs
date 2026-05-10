using CodeAlta.App.Context;
using CodeAlta.App.State;
using CodeAlta.Models;
using CodeAlta.Presentation.Shell;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI.Controls;
using IntState = XenoAtom.Terminal.UI.State<int>;

namespace CodeAlta.App;

internal sealed class ShellStatusProjectionController
{
    private readonly CodeAltaShellViewModel _shellViewModel;
    private readonly ThreadSelectionContext _threadSelection;
    private readonly ShellWorkspaceContext _workspaceContext;
    private readonly IntState _viewRefreshState;

    public ShellStatusProjectionController(
        CodeAltaShellViewModel shellViewModel,
        ThreadSelectionContext threadSelection,
        ShellWorkspaceContext workspaceContext,
        IntState viewRefreshState)
    {
        ArgumentNullException.ThrowIfNull(shellViewModel);
        ArgumentNullException.ThrowIfNull(threadSelection);
        ArgumentNullException.ThrowIfNull(workspaceContext);
        ArgumentNullException.ThrowIfNull(viewRefreshState);

        _shellViewModel = shellViewModel;
        _threadSelection = threadSelection;
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

    public void ClearThreadStatus(OpenThreadState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        SetThreadStatus(
            tab,
            ShellTextFormatter.BuildReadyStatusText(tab.Thread, _threadSelection.GetSelectedProject(), globalScopeSelected: false),
            tone: StatusTone.Ready,
            hasCustomStatus: false);
    }

    public void RefreshRunningStatusElapsed(DateTimeOffset now)
    {
        _workspaceContext.DispatchToUi(
            () =>
            {
                _workspaceContext.VerifyBindableAccess();
                var selectedThread = _threadSelection.GetSelectedThread();
                if (selectedThread is null)
                {
                    return;
                }

                var selectedTab = _threadSelection.EnsureThreadTab(selectedThread);
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
                _shellViewModel.StatusText = message;
                _workspaceContext.RefreshSidebarProjection();
                _viewRefreshState.Value++;
            });
    }

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
}
