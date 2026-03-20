using CodeAlta.Agent;

namespace CodeAlta.Models;

internal sealed class ThreadSessionState
{
    public AgentBackendId BackendId { get; set; } = AgentBackendIds.Codex;

    public string? ModelId { get; set; }

    public AgentReasoningEffort? ReasoningEffort { get; set; }

    public bool AutoScroll { get; set; } = true;

    public bool HistoryLoaded { get; set; }

    public bool HistoryLoading { get; set; }

    public Task? HistoryLoadTask { get; set; }

    public List<AgentEvent>? HistoryEvents { get; set; }

    public Dictionary<string, AgentPermissionRequest> PermissionRequests { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, AgentUserInputRequest> UserInputRequests { get; } = new(StringComparer.Ordinal);

    public object QueuedPromptsSyncRoot { get; } = new();

    public List<QueuedThreadPrompt> QueuedPrompts { get; } = [];

    public AgentSessionUsage? Usage { get; set; }
}
