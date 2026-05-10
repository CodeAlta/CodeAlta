using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using XenoAtom.Terminal.UI;
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

    public sealed record ChatBackendOption(AgentBackendId BackendId, string Label)
    {
        public override string ToString() => Label;
    }

    public sealed record ChatModelOption(string? ModelId, string Label)
    {
        public override string ToString() => Label;
    }

    public sealed record ChatReasoningOption(AgentReasoningEffort? Effort, string Label)
    {
        public override string ToString() => Label;
    }

    internal sealed record ChatMarkdownEntry(DocumentFlowItem Item, MarkdownControl Markdown, Markup TimestampText, Markup HeaderText)
    {
        public IReadOnlyList<MarkdownControl> DetailMarkdownControls { get; init; } = [];

        public IReadOnlyList<int> DetailMarkdownSectionIndexes { get; init; } = [];

        public ChatMarkdownCopyState CopyState { get; init; } = new();
    }

    internal sealed record ChatMarkdownCopyState
    {
        public IReadOnlyList<ChatCollapsibleMarkdownSection>? DetailSections { get; set; }
    }

    internal sealed record ChatCollapsibleMarkdownSection(
        string Header,
        string Markdown,
        Func<Visual>? VisualFactory = null,
        Func<Visual>? HeaderVisualFactory = null);

    internal sealed class ChatBackendState(AgentBackendId backendId, string displayName)
    {
        public AgentBackendId BackendId { get; } = backendId;

        public string DisplayName { get; set; } = displayName;

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

        public string? DraftAttemptId { get; set; }
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

    internal sealed class OptimisticUserPromptState(ChatMarkdownEntry entry, IReadOnlyList<DocumentFlowItem> timelineItems, string prompt)
    {
        public ChatMarkdownEntry Entry { get; } = entry;

        public IReadOnlyList<DocumentFlowItem> TimelineItems { get; } = timelineItems;

        public string Prompt { get; } = prompt;

        public string? EchoContentId { get; set; }
    }

    internal sealed class ChatStatusState(DocumentFlowItem item, MarkdownControl markdown, Markup timestampText, IReadOnlyList<MarkdownControl> detailMarkdownControls, IReadOnlyList<int> detailMarkdownSectionIndexes, ChatMarkdownCopyState copyState)
    {
        public DocumentFlowItem Item { get; } = item;

        public MarkdownControl Markdown { get; } = markdown;

        public Markup TimestampText { get; } = timestampText;

        public IReadOnlyList<MarkdownControl> DetailMarkdownControls { get; } = detailMarkdownControls;

        public IReadOnlyList<int> DetailMarkdownSectionIndexes { get; } = detailMarkdownSectionIndexes;

        public ChatMarkdownCopyState CopyState { get; } = copyState;

        public bool HasContentVisualFactory { get; init; }

        public string BaseMarkdown { get; set; } = string.Empty;

        public string? StatusMarkdown { get; set; }

        public IReadOnlyList<ChatCollapsibleMarkdownSection> DetailSections { get; set; } = [];

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

        public string? DiffText { get; set; }

        public JsonElement? Details { get; set; }

        public StringBuilder OutputBuffer { get; } = new();

        public StringBuilder CurrentOutputLineBuffer { get; } = new();

        public int OutputLineCount { get; set; }

        public int OutputByteCount { get; set; }

        public int OutputNewlineCount { get; set; }

        public int OutputTrailingNewlineCount { get; set; }

        public int OutputNonNewlineCharacterCount { get; set; }

        public bool SkipLeadingLineFeed { get; set; }

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

        public int PendingCount { get; set; }

        public int RunningCount { get; set; }

        public int CompletedCount { get; set; }

        public int FailedCount { get; set; }

        public int CanceledCount { get; set; }

        public DateTimeOffset LastUpdatedAt { get; set; }
    }

    internal enum FileChangeOperation
    {
        Unknown,
        Modified,
        Created,
        Deleted,
    }

    internal sealed class FileChangeEntryState(
        string filePath,
        Button button,
        Markup fileNameText,
        Markup directoryText,
        Markup countsText)
    {
        public string FilePath { get; } = filePath;

        public Button Button { get; } = button;

        public Markup FileNameText { get; } = fileNameText;

        public Markup DirectoryText { get; } = directoryText;

        public Markup CountsText { get; } = countsText;

        public FileChangeGroupState? Group { get; set; }

        public FileChangeOperation Operation { get; set; } = FileChangeOperation.Unknown;

        public int Additions { get; set; }

        public int Deletions { get; set; }

        public string? DiffText { get; set; }

        public DateTimeOffset FirstSeenAt { get; set; }

        public DateTimeOffset LastUpdatedAt { get; set; }

        public Dialog? DetailDialog { get; set; }

        public MarkdownControl? DetailMetadata { get; set; }

        public LogControl? DetailLog { get; set; }

        public Markup? DetailStatsText { get; set; }
    }

    internal sealed class FileChangeGroupState(
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

        public Dictionary<string, FileChangeEntryState> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int TotalAdditions { get; set; }

        public int TotalDeletions { get; set; }

        public DateTimeOffset LastUpdatedAt { get; set; }
    }
}
