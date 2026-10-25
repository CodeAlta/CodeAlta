using System.Text.Json;

namespace CodeAlta.Acp;

// The values and the factories of the unions the generated types leave raw, so that an agent never writes JSON by
// hand and never an undefined element (which throws when it is written).

public partial record struct ProtocolVersion
{
    /// <summary>Creates a protocol version, such as 1.</summary>
    /// <param name="version">The version.</param>
    /// <returns>The protocol version.</returns>
    public static ProtocolVersion From(ushort version) => new() { Value = AcpJson.Build(writer => writer.WriteNumberValue(version)) };
}

public partial struct StopReason
{
    /// <summary>Gets the reason of a turn that ended normally (<c>end_turn</c>).</summary>
    public static StopReason EndTurn { get; } = Create("end_turn");

    /// <summary>Gets the reason of a turn that reached the output limit of the model (<c>max_tokens</c>).</summary>
    public static StopReason MaxTokens { get; } = Create("max_tokens");

    /// <summary>Gets the reason of a turn that reached the limit of its model requests (<c>max_turn_requests</c>).</summary>
    public static StopReason MaxTurnRequests { get; } = Create("max_turn_requests");

    /// <summary>Gets the reason of a turn the model refused to go on with (<c>refusal</c>).</summary>
    public static StopReason Refusal { get; } = Create("refusal");

    /// <summary>Gets the reason of a turn the client cancelled (<c>cancelled</c>).</summary>
    public static StopReason Cancelled { get; } = Create("cancelled");

    /// <summary>Gets whether the value is defined: a <see langword="default"/> value throws when it is written.</summary>
    public readonly bool IsDefined => Value.ValueKind != JsonValueKind.Undefined;

    private static StopReason Create(string value) => new() { Value = AcpJson.StringValue(value) };
}

public partial struct ToolKind
{
    /// <summary>Gets the kind of a tool that reads files or data (<c>read</c>).</summary>
    public static ToolKind Read { get; } = Create("read");

    /// <summary>Gets the kind of a tool that changes files or content (<c>edit</c>).</summary>
    public static ToolKind Edit { get; } = Create("edit");

    /// <summary>Gets the kind of a tool that deletes files or data (<c>delete</c>).</summary>
    public static ToolKind Delete { get; } = Create("delete");

    /// <summary>Gets the kind of a tool that moves or renames files (<c>move</c>).</summary>
    public static ToolKind Move { get; } = Create("move");

    /// <summary>Gets the kind of a tool that searches (<c>search</c>).</summary>
    public static ToolKind Search { get; } = Create("search");

    /// <summary>Gets the kind of a tool that runs a command or code (<c>execute</c>).</summary>
    public static ToolKind Execute { get; } = Create("execute");

    /// <summary>Gets the kind of a tool that thinks or plans (<c>think</c>).</summary>
    public static ToolKind Think { get; } = Create("think");

    /// <summary>Gets the kind of a tool that fetches from the network (<c>fetch</c>).</summary>
    public static ToolKind Fetch { get; } = Create("fetch");

    /// <summary>Gets the kind of a tool that changes the mode of the session (<c>switch_mode</c>).</summary>
    public static ToolKind SwitchMode { get; } = Create("switch_mode");

    /// <summary>Gets the kind of any other tool (<c>other</c>).</summary>
    public static ToolKind Other { get; } = Create("other");

    /// <summary>Gets whether the value is defined: a <see langword="default"/> value throws when it is written.</summary>
    public readonly bool IsDefined => Value.ValueKind != JsonValueKind.Undefined;

    private static ToolKind Create(string value) => new() { Value = AcpJson.StringValue(value) };
}

public partial struct ToolCallStatus
{
    /// <summary>Gets the status of a call that has not started (<c>pending</c>).</summary>
    public static ToolCallStatus Pending { get; } = Create("pending");

    /// <summary>Gets the status of a call that runs (<c>in_progress</c>).</summary>
    public static ToolCallStatus InProgress { get; } = Create("in_progress");

    /// <summary>Gets the status of a call that succeeded (<c>completed</c>).</summary>
    public static ToolCallStatus Completed { get; } = Create("completed");

    /// <summary>Gets the status of a call that failed (<c>failed</c>).</summary>
    public static ToolCallStatus Failed { get; } = Create("failed");

    /// <summary>Gets whether the value is defined: a <see langword="default"/> value throws when it is written.</summary>
    public readonly bool IsDefined => Value.ValueKind != JsonValueKind.Undefined;

    private static ToolCallStatus Create(string value) => new() { Value = AcpJson.StringValue(value) };
}

