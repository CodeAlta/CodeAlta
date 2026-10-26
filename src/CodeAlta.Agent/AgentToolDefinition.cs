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
    /// Gets whether the tool is deferred: it stays callable in the session, but its definition is out of the model's
    /// context until the model loads it (an <see cref="AgentToolResultItem.ToolReference"/> names it). A provider that
    /// defers tools is sent its definition marked so; any other provider is not sent it until it is loaded.
    /// </summary>
    public bool Deferred { get; init; }

    /// <summary>
    /// Gets the group of the tool, such as the MCP server it comes from, for a provider that declares its deferred tools
    /// in groups (namespaces); null for none.
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// Gets whether the tool finds deferred tools and loads them, by returning <see cref="AgentToolResultItem.ToolReference"/>
    /// items: it is sent only to a provider that defers tools, and only while some tool is deferred.
    /// </summary>
    public bool SearchesDeferredTools { get; init; }

    /// <summary>
    /// Gets who may call the tool: the model and the scripts of Code Mode (the default), the scripts only, or the model only.
    /// </summary>
    public AgentToolExposure Exposure { get; init; }

    /// <summary>
    /// Gets whether calls of the tool that a script makes may run at the same time as its other calls. Tools that read
    /// say so; tools that change files or state run one at a time, so that <c>Promise.all</c> over them stays ordered.
    /// </summary>
    public bool RunsInParallel { get; init; }

    /// <summary>
    /// Gets the JSON Schema of the value a call gives a script (<see cref="AgentToolResult.Structured"/>), or null when a
    /// script receives the text of the result.
    /// </summary>
    public JsonElement? OutputSchema { get; init; }

    /// <summary>
    /// Gets the name of the tool in the scripts of Code Mode, within its <see cref="Group"/> (the MCP tool's own name, where
    /// <see cref="Name"/> is an alias), or null to use <see cref="Name"/>.
    /// </summary>
    public string? ScriptName { get; init; }

    /// <summary>
    /// Gets whether the tool takes over the others (Code Mode's <c>only</c> mode): while a run has it, the model is offered
    /// only the tools of the model (<see cref="AgentToolExposure.Model"/>) and those that stay direct
    /// (<see cref="StaysDirect"/>); the scripts it runs still call every other tool.
    /// </summary>
    public bool ScriptsTakeOver { get; init; }

    /// <summary>
    /// Gets whether the model keeps the tool when a tool takes over the others (<see cref="ScriptsTakeOver"/>): what it gives
    /// cannot pass through a script, such as an image the model looks at.
    /// </summary>
    public bool StaysDirect { get; init; }

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

    /// <summary>
    /// Gets the other tools of the run, which a script of Code Mode calls through this tool call; null when the call
    /// is not a call of the run's own tools that may make nested calls.
    /// </summary>
    [JsonIgnore]
    public IAgentRunToolInvoker? Tools { get; init; }
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
    string? Error = null)
{
    /// <summary>
    /// Gets the view a host shows for the call (an MCP App), or null. It is recorded with the call for the host and is
    /// never part of what the model is sent.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentToolAppResult? App { get; init; }

    /// <summary>
    /// Gets the value a script of Code Mode receives for the call, as described by <see cref="AgentToolSpec.OutputSchema"/>,
    /// or null when it receives the text. A script receives it whether or not the call succeeded (a command that exits
    /// with an error code still gives its output); the model is never sent it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Structured { get; init; }
}

/// <summary>
/// The view of a tool call of an MCP server (the MCP Apps extension): the <c>ui://</c> resource its server shows for it,
/// and what of the result is meant for that view.
/// </summary>
/// <param name="Server">The key of the MCP server.</param>
/// <param name="Tool">The name of the tool on the server.</param>
/// <param name="ResourceUri">The <c>ui://</c> resource of the view.</param>
/// <param name="StructuredContent">The structured content of the result as the server sent it, or null.</param>
/// <param name="Meta">The <c>_meta</c> of the result as the server sent it, or null.</param>
/// <param name="Content">The content blocks of the result as the server sent them (a JSON array), or null.</param>
public sealed record AgentToolAppResult(
    string Server,
    string Tool,
    string ResourceUri,
    JsonElement? StructuredContent = null,
    JsonElement? Meta = null,
    JsonElement? Content = null)
{
    /// <summary>
    /// Gets the arguments the server was called with (a JSON object) when they are not the arguments of the recorded
    /// call, as for a tool called through the <c>alta mcp tool call</c> command; null when they are.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Arguments { get; init; }
}

/// <summary>
/// Represents a tool result content item.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AgentToolResultItem.Text), "text")]
[JsonDerivedType(typeof(AgentToolResultItem.ImageUrl), "imageUrl")]
[JsonDerivedType(typeof(AgentToolResultItem.Image), "image")]
[JsonDerivedType(typeof(AgentToolResultItem.LocalImage), "localImage")]
[JsonDerivedType(typeof(AgentToolResultItem.ToolReference), "toolReference")]
public abstract record AgentToolResultItem
{
    /// <summary>
    /// A tool the result loads into the session: from this result on, the model has the definition of that deferred tool
    /// (<see cref="AgentToolSpec.Deferred"/>). It is how the journal records a load, in a form no provider owns: each
    /// provider renders it in its own terms (a <c>tool_reference</c> block of the Anthropic Messages API), and a provider
    /// without deferred tools is sent the tool from then on, and a line of text in the result.
    /// </summary>
    /// <param name="ToolName">The name of the tool, as in <see cref="AgentToolSpec.Name"/>.</param>
    public sealed record ToolReference(string ToolName) : AgentToolResultItem;

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
