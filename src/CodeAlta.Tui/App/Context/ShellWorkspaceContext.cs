using CodeAlta.Tui.App.State;
using CodeAlta.Agent;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Threading;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.App.Context;

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

    public ModelProviderId GetPreferredModelProviderId()
        => _promptAvailability.GetPreferredModelProviderId();

    public (bool HasStatus, string Message, StatusTone Tone) GetPromptUnavailableStatus()
        => _promptAvailability.GetPromptUnavailableStatus();

    public bool HasCurrentPromptDraft()
        => _promptAvailability.HasCurrentPromptDraft();

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

    public void RefreshModelProviderSelectorsForSession(OpenSessionState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _projection.RefreshModelProviderSelectorsForSession(tab);
    }

    public void SyncPromptDraftText(SessionState? session)
        => _projection.SyncPromptDraftText(session);

    public void ApplyPromptAvailabilityProjection()
        => _projection.ApplyPromptAvailabilityProjection();

    public void SyncActivePromptPanelProjection()
        => _projection.SyncActivePromptPanelProjection();

    public void SyncSessionTabControl()
        => _projection.SyncSessionTabControl();

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
