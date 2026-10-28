using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeAlta.CodeMode;

/// <summary>
/// Renders the functions of a script's API as TypeScript declarations, for the model to read. Schemas are bounded:
/// each renders in at most <see cref="MaxSchemaCharacters"/> characters and expands a limited number of references;
/// beyond that a type becomes <c>unknown</c>, so a large MCP schema never floods a description.
/// </summary>
public static class CodeModeDeclarations
{
    /// <summary>The most characters one schema renders to.</summary>
    public const int MaxSchemaCharacters = 16_000;

    private const int MaxReferenceDepth = 2;
    private const int MaxReferences = 32;

    /// <summary>Renders the declaration of one function, with its description.</summary>
    /// <param name="function">The function.</param>
    /// <returns>The declaration, such as <c>read_file(args: { path: string }): Promise&lt;string&gt;;</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="function"/> is null.</exception>
    public static string RenderFunction(CodeModeFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        var builder = new StringBuilder();
        AppendFunction(builder, function, string.Empty);
        return builder.ToString();
    }

    /// <summary>Renders functions grouped in their namespaces, as <c>declare const tools: { … };</c> blocks.</summary>
    /// <param name="functions">The functions.</param>
    /// <returns>The declarations.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="functions"/> is null.</exception>
    public static string Render(IEnumerable<CodeModeFunction> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        var root = new NamespaceNode();
        foreach (var function in functions)
        {
            var segments = function.Path.Split('.');
            var node = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                node = node.GetOrAdd(segments[i]);
            }

            node.Functions.Add((segments[^1], function));
        }

        var builder = new StringBuilder();
        foreach (var (name, child) in root.Children)
        {
            builder.Append("declare const ").Append(PropertyName(name)).Append(": ");
            AppendNamespace(builder, child, string.Empty);
            builder.Append(";\n");
        }

