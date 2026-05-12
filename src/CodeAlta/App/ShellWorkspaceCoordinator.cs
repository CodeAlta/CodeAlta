using CodeAlta.App.State;
using CodeAlta.App.Context;
using CodeAlta.App.Events;
using CodeAlta.Models;
using CodeAlta.ViewModels;
using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using FloatState = XenoAtom.Terminal.UI.State<float>;
using IntState = XenoAtom.Terminal.UI.State<int>;

namespace CodeAlta.App;

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
        ThreadWorkspaceViewModel threadWorkspaceViewModel,
        SessionUsageViewModel sessionUsageViewModel,
        Dictionary<string, ChatBackendState> chatBackendStates,
        ThreadSelectionContext threadSelection,
        ShellWorkspaceContext workspaceContext,
        FloatState welcomeAnimationPhase01)
    {
        ArgumentNullException.ThrowIfNull(shellViewModel);
        ArgumentNullException.ThrowIfNull(threadWorkspaceViewModel);
        ArgumentNullException.ThrowIfNull(sessionUsageViewModel);
        ArgumentNullException.ThrowIfNull(chatBackendStates);
        ArgumentNullException.ThrowIfNull(threadSelection);
        ArgumentNullException.ThrowIfNull(workspaceContext);
        ArgumentNullException.ThrowIfNull(welcomeAnimationPhase01);

        _shellViewModel = shellViewModel;
        _workspaceContext = workspaceContext;
        _statusProjection = new ShellStatusProjectionController(shellViewModel, threadSelection, workspaceContext, _viewRefreshState);
        _sessionUsageProjection = new SessionUsageProjectionController(sessionUsageViewModel, chatBackendStates, threadSelection, workspaceContext, _usageRefreshState);
        _workspaceProjection = new WorkspaceProjectionController(threadWorkspaceViewModel, threadSelection, workspaceContext, _viewRefreshState, _statusProjection, _sessionUsageProjection);
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

    public void SetThreadStatus(
        OpenThreadState tab,
        string message,
        bool showSpinner = false,
        StatusTone tone = StatusTone.Info,
        bool hasCustomStatus = true)
        => _statusProjection.SetThreadStatus(tab, message, showSpinner, tone, hasCustomStatus);

    public void ClearThreadStatus(OpenThreadState tab)
        => _statusProjection.ClearThreadStatus(tab);

    public void ApplySessionUsageProjection()
        => _sessionUsageProjection.ApplySessionUsageProjection();

    public void ApplyThreadChromeProjection()
        => _workspaceProjection.ApplyThreadChromeProjection();

    public void ApplyThreadStatusProjection()
        => _workspaceProjection.ApplyThreadStatusProjection();

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
