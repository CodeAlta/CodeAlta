using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeAlta.Cli;

/// <summary>
/// The structured final answer of <c>--output-schema</c>: the prompt asks for one JSON value that matches a JSON Schema,
/// and the final answer is read back (alone, or in a fenced <c>```json</c> block), checked against the schema, and given
/// as the JSON value alone. The check covers a common subset of JSON Schema (<see cref="CheckedKeywords"/>); a schema
/// that uses another keyword, other than an annotation such as <c>title</c> or <c>description</c>, is refused when it is
/// read, since an answer could pass a constraint that is not checked.
/// </summary>
internal static class CliStructuredOutput
{
    private const int MaximumErrors = 8;

    /// <summary>The keywords the check covers, in the order the help lists them.</summary>
    internal static readonly ImmutableArray<string> CheckedKeywords =
    [
        "type", "properties", "required", "additionalProperties", "items", "enum", "const", "minimum", "maximum", "exclusiveMinimum",
        "exclusiveMaximum", "minLength", "maxLength", "pattern", "minItems", "maxItems",
    ];

    private static readonly FrozenSet<string> Checked = CheckedKeywords.ToFrozenSet(StringComparer.Ordinal);

    // What only describes a value: it constrains nothing.
    private static readonly FrozenSet<string> Annotations = new[]
    {
        "$schema", "$id", "$comment", "title", "description", "default", "examples", "deprecated", "readOnly", "writeOnly",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Reads the schema of a file and checks that it uses only what the check covers.</summary>
    /// <returns>The schema, or a message when the file cannot be read, is not a JSON Schema, or uses what is not checked.</returns>
    public static (JsonElement Schema, string? Error) Load(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
                return (default, $"The schema '{path}' is not a JSON Schema: a schema is an object.");
            var problems = new List<string>();
            Inspect(root, "$", problems);
            return problems.Count == 0
                ? (root.Clone(), null)
                : (default, $"The schema '{path}' uses what --output-schema does not check: {string.Join("; ", problems.Take(MaximumErrors))}. "
                    + $"It checks {string.Join(", ", CheckedKeywords)}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (default, $"The schema '{path}' cannot be read: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return (default, $"The schema '{path}' is not JSON: {exception.Message}");
        }
    }

    // The keywords of a schema and of the schemas it holds that the check would pass over, or whose values it cannot use.
    private static void Inspect(JsonElement schema, string path, List<string> problems)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        foreach (var property in schema.EnumerateObject())
        {
            var (name, value) = (property.Name, property.Value);
            if (Annotations.Contains(name)) continue;
            if (!Checked.Contains(name))
            {
                problems.Add($"{name} (at {path})");
                continue;
            }

            switch (name)
            {
                case "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" when value.ValueKind != JsonValueKind.Number:
                    problems.Add($"{name} that is not a number (at {path})");
                    break;
                case "minLength" or "maxLength" or "minItems" or "maxItems" when !value.TryGetInt64(out var count) || count < 0:
                    problems.Add($"{name} that is not a count (at {path})");
                    break;
                case "pattern" when value.ValueKind != JsonValueKind.String || !IsPattern(value.GetString()!):
                    problems.Add($"a pattern that is not a regular expression (at {path})");
                    break;
                case "items" when value.ValueKind == JsonValueKind.Array:
                    problems.Add($"items as a list (at {path})");
                    break;
                case "items" or "additionalProperties":
                    Inspect(value, path + "." + name, problems);
                    break;
                case "properties" when value.ValueKind == JsonValueKind.Object:
                    foreach (var declared in value.EnumerateObject()) Inspect(declared.Value, path + ".properties." + declared.Name, problems);
                    break;
            }
        }
    }

