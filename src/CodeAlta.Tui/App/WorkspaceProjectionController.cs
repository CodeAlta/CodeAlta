using CodeAlta.Tui.App.Context;
using CodeAlta.Tui.App.State;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tui.App;

internal sealed class WorkspaceProjectionController
{
    private readonly SessionWorkspaceViewModel _sessionWorkspaceViewModel;
    private readonly SessionSelectionContext _sessionSelection;
    private readonly ShellWorkspaceContext _workspaceContext;
    private readonly State<int> _viewRefreshState;
    private readonly ShellStatusProjectionController _statusProjection;
    private readonly SessionUsageProjectionController _sessionUsageProjection;
    private string? _displayedSessionId;

    public WorkspaceProjectionController(
        SessionWorkspaceViewModel sessionWorkspaceViewModel,
        SessionSelectionContext sessionSelection,
        ShellWorkspaceContext workspaceContext,
        State<int> viewRefreshState,
        ShellStatusProjectionController statusProjection,
        SessionUsageProjectionController sessionUsageProjection)
    {
        ArgumentNullException.ThrowIfNull(sessionWorkspaceViewModel);
        ArgumentNullException.ThrowIfNull(sessionSelection);
        ArgumentNullException.ThrowIfNull(workspaceContext);
        ArgumentNullException.ThrowIfNull(viewRefreshState);
        ArgumentNullException.ThrowIfNull(statusProjection);
        ArgumentNullException.ThrowIfNull(sessionUsageProjection);

        _sessionWorkspaceViewModel = sessionWorkspaceViewModel;
        _sessionSelection = sessionSelection;
        _workspaceContext = workspaceContext;
        _viewRefreshState = viewRefreshState;
        _statusProjection = statusProjection;
        _sessionUsageProjection = sessionUsageProjection;
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

    public void ApplyShellChromeProjection()
        => _workspaceContext.DispatchToUi(ApplyShellChromeProjectionCore);

    public void ApplyRuntimeTimelineProjection()
        => ApplySessionChromeProjection();

    public void ApplyCatalogProjection()
        => _workspaceContext.DispatchToUi(ApplyCatalogProjectionCore);

    public void ApplyHeaderProjection()
        => _workspaceContext.DispatchToUi(ApplyHeaderProjectionCore);

    public void ApplySelectionProjection()
        => _workspaceContext.DispatchToUi(ApplySelectionProjectionCore);

    public void ApplyTabProjection()
        => ApplySelectionProjection();

    public void ApplySessionStatusProjection()
        => ApplyShellChromeProjection();

    public void ApplyPromptDraftProjection()
        => _workspaceContext.DispatchToUi(
            () =>
            {
                ApplyShellChromeProjectionCore();
                _statusProjection.SetReadyStatusForCurrentSelection();
                _viewRefreshState.Value++;
            });

    public void ApplySessionChromeProjection()
        => _workspaceContext.DispatchToUi(() => _viewRefreshState.Value++);

    private void ApplyHeaderProjectionCore()
    {
        _workspaceContext.VerifyBindableAccess();
        _workspaceContext.EnsureSelectionDefaults();
        RefreshSessionWorkspaceCore();
    }

    private void ApplyShellChromeProjectionCore()
    {
        _workspaceContext.VerifyBindableAccess();
        _workspaceContext.EnsureSelectionDefaults();
        _workspaceContext.RefreshSidebarProjection();
    }

    private void ApplyCatalogProjectionCore()
    {
        ApplyShellChromeProjectionCore();
        RefreshSessionWorkspaceCore();
    }

    private void ApplySelectionProjectionCore()
    {
        _workspaceContext.VerifyBindableAccess();
        _workspaceContext.EnsureSelectionDefaults();
        _workspaceContext.RefreshSidebarProjection();
        RefreshSessionWorkspaceCore();
    }

    private void RefreshSessionWorkspaceCore()
    {
        _sessionWorkspaceViewModel.CanShowSessionInfo = _sessionSelection.GetSelectedSession() is not null;
        _viewRefreshState.Value++;
        RefreshSessionPaneContent();
        _sessionUsageProjection.Refresh();
    }

    private void RefreshSessionPaneContent()
    {
        if (!_workspaceContext.HasWorkspaceSurface())
        {
            return;
        }

        _workspaceContext.SyncSessionTabControl();

        if (_sessionSelection.Selection.Target is not WorkspaceTarget.Session)
        {
            RefreshDraftSessionPaneContent();
            return;
        }

        var selectedSession = _sessionSelection.GetSelectedSession();
        if (selectedSession is null)
        {
            RefreshDraftSessionPaneContent();
            return;
        }

        var tab = _sessionSelection.EnsureSessionTab(selectedSession);
        _workspaceContext.ApplyQueuedPromptProjection();
        _workspaceContext.RefreshModelProviderSelectorsForSession(tab);
        _workspaceContext.SyncPromptDraftText(tab.Session);
        _workspaceContext.ApplyPromptAvailabilityProjection();
        if (!string.Equals(_displayedSessionId, selectedSession.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            _displayedSessionId = selectedSession.SessionId;
            _workspaceContext.DispatchToUiDeferred(tab.Timeline.RevealTail);
            _workspaceContext.DispatchToUiDeferred(_workspaceContext.FocusPromptTarget);
        }

        _statusProjection.SetReadyStatusForCurrentSelection();
    }

    private void RefreshDraftSessionPaneContent()
    {
        var wasDisplayingSession = _displayedSessionId is not null;
        _displayedSessionId = null;
        _workspaceContext.ApplyQueuedPromptProjection();
        _workspaceContext.RefreshModelProviderSelectorsForDraftScope();
        _workspaceContext.SyncPromptDraftText(session: null);
        _workspaceContext.ApplyPromptAvailabilityProjection();
        if (wasDisplayingSession)
        {
            _workspaceContext.DispatchToUiDeferred(_workspaceContext.FocusPromptTarget);
        }

        _statusProjection.SetReadyStatusForCurrentSelection();
    }
}
