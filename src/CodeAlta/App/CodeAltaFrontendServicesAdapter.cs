using CodeAlta.Agent;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Threading;
using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.App;

internal sealed class CodeAltaFrontendServicesAdapter : ICodeAltaFrontendServices
{
    private readonly CodeAltaApp _app;

    public CodeAltaFrontendServicesAdapter(CodeAltaApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _app = app;
    }

    public void ApplyPendingSidebarSelection() => _app.ApplyPendingSidebarSelection();
    public IUiDispatcher GetUiDispatcher() => _app.GetUiDispatcher();
    public Rectangle? GetThreadPaneBounds() => _app.ThreadPaneLayout?.GetAbsoluteBounds();
    public bool IsModelProviderReady(AgentBackendId backendId) => _app.IsModelProviderReady(backendId);
    public string? LoadPromptDraft(string threadId) => _app.LoadPromptDraft(threadId);
    public void DeletePromptDraft(string threadId) => _app.DeletePromptDraft(threadId);
    public void ApplyThreadPreference(OpenThreadState thread) => _app.ApplyThreadPreference(thread);
    public void RememberThreadPreference(string threadId, string? modelId, AgentReasoningEffort? reasoningEffort, bool persistNow) => _app.RememberThreadPreference(threadId, modelId, reasoningEffort, persistNow);
    public Task EnsureThreadHistoryLoadedAsync(WorkThreadDescriptor thread, CancellationToken cancellationToken) => _app.EnsureThreadHistoryLoadedAsync(thread, cancellationToken);
    public void RefreshSelectionAndThreadWorkspace() => _app.RefreshSelectionAndThreadWorkspace();
    public void RefreshCatalogAndThreadWorkspace() => _app.RefreshCatalogAndThreadWorkspace();
    public void ResetPendingThreadTabSelection() => _app.ResetPendingThreadTabSelection();
    public void RemoveThreadTabPage(string threadId, ShellTabCloseReason reason) => _app.RemoveThreadTabPage(threadId, reason);
    public void SetStatus(string message, bool showSpinner, StatusTone tone) => _app.SetStatus(message, showSpinner, tone);
    public void SetProviderSessionLoadStatus(string? message) => _app.SetProviderSessionLoadStatus(message);
    public bool IsSelectedThread(string threadId) => _app.IsSelectedThread(threadId);
    public void ApplyDraftModelProviderPreference(ChatBackendState backendState) => _app.ApplyDraftModelProviderPreference(backendState);
    public void RememberGlobalModelProviderPreference(AgentBackendId backendId, string? modelId, AgentReasoningEffort? reasoningEffort) => _app.RememberGlobalModelProviderPreference(backendId, modelId, reasoningEffort);
    public void InvalidateSelectedSessionUsage() => _app.InvalidateSelectedSessionUsage();
    public void RefreshHeaderAndThreadWorkspace() => _app.RefreshHeaderAndThreadWorkspace();
    public void RekeyThreadIdentity(string oldThreadId, WorkThreadDescriptor thread) => _app.RekeyThreadIdentity(oldThreadId, thread);
    public bool HasWorkspaceSurface() => _app.HasWorkspaceSurface();
    public void EnsureSelectionDefaults() => _app.EnsureSelectionDefaults();
    public void RefreshSidebarProjection() => _app.RefreshSidebarProjection();
    public void SyncSidebarSelectionToCurrentState() => _app.SyncSidebarSelectionToCurrentState();
    public void RefreshModelProviderSelectorsForDraftScope() => _app.RefreshModelProviderSelectorsForDraftScope();
    public void RefreshModelProviderSelectorsForThread(OpenThreadState thread) => _app.RefreshModelProviderSelectorsForThread(thread);
    public void SyncModelProviderSelectorItems() => _app.SyncModelProviderSelectorItems();
    public void SyncPromptText(ThreadSessionState? session) => _app.SyncPromptText(session);
    public void UpdatePromptAvailabilityUi() => _app.UpdatePromptAvailabilityUi();
    public void UpdatePromptImageAttachmentsUi() => _app.UpdatePromptImageAttachmentsUi();
    public void SyncThreadTabControl() => _app.SyncThreadTabControl();
    public void DispatchToUi(Action action) => _app.DispatchToUi(action);
    public void DispatchToUiDeferred(Action action) => _app.DispatchToUiDeferred(action);
    public void FocusPromptTarget() => _app.FocusPromptTarget();
    public void VerifyBindableAccess() => _app.VerifyBindableAccess();
    public bool GetAutoApproveEnabled() => _app.GetAutoApproveEnabled();
    public void RefreshShellChrome() => _app.RefreshShellChrome();
    public void SetThreadStatus(OpenThreadState thread, string message, bool showSpinner, StatusTone tone) => _app.SetThreadStatus(thread, message, showSpinner, tone);
    public void ClearThreadStatus(OpenThreadState thread) => _app.ClearThreadStatus(thread);
    public bool TrySetPromptUnavailableStatus() => _app.TrySetPromptUnavailableStatus();
    public void SetReadyStatusForCurrentSelection() => _app.SetReadyStatusForCurrentSelection();
    public void ClearDraftPromptText() => _app.ClearDraftPromptText();
    public void ClearPromptText() => _app.ClearPromptText();
    public bool IsPromptTextEmpty() => _app.IsPromptTextEmpty();
    public void RestorePromptText(string prompt) => _app.RestorePromptText(prompt);
    public IReadOnlyList<PromptImageAttachment> SnapshotPromptImages() => _app.SnapshotPromptImages();
    public void RestorePromptImages(IReadOnlyList<PromptImageAttachment> images) => _app.RestorePromptImages(images);
    public Task PersistViewStateAsync() => _app.PersistViewStateAsync();
    public Task RegisterCreatedThreadAsync(WorkThreadDescriptor thread) => _app.RegisterCreatedThreadAsync(thread);
    public Visual? GetPromptFocusTarget() => _app.ThreadInput;
}
