using System.Buffers;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Browser.Tools;

/// <summary>An image a tool gives back.</summary>
/// <param name="Data">The encoded image.</param>
/// <param name="MediaType">Its media type, such as <c>image/png</c>.</param>
internal sealed record BrowserImage(byte[] Data, string MediaType);

/// <summary>What a tool call gives back.</summary>
/// <param name="Text">The text; when the tool failed, why and what to do instead.</param>
/// <param name="Images">The images that go with the text.</param>
/// <param name="IsError">Whether the tool failed.</param>
internal sealed record BrowserToolResult(string Text, IReadOnlyList<BrowserImage> Images, bool IsError);

/// <summary>Which browsers a tool is offered for.</summary>
internal enum BrowserToolScope
{
    /// <summary>Every browser.</summary>
    All,

    /// <summary>Chromium browsers only: what needs the DevTools Protocol.</summary>
    Chromium,
}

/// <summary>A tool of a browser session: the name, arguments and texts of the tool of Chrome DevTools MCP.</summary>
/// <param name="Name">The name.</param>
/// <param name="Category">The category, as in Chrome DevTools MCP.</param>
/// <param name="Description">What it does.</param>
/// <param name="ReadOnly">Whether it only reads.</param>
/// <param name="InputSchema">The JSON Schema of its arguments.</param>
/// <param name="Scope">The browsers it is offered for.</param>
/// <param name="Handler">What it does.</param>
internal sealed record BrowserToolDefinition(string Name, string Category, string Description, bool ReadOnly, JsonElement InputSchema, BrowserToolScope Scope, Func<BrowserToolCall, Task> Handler);

/// <summary>One call of a tool: its arguments, and what its answer is made of.</summary>
internal sealed class BrowserToolCall
{
    private readonly JsonElement _arguments;

    public BrowserToolCall(BrowserSession session, JsonElement arguments, CancellationToken cancellationToken)
    {
        Session = session;
        _arguments = arguments;
        CancellationToken = cancellationToken;
    }

    /// <summary>Gets the session.</summary>
    public BrowserSession Session { get; }

    /// <summary>Gets the cancellation of the call.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Gets the lines that start the answer.</summary>
    public List<string> Lines { get; } = [];

    /// <summary>Gets the sections that end it.</summary>
    public List<string> Sections { get; } = [];

    /// <summary>Gets the images of the answer.</summary>
    public List<BrowserImage> Images { get; } = [];

    /// <summary>Gets or sets the page the call acted on, whose dialog and emulation the answer reports.</summary>
    public BrowserPageState? Page { get; set; }

    /// <summary>Gets or sets whether the answer lists the pages.</summary>
    public bool IncludePages { get; set; }

    /// <summary>Gets or sets the snapshot the answer includes.</summary>
    public BrowserSnapshot? Snapshot { get; set; }

    /// <summary>Gets or sets whether the answer carries what a page wrote: it then says that this is data.</summary>
    public bool CarriesPageContent { get; set; }

    private JsonElement? Get(string name)
        => _arguments.ValueKind == JsonValueKind.Object && _arguments.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value : null;

    public string? OptionalString(string name) => Get(name) is { } value
        ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw Invalid(name, "a string")
        : null;

    public string RequiredString(string name) => OptionalString(name) ?? throw Missing(name);

