using System.Buffers;
using System.Text.Json;

namespace CodeAlta.Acp;

/// <summary>
/// Reads and writes the parts of ACP messages that the generated types leave raw: the session updates, the content
/// blocks and the configuration options are read as JSON, so that a field a newer peer adds is not an error.
/// </summary>
public static class AcpJson
{
    /// <summary>Gets an empty JSON object.</summary>
    public static JsonElement EmptyObject { get; } = Build(static writer =>
    {
        writer.WriteStartObject();
        writer.WriteEndObject();
    });

    /// <summary>Returns the string property of an object, or <see langword="null"/> when it is missing or not a string.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The name of the property.</param>
    /// <returns>The string, or <see langword="null"/>.</returns>
    public static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Returns the integer property of an object, or <see langword="null"/> when it is missing or not an integer.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The name of the property.</param>
    /// <returns>The integer, or <see langword="null"/>.</returns>
    public static long? GetInt64(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    /// <summary>Returns the number property of an object, or <see langword="null"/> when it is missing or not a number.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The name of the property.</param>
    /// <returns>The number, or <see langword="null"/>.</returns>
    public static double? GetDouble(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    /// <summary>Returns whether the property of an object is <see langword="true"/>.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The name of the property.</param>
    /// <returns><see langword="true"/> when the property is the JSON value <c>true</c>.</returns>
    public static bool GetBoolean(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Gets the object property of an object.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The name of the property.</param>
    /// <param name="value">The object, or <see langword="default"/>.</param>
    /// <returns><see langword="true"/> when the property is an object.</returns>
    public static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Gets the array property of an object.</summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The name of the property.</param>
    /// <param name="value">The array, or <see langword="default"/>.</param>
    /// <returns><see langword="true"/> when the property is an array.</returns>
    public static bool TryGetArray(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Returns whether a capability is advertised: present and not <c>false</c> or <c>null</c> (<c>{}</c> advertises it).</summary>
    /// <param name="element">The object of the capabilities.</param>
    /// <param name="name">The name of the capability.</param>
    /// <returns><see langword="true"/> when the capability is advertised.</returns>
    public static bool HasCapability(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
           value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>Builds a JSON value with a writer.</summary>
    /// <param name="write">Writes the value.</param>
    /// <returns>The value, which owns its memory.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="write"/> is <see langword="null"/>.</exception>
    public static JsonElement Build(Action<Utf8JsonWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>Parses a JSON text.</summary>
    /// <param name="json">The text.</param>
    /// <returns>The value, which owns its memory.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">Thrown when <paramref name="json"/> is not valid JSON.</exception>
    public static JsonElement Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Creates a JSON string value.</summary>
    /// <param name="value">The string.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is <see langword="null"/>.</exception>
    public static JsonElement StringValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Build(writer => writer.WriteStringValue(value));
    }
}
