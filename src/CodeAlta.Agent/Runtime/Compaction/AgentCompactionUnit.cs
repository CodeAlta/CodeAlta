namespace CodeAlta.Agent.Runtime.Compaction;

internal abstract record AgentCompactionUnit(IReadOnlyList<AgentConversationMessage> SourceMessages)
{
    public abstract AgentConversationRole Role { get; }
}

internal sealed record AgentCompactionMessageUnit(AgentConversationMessage Message)
    : AgentCompactionUnit([Message])
{
    public override AgentConversationRole Role => Message.Role;
}

internal sealed record AgentCompactionToolInteractionUnit(
    AgentConversationMessage AssistantMessage,
    IReadOnlyList<AgentConversationMessage> ToolMessages,
    int RepeatCount = 1,
    bool IsCollapsed = false,
    string? CollapseKey = null)
    : AgentCompactionUnit([AssistantMessage, .. ToolMessages])
{
    public override AgentConversationRole Role => AssistantMessage.Role;

    public IReadOnlyList<AgentMessagePart.ToolCall> ToolCalls { get; }
        = AssistantMessage.Parts.OfType<AgentMessagePart.ToolCall>().ToArray();

    public IReadOnlyList<AgentMessagePart.ToolResult> ToolResults { get; }
        = ToolMessages.SelectMany(static message => message.Parts.OfType<AgentMessagePart.ToolResult>()).ToArray();
}
