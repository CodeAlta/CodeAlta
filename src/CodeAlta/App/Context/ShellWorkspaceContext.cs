using CodeAlta.App.State;
using CodeAlta.Agent;
using CodeAlta.Models;
using CodeAlta.Threading;
using XenoAtom.Terminal.UI;

namespace CodeAlta.App.Context;

internal sealed class ShellWorkspaceContext
{
    private readonly IShellPromptAvailabilityPort _promptAvailability;
    private readonly IWorkspaceSurface _surface;
    private readonly IShellWorkspaceProjectionPort _projection;
    private readonly IUiDispatcher _uiDispatcher;

    public ShellWorkspaceContext(
        IShellPromptAvailabilityPort promptAvailability,
        IWorkspaceSurface surface,
        IShellWorkspaceProjectionPort projection,
        IUiDispatcher uiDispatcher)
    {
        ArgumentNullException.ThrowIfNull(promptAvailability);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(uiDispatcher);

        _promptAvailability = promptAvailability;
        _surface = surface;
        _projection = projection;
        _uiDispatcher = uiDispatcher;
    }

    public AgentBackendId GetPreferredModelProviderId()
        => _promptAvailability.GetPreferredModelProviderId();

    public (bool HasStatus, string Message, StatusTone Tone) GetPromptUnavailableStatus()
        => _promptAvailability.GetPromptUnavailableStatus();

    public bool HasWorkspaceSurface()
        => _surface.HasWorkspaceSurface;

    public void EnsureSelectionDefaults()
        => _projection.EnsureSelectionDefaults();

    public void RefreshSidebarProjection()
        => _projection.RefreshSidebarProjection();

    public void SyncSidebarSelectionToCurrentState()
        => _projection.SyncSidebarSelectionToCurrentState();

    public void ApplyQueuedPromptProjection()
        => _projection.ApplyQueuedPromptProjection();

    public void RefreshModelProviderSelectorsForDraftScope()
        => _projection.RefreshModelProviderSelectorsForDraftScope();

    public void RefreshModelProviderSelectorsForThread(OpenThreadState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _projection.RefreshModelProviderSelectorsForThread(tab);
    }

    public void SyncPromptDraftText(ThreadSessionState? session)
        => _projection.SyncPromptDraftText(session);

    public void ApplyPromptAvailabilityProjection()
        => _projection.ApplyPromptAvailabilityProjection();

    public void SyncActivePromptPanelProjection()
        => _projection.SyncActivePromptPanelProjection();

    public void SyncThreadTabControl()
        => _projection.SyncThreadTabControl();

    public void DispatchToUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        UiDispatch.Post(_uiDispatcher, action);
    }

    public void DispatchToUiDeferred(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _uiDispatcher.PostDeferred(action);
    }

    public void FocusPromptTarget()
        => _surface.FocusPromptTarget();

    public void VerifyBindableAccess()
        => _uiDispatcher.VerifyAccess();
}
