using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Acornima;
using Acornima.Ast;

namespace CodeAlta.CodeMode;

/// <summary>A step a script declares in its descriptor, drawn before anything runs.</summary>
/// <param name="Id">The id that calls and <c>step()</c> name.</param>
/// <param name="Title">The title shown on its box.</param>
public sealed record CodeModeStepDescriptor(string Id, string Title)
{
    /// <summary>Gets a line of detail under the title.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Gets the steps it runs after. Null means after the step declared before it; an empty list means at the start.
    /// Steps with the same <see cref="After"/> are parallel branches.
    /// </summary>
    public IReadOnlyList<string>? After { get; init; }

    /// <summary>Gets whether the step fans out: its instances appear at run time, one per label.</summary>
    public bool Each { get; init; }

    /// <summary>Gets the kind the script announces (<c>session</c>, <c>tool</c>, <c>script</c>, <c>wait</c>), or null.</summary>
    public string? Kind { get; init; }
}

/// <summary>
/// The descriptor of a script: <c>export const meta = { … }</c>, a pure literal read without running anything.
/// It names the script, types its parameters and declares its steps.
/// </summary>
/// <param name="Json">The literal as JSON.</param>
public sealed record CodeModeDescriptor(string Json)
{
    /// <summary>Gets the name of the script in the library.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the title of the script.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the one-line description of the script.</summary>
    public string? Description { get; init; }

    /// <summary>Gets when an agent should use the script.</summary>
    public string? WhenToUse { get; init; }

    /// <summary>Gets the JSON Schema of <c>args</c>, built from <c>params</c>, or null when the script takes none.</summary>
    public string? ParametersSchemaJson { get; init; }

    /// <summary>Gets the declared steps, in their order.</summary>
    public IReadOnlyList<CodeModeStepDescriptor> Steps { get; init; } = [];

    /// <summary>Gets whether the script asks to be offered to sessions as a tool.</summary>
    public bool Tool { get; init; }

    /// <summary>
    /// Checks arguments against the parameters of the script, as <c>workflow("name", args)</c> does before it runs it: a
    /// missing argument, an unknown one, or one of the wrong type.
    /// </summary>
    /// <param name="argumentsJson">The arguments, a JSON object.</param>
    /// <returns>What is wrong with them, or null when they fit.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="argumentsJson"/> is null.</exception>
    /// <exception cref="JsonException"><paramref name="argumentsJson"/> is not JSON.</exception>
    public string? CheckArguments(string argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(argumentsJson);
        return ParametersSchemaJson is { } parameters ? CodeModeRun.CheckArguments(parameters, argumentsJson) : null;
    }

