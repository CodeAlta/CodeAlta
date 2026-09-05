using CodeAlta.Tui.App.State;
using CodeAlta.Agent;
using CodeAlta.Tui.Models;

namespace CodeAlta.Tui.App;

internal interface IShellPromptAvailabilityPort
{
    ModelProviderId GetPreferredModelProviderId();

    (bool HasStatus, string Message, StatusTone Tone) GetPromptUnavailableStatus();

    bool HasCurrentPromptDraft();
}

internal interface IShellWorkspaceProjectionPort
{
    void EnsureSelectionDefaults();

    void RefreshSidebarProjection();

    void SyncSidebarSelectionToCurrentState();

    void ApplyQueuedPromptProjection();

    void RefreshModelProviderSelectorsForDraftScope();

    void RefreshModelProviderSelectorsForSession(OpenSessionState tab);

    void SyncPromptDraftText(SessionState? session);

    void ApplyPromptAvailabilityProjection();

    void SyncActivePromptPanelProjection();

    void SyncSessionTabControl();
}

internal sealed class DelegatingShellPromptAvailabilityPort : IShellPromptAvailabilityPort
{
    private readonly Func<ModelProviderId> _getPreferredModelProviderId;
    private readonly Func<(bool HasStatus, string Message, StatusTone Tone)> _getPromptUnavailableStatus;
    private readonly Func<bool> _hasCurrentPromptDraft;

    public DelegatingShellPromptAvailabilityPort(
        Func<ModelProviderId> getPreferredModelProviderId,
        Func<(bool HasStatus, string Message, StatusTone Tone)> getPromptUnavailableStatus,
        Func<bool>? hasCurrentPromptDraft = null)
    {
        ArgumentNullException.ThrowIfNull(getPreferredModelProviderId);
        ArgumentNullException.ThrowIfNull(getPromptUnavailableStatus);

        _getPreferredModelProviderId = getPreferredModelProviderId;
        _getPromptUnavailableStatus = getPromptUnavailableStatus;
        _hasCurrentPromptDraft = hasCurrentPromptDraft ?? (static () => false);
    }

    public ModelProviderId GetPreferredModelProviderId()
        => _getPreferredModelProviderId();

    public (bool HasStatus, string Message, StatusTone Tone) GetPromptUnavailableStatus()
        => _getPromptUnavailableStatus();

    public bool HasCurrentPromptDraft()
        => _hasCurrentPromptDraft();
}

internal sealed class DelegatingShellWorkspaceProjectionPort : IShellWorkspaceProjectionPort
{
    private readonly Action _ensureSelectionDefaults;
    private readonly Action _refreshSidebarProjection;
    private readonly Action _syncSidebarSelectionToCurrentState;
    private readonly Action _refreshQueuedPromptList;
    private readonly Action _refreshModelProviderSelectorsForDraftScope;
    private readonly Action<OpenSessionState> _refreshModelProviderSelectorsForSession;
    private readonly Action<SessionState?> _syncPromptDraftText;
    private readonly Action _updatePromptAvailabilityUi;
    private readonly Action _syncActivePromptPanelProjection;
    private readonly Action _syncSessionTabControl;

    public DelegatingShellWorkspaceProjectionPort(
        Action ensureSelectionDefaults,
        Action refreshSidebarProjection,
        Action syncSidebarSelectionToCurrentState,
        Action refreshQueuedPromptList,
        Action refreshModelProviderSelectorsForDraftScope,
        Action<OpenSessionState> refreshModelProviderSelectorsForSession,
        Action<SessionState?> syncPromptDraftText,
        Action updatePromptAvailabilityUi,
        Action syncActivePromptPanelProjection,
        Action syncSessionTabControl)
    {
        ArgumentNullException.ThrowIfNull(ensureSelectionDefaults);
        ArgumentNullException.ThrowIfNull(refreshSidebarProjection);
        ArgumentNullException.ThrowIfNull(syncSidebarSelectionToCurrentState);
        ArgumentNullException.ThrowIfNull(refreshQueuedPromptList);
        ArgumentNullException.ThrowIfNull(refreshModelProviderSelectorsForDraftScope);
        ArgumentNullException.ThrowIfNull(refreshModelProviderSelectorsForSession);
        ArgumentNullException.ThrowIfNull(syncPromptDraftText);
        ArgumentNullException.ThrowIfNull(updatePromptAvailabilityUi);
        ArgumentNullException.ThrowIfNull(syncActivePromptPanelProjection);
        ArgumentNullException.ThrowIfNull(syncSessionTabControl);

        _ensureSelectionDefaults = ensureSelectionDefaults;
        _refreshSidebarProjection = refreshSidebarProjection;
        _syncSidebarSelectionToCurrentState = syncSidebarSelectionToCurrentState;
        _refreshQueuedPromptList = refreshQueuedPromptList;
        _refreshModelProviderSelectorsForDraftScope = refreshModelProviderSelectorsForDraftScope;
        _refreshModelProviderSelectorsForSession = refreshModelProviderSelectorsForSession;
        _syncPromptDraftText = syncPromptDraftText;
        _updatePromptAvailabilityUi = updatePromptAvailabilityUi;
        _syncActivePromptPanelProjection = syncActivePromptPanelProjection;
        _syncSessionTabControl = syncSessionTabControl;
    }

    public void EnsureSelectionDefaults()
        => _ensureSelectionDefaults();

    public void RefreshSidebarProjection()
        => _refreshSidebarProjection();

    public void SyncSidebarSelectionToCurrentState()
        => _syncSidebarSelectionToCurrentState();

    public void ApplyQueuedPromptProjection()
    {
        _refreshQueuedPromptList();
        _syncActivePromptPanelProjection();
    }

    public void RefreshModelProviderSelectorsForDraftScope()
    {
        _refreshModelProviderSelectorsForDraftScope();
        _syncActivePromptPanelProjection();
    }

    public void RefreshModelProviderSelectorsForSession(OpenSessionState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _refreshModelProviderSelectorsForSession(tab);
        _syncActivePromptPanelProjection();
    }

    public void SyncPromptDraftText(SessionState? session)
        => _syncPromptDraftText(session);

    public void ApplyPromptAvailabilityProjection()
    {
        _updatePromptAvailabilityUi();
        _syncActivePromptPanelProjection();
    }

    public void SyncActivePromptPanelProjection()
        => _syncActivePromptPanelProjection();

    public void SyncSessionTabControl()
        => _syncSessionTabControl();
}