    public bool? OptionalBoolean(string name) => Get(name) is { } value
        ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Invalid(name, "a boolean") }
        : null;

    public double? OptionalNumber(string name) => Get(name) is { } value
        ? value.ValueKind == JsonValueKind.Number ? value.GetDouble()
            : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed
            : throw Invalid(name, "a number")
        : null;

    public double RequiredNumber(string name) => OptionalNumber(name) ?? throw Missing(name);

    public int? OptionalInteger(string name) => OptionalNumber(name) is { } number
        ? number == Math.Floor(number) && number is >= int.MinValue and <= int.MaxValue ? (int)number : throw Invalid(name, "an integer")
        : null;

    public IReadOnlyList<string>? OptionalStrings(string name)
    {
        if (Get(name) is not { } value) return null;
        if (value.ValueKind != JsonValueKind.Array) throw Invalid(name, "an array of strings");
        var list = new List<string>();
        foreach (var item in value.EnumerateArray()) list.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : throw Invalid(name, "an array of strings"));
        return list;
    }

    public JsonElement RequiredArray(string name) => Get(name) is { ValueKind: JsonValueKind.Array } value ? value : throw Missing(name);

    /// <summary>A timeout argument: 0 or none is the default of the session.</summary>
    public TimeSpan Timeout(string name, TimeSpan fallback) => OptionalInteger(name) is { } milliseconds and > 0 ? TimeSpan.FromMilliseconds(milliseconds) : fallback;

    public static BrowserToolException Invalid(string name, string expected) => new("invalid-arguments", $"The argument \"{name}\" must be {expected}.");

    public static BrowserToolException Missing(string name) => new("invalid-arguments", $"The argument \"{name}\" is required.");
}

/// <summary>Builds the JSON Schema of an object whose properties are the arguments of a tool.</summary>
internal sealed class BrowserToolSchema
{
    private readonly List<(string Name, Action<Utf8JsonWriter> Write)> _properties = [];
    private readonly List<string> _required = [];

    /// <summary>A schema that starts with the optional <c>pageId</c> of the tools that act on a page.</summary>
    public static BrowserToolSchema ForPage() => new BrowserToolSchema().Number("pageId", "Targets a specific page by ID. When omitted, the selected page is used.");

    public BrowserToolSchema String(string name, string description, bool required = false) => Add(name, required, writer =>
    {
        writer.WriteString("type", "string");
        writer.WriteString("description", description);
    });

    public BrowserToolSchema Boolean(string name, string description) => Add(name, false, writer =>
    {
        writer.WriteString("type", "boolean");
        writer.WriteString("description", description);
    });

    public BrowserToolSchema Number(string name, string description, bool required = false, double? minimum = null, double? maximum = null) => Add(name, required, writer =>
    {
        writer.WriteString("type", "number");
        if (minimum is { } low) writer.WriteNumber("minimum", low);
        if (maximum is { } high) writer.WriteNumber("maximum", high);
        writer.WriteString("description", description);
    });

    public BrowserToolSchema Integer(string name, string description, int? minimum = null) => Add(name, false, writer =>
    {
        writer.WriteString("type", "integer");
        if (minimum is { } low) writer.WriteNumber("minimum", low);
        writer.WriteString("description", description);
    });

    public BrowserToolSchema Enum(string name, string description, string[] values, bool required = false) => Add(name, required, writer =>
    {
        WriteEnum(writer, values);
        writer.WriteString("description", description);
    });

    public BrowserToolSchema Array(string name, string description, bool required, Action<Utf8JsonWriter> items, int? minItems = null) => Add(name, required, writer =>
    {
        writer.WriteString("type", "array");
        if (minItems is { } count) writer.WriteNumber("minItems", count);
        writer.WriteStartObject("items");
        items(writer);
        writer.WriteEndObject();
        writer.WriteString("description", description);
    });

    public static void WriteEnum(Utf8JsonWriter writer, string[] values)
    {
        writer.WriteString("type", "string");
        writer.WriteStartArray("enum");
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    public static void WriteProperty(Utf8JsonWriter writer, string name, string type, string description)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", type);
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }

    public JsonElement Build()
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            foreach (var (name, write) in _properties)
            {
                writer.WriteStartObject(name);
                write(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            if (_required.Count != 0)
            {
                writer.WriteStartArray("required");
                foreach (var name in _required) writer.WriteStringValue(name);
                writer.WriteEndArray();
            }

            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private BrowserToolSchema Add(string name, bool required, Action<Utf8JsonWriter> write)
    {
        _properties.Add((name, write));
        if (required) _required.Add(name);
        return this;
    }

    /// <summary>Writes a value as readable JSON text: quotes and angle brackets kept.</summary>
    public static string Readable(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) write(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
