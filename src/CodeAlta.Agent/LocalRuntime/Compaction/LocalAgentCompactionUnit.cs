namespace CodeAlta.Agent.LocalRuntime.Compaction;

internal abstract record LocalAgentCompactionUnit(IReadOnlyList<LocalAgentConversationMessage> SourceMessages)
{
    public abstract LocalAgentConversationRole Role { get; }
}

internal sealed record LocalAgentCompactionMessageUnit(LocalAgentConversationMessage Message)
    : LocalAgentCompactionUnit([Message])
{
    public override LocalAgentConversationRole Role => Message.Role;
}

internal sealed record LocalAgentCompactionToolInteractionUnit(
    LocalAgentConversationMessage AssistantMessage,
    IReadOnlyList<LocalAgentConversationMessage> ToolMessages,
    int RepeatCount = 1,
    bool IsCollapsed = false,
    string? CollapseKey = null)
    : LocalAgentCompactionUnit([AssistantMessage, .. ToolMessages])
{
    public override LocalAgentConversationRole Role => AssistantMessage.Role;

    public IReadOnlyList<LocalAgentMessagePart.ToolCall> ToolCalls { get; }
        = AssistantMessage.Parts.OfType<LocalAgentMessagePart.ToolCall>().ToArray();

    public IReadOnlyList<LocalAgentMessagePart.ToolResult> ToolResults { get; }
        = ToolMessages.SelectMany(static message => message.Parts.OfType<LocalAgentMessagePart.ToolResult>()).ToArray();
}