        return builder.ToString();
    }

    /// <summary>Renders a JSON Schema as a TypeScript type, bounded.</summary>
    /// <param name="schemaJson">The schema.</param>
    /// <returns>The type, or <c>unknown</c> when the schema cannot be read or is too large.</returns>
    public static string RenderType(string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return "unknown";
        }

        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            foreach (var detailed in new[] { true, false })
            {
                var renderer = new SchemaRenderer(document.RootElement, detailed);
                var rendered = renderer.Render(document.RootElement, string.Empty);
                if (rendered.Length <= MaxSchemaCharacters)
                {
                    return rendered;
                }
            }
        }
        catch (JsonException)
        {
        }

        return "unknown";
    }

    private static void AppendNamespace(StringBuilder builder, NamespaceNode node, string indent, CodeModeFunction? callable = null)
    {
        var inner = indent + "  ";
        builder.Append("{\n");
        if (callable is not null)
        {
            AppendFunction(builder, callable, inner, callSignature: true);
        }

        foreach (var (name, child) in node.Children)
        {
            builder.Append(inner).Append(PropertyName(name)).Append(": ");
            AppendNamespace(builder, child, inner, node.Functions.FirstOrDefault(entry => entry.Name == name).Function);
            builder.Append(";\n");
        }

        foreach (var (name, function) in node.Functions)
        {
            // A function that also has members is written as the call signature of its namespace.
            if (node.Children.Any(child => child.Name == name))
            {
                continue;
            }

            AppendFunction(builder, function with { Path = name }, inner);
        }

        builder.Append(indent).Append('}');
    }

    private static void AppendFunction(StringBuilder builder, CodeModeFunction function, string indent, bool callSignature = false)
    {
        var name = function.Path[(function.Path.LastIndexOf('.') + 1)..];
        var description = function.Description?.Trim();
        var annotations = function.Annotations.Count == 0 ? string.Empty : " " + string.Join(' ', function.Annotations.Select(static tag => "@" + tag));
        if (!string.IsNullOrEmpty(description) || annotations.Length > 0)
        {
            var lines = (description ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries);
            if (lines.Length == 1 && (lines[0] + annotations).Length < 100)
            {
                builder.Append(indent).Append("/** ").Append((lines[0] + annotations).Trim()).Append(" */\n");
            }
            else
            {
                builder.Append(indent).Append("/**\n");
                foreach (var line in lines)
                {
                    builder.Append(indent).Append(" * ").Append(line.Replace("*/", "*\\/", StringComparison.Ordinal)).Append('\n');
                }

                if (annotations.Length > 0)
                {
                    builder.Append(indent).Append(" *").Append(annotations).Append('\n');
                }

                builder.Append(indent).Append(" */\n");
            }
        }

        var input = function.InputSchemaJson is null ? "Record<string, unknown>" : RenderType(function.InputSchemaJson);
        if (function.TextArgument is not null)
        {
            input = "string | " + input;
        }

        var optional = function.InputSchemaJson is null || !HasRequired(function.InputSchemaJson) ? "?" : string.Empty;
        var output = function.ResultType ?? (function.OutputSchemaJson is null ? "unknown" : RenderType(function.OutputSchemaJson));
        builder.Append(indent).Append(callSignature ? string.Empty : PropertyName(name)).Append("(args").Append(optional).Append(": ")
            .Append(Indent(input, indent)).Append("): Promise<").Append(Indent(output, indent)).Append(">;\n");
    }

    private static bool HasRequired(string schemaJson)
    {
        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("required", out var required) &&
                required.ValueKind == JsonValueKind.Array &&
                required.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Indent(string text, string indent)
        => indent.Length == 0 ? text : text.Replace("\n", "\n" + indent, StringComparison.Ordinal);

    /// <summary>Writes a property name as it can appear in TypeScript: as is when it is an identifier, quoted otherwise.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The name, quoted when needed.</returns>
    internal static string PropertyName(string name)
        => CodeModeNames.IsIdentifier(name) ? name : JsonSerializer.Serialize(name, CodeModeJsonContext.Default.String);

    private sealed class NamespaceNode
    {
        public List<(string Name, NamespaceNode Node)> Children { get; } = [];

        public List<(string Name, CodeModeFunction Function)> Functions { get; } = [];

        public NamespaceNode GetOrAdd(string name)
        {
            foreach (var (existing, node) in Children)
            {
                if (existing == name)
                {
                    return node;
                }
            }

            var added = new NamespaceNode();
            Children.Add((name, added));
            return added;
        }
    }

    private sealed class SchemaRenderer(JsonElement root, bool detailed)
    {
        private int _references;

        public string Render(JsonElement schema, string indent) => Render(schema, indent, []);

        private string Render(JsonElement schema, string indent, List<string> refPath)
        {
            if (schema.ValueKind == JsonValueKind.True)
            {
                return "unknown";
            }

            if (schema.ValueKind != JsonValueKind.Object)
            {
                return "unknown";
            }

            if (schema.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String)
            {
                var path = reference.GetString()!;
                var depth = refPath.Count(entry => entry == path);
                if (depth >= MaxReferenceDepth || ++_references > MaxReferences || Resolve(path) is not { } target)
                {
                    return "unknown";
                }

                refPath.Add(path);
                try
                {
                    return Render(target, indent, refPath);
                }
                finally
                {
                    refPath.RemoveAt(refPath.Count - 1);
                }
            }

            if (schema.TryGetProperty("const", out var constant))
            {
                return constant.GetRawText();
            }

            if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                return string.Join(" | ", values.EnumerateArray().Select(static value => value.GetRawText()));
            }

            foreach (var (keyword, separator) in new[] { ("anyOf", " | "), ("oneOf", " | "), ("allOf", " & ") })
            {
                if (schema.TryGetProperty(keyword, out var alternatives) && alternatives.ValueKind == JsonValueKind.Array)
                {
                    var parts = alternatives.EnumerateArray().Select(alternative => Wrap(Render(alternative, indent, refPath))).Distinct().ToArray();
                    return parts.Length == 0 ? "unknown" : string.Join(separator, parts);
                }
            }

            if (schema.TryGetProperty("type", out var type))
            {
                if (type.ValueKind == JsonValueKind.Array)
                {
                    return string.Join(" | ", type.EnumerateArray().Where(static t => t.ValueKind == JsonValueKind.String)
                        .Select(t => RenderTyped(t.GetString()!, schema, indent, refPath)).Distinct());
                }

                if (type.ValueKind == JsonValueKind.String)
                {
                    return RenderTyped(type.GetString()!, schema, indent, refPath);
                }
            }

            if (schema.TryGetProperty("properties", out _))
            {
                return RenderObject(schema, indent, refPath);
            }

            return "unknown";
        }

        private string RenderTyped(string type, JsonElement schema, string indent, List<string> refPath) => type switch
        {
            "string" => "string",
            "integer" or "number" => "number",
            "boolean" => "boolean",
            "null" => "null",
            "array" => schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object
                ? Wrap(Render(items, indent, refPath)) + "[]"
                : "unknown[]",
            "object" => RenderObject(schema, indent, refPath),
            _ => "unknown",
        };

        private string RenderObject(JsonElement schema, string indent, List<string> refPath)
        {
            var hasProperties = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;
            if (!hasProperties || !properties.EnumerateObject().Any())
            {
                return schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.Object
                    ? $"Record<string, {Render(additional, indent, refPath)}>"
                    : "Record<string, unknown>";
            }

            var required = schema.TryGetProperty("required", out var requiredNames) && requiredNames.ValueKind == JsonValueKind.Array
                ? requiredNames.EnumerateArray().Where(static name => name.ValueKind == JsonValueKind.String).Select(static name => name.GetString()!).ToHashSet(StringComparer.Ordinal)
                : [];
            var inner = indent + "  ";
            var builder = new StringBuilder("{\n");
            foreach (var property in properties.EnumerateObject())
            {
                var comment = detailed ? Comment(property.Value) : null;
                if (comment is not null && comment.Contains('\n', StringComparison.Ordinal) is false && comment.Length > 0)
                {
                    builder.Append(inner).Append("/** ").Append(comment).Append(" */\n");
                }

                builder.Append(inner).Append(PropertyName(property.Name)).Append(required.Contains(property.Name) ? ": " : "?: ")
                    .Append(Render(property.Value, inner, refPath));
                var tags = detailed ? Tags(property.Value) : null;
                builder.Append(';');
                if (!string.IsNullOrEmpty(tags))
                {
                    builder.Append(" // ").Append(tags);
                }

                builder.Append('\n');
            }

            return builder.Append(indent).Append('}').ToString();
        }

        private static string? Comment(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = description.GetString()!.Trim().Replace("*/", "*\\/", StringComparison.Ordinal);
            var newline = text.IndexOf('\n', StringComparison.Ordinal);
            return newline < 0 ? text : text[..newline].TrimEnd();
        }

        private static string? Tags(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var tags = new List<string>();
            if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "integer")
            {
                tags.Add("@integer");
            }

            foreach (var keyword in new[] { "minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems", "pattern", "format", "default" })
            {
                if (schema.TryGetProperty(keyword, out var value))
                {
                    tags.Add($"@{keyword} {(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText())}");
                }
            }

            return tags.Count == 0 ? null : string.Join(' ', tags);
        }

        private JsonElement? Resolve(string path)
        {
            if (!path.StartsWith("#/", StringComparison.Ordinal))
            {
                return path == "#" ? root : null;
            }

            var current = root;
            foreach (var segment in path[2..].Split('/'))
            {
                var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                {
                    return null;
                }
            }

            return current;
        }

        private static string Wrap(string type)
            => type.Contains(" | ", StringComparison.Ordinal) || type.Contains(" & ", StringComparison.Ordinal) ? $"({type})" : type;
    }
}

