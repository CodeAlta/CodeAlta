using System.Buffers;
using System.Text.Json;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Harness;

/// <summary>
/// A call of a tool of CodeAlta an agent harness made through the MCP server of its session.
/// </summary>
/// <param name="Name">The name of the tool.</param>
/// <param name="Arguments">The arguments, an object.</param>
/// <param name="Meta">The <c>_meta</c> object of the call, when it has one.</param>
public sealed record HarnessMcpToolCall(string Name, JsonElement Arguments, JsonElement? Meta);

/// <summary>
/// The MCP server of the tools of a CodeAlta session, independent of its transport: it answers one JSON-RPC message
/// at a time (<c>initialize</c>, <c>ping</c>, <c>tools/list</c>, <c>tools/call</c>) with the tools the session offers
/// the harness now, and gives each call to the session to run.
/// </summary>
public sealed class HarnessMcpToolServer
{
    private const string DefaultProtocolVersion = "2025-06-18";

    private readonly string _serverName;
    private readonly Func<IReadOnlyDictionary<string, AgentToolDefinition>> _tools;
    private readonly Func<HarnessMcpToolCall, CancellationToken, Task<AgentToolResult>> _callTool;
    private int _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="HarnessMcpToolServer" /> class.
    /// </summary>
    /// <param name="serverName">The name the server gives itself, such as <c>codealta</c>.</param>
    /// <param name="tools">Returns the tools the session offers now, by the name the harness calls them.</param>
    /// <param name="callTool">Runs a call; its result is given back to the harness.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="serverName" /> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tools" /> or <paramref name="callTool" /> is <see langword="null" />.</exception>
    public HarnessMcpToolServer(
        string serverName,
        Func<IReadOnlyDictionary<string, AgentToolDefinition>> tools,
        Func<HarnessMcpToolCall, CancellationToken, Task<AgentToolResult>> callTool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(callTool);
        _serverName = serverName;
        _tools = tools;
        _callTool = callTool;
    }

    /// <summary>Gets whether the harness listed the tools or said it initialized: from then on it is told when they change.</summary>
    public bool IsInitialized => Volatile.Read(ref _initialized) != 0;