public partial struct PlanEntryStatus
{
    /// <summary>Gets the status of an entry not started (<c>pending</c>).</summary>
    public static PlanEntryStatus Pending { get; } = Create("pending");

    /// <summary>Gets the status of the entry being worked on (<c>in_progress</c>).</summary>
    public static PlanEntryStatus InProgress { get; } = Create("in_progress");

    /// <summary>Gets the status of a done entry (<c>completed</c>).</summary>
    public static PlanEntryStatus Completed { get; } = Create("completed");

    /// <summary>Gets whether the value is defined: a <see langword="default"/> value throws when it is written.</summary>
    public readonly bool IsDefined => Value.ValueKind != JsonValueKind.Undefined;

    private static PlanEntryStatus Create(string value) => new() { Value = AcpJson.StringValue(value) };
}

public partial struct PlanEntryPriority
{
    /// <summary>Gets the high priority (<c>high</c>).</summary>
    public static PlanEntryPriority High { get; } = Create("high");

    /// <summary>Gets the medium priority (<c>medium</c>).</summary>
    public static PlanEntryPriority Medium { get; } = Create("medium");

    /// <summary>Gets the low priority (<c>low</c>).</summary>
    public static PlanEntryPriority Low { get; } = Create("low");

    /// <summary>Gets whether the value is defined: a <see langword="default"/> value throws when it is written.</summary>
    public readonly bool IsDefined => Value.ValueKind != JsonValueKind.Undefined;

    private static PlanEntryPriority Create(string value) => new() { Value = AcpJson.StringValue(value) };
}

public partial struct SessionUpdate
{
    /// <summary>Gets the kind of the update (its <c>sessionUpdate</c>, such as <c>agent_message_chunk</c>), or <see langword="null"/>.</summary>
    public readonly string? Kind => AcpJson.GetString(Value, "sessionUpdate");
}

public partial struct ContentBlock
{
    /// <summary>Gets the type of the block (<c>text</c>, <c>image</c>, <c>resource</c>...), or <see langword="null"/>.</summary>
    public readonly string? Type => AcpJson.GetString(Value, "type");

    /// <summary>Creates a text block.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The block.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <see langword="null"/>.</exception>
    public static ContentBlock FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ContentBlock
        {
            Value = AcpJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", text);
                writer.WriteEndObject();
            }),
        };
    }

    /// <summary>Creates an image block.</summary>
    /// <param name="base64Data">The image, in base64.</param>
    /// <param name="mimeType">The media type, such as <c>image/png</c>.</param>
    /// <returns>The block.</returns>
    /// <exception cref="ArgumentException">Thrown when an argument is <see langword="null"/> or empty.</exception>
    public static ContentBlock FromImage(string base64Data, string mimeType)
    {
        ArgumentException.ThrowIfNullOrEmpty(base64Data);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        return new ContentBlock
        {
            Value = AcpJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "image");
                writer.WriteString("data", base64Data);
                writer.WriteString("mimeType", mimeType);
                writer.WriteEndObject();
            }),
        };
    }

    /// <summary>Creates a block that links a resource, such as a file, without its content.</summary>
    /// <param name="uri">The URI of the resource.</param>
    /// <param name="name">Its name.</param>
    /// <returns>The block.</returns>
    /// <exception cref="ArgumentException">Thrown when an argument is <see langword="null"/> or empty.</exception>
    public static ContentBlock FromResourceLink(string uri, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new ContentBlock
        {
            Value = AcpJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "resource_link");
                writer.WriteString("uri", uri);
                writer.WriteString("name", name);
                writer.WriteEndObject();
            }),
        };
    }
}

public partial struct ToolCallContent
{
    /// <summary>Creates the content of a tool call that is a text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The content.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <see langword="null"/>.</exception>
    public static ToolCallContent FromText(string text) => FromContent(ContentBlock.FromText(text));

    /// <summary>Creates the content of a tool call that is a content block.</summary>
    /// <param name="content">The block.</param>
    /// <returns>The content.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="content"/> is not defined.</exception>
    public static ToolCallContent FromContent(ContentBlock content)
    {
        AcpSessionUpdates.ThrowIfUndefined(content.Value, nameof(content));
        return new ToolCallContent
        {
            Value = AcpJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "content");
                writer.WritePropertyName("content");
                content.Value.WriteTo(writer);
                writer.WriteEndObject();
            }),
        };
    }

    /// <summary>Creates the content of a tool call that changes a file.</summary>
    /// <param name="path">The absolute path of the file.</param>
    /// <param name="oldText">The text before, or <see langword="null"/> for a new file.</param>
    /// <param name="newText">The text after.</param>
    /// <returns>The content.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="newText"/> is <see langword="null"/>.</exception>
    public static ToolCallContent FromDiff(string path, string? oldText, string newText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(newText);
        return new ToolCallContent
        {
            Value = AcpJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "diff");
                writer.WriteString("path", path);
                if (oldText is not null)
                {
                    writer.WriteString("oldText", oldText);
                }

                writer.WriteString("newText", newText);
                writer.WriteEndObject();
            }),
        };
    }
}

