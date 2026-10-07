using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// Reads the messages of the CLI without assuming more of their shape than what is used: a property that is
/// missing or of another kind reads as absent.
/// </summary>
internal static class ClaudeCodeJson
{
    public static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool GetBoolean(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.True;

    public static long? GetInt64(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt64(out var number)
            ? number
            : null;

    public static double? GetDouble(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetDouble(out var number)
            ? number
            : null;

    public static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    public static bool TryGetArray(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out value) &&
            value.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Returns whether the property is absent or JSON <c>null</c>.</summary>
    public static bool IsNullOrMissing(JsonElement element, string name)
        => element.ValueKind != JsonValueKind.Object ||
           !element.TryGetProperty(name, out var value) ||
           value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    public static JsonElement EmptyObject { get; } = Parse("{}");

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static JsonElement Build(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Reads the content of a tool result or of a message: a string, or blocks of which the text ones are joined.
    /// </summary>
    public static string ReadText(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString() ?? string.Empty;
            case JsonValueKind.Array:
                var builder = new StringBuilder();
                foreach (var block in content.EnumerateArray())
                {
                    if (GetString(block, "text") is { Length: > 0 } text)
                    {
                        if (builder.Length > 0)
                        {
                            builder.Append('\n');
                        }

                        builder.Append(text);
                    }
                }

                return builder.ToString();
            default:
                return string.Empty;
        }
    }

    /// <summary>Compares two JSON values, whatever the order of the properties of their objects.</summary>
    public static bool DeepEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                var count = 0;
                foreach (var property in left.EnumerateObject())
                {
                    count++;
                    if (!right.TryGetProperty(property.Name, out var other) || !DeepEquals(property.Value, other))
                    {
                        return false;
                    }
                }

                var otherCount = 0;
                foreach (var _ in right.EnumerateObject())
                {
                    otherCount++;
                }

                return count == otherCount;
            case JsonValueKind.Array:
                if (left.GetArrayLength() != right.GetArrayLength())
                {
                    return false;
                }

                using (var leftItems = left.EnumerateArray())
                using (var rightItems = right.EnumerateArray())
                {
                    while (leftItems.MoveNext() && rightItems.MoveNext())
                    {
                        if (!DeepEquals(leftItems.Current, rightItems.Current))
                        {
                            return false;
                        }
                    }
                }

                return true;
            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
            default:
                return true;
        }
    }
}