    /// <summary>Reads a descriptor from the object literal of <c>meta</c>.</summary>
    /// <param name="literal">The literal.</param>
    /// <param name="error">Why it could not be read.</param>
    /// <returns>The descriptor, or null when <paramref name="error"/> is set.</returns>
    internal static CodeModeDescriptor? TryRead(ObjectExpression literal, out string? error)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (!TryWriteLiteral(writer, literal, out error))
            {
                return null;
            }
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var steps = new List<CodeModeStepDescriptor>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("steps", out var declaredSteps) || root.TryGetProperty("phases", out declaredSteps))
        {
            if (declaredSteps.ValueKind != JsonValueKind.Array)
            {
                error = "meta.steps must be an array.";
                return null;
            }

            foreach (var item in declaredSteps.EnumerateArray())
            {
                var step = ReadStep(item, out error);
                if (step is null)
                {
                    return null;
                }

                if (!ids.Add(step.Id))
                {
                    error = $"meta.steps declares the step `{step.Id}` twice.";
                    return null;
                }

                steps.Add(step);
            }
        }

        foreach (var step in steps)
        {
            foreach (var after in step.After ?? [])
            {
                if (!ids.Contains(after))
                {
                    error = $"The step `{step.Id}` runs after `{after}`, which meta.steps does not declare.";
                    return null;
                }
            }
        }

        string? parameters = null;
        if (root.TryGetProperty("params", out var declaredParameters))
        {
            parameters = ReadParameters(declaredParameters, out error);
            if (parameters is null)
            {
                return null;
            }
        }

        error = null;
        return new CodeModeDescriptor(json)
        {
            Name = GetString(root, "name"),
            Title = GetString(root, "title"),
            Description = GetString(root, "description"),
            WhenToUse = GetString(root, "whenToUse"),
            ParametersSchemaJson = parameters,
            Steps = steps,
            Tool = root.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.True,
        };
    }

    private static CodeModeStepDescriptor? ReadStep(JsonElement item, out string? error)
    {
        error = null;
        if (item.ValueKind != JsonValueKind.Object)
        {
            error = "Each entry of meta.steps must be an object such as { id: \"review\", title: \"Review\" }.";
            return null;
        }

        var title = GetString(item, "title");
        var id = GetString(item, "id") ?? (title is null ? null : Slug(title));
        if (string.IsNullOrWhiteSpace(id))
        {
            error = "Each entry of meta.steps needs an `id` (or a `title`).";
            return null;
        }

        IReadOnlyList<string>? after = null;
        if (item.TryGetProperty("after", out var declaredAfter))
        {
            if (declaredAfter.ValueKind == JsonValueKind.String)
            {
                after = [declaredAfter.GetString()!];
            }
            else if (declaredAfter.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var entry in declaredAfter.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String)
                    {
                        error = $"meta.steps: `after` of `{id}` must list step ids.";
                        return null;
                    }

                    list.Add(entry.GetString()!);
                }

                after = list;
            }
            else
            {
                error = $"meta.steps: `after` of `{id}` must be a step id or a list of them.";
                return null;
            }
        }

        return new CodeModeStepDescriptor(id, title ?? id)
        {
            Detail = GetString(item, "detail"),
            After = after,
            Each = item.TryGetProperty("each", out var each) && each.ValueKind is JsonValueKind.True or JsonValueKind.String,
            Kind = GetString(item, "kind"),
        };
    }

    // `params` maps each name to a JSON Schema, or to a type name as shorthand ({ focus: "string" }). A parameter
    // is required unless it has a `default` or says `optional: true`.
    private static string? ReadParameters(JsonElement parameters, out string? error)
    {
        error = null;
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            error = "meta.params must be an object that maps each parameter to its schema, such as { pr: { type: \"integer\" } }.";
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            var required = new List<string>();
            foreach (var parameter in parameters.EnumerateObject())
            {
                writer.WritePropertyName(parameter.Name);
                switch (parameter.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        writer.WriteStartObject();
                        writer.WriteString("type", parameter.Value.GetString());
                        writer.WriteEndObject();
                        required.Add(parameter.Name);
                        break;
                    case JsonValueKind.Object:
                        writer.WriteStartObject();
                        foreach (var property in parameter.Value.EnumerateObject())
                        {
                            if (property.Name != "optional")
                            {
                                property.WriteTo(writer);
                            }
                        }

                        writer.WriteEndObject();
                        var optional = parameter.Value.TryGetProperty("optional", out var flag) && flag.ValueKind == JsonValueKind.True;
                        if (!optional && !parameter.Value.TryGetProperty("default", out _))
                        {
                            required.Add(parameter.Name);
                        }

                        break;
                    default:
                        error = $"meta.params: `{parameter.Name}` must be a JSON Schema object or a type name.";
                        return null;
                }
            }

            writer.WriteEndObject();
            if (required.Count > 0)
            {
                writer.WritePropertyName("required");
                writer.WriteStartArray();
                foreach (var name in required)
                {
                    writer.WriteStringValue(name);
                }

                writer.WriteEndArray();
            }

            writer.WriteBoolean("additionalProperties", false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Makes an id from a title: lower case, words joined by dashes.</summary>
    /// <param name="title">The title.</param>
    /// <returns>The id.</returns>
    internal static string Slug(string title)
    {
        var builder = new StringBuilder(title.Length);
        foreach (var c in title.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().TrimEnd('-');
    }

    // Writes a literal as JSON, refusing anything that would need running code to know its value.
    private static bool TryWriteLiteral(Utf8JsonWriter writer, Node node, out string? error)
    {
        error = null;
        switch (node)
        {
            case StringLiteral text:
                writer.WriteStringValue(text.Value);
                return true;
            case NumericLiteral number:
                writer.WriteNumberValue(number.Value);
                return true;
            case BooleanLiteral flag:
                writer.WriteBooleanValue(flag.Value);
                return true;
            case NullLiteral:
                writer.WriteNullValue();
                return true;
            case UnaryExpression { Operator: Operator.UnaryNegation, Argument: NumericLiteral negative }:
                writer.WriteNumberValue(-negative.Value);
                return true;
            case TemplateLiteral { Expressions.Count: 0 } template:
                writer.WriteStringValue(string.Concat(template.Quasis.Select(static quasi => quasi.Value.Cooked ?? quasi.Value.Raw)));
                return true;
            case ArrayExpression array:
                writer.WriteStartArray();
                foreach (var element in array.Elements)
                {
                    if (element is null || !TryWriteLiteral(writer, element, out error))
                    {
                        error ??= "meta has a hole in an array.";
                        return false;
                    }
                }

                writer.WriteEndArray();
                return true;
            case ObjectExpression literal:
                writer.WriteStartObject();
                foreach (var property in literal.Properties)
                {
                    if (property is not ObjectProperty { Kind: PropertyKind.Init, Computed: false, Method: false } entry)
                    {
                        error = $"meta must be a plain literal: line {property.Location.Start.Line} has a spread, a method or a computed key.";
                        return false;
                    }

                    var key = entry.Key switch
                    {
                        Identifier identifier => identifier.Name,
                        StringLiteral text => text.Value,
                        NumericLiteral number => number.Value.ToString(CultureInfo.InvariantCulture),
                        _ => null,
                    };
                    if (key is null)
                    {
                        error = $"meta must be a plain literal: line {property.Location.Start.Line} has a key that is not a name.";
                        return false;
                    }

                    writer.WritePropertyName(key);
                    if (!TryWriteLiteral(writer, entry.Value, out error))
                    {
                        return false;
                    }
                }

                writer.WriteEndObject();
                return true;
            default:
                error = $"meta must be a plain literal: line {node.Location.Start.Line} computes a value ({node.Type}). Write the value itself.";
                return false;
        }
    }
}