/// <summary>Names of Code Mode: what an identifier is, and how a tool name becomes one.</summary>
public static class CodeModeNames
{
    /// <summary>Gets whether a name can be written as a JavaScript identifier.</summary>
    /// <param name="name">The name.</param>
    /// <returns>True when it is an identifier.</returns>
    public static bool IsIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name) || !(char.IsLetter(name[0]) || name[0] is '_' or '$'))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c is '_' or '$'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Turns a tool name into an identifier: every character that cannot be in one becomes <c>_</c>, and a name that
    /// starts with a digit gets <c>_</c> in front (<c>my-tool</c> → <c>my_tool</c>).
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The identifier.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public static string ToIdentifier(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var builder = new StringBuilder(name.Length + 1);
        if (char.IsDigit(name[0]))
        {
            builder.Append('_');
        }

        foreach (var c in name)
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '_' or '$' ? c : '_');
        }

        return builder.ToString();
    }

    /// <summary>Turns a kebab-case or snake_case command name into camelCase (<c>set_agent</c> → <c>setAgent</c>).</summary>
    /// <param name="name">The name.</param>
    /// <returns>The camelCase name.</returns>
    public static string ToCamelCase(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var builder = new StringBuilder(name.Length);
        var upper = false;
        foreach (var c in name)
        {
            if (c is '-' or '_')
            {
                upper = builder.Length > 0;
                continue;
            }

            builder.Append(upper ? char.ToUpper(c, CultureInfo.InvariantCulture) : c);
            upper = false;
        }

        return ToIdentifier(builder.ToString());
    }
}
