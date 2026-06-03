using CodeAlta.Agent;
using CodeAlta.App;
using CodeAlta.Presentation.Prompting;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Models;

internal sealed partial class SessionState
{
    public SessionState()
    {
        NotesMarkdown = string.Empty;
    }

    public ModelProviderId ProviderId { get; set; } = ModelProviderIds.Codex;

    public string? ModelId { get; set; }

    public AgentReasoningEffort? ReasoningEffort { get; set; }

    public string? UserPromptName { get; set; }

    public string PromptDraftText { get; set; } = string.Empty;

    public List<PromptImageAttachment> PromptImageAttachments { get; } = [];

    public bool HistoryLoaded { get; set; }

    public bool HistoryLoading { get; set; }

    public bool PendingManualCompaction { get; set; }

    public Task? HistoryLoadTask { get; set; }

    public List<AgentEvent>? HistoryEvents { get; set; }

    public List<AgentEvent> RenderedHistoryEvents { get; } = [];

    public Dictionary<string, AgentPermissionRequest> PermissionRequests { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, AgentUserInputRequest> UserInputRequests { get; } = new(StringComparer.Ordinal);

    public object PromptStripSyncRoot { get; } = new();

    public List<QueuedSessionPrompt> QueuedPrompts { get; } = [];

    public List<PendingSteerPrompt> PendingSteers { get; } = [];

    [Bindable]
    public partial string NotesMarkdown { get; set; }

    public string? LastObservedPendingSteerUserContentId { get; set; }

    public AgentRunId? ActiveRunId { get; set; }

    public DateTimeOffset? ActiveRunStartedAt { get; set; }

    public AgentSessionUsage? Usage { get; set; }

    public AgentSystemPromptEvent? LastRenderedSystemPromptEvent { get; set; }

    public PluginTransientEventProjectionStore PluginTransientEvents { get; } = new();

    public object PluginProjectionSyncRoot { get; } = new();

    public Dictionary<string, PluginDynamicProjectionSubscription> PluginDynamicProjectionSubscriptions { get; } = new(StringComparer.Ordinal);

    public long PluginProjectionVersion;
}