    private static bool IsPattern(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, PatternTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The instruction appended to the prompt.</summary>
    public static string Instruction(JsonElement schema)
        => "\n\nYour final answer must be only one JSON value that matches the JSON Schema below: no text before or after it, no code fence.\n\n```json\n"
           + schema.GetRawText() + "\n```";

    /// <summary>Reads the JSON value of a final answer and checks it against the schema.</summary>
    /// <returns>The JSON text of the value, or the reasons it is not the value asked for.</returns>
    public static (string? Json, IReadOnlyList<string> Errors) Check(string? finalMessage, JsonElement schema)
    {
        if (string.IsNullOrWhiteSpace(finalMessage)) return (null, ["The run gave no final answer."]);
        if (!TryParse(finalMessage, out var value)) return (null, ["The final answer is not a JSON value."]);
        var errors = new List<string>();
        Validate(schema, value, "$", errors);
        return errors.Count == 0 ? (value.GetRawText(), []) : (null, errors);
    }

    // The answer alone, or the first fenced block of it.
    private static bool TryParse(string text, out JsonElement value)
    {
        var trimmed = text.Trim();
        if (TryParseJson(trimmed, out value)) return true;
        var fence = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (fence < 0) return false;
        var start = trimmed.IndexOf('\n', fence);
        var end = start < 0 ? -1 : trimmed.IndexOf("```", start, StringComparison.Ordinal);
        return end > start && TryParseJson(trimmed[(start + 1)..end].Trim(), out value);
    }

    private static bool TryParseJson(string text, out JsonElement value)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            value = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    internal static void Validate(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (errors.Count >= MaximumErrors) return;
        if (schema.ValueKind == JsonValueKind.False)
        {
            errors.Add($"{path}: no value is allowed here.");
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("type", out var type) && !MatchesType(type, value))
        {
            errors.Add($"{path}: expected {DescribeType(type)}, got {Kind(value)}.");
            return;
        }

        if (schema.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array
            && !choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(choice, value)))
        {
            errors.Add($"{path}: the value is not one of {choices.GetRawText()}.");
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value))
        {
            errors.Add($"{path}: the value must be {constant.GetRawText()}.");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.TryGetProperty("properties", out var declared) && declared.ValueKind == JsonValueKind.Object ? declared : default;
            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in required.EnumerateArray().Where(static name => name.ValueKind == JsonValueKind.String).Select(static name => name.GetString()!))
                {
                    if (!value.TryGetProperty(name, out _)) errors.Add($"{path}: the property '{name}' is missing.");
                }
            }

            var closed = schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;
            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
                {
                    Validate(propertySchema, property.Value, path + "." + property.Name, errors);
                }
                else if (closed)
                {
                    errors.Add($"{path}: the property '{property.Name}' is not allowed.");
                }
                else if (additional.ValueKind == JsonValueKind.Object)
                {
                    Validate(additional, property.Value, path + "." + property.Name, errors);
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var length = value.GetArrayLength();
            if (Count(schema, "minItems") is { } minItems && length < minItems) errors.Add($"{path}: expected at least {minItems} items, got {length}.");
            if (Count(schema, "maxItems") is { } maxItems && length > maxItems) errors.Add($"{path}: expected at most {maxItems} items, got {length}.");
            if (schema.TryGetProperty("items", out var items) && items.ValueKind is JsonValueKind.Object or JsonValueKind.False)
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    Validate(items, item, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", errors);
                    index++;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Number)
        {
            CheckBound(schema, "minimum", value, path, errors, static order => order >= 0, "at least");
            CheckBound(schema, "maximum", value, path, errors, static order => order <= 0, "at most");
            CheckBound(schema, "exclusiveMinimum", value, path, errors, static order => order > 0, "more than");
            CheckBound(schema, "exclusiveMaximum", value, path, errors, static order => order < 0, "less than");
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            // The length of a string counts its characters (code points), as JSON Schema does.
            var length = text.EnumerateRunes().Count();
            if (Count(schema, "minLength") is { } minLength && length < minLength) errors.Add($"{path}: expected at least {minLength} characters, got {length}.");
            if (Count(schema, "maxLength") is { } maxLength && length > maxLength) errors.Add($"{path}: expected at most {maxLength} characters, got {length}.");
            if (schema.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String)
            {
                try
                {
                    if (!Regex.IsMatch(text, pattern.GetString()!, RegexOptions.None, PatternTimeout)) errors.Add($"{path}: the value does not match the pattern {pattern.GetRawText()}.");
                }
                catch (RegexMatchTimeoutException)
                {
                    errors.Add($"{path}: the pattern {pattern.GetRawText()} took too long to match.");
                }
            }
        }
    }

    private static long? Count(JsonElement schema, string keyword)
        => schema.TryGetProperty(keyword, out var count) && count.TryGetInt64(out var value) ? value : null;

    // A bound of a number: the order of the value against the bound must satisfy the keyword.
    private static void CheckBound(JsonElement schema, string keyword, JsonElement value, string path, List<string> errors, Func<int, bool> satisfies, string expected)
    {
        if (!schema.TryGetProperty(keyword, out var bound) || bound.ValueKind != JsonValueKind.Number) return;
        var order = value.TryGetDecimal(out var number) && bound.TryGetDecimal(out var limit) ? number.CompareTo(limit) : value.GetDouble().CompareTo(bound.GetDouble());
        if (!satisfies(order)) errors.Add($"{path}: expected a number {expected} {bound.GetRawText()}, got {value.GetRawText()}.");
    }

    private static bool MatchesType(JsonElement type, JsonElement value)
        => type.ValueKind switch
        {
            JsonValueKind.String => MatchesType(type.GetString(), value),
            JsonValueKind.Array => type.EnumerateArray().Any(candidate => candidate.ValueKind == JsonValueKind.String && MatchesType(candidate.GetString(), value)),
            _ => true,
        };

    private static bool MatchesType(string? type, JsonElement value)
        => type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number == decimal.Truncate(number),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true,
        };

    private static string DescribeType(JsonElement type)
        => type.ValueKind == JsonValueKind.Array ? string.Join(" or ", type.EnumerateArray().Select(static item => item.ToString())) : type.ToString();

    private static string Kind(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number => "number",
            _ => value.ValueKind.ToString().ToLowerInvariant(),
        };
}
