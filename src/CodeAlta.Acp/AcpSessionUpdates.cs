using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CodeAlta.Acp;

/// <summary>
/// Creates the updates an agent sends with <see cref="IAcpClientConnection.SessionUpdateAsync"/>: each one has its
/// <c>sessionUpdate</c> kind, and an optional value left <see langword="default"/> is not written.
/// </summary>
public static class AcpSessionUpdates
{
    /// <summary>Creates a chunk of a message of the user (<c>user_message_chunk</c>), as <c>session/load</c> replays the history.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The update.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <see langword="null"/>.</exception>
    public static SessionUpdate UserMessageChunk(string text) => UserMessageChunk(ContentBlock.FromText(text));

    /// <summary>Creates a chunk of a message of the user (<c>user_message_chunk</c>).</summary>
    /// <param name="content">The content.</param>
    /// <returns>The update.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="content"/> is not defined.</exception>
    public static SessionUpdate UserMessageChunk(ContentBlock content) => Chunk("user_message_chunk", content);

    /// <summary>Creates a chunk of the answer of the agent (<c>agent_message_chunk</c>).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The update.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <see langword="null"/>.</exception>
    public static SessionUpdate AgentMessageChunk(string text) => AgentMessageChunk(ContentBlock.FromText(text));

    /// <summary>Creates a chunk of the answer of the agent (<c>agent_message_chunk</c>).</summary>
    /// <param name="content">The content.</param>
    /// <returns>The update.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="content"/> is not defined.</exception>
    public static SessionUpdate AgentMessageChunk(ContentBlock content) => Chunk("agent_message_chunk", content);

    /// <summary>Creates a chunk of the reasoning of the agent (<c>agent_thought_chunk</c>).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The update.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <see langword="null"/>.</exception>
    public static SessionUpdate AgentThoughtChunk(string text) => AgentThoughtChunk(ContentBlock.FromText(text));

    /// <summary>Creates a chunk of the reasoning of the agent (<c>agent_thought_chunk</c>).</summary>
    /// <param name="content">The content.</param>
    /// <returns>The update.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="content"/> is not defined.</exception>
    public static SessionUpdate AgentThoughtChunk(ContentBlock content) => Chunk("agent_thought_chunk", content);

    /// <summary>
    /// Creates the announce of a tool call (<c>tool_call</c>). Its <see cref="ToolCall.Kind"/> and
    /// <see cref="ToolCall.Status"/> are written when they are defined; the client takes <c>other</c> and
    /// <c>pending</c> otherwise.
    /// </summary>
    /// <param name="toolCall">The tool call.</param>
    /// <returns>The update.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="toolCall"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the call has no id or no title, or a content that is not defined.</exception>
    public static SessionUpdate ToolCall(ToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCall.ToolCallId.Value, nameof(toolCall));
        if (toolCall.Title is null)
        {
            throw new ArgumentException("A tool call has a title.", nameof(toolCall));
        }

