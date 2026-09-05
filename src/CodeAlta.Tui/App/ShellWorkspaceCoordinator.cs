using CodeAlta.Tui.App.State;
using CodeAlta.Tui.App.Context;
using CodeAlta.Tui.App.Events;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.ViewModels;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using IntState = XenoAtom.Terminal.UI.State<int>;

namespace CodeAlta.Tui.App;

internal sealed class ShellWorkspaceCoordinator : IWorkspaceProjectionController
{
    private readonly CodeAltaShellViewModel _shellViewModel;
    private readonly ShellWorkspaceContext _workspaceContext;
    private readonly IntState _viewRefreshState = new(0);
    private readonly IntState _usageRefreshState = new(0);
    private readonly ShellStatusProjectionController _statusProjection;
    private readonly SessionUsageProjectionController _sessionUsageProjection;
    private readonly WorkspaceProjectionController _workspaceProjection;

    public ShellWorkspaceCoordinator(
        CodeAltaShellViewModel shellViewModel,
        SessionWorkspaceViewModel sessionWorkspaceViewModel,
        SessionUsageViewModel sessionUsageViewModel,
        Dictionary<string, ModelProviderState> modelProviderStates,
        SessionSelectionContext sessionSelection,
        ShellWorkspaceContext workspaceContext)
    {
        ArgumentNullException.ThrowIfNull(shellViewModel);
        ArgumentNullException.ThrowIfNull(sessionWorkspaceViewModel);
        ArgumentNullException.ThrowIfNull(sessionUsageViewModel);
        ArgumentNullException.ThrowIfNull(modelProviderStates);
        ArgumentNullException.ThrowIfNull(sessionSelection);
        ArgumentNullException.ThrowIfNull(workspaceContext);

        _shellViewModel = shellViewModel;
        _workspaceContext = workspaceContext;
        _statusProjection = new ShellStatusProjectionController(shellViewModel, sessionSelection, workspaceContext, _viewRefreshState);
        _sessionUsageProjection = new SessionUsageProjectionController(sessionUsageViewModel, modelProviderStates, sessionSelection, workspaceContext, _usageRefreshState);
        _workspaceProjection = new WorkspaceProjectionController(sessionWorkspaceViewModel, sessionSelection, workspaceContext, _viewRefreshState, _statusProjection, _sessionUsageProjection);
    }

    public ComputedVisual CreateComputedVisual(Func<Visual> build)
        => _workspaceProjection.CreateComputedVisual(build);

    public ComputedVisual CreateUsageComputedVisual(Func<Visual> build)
        => _sessionUsageProjection.CreateComputedVisual(build);

    public void ApplyShellChromeProjection()
        => _workspaceProjection.ApplyShellChromeProjection();

    public void ApplyRuntimeTimelineProjection()
        => _workspaceProjection.ApplyRuntimeTimelineProjection();

    public void ApplyCatalogProjection()
        => _workspaceProjection.ApplyCatalogProjection();

    public void ApplyHeaderProjection()
        => _workspaceProjection.ApplyHeaderProjection();

    public void ApplySelectionProjection()
        => _workspaceProjection.ApplySelectionProjection();

    public void ApplyTabProjection()
        => _workspaceProjection.ApplyTabProjection();

    public void SetStatus(string message, bool showSpinner = false, StatusTone tone = StatusTone.Info)
        => _statusProjection.SetStatus(message, showSpinner, tone);

    public void SetProviderSessionLoadStatus(string? message)
        => _statusProjection.SetProviderSessionLoadStatus(message);

    public void SetStatus(string message, bool showSpinner, StatusTone tone, string? iconMarkup)
        => _statusProjection.SetStatus(message, showSpinner, tone, iconMarkup);

    public void SetSessionStatus(
        OpenSessionState tab,
        string message,
        bool showSpinner = false,
        StatusTone tone = StatusTone.Info,
        bool hasCustomStatus = true)
        => _statusProjection.SetSessionStatus(tab, message, showSpinner, tone, hasCustomStatus);

    public void ClearSessionStatus(OpenSessionState tab)
        => _statusProjection.ClearSessionStatus(tab);

    public void ApplySessionUsageProjection()
        => _sessionUsageProjection.ApplySessionUsageProjection();

    public void ApplySessionChromeProjection()
        => _workspaceProjection.ApplySessionChromeProjection();

    public void ApplySessionStatusProjection()
        => _workspaceProjection.ApplySessionStatusProjection();

    public void ApplyPromptDraftProjection()
        => _workspaceProjection.ApplyPromptDraftProjection();

    public void RequestPromptFocus()
        => _workspaceContext.DispatchToUiDeferred(_workspaceContext.FocusPromptTarget);

    public void RefreshRunningStatusElapsed(DateTimeOffset now)
        => _statusProjection.RefreshRunningStatusElapsed(now);

    public void SetReadyStatusForCurrentSelection()
        => _statusProjection.SetReadyStatusForCurrentSelection();

    public void SetShellInitialized(bool isInitialized)
        => _workspaceContext.DispatchToUi(() => _shellViewModel.IsInitialized = isInitialized);

}
