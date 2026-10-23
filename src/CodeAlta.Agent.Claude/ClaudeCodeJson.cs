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
            ? ReadString(value)
            : null;

    /// <summary>
    /// Reads a JSON string, with U+FFFD for each half of a surrogate pair that is alone. Claude Code before 2.1.309 could
    /// cut a long tool output through an emoji and write the first half as an escape (<c>\ud83d</c>): such a line parses,
    /// but <see cref="JsonElement.GetString"/> throws <see cref="InvalidOperationException"/> on it.
    /// </summary>
    public static string ReadString(JsonElement value)
    {
        try
        {
            return value.GetString() ?? string.Empty;
        }
        catch (InvalidOperationException) when (value.ValueKind == JsonValueKind.String)
        {
            using var document = JsonDocument.Parse(ReplaceLoneSurrogateEscapes(value.GetRawText()));
            return document.RootElement.GetString() ?? string.Empty;
        }
    }

    // The raw text of a string is valid UTF-8 (the reader checked it): only its \uXXXX escapes can be halves of a pair.
    private static string ReplaceLoneSurrogateEscapes(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        var index = 0;
        while (index < raw.Length)
        {
            if (raw[index] != '\\' || index + 1 >= raw.Length)
            {
                builder.Append(raw[index++]);
                continue;
            }

            if (raw[index + 1] != 'u' || !TryReadEscape(raw, index, out var unit))
            {
                // \" \\ \n and the others: kept as they are, with the character they escape.
                builder.Append(raw, index, 2);
                index += 2;
                continue;
            }

            if (char.IsHighSurrogate(unit) && TryReadEscape(raw, index + 6, out var next) && char.IsLowSurrogate(next))
            {
                builder.Append(raw, index, 12);
                index += 12;
            }
            else if (char.IsSurrogate(unit))
            {
                builder.Append("\\uFFFD");
                index += 6;
            }
            else
            {
                builder.Append(raw, index, 6);
                index += 6;
            }
        }

        return builder.ToString();
    }

    private static bool TryReadEscape(string raw, int index, out char unit)
    {
        unit = default;
        if (index + 6 > raw.Length || raw[index] != '\\' || raw[index + 1] != 'u' ||
            !ushort.TryParse(raw.AsSpan(index + 2, 4), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var code))
        {
            return false;
        }

        unit = (char)code;
        return true;
    }

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
                return ReadString(content);
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
                return string.Equals(ReadString(left), ReadString(right), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
            default:
                return true;
        }
    }
}
