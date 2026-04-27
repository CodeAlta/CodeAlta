using CodeAlta.Agent;
using CodeAlta.Presentation.Prompting;

namespace CodeAlta.Models;

internal sealed class ThreadSessionState
{
    public AgentBackendId BackendId { get; set; } = AgentBackendIds.Codex;

    public string? ModelId { get; set; }

    public AgentReasoningEffort? ReasoningEffort { get; set; }

    public bool AutoScroll { get; set; } = true;

    public string PromptDraftText { get; set; } = string.Empty;

    public List<PromptImageAttachment> PromptImageAttachments { get; } = [];

    public bool HistoryLoaded { get; set; }

    public bool HistoryLoading { get; set; }

    public bool PendingManualCompaction { get; set; }

    public Task? HistoryLoadTask { get; set; }

    public List<AgentEvent>? HistoryEvents { get; set; }

    public Dictionary<string, AgentPermissionRequest> PermissionRequests { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, AgentUserInputRequest> UserInputRequests { get; } = new(StringComparer.Ordinal);

    public object PromptStripSyncRoot { get; } = new();

    public List<QueuedThreadPrompt> QueuedPrompts { get; } = [];

    public List<PendingSteerPrompt> PendingSteers { get; } = [];

    public string? LastObservedPendingSteerUserContentId { get; set; }

    public AgentRunId? ActiveRunId { get; set; }

    public DateTimeOffset? ActiveRunStartedAt { get; set; }

    public AgentSessionUsage? Usage { get; set; }
}
