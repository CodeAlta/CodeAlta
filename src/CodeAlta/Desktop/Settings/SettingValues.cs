using System.Buffers;
using System.Globalization;
using System.Text.Json;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop.Settings;

/// <summary>The values of settings as JSON: how they are made, compared and checked against the kind of their setting.</summary>
internal static class SettingValues
{
    /// <summary>The longest text a text or a path setting takes.</summary>
    internal const int MaximumTextLength = 4096;

    /// <summary>The most items a list setting takes.</summary>
    internal const int MaximumListItems = 256;

    internal static JsonElement Of(bool value) => Write(writer => writer.WriteBooleanValue(value));

    internal static JsonElement Of(long value) => Write(writer => writer.WriteNumberValue(value));

    internal static JsonElement Of(string? value) => Write(writer => { if (value is null) writer.WriteNullValue(); else writer.WriteStringValue(value); });

    internal static JsonElement Of(IEnumerable<string> values) => Write(writer =>
    {
        writer.WriteStartArray();
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    });

    /// <summary>Writes a value with a writer, and gives it as an element the caller owns.</summary>
    internal static JsonElement Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>Reads a value from its JSON text.</summary>
    /// <exception cref="JsonException">The text is not JSON.</exception>
    internal static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>The JSON text of a value; <c>null</c> for none.</summary>
    internal static string Text(JsonElement? value) => value is { } element ? element.GetRawText() : "null";

    /// <summary>Whether two values say the same.</summary>
    internal static bool Same(JsonElement? left, JsonElement? right) => JsonElement.DeepEquals(left ?? default, right ?? default)
        || left is null && right is { ValueKind: JsonValueKind.Null } || right is null && left is { ValueKind: JsonValueKind.Null };

    /// <summary>
    /// Checks a value against its setting, and gives it in the form the setting keeps: a whole number for an integer,
    /// a trimmed text, a list of texts. Null when it fits, else why it does not.
    /// </summary>
    internal static string? Check(AltaSettingDescriptor setting, JsonElement value, IReadOnlyList<AltaSettingChoice>? choices, out JsonElement normalized)
    {
        normalized = value;
        switch (setting.Kind)
        {
            case AltaSettingKinds.Boolean:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : $"{setting.Id} is on or off: true or false.";
            case AltaSettingKinds.Choice:
            {
                if (value.ValueKind != JsonValueKind.String) return $"{setting.Id} takes one of its choices, as a text.";
                var text = value.GetString()!.Trim();
                var offered = setting.Choices.Count > 0 ? setting.Choices : choices ?? [];
                if (offered.Count > 0 && offered.FirstOrDefault(choice => string.Equals(choice.Value, text, StringComparison.OrdinalIgnoreCase)) is not { } chosen)
                    return $"{setting.Id} is one of: {string.Join(", ", offered.Select(static choice => choice.Value))}.";
                normalized = Of(offered.Count > 0 ? offered.First(choice => string.Equals(choice.Value, text, StringComparison.OrdinalIgnoreCase)).Value : text);
                return text.Length is 0 or > MaximumTextLength ? $"{setting.Id} takes a choice of 1 to {MaximumTextLength} characters." : null;
            }
            case AltaSettingKinds.Integer or AltaSettingKinds.Number:
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || double.IsNaN(number) || double.IsInfinity(number))
                    return $"{setting.Id} is a number.";
                if (setting.Kind == AltaSettingKinds.Integer && (number != Math.Floor(number) || !value.TryGetInt64(out _))) return $"{setting.Id} is a whole number.";
                if (setting.Minimum is { } minimum && number < minimum || setting.Maximum is { } maximum && number > maximum)
                    return string.Create(CultureInfo.InvariantCulture, $"{setting.Id} is between {setting.Minimum} and {setting.Maximum}.");
                return null;
            }
            case AltaSettingKinds.Text or AltaSettingKinds.Path:
            {
                if (value.ValueKind != JsonValueKind.String) return $"{setting.Id} is a text.";
                var text = value.GetString()!.Trim();
                if (text.Length > MaximumTextLength || text.Any(char.IsControl)) return $"{setting.Id} takes at most {MaximumTextLength} characters, on one line.";
                normalized = Of(text);
                return null;
            }
            case AltaSettingKinds.List:
            {
                if (value.ValueKind != JsonValueKind.Array) return $"{setting.Id} is a list of texts, as a JSON array.";
                var items = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) return $"{setting.Id} is a list of texts.";
                    var text = item.GetString()!.Trim();
                    if (text.Length is 0 or > 256 || text.Any(char.IsControl)) return $"Each item of {setting.Id} has 1 to 256 characters, on one line.";
                    if (!items.Contains(text, StringComparer.Ordinal)) items.Add(text);
                }

                if (items.Count > MaximumListItems) return $"{setting.Id} has at most {MaximumListItems} items.";
                normalized = Of(items);
                return null;
            }
            default:
                return $"{setting.Id} is changed on its page.";
        }
    }
}
