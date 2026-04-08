using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Agent;

/// <summary>
/// Defines a custom tool and its handler.
/// </summary>
/// <param name="Spec">Tool specification.</param>
/// <param name="Handler">Tool handler.</param>
public sealed record AgentToolDefinition(AgentToolSpec Spec, AgentToolHandler Handler);

/// <summary>
/// Defines tool metadata required for registration with an agent backend.
/// </summary>
/// <param name="Name">The tool name.</param>
/// <param name="Description">The tool description.</param>
/// <param name="InputSchema">The JSON schema for tool arguments.</param>
public sealed record AgentToolSpec(string Name, string Description, JsonElement InputSchema);

/// <summary>
/// Represents a tool invocation.
/// </summary>
/// <param name="BackendId">The backend identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="ToolCallId">The tool call identifier.</param>
/// <param name="ToolName">The tool name.</param>
/// <param name="Arguments">The tool arguments.</param>
/// <param name="Progress">Optional progress callback for streaming tool output.</param>
public sealed record AgentToolInvocation(
    AgentBackendId BackendId,
    string SessionId,
    string ToolCallId,
    string ToolName,
    JsonElement Arguments,
    AgentToolProgressHandler? Progress = null);

/// <summary>
/// Represents a streaming tool-progress update.
/// </summary>
/// <param name="Delta">The incremental text delta.</param>
/// <param name="Details">Optional structured metadata for the progress update.</param>
public sealed record AgentToolProgressUpdate(
    string Delta,
    JsonElement? Details = null);

/// <summary>
/// Progress callback invoked by tools that can stream incremental output.
/// </summary>
/// <param name="update">The progress update.</param>
/// <param name="cancellationToken">A token to cancel progress delivery.</param>
public delegate ValueTask AgentToolProgressHandler(
    AgentToolProgressUpdate update,
    CancellationToken cancellationToken);

/// <summary>
/// Tool handler delegate.
/// </summary>
/// <param name="invocation">The tool invocation.</param>
/// <param name="cancellationToken">A token to cancel the operation.</param>
public delegate Task<AgentToolResult> AgentToolHandler(
    AgentToolInvocation invocation,
    CancellationToken cancellationToken);

/// <summary>
/// Represents a tool result returned to the backend.
/// </summary>
/// <param name="Success">Whether the tool call succeeded.</param>
/// <param name="Items">Content items returned to the backend/LLM.</param>
/// <param name="Error">Optional error message.</param>
public sealed record AgentToolResult(
    bool Success,
    IReadOnlyList<AgentToolResultItem> Items,
    string? Error = null);

/// <summary>
/// Represents a tool result content item.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AgentToolResultItem.Text), "text")]
[JsonDerivedType(typeof(AgentToolResultItem.ImageUrl), "imageUrl")]
public abstract record AgentToolResultItem
{
    /// <summary>
    /// Text tool output.
    /// </summary>
    /// <param name="Value">The text output.</param>
    public sealed record Text(string Value) : AgentToolResultItem;

    /// <summary>
    /// Image URL tool output.
    /// </summary>
    /// <param name="Url">The image URL (or data URL).</param>
    public sealed record ImageUrl(string Url) : AgentToolResultItem;
}
