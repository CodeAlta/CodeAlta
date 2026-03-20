using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Models
{
    internal sealed record PendingChatMessage(
        DocumentFlowItem UserItem,
        DocumentFlowItem AssistantItem,
        MarkdownControl StreamingMarkdown,
        Markup TimestampText);

    internal enum ChatBackendAvailability
    {
        Unknown,
        Connecting,
        Ready,
        Unsupported,
        Failed,
    }

    internal enum ChatTimelineTone
    {
        User,
        Assistant,
        Reasoning,
        Activity,
        Notice,
        Interaction,
    }

    internal sealed record ChatBackendOption(AgentBackendId BackendId, string Label)
    {
        public override string ToString() => Label;
    }

    internal sealed record ChatModelOption(string? ModelId, string Label)
    {
        public override string ToString() => Label;
    }

    internal sealed record ChatReasoningOption(AgentReasoningEffort? Effort, string Label)
    {
        public override string ToString() => Label;
    }

    internal sealed record ChatMarkdownEntry(DocumentFlowItem Item, MarkdownControl Markdown, Markup TimestampText, Markup HeaderText);

    internal sealed class ChatBackendState(AgentBackendId backendId, string displayName)
    {
        public AgentBackendId BackendId { get; } = backendId;

        public string DisplayName { get; } = displayName;

        public ChatBackendAvailability Availability { get; set; }

        public string StatusMessage { get; set; } = "Not initialized.";

        public List<AgentModelInfo> Models { get; } = [];

        public string? SelectedModelId { get; set; }

        public AgentReasoningEffort? SelectedReasoningEffort { get; set; }

        public string? DraftScopeKey { get; set; }
    }

    internal sealed class ChatContentState(
        DocumentFlowItem item,
        MarkdownControl markdown,
        Markup timestampText,
        Markup headerText,
        StringBuilder buffer,
        AgentContentKind kind)
    {
        public DocumentFlowItem Item { get; } = item;

        public MarkdownControl Markdown { get; } = markdown;

        public Markup TimestampText { get; } = timestampText;

        public Markup HeaderText { get; } = headerText;

        public StringBuilder Buffer { get; } = buffer;

        public AgentContentKind Kind { get; } = kind;
    }

    internal sealed class PendingAssistantState(DocumentFlowItem item, MarkdownControl markdown, Markup timestampText, Markup headerText)
    {
        public DocumentFlowItem Item { get; } = item;

        public MarkdownControl Markdown { get; } = markdown;

        public Markup TimestampText { get; } = timestampText;

        public Markup HeaderText { get; } = headerText;

        public StringBuilder Buffer { get; } = new();

        public string? ContentId { get; set; }
    }

    internal sealed class ChatStatusState(DocumentFlowItem item, MarkdownControl markdown, Markup timestampText)
    {
        public DocumentFlowItem Item { get; } = item;

        public MarkdownControl Markdown { get; } = markdown;

        public Markup TimestampText { get; } = timestampText;

        public string BaseMarkdown { get; set; } = string.Empty;

        public string? StatusMarkdown { get; set; }

        public string MarkdownValue =>
            string.IsNullOrWhiteSpace(StatusMarkdown)
                ? BaseMarkdown
                : $"{BaseMarkdown}\n\n{StatusMarkdown}";
    }

    internal sealed class TruncatedHistoryState(DocumentFlowItem item, Rule rule, int omittedMessageCount)
    {
        public DocumentFlowItem Item { get; } = item;

        public Rule Rule { get; } = rule;

        public int OmittedMessageCount { get; } = omittedMessageCount;

        public bool CanLoad { get; set; } = true;
    }

    internal sealed record ThreadHistoryLoadPlan(IReadOnlyList<AgentEvent> EventsToRender, int OmittedMessageCount);

    internal enum ToolCallDisplayStatus
    {
        Pending,
        Running,
        Completed,
        Failed,
        Canceled,
    }

    internal sealed class ToolCallEntryState(string toolCallId, Button button, Markup summaryText)
    {
        public string ToolCallId { get; } = toolCallId;

        public Button Button { get; } = button;

        public Markup SummaryText { get; } = summaryText;

        public ToolCallGroupState? Group { get; set; }

        public AgentActivityKind ActivityKind { get; set; } = AgentActivityKind.ToolCall;

        public ToolCallDisplayStatus Status { get; set; } = ToolCallDisplayStatus.Pending;

        public string DisplayName { get; set; } = "Tool";

        public string? ArgumentPreview { get; set; }

        public string? OutputPreview { get; set; }

        public string? ParentToolCallId { get; set; }

        public string? CommandText { get; set; }

        public string? ArgumentText { get; set; }

        public string? StatusMessage { get; set; }

        public JsonElement? Details { get; set; }

        public StringBuilder OutputBuffer { get; } = new();

        public int OutputLineCount { get; set; }

        public int OutputByteCount { get; set; }

        public DateTimeOffset FirstSeenAt { get; set; }

        public DateTimeOffset LastUpdatedAt { get; set; }

        public DateTimeOffset? CompletedAt { get; set; }

        public Dialog? DetailDialog { get; set; }

        public MarkdownControl? DetailMetadata { get; set; }

        public LogControl? DetailLog { get; set; }

        public Markup? DetailStatsText { get; set; }
    }

    internal sealed class ToolCallGroupState(
        DocumentFlowItem item,
        WrapHStack itemsHost,
        Markup headerText,
        Markup summaryText,
        Markup timestampText)
    {
        public DocumentFlowItem Item { get; } = item;

        public WrapHStack ItemsHost { get; } = itemsHost;

        public Markup HeaderText { get; } = headerText;

        public Markup SummaryText { get; } = summaryText;

        public Markup TimestampText { get; } = timestampText;

        public Dictionary<string, ToolCallEntryState> ToolCalls { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset LastUpdatedAt { get; set; }
    }
}