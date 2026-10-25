using System.Globalization;
using System.Text.Json;

namespace CodeAlta.Cli;

/// <summary>
/// The structured final answer of <c>--output-schema</c>: the prompt asks for one JSON value that matches a JSON Schema,
/// and the final answer is read back (alone, or in a fenced <c>```json</c> block), checked against the schema, and given
/// as the JSON value alone. The check covers the common subset of JSON Schema: <c>type</c> (one or a list),
/// <c>properties</c>, <c>required</c>, <c>items</c>, <c>enum</c>, <c>const</c> and <c>additionalProperties: false</c>;
/// other keywords are not checked.
/// </summary>
internal static class CliStructuredOutput
{
    private const int MaximumErrors = 8;

    /// <summary>Reads the schema of a file.</summary>
    /// <returns>The schema, or a message when the file cannot be read or is not a JSON Schema.</returns>
    public static (JsonElement Schema, string? Error) Load(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            return root.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False
                ? (root.Clone(), null)
                : (default, $"The schema '{path}' is not a JSON Schema: a schema is an object.");
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
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items) && items.ValueKind is JsonValueKind.Object or JsonValueKind.False)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                Validate(items, item, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", errors);
                index++;
            }
        }
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
