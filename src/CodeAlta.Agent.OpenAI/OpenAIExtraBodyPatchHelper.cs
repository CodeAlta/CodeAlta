#pragma warning disable SCME0001

using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Collections;
using System.Globalization;

namespace CodeAlta.Agent.OpenAI;

internal static class OpenAIExtraBodyPatchHelper
{
    public static void Apply(
        ref JsonPatch patch,
        IReadOnlyDictionary<string, object?>? extraBody)
    {
        if (extraBody is null || extraBody.Count == 0)
        {
            return;
        }

        foreach (var entry in extraBody)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            ApplyValue(ref patch, Encoding.UTF8.GetBytes($"$.{entry.Key.Trim()}"), entry.Value);
        }
    }

    private static void ApplyValue(
        ref JsonPatch patch,
        ReadOnlySpan<byte> jsonPath,
        object? value)
    {
        switch (value)
        {
            case null:
                patch.Set(jsonPath, "null"u8);
                return;
            case bool boolean:
                patch.Set(jsonPath, boolean);
                return;
            case byte byteValue:
                patch.Set(jsonPath, byteValue);
                return;
            case sbyte signedByteValue:
                patch.Set(jsonPath, signedByteValue);
                return;
            case short shortValue:
                patch.Set(jsonPath, shortValue);
                return;
            case ushort unsignedShortValue:
                patch.Set(jsonPath, unsignedShortValue);
                return;
            case int intValue:
                patch.Set(jsonPath, intValue);
                return;
            case uint unsignedIntValue:
                patch.Set(jsonPath, unsignedIntValue);
                return;
            case long longValue:
                patch.Set(jsonPath, longValue);
                return;
            case ulong unsignedLongValue:
                patch.Set(jsonPath, unsignedLongValue);
                return;
            case float floatValue:
                patch.Set(jsonPath, floatValue);
                return;
            case double doubleValue:
                patch.Set(jsonPath, doubleValue);
                return;
            case decimal decimalValue:
                patch.Set(jsonPath, decimalValue);
                return;
            case string stringValue:
                patch.Set(jsonPath, stringValue);
                return;
            case DateTime dateTimeValue:
                patch.Set(jsonPath, dateTimeValue);
                return;
            case DateTimeOffset dateTimeOffsetValue:
                patch.Set(jsonPath, dateTimeOffsetValue);
                return;
            case TimeSpan timeSpanValue:
                patch.Set(jsonPath, timeSpanValue);
                return;
            case Guid guidValue:
                patch.Set(jsonPath, guidValue);
                return;
            case DateOnly dateOnlyValue:
                patch.Set(jsonPath, dateOnlyValue.ToString("O", CultureInfo.InvariantCulture));
                return;
            case TimeOnly timeOnlyValue:
                patch.Set(jsonPath, timeOnlyValue.ToString("O", CultureInfo.InvariantCulture));
                return;
            case JsonElement jsonElement:
                patch.Set(jsonPath, BinaryData.FromString(jsonElement.GetRawText()));
                return;
            case JsonDocument jsonDocument:
                patch.Set(jsonPath, BinaryData.FromString(jsonDocument.RootElement.GetRawText()));
                return;
            default:
                patch.Set(jsonPath, SerializeArbitraryValue(value));
                return;
        }
    }

    private static BinaryData SerializeArbitraryValue(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteJsonValue(writer, value);
        }

        return BinaryData.FromBytes(stream.ToArray());
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        ArgumentNullException.ThrowIfNull(writer);

        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonElement jsonElement:
                jsonElement.WriteTo(writer);
                return;
            case JsonDocument jsonDocument:
                jsonDocument.RootElement.WriteTo(writer);
                return;
            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                writer.WriteStartObject();
                foreach (var entry in readOnlyDictionary)
                {
                    writer.WritePropertyName(entry.Key);
                    WriteJsonValue(writer, entry.Value);
                }

                writer.WriteEndObject();
                return;
            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string propertyName)
                    {
                        throw new InvalidOperationException("extra_body dictionaries must use string keys.");
                    }

                    writer.WritePropertyName(propertyName);
                    WriteJsonValue(writer, entry.Value);
                }

                writer.WriteEndObject();
                return;
            case IEnumerable enumerable when value is not string:
                writer.WriteStartArray();
                foreach (var item in enumerable)
                {
                    WriteJsonValue(writer, item);
                }

                writer.WriteEndArray();
                return;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                return;
            case byte byteValue:
                writer.WriteNumberValue(byteValue);
                return;
            case sbyte signedByteValue:
                writer.WriteNumberValue(signedByteValue);
                return;
            case short shortValue:
                writer.WriteNumberValue(shortValue);
                return;
            case ushort unsignedShortValue:
                writer.WriteNumberValue(unsignedShortValue);
                return;
            case int intValue:
                writer.WriteNumberValue(intValue);
                return;
            case uint unsignedIntValue:
                writer.WriteNumberValue(unsignedIntValue);
                return;
            case long longValue:
                writer.WriteNumberValue(longValue);
                return;
            case ulong unsignedLongValue:
                writer.WriteNumberValue(unsignedLongValue);
                return;
            case float floatValue:
                writer.WriteNumberValue(floatValue);
                return;
            case double doubleValue:
                writer.WriteNumberValue(doubleValue);
                return;
            case decimal decimalValue:
                writer.WriteNumberValue(decimalValue);
                return;
            case string stringValue:
                writer.WriteStringValue(stringValue);
                return;
            case DateTime dateTimeValue:
                writer.WriteStringValue(dateTimeValue);
                return;
            case DateTimeOffset dateTimeOffsetValue:
                writer.WriteStringValue(dateTimeOffsetValue);
                return;
            case TimeSpan timeSpanValue:
                writer.WriteStringValue(timeSpanValue.ToString("c", CultureInfo.InvariantCulture));
                return;
            case Guid guidValue:
                writer.WriteStringValue(guidValue);
                return;
            case DateOnly dateOnlyValue:
                writer.WriteStringValue(dateOnlyValue.ToString("O", CultureInfo.InvariantCulture));
                return;
            case TimeOnly timeOnlyValue:
                writer.WriteStringValue(timeOnlyValue.ToString("O", CultureInfo.InvariantCulture));
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported extra_body value type '{value.GetType().FullName}'.");
        }
    }
}
