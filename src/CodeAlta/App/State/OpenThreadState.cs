using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Timeline;
using CodeAlta.ViewModels;

namespace CodeAlta.App.State;

internal sealed class OpenThreadState
{
    public OpenThreadState(WorkThreadDescriptor thread, ThreadTimelinePresenter timeline)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(timeline);

        Thread = thread;
        Session = new ThreadSessionState();
        Workspace = new ThreadWorkspaceState(
            new ThreadTabViewModel
            {
                ThreadId = thread.ThreadId,
                Title = thread.Title,
            });
        TimelineState = new ThreadTimelineState(timeline);
    }

    public WorkThreadDescriptor Thread { get; set; }

    public ThreadSessionState Session { get; }

    public ThreadWorkspaceState Workspace { get; }

    public ThreadTimelineState TimelineState { get; }

    public ThreadTimelinePresenter Timeline => TimelineState.Presenter;

    public ThreadTabViewModel ViewModel => Workspace.ViewModel;

    public AgentBackendId BackendId
    {
        get => Session.BackendId;
        set => Session.BackendId = value;
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

    public bool AutoScroll
    {
        get => Session.AutoScroll;
        set => Session.AutoScroll = value;
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

    public Dictionary<string, AgentPermissionRequest> PermissionRequests => Session.PermissionRequests;

    public Dictionary<string, AgentUserInputRequest> UserInputRequests => Session.UserInputRequests;

    public object PromptStripSyncRoot => Session.PromptStripSyncRoot;

    public object QueuedPromptsSyncRoot => Session.PromptStripSyncRoot;

    public List<QueuedThreadPrompt> QueuedPrompts => Session.QueuedPrompts;

    public List<PendingSteerPrompt> PendingSteers => Session.PendingSteers;

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
