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
/// Defines tool metadata required for registration with an agent provider.
/// </summary>
public sealed record AgentToolSpec
{
    private const string NamePattern = "^[a-zA-Z0-9_-]+$";
    private string _name = string.Empty;
    private string _description = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentToolSpec"/> record.
    /// </summary>
    /// <param name="name">The tool name. Tool names must match <c>^[a-zA-Z0-9_-]+$</c>.</param>
    /// <param name="description">The tool description.</param>
    /// <param name="inputSchema">The JSON schema for tool arguments.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name" /> is empty or contains unsupported characters.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="description" /> is <see langword="null" />.</exception>
    public AgentToolSpec(string name, string description, JsonElement inputSchema)
    {
        Name = name;
        Description = description;
        InputSchema = inputSchema;
    }

    /// <summary>
    /// Gets the tool name.
    /// </summary>
    public string Name
    {
        get => _name;
        init => _name = ValidateName(value);
    }

    /// <summary>
    /// Gets the tool description.
    /// </summary>
    public string Description
    {
        get => _description;
        init => _description = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Gets the JSON schema for tool arguments.
    /// </summary>
    public JsonElement InputSchema { get; init; }

    /// <summary>
    /// Deconstructs this tool specification into its components.
    /// </summary>
    /// <param name="name">The tool name.</param>
    /// <param name="description">The tool description.</param>
    /// <param name="inputSchema">The JSON schema for tool arguments.</param>
    public void Deconstruct(out string name, out string description, out JsonElement inputSchema)
    {
        name = Name;
        description = Description;
        inputSchema = InputSchema;
    }

    private static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!IsValidName(name))
        {
            throw new ArgumentException($"Tool name '{name}' is invalid. Tool names must match {NamePattern}.", nameof(name));
        }

        return name;
    }

    private static bool IsValidName(string name)
    {
        foreach (var ch in name)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Represents a tool invocation.
/// </summary>
/// <param name="ProviderId">The model provider identifier. Serialized as <c>backendId</c> for tool-journal compatibility.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="ToolCallId">The tool call identifier.</param>
/// <param name="ToolName">The tool name.</param>
/// <param name="Arguments">The tool arguments.</param>
/// <param name="Progress">Optional progress callback for streaming tool output.</param>
public sealed record AgentToolInvocation(
    [property: JsonPropertyName("backendId")]
    ModelProviderId ProviderId,
    string SessionId,
    string ToolCallId,
    string ToolName,
    JsonElement Arguments,
    AgentToolProgressHandler? Progress = null)
{
    /// <summary>
    /// Gets the tools of the run this call belongs to: tools the handler adds there are offered to the model
    /// from its next request of the same run. Null when the caller is not a run that can take them.
    /// </summary>
    [JsonIgnore]
    public AgentRunTools? RunTools { get; init; }
}

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
/// Represents a tool result returned to the provider.
/// </summary>
/// <param name="Success">Whether the tool call succeeded.</param>
/// <param name="Items">Content items returned to the provider/LLM.</param>
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
[JsonDerivedType(typeof(AgentToolResultItem.Image), "image")]
[JsonDerivedType(typeof(AgentToolResultItem.LocalImage), "localImage")]
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

    /// <summary>
    /// An image a tool returns for the model to look at, as encoded bytes.
    /// </summary>
    /// <remarks>
    /// This is what a tool handler returns. A session checks the image, resizes it when it is too large and,
    /// when it has a folder for its attachments, saves it there and keeps a <see cref="LocalImage"/> instead.
    /// </remarks>
    /// <param name="Base64Data">The encoded image file (PNG, JPEG, GIF, WebP or BMP), in base64.</param>
    /// <param name="MediaType">The image MIME type the tool declares; the bytes decide.</param>
    /// <param name="DisplayName">Optional name shown for the image, such as its file name.</param>
    public sealed record Image(string Base64Data, string MediaType, string? DisplayName = null) : AgentToolResultItem;

    /// <summary>
    /// An image of a tool result saved in the session's attachment folder.
    /// </summary>
    /// <remarks>
    /// A session records this item in place of an <see cref="Image"/>: the journal names the file and does not
    /// hold its bytes. The model receives the image while the run that read it is active.
    /// </remarks>
    /// <param name="Path">The full path of the saved image file.</param>
    /// <param name="MediaType">The image MIME type.</param>
    /// <param name="DisplayName">Optional name shown for the image, such as its file name.</param>
    /// <param name="Width">Width in pixels, or 0 when unknown.</param>
    /// <param name="Height">Height in pixels, or 0 when unknown.</param>
    public sealed record LocalImage(string Path, string MediaType, string? DisplayName = null, int Width = 0, int Height = 0) : AgentToolResultItem;
}
