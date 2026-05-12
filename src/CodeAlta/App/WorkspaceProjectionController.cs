using CodeAlta.App.Context;
using CodeAlta.App.State;
using CodeAlta.Models;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.App;

internal sealed class WorkspaceProjectionController
{
    private readonly ThreadWorkspaceViewModel _threadWorkspaceViewModel;
    private readonly ThreadSelectionContext _threadSelection;
    private readonly ShellWorkspaceContext _workspaceContext;
    private readonly State<int> _viewRefreshState;
    private readonly ShellStatusProjectionController _statusProjection;
    private readonly SessionUsageProjectionController _sessionUsageProjection;
    private string? _displayedThreadId;

    public WorkspaceProjectionController(
        ThreadWorkspaceViewModel threadWorkspaceViewModel,
        ThreadSelectionContext threadSelection,
        ShellWorkspaceContext workspaceContext,
        State<int> viewRefreshState,
        ShellStatusProjectionController statusProjection,
        SessionUsageProjectionController sessionUsageProjection)
    {
        ArgumentNullException.ThrowIfNull(threadWorkspaceViewModel);
        ArgumentNullException.ThrowIfNull(threadSelection);
        ArgumentNullException.ThrowIfNull(workspaceContext);
        ArgumentNullException.ThrowIfNull(viewRefreshState);
        ArgumentNullException.ThrowIfNull(statusProjection);
        ArgumentNullException.ThrowIfNull(sessionUsageProjection);

        _threadWorkspaceViewModel = threadWorkspaceViewModel;
        _threadSelection = threadSelection;
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
        => ApplyThreadChromeProjection();

    public void ApplyCatalogProjection()
        => _workspaceContext.DispatchToUi(ApplyCatalogProjectionCore);

    public void ApplyHeaderProjection()
        => _workspaceContext.DispatchToUi(ApplyHeaderProjectionCore);

    public void ApplySelectionProjection()
        => _workspaceContext.DispatchToUi(ApplySelectionProjectionCore);

    public void ApplyTabProjection()
        => ApplySelectionProjection();

    public void ApplyThreadStatusProjection()
        => ApplyShellChromeProjection();

    public void ApplyPromptDraftProjection()
        => _workspaceContext.DispatchToUi(
            () =>
            {
                ApplyShellChromeProjectionCore();
                _statusProjection.SetReadyStatusForCurrentSelection();
                _viewRefreshState.Value++;
            });

    public void ApplyThreadChromeProjection()
        => _workspaceContext.DispatchToUi(() => _viewRefreshState.Value++);

    private void ApplyHeaderProjectionCore()
    {
        _workspaceContext.VerifyBindableAccess();
        _workspaceContext.EnsureSelectionDefaults();
        RefreshThreadWorkspaceCore();
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
        RefreshThreadWorkspaceCore();
    }

    private void ApplySelectionProjectionCore()
    {
        _workspaceContext.VerifyBindableAccess();
        _workspaceContext.EnsureSelectionDefaults();
        _workspaceContext.RefreshSidebarProjection();
        RefreshThreadWorkspaceCore();
    }

    private void RefreshThreadWorkspaceCore()
    {
        _sessionUsageProjection.Refresh();
        _threadWorkspaceViewModel.CanShowThreadInfo = _threadSelection.GetSelectedThread() is not null;
        _viewRefreshState.Value++;
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
            RefreshDraftThreadPaneContent();
            return;
        }

        var selectedThread = _threadSelection.GetSelectedThread();
        if (selectedThread is null)
        {
            RefreshDraftThreadPaneContent();
            return;
        }

        var tab = _threadSelection.EnsureThreadTab(selectedThread);
        _workspaceContext.ApplyQueuedPromptProjection();
        _workspaceContext.RefreshModelProviderSelectorsForThread(tab);
        _workspaceContext.SyncPromptDraftText(tab.Session);
        _workspaceContext.ApplyPromptAvailabilityProjection();
        if (!string.Equals(_displayedThreadId, selectedThread.ThreadId, StringComparison.OrdinalIgnoreCase))
        {
            _displayedThreadId = selectedThread.ThreadId;
            _workspaceContext.DispatchToUiDeferred(tab.Timeline.RevealTail);
            _workspaceContext.DispatchToUiDeferred(_workspaceContext.FocusPromptTarget);
        }

        _statusProjection.SetReadyStatusForCurrentSelection();
    }

    private void RefreshDraftThreadPaneContent()
    {
        var wasDisplayingThread = _displayedThreadId is not null;
        _displayedThreadId = null;
        _workspaceContext.ApplyQueuedPromptProjection();
        _workspaceContext.RefreshModelProviderSelectorsForDraftScope();
        _workspaceContext.SyncPromptDraftText(session: null);
        _workspaceContext.ApplyPromptAvailabilityProjection();
        if (wasDisplayingThread)
        {
            _workspaceContext.DispatchToUiDeferred(_workspaceContext.FocusPromptTarget);
        }

        _statusProjection.SetReadyStatusForCurrentSelection();
    }
}
