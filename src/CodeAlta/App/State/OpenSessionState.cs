using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Timeline;
using CodeAlta.ViewModels;

namespace CodeAlta.App.State;

internal sealed class OpenSessionState
{
    public OpenSessionState(SessionViewDescriptor session, SessionTimelinePresenter timeline)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(timeline);

        SessionView = session;
        Session = new SessionState();
        Workspace = new SessionWorkspaceState(
            new SessionTabViewModel
            {
                SessionId = session.SessionId,
                Title = session.Title,
            });
        TimelineState = new SessionTimelineState(timeline);
    }

    public SessionViewDescriptor SessionView { get; set; }

    public SessionState Session { get; }

    public SessionWorkspaceState Workspace { get; }

    public SessionTimelineState TimelineState { get; }

    public SessionTimelinePresenter Timeline => TimelineState.Presenter;

    public SessionTabViewModel ViewModel => Workspace.ViewModel;

    public ModelProviderId ProviderId
    {
        get => Session.ProviderId;
        set => Session.ProviderId = value;
    }

    public string? ModelId
    {
        get => Session.ModelId;
        set => Session.ModelId = value;
    }

    public AgentReasoningEffort? ReasoningEffort
    {
        get => Session.ReasoningEffort;
        set => Session.ReasoningEffort = value;
    }

    public string? UserPromptName
    {
        get => Session.UserPromptName;
        set => Session.UserPromptName = value;
    }

    public bool HistoryLoaded
    {
        get => Session.HistoryLoaded;
        set => Session.HistoryLoaded = value;
    }

    public bool HistoryLoading
    {
        get => Session.HistoryLoading;
        set => Session.HistoryLoading = value;
    }

    public bool PendingManualCompaction
    {
        get => Session.PendingManualCompaction;
        set => Session.PendingManualCompaction = value;
    }

    public Task? HistoryLoadTask
    {
        get => Session.HistoryLoadTask;
        set => Session.HistoryLoadTask = value;
    }

    public List<AgentEvent>? HistoryEvents
    {
        get => Session.HistoryEvents;
        set => Session.HistoryEvents = value;
    }

    public List<AgentEvent> RenderedHistoryEvents => Session.RenderedHistoryEvents;

    public Dictionary<string, AgentPermissionRequest> PermissionRequests => Session.PermissionRequests;

    public Dictionary<string, AgentUserInputRequest> UserInputRequests => Session.UserInputRequests;

    public object PromptStripSyncRoot => Session.PromptStripSyncRoot;

    public object QueuedPromptsSyncRoot => Session.PromptStripSyncRoot;

    public List<QueuedSessionPrompt> QueuedPrompts => Session.QueuedPrompts;

    public List<PendingSteerPrompt> PendingSteers => Session.PendingSteers;

    public string NotesMarkdown
    {
        get => Session.NotesMarkdown;
        set => Session.NotesMarkdown = value;
    }

    public string? LastObservedPendingSteerUserContentId
    {
        get => Session.LastObservedPendingSteerUserContentId;
        set => Session.LastObservedPendingSteerUserContentId = value;
    }

    public AgentRunId? ActiveRunId
    {
        get => Session.ActiveRunId;
        set => Session.ActiveRunId = value;
    }

    public DateTimeOffset? ActiveRunStartedAt
    {
        get => Session.ActiveRunStartedAt;
        set => Session.ActiveRunStartedAt = value;
    }

    public AgentSessionUsage? Usage
    {
        get => Session.Usage;
        set => Session.Usage = value;
    }

    public PluginTransientEventProjectionStore PluginTransientEvents => Session.PluginTransientEvents;

    public bool HasPromptDraft
        => !string.IsNullOrWhiteSpace(Session.PromptDraftText) || Session.PromptImageAttachments.Count > 0;

    public string? StatusMessage
    {
        get => ViewModel.StatusMessage;
        set => ViewModel.StatusMessage = value;
    }

    public StatusTone StatusTone
    {
        get => ViewModel.StatusTone;
        set => ViewModel.StatusTone = value;
    }

    public bool StatusBusy
    {
        get => ViewModel.StatusBusy;
        set => ViewModel.StatusBusy = value;
    }

    public bool HasCustomStatus
    {
        get => ViewModel.HasCustomStatus;
        set => ViewModel.HasCustomStatus = value;
    }
}