        ThrowIfUndefined(toolCall.Content, nameof(toolCall));
        return Create("tool_call", writer =>
        {
            writer.WriteString("toolCallId", toolCall.ToolCallId.Value);
            writer.WriteString("title", toolCall.Title);
            WriteIfDefined(writer, "kind", toolCall.Kind.Value);
            WriteIfDefined(writer, "status", toolCall.Status.Value);
            WriteToolCallDetails(writer, toolCall.Name, toolCall.Content, toolCall.Locations, toolCall.RawInput, toolCall.RawOutput, toolCall.Meta);
        });
    }

    /// <summary>Creates an update of a tool call (<c>tool_call_update</c>): only what it sets is written.</summary>
    /// <param name="update">The update.</param>
    /// <returns>The update.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="update"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the update has no id, or a kind, a status or a content that is not defined.</exception>
    public static SessionUpdate ToolCallUpdate(ToolCallUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.ToolCallId.Value, nameof(update));
        if (update.Kind is { IsDefined: false } || update.Status is { IsDefined: false })
        {
            throw new ArgumentException("The kind or the status of the update is not defined.", nameof(update));
        }

        ThrowIfUndefined(update.Content, nameof(update));
        return Create("tool_call_update", writer =>
        {
            writer.WriteString("toolCallId", update.ToolCallId.Value);
            if (update.Title is not null)
            {
                writer.WriteString("title", update.Title);
            }

            if (update.Kind is { } kind)
            {
                writer.WritePropertyName("kind");
                kind.Value.WriteTo(writer);
            }

            if (update.Status is { } status)
            {
                writer.WritePropertyName("status");
                status.Value.WriteTo(writer);
            }

            WriteToolCallDetails(writer, update.Name, update.Content, update.Locations, update.RawInput, update.RawOutput, update.Meta);
        });
    }

    /// <summary>Creates the plan of the agent (<c>plan</c>): the whole list of its entries.</summary>
    /// <param name="entries">The entries, each with its <see cref="PlanEntryStatus"/> and <see cref="PlanEntryPriority"/>.</param>
    /// <returns>The update.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when an entry is <see langword="null"/>, or its status or its priority is not defined.</exception>
    public static SessionUpdate Plan(IEnumerable<PlanEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToList();
        if (list.Any(static entry => entry is null || !entry.Status.IsDefined || !entry.Priority.IsDefined || entry.Content is null))
        {
            throw new ArgumentException("An entry of the plan has no content, status or priority.", nameof(entries));
        }

        return Create("plan", writer => WriteArray(writer, "entries", list, AcpJsonSerializerContext.Default.PlanEntry));
    }

    /// <summary>Creates the list of the commands the client can offer (<c>available_commands_update</c>).</summary>
    /// <param name="commands">The commands.</param>
    /// <returns>The update.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="commands"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when a command is <see langword="null"/>, has no name, or has an input that is not defined.</exception>
    public static SessionUpdate AvailableCommands(IEnumerable<AvailableCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var list = commands.ToList();
        if (list.Any(static command => command is null || string.IsNullOrWhiteSpace(command.Name) || command.Description is null ||
                                       command.Input is { Value.ValueKind: JsonValueKind.Undefined }))
        {
            throw new ArgumentException("A command has no name or description, or an input that is not defined.", nameof(commands));
        }

        return Create("available_commands_update", writer => WriteArray(writer, "availableCommands", list, AcpJsonSerializerContext.Default.AvailableCommand));
    }

    /// <summary>Creates the change of the mode of the session (<c>current_mode_update</c>).</summary>
    /// <param name="modeId">The id of the mode.</param>
    /// <returns>The update.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="modeId"/> is empty.</exception>
    public static SessionUpdate CurrentMode(string modeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modeId);
        return Create("current_mode_update", writer => writer.WriteString("currentModeId", modeId));
    }

    /// <summary>Creates the configuration options of the session as they are now (<c>config_option_update</c>).</summary>
    /// <param name="options">The options, made with <see cref="SessionConfigOption.Select(string, string, string?, string, IEnumerable{SessionConfigSelectOption})"/>.</param>
    /// <returns>The update.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when an option is not defined.</exception>
    public static SessionUpdate ConfigOptions(IEnumerable<SessionConfigOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var list = options.ToList();
        foreach (var option in list)
        {
            ThrowIfUndefined(option.Value, nameof(options));
        }

        return Create("config_option_update", writer =>
        {
            writer.WriteStartArray("configOptions");
            foreach (var option in list)
            {
                option.Value.WriteTo(writer);
            }

            writer.WriteEndArray();
        });
    }

    /// <summary>Creates the use of the context window (<c>usage_update</c>).</summary>
    /// <param name="used">The tokens in the context.</param>
    /// <param name="size">The size of the context window, in tokens.</param>
    /// <returns>The update.</returns>
    public static SessionUpdate Usage(ulong used, ulong size)
        => Create("usage_update", writer =>
        {
            writer.WriteNumber("used", used);
            writer.WriteNumber("size", size);
        });

    /// <summary>Creates the use of the context window and the cost of the session (<c>usage_update</c>).</summary>
    /// <param name="used">The tokens in the context.</param>
    /// <param name="size">The size of the context window, in tokens.</param>
    /// <param name="cost">The cost of the session so far.</param>
    /// <returns>The update.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="cost"/> is <see langword="null"/>.</exception>
    public static SessionUpdate Usage(ulong used, ulong size, Cost cost)
    {
        ArgumentNullException.ThrowIfNull(cost);
        return Create("usage_update", writer =>
        {
            writer.WriteNumber("used", used);
            writer.WriteNumber("size", size);
            writer.WritePropertyName("cost");
            JsonSerializer.Serialize(writer, cost, AcpJsonSerializerContext.Default.Cost);
        });
    }

    /// <summary>Creates a change of the title or of the time of the last change of the session (<c>session_info_update</c>).</summary>
    /// <param name="title">The title, or <see langword="null"/> when it does not change.</param>
    /// <param name="updatedAt">The time of the last change in ISO 8601, or <see langword="null"/> when it does not change.</param>
    /// <returns>The update.</returns>
    public static SessionUpdate SessionInfo(string? title, string? updatedAt)
        => Create("session_info_update", writer =>
        {
            if (title is not null)
            {
                writer.WriteString("title", title);
            }

            if (updatedAt is not null)
            {
                writer.WriteString("updatedAt", updatedAt);
            }
        });

    internal static void ThrowIfUndefined(JsonElement value, string parameterName)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("The value is not defined.", parameterName);
        }
    }

    private static void ThrowIfUndefined(List<ToolCallContent>? content, string parameterName)
    {
        if (content?.Any(static item => item.Value.ValueKind == JsonValueKind.Undefined) == true)
        {
            throw new ArgumentException("A content of the tool call is not defined.", parameterName);
        }
    }

    private static SessionUpdate Chunk(string kind, ContentBlock content)
    {
        ThrowIfUndefined(content.Value, nameof(content));
        return Create(kind, writer =>
        {
            writer.WritePropertyName("content");
            content.Value.WriteTo(writer);
        });
    }

    private static SessionUpdate Create(string kind, Action<Utf8JsonWriter> writeFields)
        => new()
        {
            Value = AcpJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("sessionUpdate", kind);
                writeFields(writer);
                writer.WriteEndObject();
            }),
        };

    private static void WriteToolCallDetails(
        Utf8JsonWriter writer,
        string? name,
        List<ToolCallContent>? content,
        List<ToolCallLocation>? locations,
        JsonElement? rawInput,
        JsonElement? rawOutput,
        Dictionary<string, JsonElement>? meta)
    {
        if (name is not null)
        {
            writer.WriteString("name", name);
        }

        if (content is not null)
        {
            writer.WriteStartArray("content");
            foreach (var item in content)
            {
                item.Value.WriteTo(writer);
            }

            writer.WriteEndArray();
        }

        if (locations is not null)
        {
            WriteArray(writer, "locations", locations, AcpJsonSerializerContext.Default.ToolCallLocation);
        }

        WriteIfDefined(writer, "rawInput", rawInput ?? default);
        WriteIfDefined(writer, "rawOutput", rawOutput ?? default);
        if (meta is not null)
        {
            writer.WriteStartObject("_meta");
            foreach (var (key, value) in meta)
            {
                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }
    }

    private static void WriteArray<T>(Utf8JsonWriter writer, string name, List<T> items, JsonTypeInfo<T> typeInfo)
    {
        writer.WriteStartArray(name);
        foreach (var item in items)
        {
            JsonSerializer.Serialize(writer, item, typeInfo);
        }

        writer.WriteEndArray();
    }

    private static void WriteIfDefined(Utf8JsonWriter writer, string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Undefined)
        {
            writer.WritePropertyName(name);
            value.WriteTo(writer);
        }
    }
}