public partial struct SessionConfigOption
{
    /// <summary>Gets the id of the option, or <see langword="null"/>.</summary>
    public readonly string? Id => AcpJson.GetString(Value, "id");

    /// <summary>Gets the category of the option (<c>mode</c>, <c>model</c>, <c>thought_level</c>), or <see langword="null"/>.</summary>
    public readonly string? Category => AcpJson.GetString(Value, "category");

    /// <summary>Gets the current value of a select option, or <see langword="null"/>.</summary>
    public readonly string? CurrentValue => AcpJson.GetString(Value, "currentValue");

    /// <summary>Creates a select option, such as the model or the reasoning effort of a session.</summary>
    /// <param name="id">The id of the option, which <c>session/set_config_option</c> names.</param>
    /// <param name="name">The name shown.</param>
    /// <param name="category">The category (<c>mode</c>, <c>model</c>, <c>thought_level</c>), or <see langword="null"/>.</param>
    /// <param name="currentValue">The current value, one of <paramref name="values"/>.</param>
    /// <param name="values">The values.</param>
    /// <returns>The option.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when a text is empty, when there is no value, or when <paramref name="currentValue"/> is not one of them.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="values"/> is <see langword="null"/>.</exception>
    public static SessionConfigOption Select(string id, string name, string? category, string currentValue, IEnumerable<SessionConfigSelectOption> values)
        => Select(id, name, description: null, category, currentValue, values);

    /// <summary>Creates a select option with a description.</summary>
    /// <param name="id">The id of the option, which <c>session/set_config_option</c> names.</param>
    /// <param name="name">The name shown.</param>
    /// <param name="description">The description, or <see langword="null"/>.</param>
    /// <param name="category">The category (<c>mode</c>, <c>model</c>, <c>thought_level</c>), or <see langword="null"/>.</param>
    /// <param name="currentValue">The current value, one of <paramref name="values"/>.</param>
    /// <param name="values">The values.</param>
    /// <returns>The option.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when a text is empty, when there is no value, or when <paramref name="currentValue"/> is not one of them.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="values"/> is <see langword="null"/>.</exception>
    public static SessionConfigOption Select(string id, string name, string? description, string? category, string currentValue, IEnumerable<SessionConfigSelectOption> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentValue);
        ArgumentNullException.ThrowIfNull(values);
        var list = values.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException($"The option '{id}' has no value.", nameof(values));
        }

        if (list.Any(static value => value is null || string.IsNullOrWhiteSpace(value.Value.Value) || string.IsNullOrWhiteSpace(value.Name)))
        {
            throw new ArgumentException($"A value of the option '{id}' has no value or no name.", nameof(values));
        }

        if (!list.Any(value => string.Equals(value.Value.Value, currentValue, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"The current value '{currentValue}' is not a value of the option '{id}'.", nameof(currentValue));
        }

        return new SessionConfigOption
        {
            Value = AcpJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "select");
                writer.WriteString("id", id);
                writer.WriteString("name", name);
                if (description is not null)
                {
                    writer.WriteString("description", description);
                }

                if (category is not null)
                {
                    writer.WriteString("category", category);
                }

                writer.WriteString("currentValue", currentValue);
                writer.WriteStartArray("options");
                foreach (var value in list)
                {
                    System.Text.Json.JsonSerializer.Serialize(writer, value, AcpJsonSerializerContext.Default.SessionConfigSelectOption);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }),
        };
    }
}

public partial struct SetSessionConfigOptionRequest
{
    /// <summary>Gets the session whose option is set, or an empty string.</summary>
    public readonly string SessionId => AcpJson.GetString(Value, "sessionId") ?? string.Empty;

    /// <summary>Gets the id of the option set, or an empty string.</summary>
    public readonly string ConfigId => AcpJson.GetString(Value, "configId") ?? string.Empty;

    /// <summary>Gets the value the option is set to: a string for a select option, a boolean for a boolean one.</summary>
    public readonly JsonElement OptionValue => Value.ValueKind == JsonValueKind.Object && Value.TryGetProperty("value", out var value) ? value : default;

    /// <summary>Gets the value the option is set to when it is a string, or <see langword="null"/>.</summary>
    public readonly string? StringValue => AcpJson.GetString(Value, "value");
}
