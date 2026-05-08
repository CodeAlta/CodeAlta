using CodeAlta.Agent;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Threading;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.App;

internal interface ICodeAltaFrontendServices : IShellStatusService, IPromptSessionService, ISelectionQuery
{
    void ApplyPendingSidebarSelection();
    IUiDispatcher GetUiDispatcher();
    Rectangle? GetThreadPaneBounds();
    bool IsModelProviderReady(AgentBackendId backendId);
    string? LoadPromptDraft(string threadId);
    void DeletePromptDraft(string threadId);
    void ApplyThreadPreference(OpenThreadState thread);
    void RememberThreadPreference(string threadId, string? modelId, AgentReasoningEffort? reasoningEffort, bool persistNow);
    Task EnsureThreadHistoryLoadedAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken);
    void RefreshSelectionAndThreadWorkspace();
    void RefreshCatalogAndThreadWorkspace();
    void ResetPendingThreadTabSelection();
    void RemoveThreadTabPage(string threadId, ShellTabCloseReason reason);
    void SetProviderSessionLoadStatus(string? message);
    void ApplyDraftModelProviderPreference(ChatBackendState backendState);
    void RememberGlobalModelProviderPreference(AgentBackendId backendId, string? modelId, AgentReasoningEffort? reasoningEffort);
    void InvalidateSelectedSessionUsage();
    void RefreshHeaderAndThreadWorkspace();
    void RekeyThreadIdentity(string oldThreadId, WorkThreadDescriptor thread);
    bool HasWorkspaceSurface();
    void EnsureSelectionDefaults();
    void RefreshSidebarProjection();
    void SyncSidebarSelectionToCurrentState();
    void RefreshModelProviderSelectorsForDraftScope();
    void RefreshModelProviderSelectorsForThread(OpenThreadState thread);
    void SyncModelProviderSelectorItems();
    void SyncPromptText(ThreadSessionState? session);
    void SyncThreadTabControl();
    void DispatchToUi(Action action);
    void DispatchToUiDeferred(Action action);
    void FocusPromptTarget();
    void VerifyBindableAccess();
    bool GetAutoApproveEnabled();
    void RefreshShellChrome();
    void SetThreadStatus(OpenThreadState thread, string message, bool showSpinner, StatusTone tone);
    void ClearThreadStatus(OpenThreadState thread);
    bool TrySetPromptUnavailableStatus();
    void SetReadyStatusForCurrentSelection();
    void ClearDraftPromptText();
    Task PersistViewStateAsync();
    Task RegisterCreatedThreadAsync(WorkThreadDescriptor thread);
    Visual? GetPromptFocusTarget();
}