    /// <summary>
    /// Answers one JSON-RPC message of the harness.
    /// </summary>
    /// <param name="message">The message, a JSON object.</param>
    /// <param name="cancellationToken">A token to cancel a tool call.</param>
    /// <returns>The response to write back, or <see langword="null" /> for a notification, which has none.</returns>
    public async Task<JsonElement?> HandleAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return CreateError(null, -32600, "The message is not a JSON-RPC object.");
        }

        JsonElement? id = message.TryGetProperty("id", out var idElement) && idElement.ValueKind is JsonValueKind.Number or JsonValueKind.String
            ? idElement.Clone()
            : null;
        var method = message.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String
            ? methodElement.GetString()
            : null;
        var parameters = message.TryGetProperty("params", out var paramsElement) && paramsElement.ValueKind == JsonValueKind.Object
            ? paramsElement
            : default;

        if (id is null)
        {
            if (string.Equals(method, "notifications/initialized", StringComparison.Ordinal))
            {
                Volatile.Write(ref _initialized, 1);
            }

            return null;
        }

        switch (method)
        {
            case "initialize":
                var protocolVersion = parameters.ValueKind == JsonValueKind.Object &&
                                      parameters.TryGetProperty("protocolVersion", out var version) &&
                                      version.ValueKind == JsonValueKind.String
                    ? version.GetString() ?? DefaultProtocolVersion
                    : DefaultProtocolVersion;
                return CreateResult(id, writer =>
                {
                    writer.WriteString("protocolVersion", protocolVersion);
                    writer.WriteStartObject("capabilities");
                    writer.WriteStartObject("tools");
                    writer.WriteBoolean("listChanged", true);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteStartObject("serverInfo");
                    writer.WriteString("name", _serverName);
                    writer.WriteString("title", "CodeAlta");
                    writer.WriteString("version", "1");
                    writer.WriteEndObject();
                });
            case "ping":
                return CreateResult(id, static _ => { });
            case "tools/list":
                Volatile.Write(ref _initialized, 1);
                var tools = _tools();
                return CreateResult(id, writer =>
                {
                    writer.WriteStartArray("tools");
                    foreach (var (name, definition) in tools)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", name);
                        writer.WriteString("description", definition.Spec.Description);
                        writer.WritePropertyName("inputSchema");
                        if (definition.Spec.InputSchema.ValueKind == JsonValueKind.Object)
                        {
                            definition.Spec.InputSchema.WriteTo(writer);
                        }
                        else
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "object");
                            writer.WriteEndObject();
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                });
            case "tools/call":
                var name = parameters.ValueKind == JsonValueKind.Object &&
                           parameters.TryGetProperty("name", out var nameElement) &&
                           nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString() ?? string.Empty
                    : string.Empty;
                var arguments = parameters.ValueKind == JsonValueKind.Object &&
                                parameters.TryGetProperty("arguments", out var argumentsElement) &&
                                argumentsElement.ValueKind == JsonValueKind.Object
                    ? argumentsElement.Clone()
                    : EmptyObject;
                JsonElement? meta = parameters.ValueKind == JsonValueKind.Object &&
                                    parameters.TryGetProperty("_meta", out var metaElement) &&
                                    metaElement.ValueKind == JsonValueKind.Object
                    ? metaElement.Clone()
                    : null;
                AgentToolResult result;
                try
                {
                    result = await _callTool(new HarnessMcpToolCall(name, arguments, meta), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    result = new AgentToolResult(false, [new AgentToolResultItem.Text("The tool call was interrupted.")], "The tool call was interrupted.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = new AgentToolResult(false, [new AgentToolResultItem.Text($"Tool '{name}' failed: {ex.Message}")], ex.Message);
                }

                return CreateResult(id, writer => WriteToolResult(writer, result));
            default:
                return CreateError(id, -32601, $"Method '{method}' is not supported.");
        }
    }

    /// <summary>
    /// Creates the notification that tells the harness the tools changed (<c>notifications/tools/list_changed</c>).
    /// </summary>
    /// <returns>The notification.</returns>
    public static JsonElement CreateToolsChangedNotification()
        => Build(static writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", "notifications/tools/list_changed");
            writer.WriteEndObject();
        });

    /// <summary>
    /// Writes the members of the MCP result of a tool call: its <c>content</c> and <c>isError</c>.
    /// </summary>
    /// <param name="writer">The writer, inside the result object.</param>
    /// <param name="result">The result of the tool.</param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is <see langword="null" />.</exception>
    public static void WriteToolResult(Utf8JsonWriter writer, AgentToolResult result)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(result);
        writer.WriteStartArray("content");
        var written = 0;
        foreach (var item in result.Items)
        {
            switch (item)
            {
                case AgentToolResultItem.Text text:
                    WriteText(writer, text.Value);
                    written++;
                    break;
                case AgentToolResultItem.ImageUrl imageUrl:
                    WriteText(writer, imageUrl.Url);
                    written++;
                    break;
                case AgentToolResultItem.Image image:
                    WriteImage(writer, image.Base64Data, image.MediaType);
                    written++;
                    break;
                case AgentToolResultItem.LocalImage localImage:
                    try
                    {
                        WriteImage(writer, Convert.ToBase64String(File.ReadAllBytes(localImage.Path)), localImage.MediaType);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        WriteText(writer, $"[image {localImage.Path} could not be read: {ex.Message}]");
                    }

                    written++;
                    break;
            }
        }

        if (written == 0)
        {
            WriteText(writer, result.Error ?? (result.Success ? "(no output)" : "The tool call failed."));
        }

        writer.WriteEndArray();
        writer.WriteBoolean("isError", !result.Success);
    }

    private static readonly JsonElement EmptyObject = Build(static writer =>
    {
        writer.WriteStartObject();
        writer.WriteEndObject();
    });

    private static JsonElement CreateResult(JsonElement? id, Action<Utf8JsonWriter> writeResult)
        => Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, id);
            writer.WriteStartObject("result");
            writeResult(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    private static JsonElement CreateError(JsonElement? id, int code, string message)
        => Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, id);
            writer.WriteStartObject("error");
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    private static void WriteId(Utf8JsonWriter writer, JsonElement? id)
    {
        writer.WritePropertyName("id");
        if (id is { } value)
        {
            value.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static void WriteText(Utf8JsonWriter writer, string text)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", text);
        writer.WriteEndObject();
    }

    private static void WriteImage(Utf8JsonWriter writer, string base64Data, string mediaType)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "image");
        writer.WriteString("data", base64Data);
        writer.WriteString("mimeType", mediaType);
        writer.WriteEndObject();
    }

    private static JsonElement Build(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
