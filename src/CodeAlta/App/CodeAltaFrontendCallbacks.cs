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

internal sealed class CodeAltaFrontendCallbacks
{
    public required Action<IUiDispatcher> AssignUiDispatcher { get; init; }
    public required Action ApplyPendingSidebarSelection { get; init; }
    public required Func<IUiDispatcher> GetUiDispatcher { get; init; }
    public required Func<Rectangle?> GetThreadPaneBounds { get; init; }
    public required Func<AgentBackendId, bool> IsChatBackendReady { get; init; }
    public required Func<string, string?> LoadPromptDraft { get; init; }
    public required Action<string> DeletePromptDraft { get; init; }
    public required Action<OpenThreadState> ApplyThreadPreference { get; init; }
    public required Action<string, string?, AgentReasoningEffort?, bool, bool> RememberThreadPreference { get; init; }
    public required Func<WorkThreadDescriptor, CancellationToken, Task> EnsureThreadHistoryLoadedAsync { get; init; }
    public required Action RefreshSelectionAndThreadWorkspace { get; init; }
    public required Action RefreshCatalogAndThreadWorkspace { get; init; }
    public required Action ResetPendingThreadTabSelection { get; init; }
    public required Action<string> RemoveThreadTabPage { get; init; }
    public required Action<string, bool, StatusTone> SetStatus { get; init; }
    public required Action<string?> SetProviderSessionLoadStatus { get; init; }
    public required Func<string, bool> IsSelectedThread { get; init; }
    public required Action<ChatBackendState> ApplyDraftBackendPreference { get; init; }
    public required Action<AgentBackendId, string?, AgentReasoningEffort?> RememberGlobalBackendPreference { get; init; }
    public required Action InvalidateSelectedSessionUsage { get; init; }
    public required Action RefreshHeaderAndThreadWorkspace { get; init; }
    public required Action<string, WorkThreadDescriptor> RekeyThreadIdentity { get; init; }
    public required Func<bool> HasWorkspaceSurface { get; init; }
    public required Action<Visual> SetThreadPaneContent { get; init; }
    public required Action EnsureSelectionDefaults { get; init; }
    public required Action RefreshSidebarProjection { get; init; }
    public required Action SyncSidebarSelectionToCurrentState { get; init; }
    public required Action RefreshChatSelectorsForDraftScope { get; init; }
    public required Action<OpenThreadState> RefreshChatSelectorsForThread { get; init; }
    public required Action SyncChatSelectorItems { get; init; }
    public required Action<ThreadSessionState?> SyncPromptText { get; init; }
    public required Action UpdatePromptAvailabilityUi { get; init; }
    public required Action UpdatePromptImageAttachmentsUi { get; init; }
    public required Action SyncThreadTabControl { get; init; }
    public required Action<Action> DispatchToUi { get; init; }
    public required Action<Action> DispatchToUiDeferred { get; init; }
    public required Action FocusPromptTarget { get; init; }
    public required Action VerifyBindableAccess { get; init; }
    public required Func<bool> GetAutoApproveEnabled { get; init; }
    public required Action RefreshShellChrome { get; init; }
    public required Action<OpenThreadState, string, bool, StatusTone> SetThreadStatus { get; init; }
    public required Action<OpenThreadState> ClearThreadStatus { get; init; }
    public required Func<bool> TrySetPromptUnavailableStatus { get; init; }
    public required Action SetReadyStatusForCurrentSelection { get; init; }
    public required Action ClearDraftPromptText { get; init; }
    public required Action ClearPromptText { get; init; }
    public required Func<bool> IsPromptTextEmpty { get; init; }
    public required Action<string> RestorePromptText { get; init; }
    public required Func<IReadOnlyList<PromptImageAttachment>> SnapshotPromptImages { get; init; }
    public required Action<IReadOnlyList<PromptImageAttachment>> RestorePromptImages { get; init; }
    public required Func<Task> PersistViewStateAsync { get; init; }
    public required Func<WorkThreadDescriptor, Task> RegisterCreatedThreadAsync { get; init; }
    public required Func<Visual?> GetPromptFocusTarget { get; init; }
}
