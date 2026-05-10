using CodeAlta.App.State;
using CodeAlta.Agent;
using CodeAlta.Models;

namespace CodeAlta.App;

internal interface IShellPromptAvailabilityPort
{
    AgentBackendId GetPreferredModelProviderId();

    (bool HasStatus, string Message, StatusTone Tone) GetPromptUnavailableStatus();
}

internal interface IShellWorkspaceProjectionPort
{
    void EnsureSelectionDefaults();

    void RefreshSidebarProjection();

    void SyncSidebarSelectionToCurrentState();

    void ApplyQueuedPromptProjection();

    void RefreshModelProviderSelectorsForDraftScope();

    void RefreshModelProviderSelectorsForThread(OpenThreadState tab);

    void SyncPromptDraftText(ThreadSessionState? session);

    void ApplyPromptAvailabilityProjection();

    void SyncActivePromptPanelProjection();

    void SyncThreadTabControl();
}

internal sealed class DelegatingShellPromptAvailabilityPort : IShellPromptAvailabilityPort
{
    private readonly Func<AgentBackendId> _getPreferredModelProviderId;
    private readonly Func<(bool HasStatus, string Message, StatusTone Tone)> _getPromptUnavailableStatus;

    public DelegatingShellPromptAvailabilityPort(
        Func<AgentBackendId> getPreferredModelProviderId,
        Func<(bool HasStatus, string Message, StatusTone Tone)> getPromptUnavailableStatus)
    {
        ArgumentNullException.ThrowIfNull(getPreferredModelProviderId);
        ArgumentNullException.ThrowIfNull(getPromptUnavailableStatus);

        _getPreferredModelProviderId = getPreferredModelProviderId;
        _getPromptUnavailableStatus = getPromptUnavailableStatus;
    }

    public AgentBackendId GetPreferredModelProviderId()
        => _getPreferredModelProviderId();

    public (bool HasStatus, string Message, StatusTone Tone) GetPromptUnavailableStatus()
        => _getPromptUnavailableStatus();
}

internal sealed class DelegatingShellWorkspaceProjectionPort : IShellWorkspaceProjectionPort
{
    private readonly Action _ensureSelectionDefaults;
    private readonly Action _refreshSidebarProjection;
    private readonly Action _syncSidebarSelectionToCurrentState;
    private readonly Action _refreshQueuedPromptList;
    private readonly Action _refreshModelProviderSelectorsForDraftScope;
    private readonly Action<OpenThreadState> _refreshModelProviderSelectorsForThread;
    private readonly Action<ThreadSessionState?> _syncPromptDraftText;
    private readonly Action _updatePromptAvailabilityUi;
    private readonly Action _syncActivePromptPanelProjection;
    private readonly Action _syncThreadTabControl;

    public DelegatingShellWorkspaceProjectionPort(
        Action ensureSelectionDefaults,
        Action refreshSidebarProjection,
        Action syncSidebarSelectionToCurrentState,
        Action refreshQueuedPromptList,
        Action refreshModelProviderSelectorsForDraftScope,
        Action<OpenThreadState> refreshModelProviderSelectorsForThread,
        Action<ThreadSessionState?> syncPromptDraftText,
        Action updatePromptAvailabilityUi,
        Action syncActivePromptPanelProjection,
        Action syncThreadTabControl)
    {
        ArgumentNullException.ThrowIfNull(ensureSelectionDefaults);
        ArgumentNullException.ThrowIfNull(refreshSidebarProjection);
        ArgumentNullException.ThrowIfNull(syncSidebarSelectionToCurrentState);
        ArgumentNullException.ThrowIfNull(refreshQueuedPromptList);
        ArgumentNullException.ThrowIfNull(refreshModelProviderSelectorsForDraftScope);
        ArgumentNullException.ThrowIfNull(refreshModelProviderSelectorsForThread);
        ArgumentNullException.ThrowIfNull(syncPromptDraftText);
        ArgumentNullException.ThrowIfNull(updatePromptAvailabilityUi);
        ArgumentNullException.ThrowIfNull(syncActivePromptPanelProjection);
        ArgumentNullException.ThrowIfNull(syncThreadTabControl);

        _ensureSelectionDefaults = ensureSelectionDefaults;
        _refreshSidebarProjection = refreshSidebarProjection;
        _syncSidebarSelectionToCurrentState = syncSidebarSelectionToCurrentState;
        _refreshQueuedPromptList = refreshQueuedPromptList;
        _refreshModelProviderSelectorsForDraftScope = refreshModelProviderSelectorsForDraftScope;
        _refreshModelProviderSelectorsForThread = refreshModelProviderSelectorsForThread;
        _syncPromptDraftText = syncPromptDraftText;
        _updatePromptAvailabilityUi = updatePromptAvailabilityUi;
        _syncActivePromptPanelProjection = syncActivePromptPanelProjection;
        _syncThreadTabControl = syncThreadTabControl;
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

    public void RefreshModelProviderSelectorsForThread(OpenThreadState tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _refreshModelProviderSelectorsForThread(tab);
        _syncActivePromptPanelProjection();
    }

    public void SyncPromptDraftText(ThreadSessionState? session)
        => _syncPromptDraftText(session);

    public void ApplyPromptAvailabilityProjection()
    {
        _updatePromptAvailabilityUi();
        _syncActivePromptPanelProjection();
    }

    public void SyncActivePromptPanelProjection()
        => _syncActivePromptPanelProjection();

    public void SyncThreadTabControl()
        => _syncThreadTabControl();
}
