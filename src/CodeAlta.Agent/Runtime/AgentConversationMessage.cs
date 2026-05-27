using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Represents a replayable agent-runtime conversation message.
/// </summary>
/// <param name="Role">The conversation role.</param>
/// <param name="Parts">The message parts.</param>
public sealed record AgentConversationMessage(
    AgentConversationRole Role,
    IReadOnlyList<AgentMessagePart> Parts);

/// <summary>
/// Identifies a replayable agent-runtime conversation role.
/// </summary>
public enum AgentConversationRole
{
    /// <summary>
    /// A system message.
    /// </summary>
    System,

    /// <summary>
    /// A user-authored message.
    /// </summary>
    User,

    /// <summary>
    /// An assistant-authored message.
    /// </summary>
    Assistant,

    /// <summary>
    /// A tool-result message.
    /// </summary>
    Tool,
}

/// <summary>
/// Base type for replayable agent-runtime message parts.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AgentMessagePart.Text), "text")]
[JsonDerivedType(typeof(AgentMessagePart.Reasoning), "reasoning")]
[JsonDerivedType(typeof(AgentMessagePart.ToolCall), "toolCall")]
[JsonDerivedType(typeof(AgentMessagePart.ToolResult), "toolResult")]
[JsonDerivedType(typeof(AgentMessagePart.Uri), "uri")]
[JsonDerivedType(typeof(AgentMessagePart.Data), "data")]
public abstract record AgentMessagePart
{
    /// <summary>
    /// A plain text message part.
    /// </summary>
    /// <param name="Value">The text value.</param>
    public sealed record Text(string Value) : AgentMessagePart;

    /// <summary>
    /// A reasoning message part.
    /// </summary>
    /// <param name="Value">The optional visible reasoning text.</param>
    /// <param name="ProtectedData">Optional provider-protected reasoning payload.</param>
    /// <param name="Provenance">Optional provider/model identity that produced the reasoning payload.</param>
    public sealed record Reasoning(
        string? Value,
        string? ProtectedData = null,
        AgentReasoningProvenance? Provenance = null) : AgentMessagePart;

    /// <summary>
    /// A tool-call message part.
    /// </summary>
    /// <param name="CallId">The stable tool call identifier.</param>
    /// <param name="Name">The tool name.</param>
    /// <param name="Arguments">The tool arguments.</param>
    public sealed record ToolCall(string CallId, string Name, JsonElement Arguments) : AgentMessagePart;

    /// <summary>
    /// A tool-result message part.
    /// </summary>
    /// <param name="CallId">The stable tool call identifier.</param>
    /// <param name="Result">The structured tool result.</param>
    public sealed record ToolResult(string CallId, AgentToolResult Result) : AgentMessagePart;

    /// <summary>
    /// A URI-backed content part.
    /// </summary>
    /// <param name="Value">The URI value.</param>
    /// <param name="MediaType">The optional media type.</param>
    /// <param name="Name">The optional display name.</param>
    public sealed record Uri(string Value, string? MediaType = null, string? Name = null) : AgentMessagePart;

    /// <summary>
    /// An inline data content part.
    /// </summary>
    /// <param name="Base64Data">The base64-encoded payload.</param>
    /// <param name="MediaType">The media type.</param>
    /// <param name="Name">The optional display name.</param>
    public sealed record Data(string Base64Data, string MediaType, string? Name = null) : AgentMessagePart;
}