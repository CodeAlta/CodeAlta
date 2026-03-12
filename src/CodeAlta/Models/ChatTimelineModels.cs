using System.Text;
using CodeAlta.Agent;
using XenoAtom.Terminal.UI.Controls;

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

internal sealed record ChatMarkdownEntry(DocumentFlowItem Item, MarkdownControl Markdown, Markup TimestampText);

internal sealed class ChatBackendState(AgentBackendId backendId, string displayName)
{
    public AgentBackendId BackendId { get; } = backendId;

    public string DisplayName { get; } = displayName;

    public ChatBackendAvailability Availability { get; set; }

    public string StatusMessage { get; set; } = "Not initialized.";

    public List<AgentModelInfo> Models { get; } = [];

    public string? SelectedModelId { get; set; }

    public AgentReasoningEffort? SelectedReasoningEffort { get; set; }
}

internal sealed class ChatContentState(
    DocumentFlowItem item,
    MarkdownControl markdown,
    Markup timestampText,
    StringBuilder buffer,
    AgentContentKind kind)
{
    public DocumentFlowItem Item { get; } = item;

    public MarkdownControl Markdown { get; } = markdown;

    public Markup TimestampText { get; } = timestampText;

    public StringBuilder Buffer { get; } = buffer;

    public AgentContentKind Kind { get; } = kind;
}

internal sealed class PendingAssistantState(DocumentFlowItem item, MarkdownControl markdown, Markup timestampText)
{
    public DocumentFlowItem Item { get; } = item;

    public MarkdownControl Markdown { get; } = markdown;

    public Markup TimestampText { get; } = timestampText;

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
