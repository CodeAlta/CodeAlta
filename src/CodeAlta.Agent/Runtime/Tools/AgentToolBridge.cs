using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.Runtime.Tools;

/// <summary>
/// Converts local agent tool definitions into provider-agnostic chat-tool declarations.
/// </summary>
public static class AgentToolBridge
{
    private static readonly FrozenSet<string> UnsupportedOpenAIStrictKeywords = new[]
    {
        "contentEncoding",
        "contentMediaType",
        "not",
        "minLength",
        "maxLength",
        "pattern",
        "format",
        "minimum",
        "maximum",
        "multipleOf",
        "patternProperties",
        "minItems",
        "maxItems",
        "unevaluatedProperties",
        "propertyNames",
        "minProperties",
        "maxProperties",
        "unevaluatedItems",
        "contains",
        "minContains",
        "maxContains",
        "uniqueItems",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Converts tool definitions into <see cref="AITool"/> declarations.
    /// </summary>
    /// <param name="tools">Tool definitions.</param>
    /// <returns>The declared tools.</returns>
    public static IReadOnlyList<AITool> CreateDeclarations(IReadOnlyList<AgentToolDefinition>? tools)
    {
        if (tools is not { Count: > 0 })
        {
            return [];
        }

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var declarations = new List<AITool>(tools.Count);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            var registeredName = GetRegisteredToolName(tool.Spec.Name, usedNames);
            declarations.Add(AIFunctionFactory.CreateDeclaration(
                registeredName,
                tool.Spec.Description,
                tool.Spec.InputSchema));
        }

        return declarations;
    }

    /// <summary>
    /// Normalizes a tool schema for OpenAI strict function calling.
    /// </summary>
    /// <param name="schema">The original tool input schema.</param>
    /// <returns>A schema compatible with OpenAI strict mode.</returns>
    internal static JsonElement CreateOpenAIStrictInputSchema(JsonElement schema)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteOpenAIStrictSchema(writer, schema, forceNullable: false);
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Creates a lookup map from registered tool name to the underlying definition.
    /// </summary>
    /// <param name="tools">Tool definitions.</param>
    /// <returns>A case-sensitive map keyed by registered tool name.</returns>
    public static IReadOnlyDictionary<string, AgentToolDefinition> CreateDefinitionMap(
        IReadOnlyList<AgentToolDefinition>? tools)
    {
        if (tools is not { Count: > 0 })
        {
            return new Dictionary<string, AgentToolDefinition>(StringComparer.Ordinal);
        }

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var map = new Dictionary<string, AgentToolDefinition>(tools.Count, StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            map.Add(GetRegisteredToolName(tool.Spec.Name, usedNames), tool);
        }

        return map;
    }

    internal static string GetRegisteredToolName(string toolName, ISet<string>? usedNames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        Span<char> buffer = stackalloc char[toolName.Length];
        var length = 0;
        var lastWasSeparator = false;

        foreach (var ch in toolName)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-')
            {
                buffer[length++] = ch;
                lastWasSeparator = false;
                continue;
            }

            if (!lastWasSeparator)
            {
                buffer[length++] = '_';
                lastWasSeparator = true;
            }
        }

        var candidate = length == 0
            ? "tool"
            : new string(buffer[..length]).Trim('_');
        if (candidate.Length == 0)
        {
            candidate = "tool";
        }

        const int maxToolNameLength = 64;
        if (candidate.Length > maxToolNameLength)
        {
            candidate = candidate[..maxToolNameLength];
        }

        if (usedNames is null)
        {
            return candidate;
        }

        if (usedNames.Add(candidate))
        {
            return candidate;
        }

        for (var suffix = 2; ; suffix++)
        {
            var suffixText = $"_{suffix}";
            var baseLength = Math.Min(candidate.Length, maxToolNameLength - suffixText.Length);
            var uniqueCandidate = string.Concat(candidate.AsSpan(0, baseLength), suffixText);
            if (usedNames.Add(uniqueCandidate))
            {
                return uniqueCandidate;
            }
        }
    }

    private static void WriteOpenAIStrictSchema(Utf8JsonWriter writer, JsonElement schema, bool forceNullable)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (forceNullable &&
            schema.ValueKind == JsonValueKind.Object &&
            !schema.TryGetProperty("type", out _) &&
            !schema.TryGetProperty("anyOf", out _))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("anyOf");
            writer.WriteStartArray();
            WriteOpenAIStrictSchema(writer, schema, forceNullable: false);
            WriteNullSchema(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            schema.WriteTo(writer);
            return;
        }

        var description = schema.TryGetProperty("description", out var descriptionProperty) && descriptionProperty.ValueKind == JsonValueKind.String
            ? descriptionProperty.GetString()
            : null;
        var extraDescriptionLines = new List<string>();
        var hasProperties = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;
        var requiredPropertyNames = hasProperties
            ? GetRequiredPropertyNames(schema)
            : null;
        var shouldWriteAdditionalPropertiesFalse = hasProperties || IsObjectType(schema);

        writer.WriteStartObject();
        foreach (var property in schema.EnumerateObject())
        {
            if (hasProperties && property.NameEquals("required"))
            {
                continue;
            }

            if (property.NameEquals("description"))
            {
                continue;
            }

            if (UnsupportedOpenAIStrictKeywords.Contains(property.Name))
            {
                extraDescriptionLines.Add($"{property.Name}: {property.Value.GetRawText()}");
                continue;
            }

            if (property.NameEquals("properties"))
            {
                writer.WritePropertyName(property.Name);
                writer.WriteStartObject();
                foreach (var childProperty in property.Value.EnumerateObject())
                {
                    writer.WritePropertyName(childProperty.Name);
                    WriteOpenAIStrictSchema(
                        writer,
                        childProperty.Value,
                        forceNullable: requiredPropertyNames is null || !requiredPropertyNames.Contains(childProperty.Name));
                }

                writer.WriteEndObject();
                continue;
            }

            if (property.NameEquals("items"))
            {
                writer.WritePropertyName(property.Name);
                WriteOpenAIStrictSchema(writer, property.Value, forceNullable: false);
                continue;
            }

            if (property.NameEquals("anyOf"))
            {
                writer.WritePropertyName(property.Name);
                WriteSchemaVariantArray(writer, property.Value, appendNullVariant: forceNullable);
                continue;
            }

            if (property.NameEquals("oneOf") || property.NameEquals("allOf"))
            {
                writer.WritePropertyName(property.Name);
                WriteSchemaVariantArray(writer, property.Value, appendNullVariant: false);
                continue;
            }

            if (property.NameEquals("additionalProperties"))
            {
                if (!shouldWriteAdditionalPropertiesFalse)
                {
                    property.WriteTo(writer);
                }

                continue;
            }

            if (property.NameEquals("type") && forceNullable)
            {
                writer.WritePropertyName(property.Name);
                WriteNullableType(writer, property.Value);
                continue;
            }

            property.WriteTo(writer);
        }

        if (!string.IsNullOrWhiteSpace(description) || extraDescriptionLines.Count > 0)
        {
            writer.WriteString("description", AppendDescription(description, extraDescriptionLines));
        }

        if (hasProperties)
        {
            writer.WritePropertyName("required");
            writer.WriteStartArray();
            foreach (var propertyName in properties.EnumerateObject().Select(static property => property.Name))
            {
                writer.WriteStringValue(propertyName);
            }

            writer.WriteEndArray();
        }

        if (shouldWriteAdditionalPropertiesFalse)
        {
            writer.WriteBoolean("additionalProperties", false);
        }

        writer.WriteEndObject();
    }

    private static HashSet<string> GetRequiredPropertyNames(JsonElement schema)
    {
        var requiredNames = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                {
                    requiredNames.Add(name);
                }
            }
        }

        return requiredNames;
    }

    private static void WriteSchemaVariantArray(Utf8JsonWriter writer, JsonElement variants, bool appendNullVariant)
    {
        writer.WriteStartArray();
        var hasNullVariant = false;
        foreach (var variant in variants.EnumerateArray())
        {
            if (IsNullSchema(variant))
            {
                hasNullVariant = true;
            }

            WriteOpenAIStrictSchema(writer, variant, forceNullable: false);
        }

        if (appendNullVariant && !hasNullVariant)
        {
            WriteNullSchema(writer);
        }

        writer.WriteEndArray();
    }

    private static void WriteNullableType(Utf8JsonWriter writer, JsonElement typeElement)
    {
        if (typeElement.ValueKind == JsonValueKind.String)
        {
            var typeName = typeElement.GetString();
            if (string.Equals(typeName, "null", StringComparison.Ordinal))
            {
                writer.WriteStringValue("null");
                return;
            }

            writer.WriteStartArray();
            writer.WriteStringValue(typeName);
            writer.WriteStringValue("null");
            writer.WriteEndArray();
            return;
        }

        if (typeElement.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            var hasNullType = false;
            foreach (var item in typeElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && string.Equals(item.GetString(), "null", StringComparison.Ordinal))
                {
                    hasNullType = true;
                }

                item.WriteTo(writer);
            }

            if (!hasNullType)
            {
                writer.WriteStringValue("null");
            }

            writer.WriteEndArray();
            return;
        }

        typeElement.WriteTo(writer);
    }

    private static bool IsNullSchema(JsonElement schema)
        => schema.ValueKind == JsonValueKind.Object &&
           schema.TryGetProperty("type", out var typeProperty) &&
           typeProperty.ValueKind == JsonValueKind.String &&
           string.Equals(typeProperty.GetString(), "null", StringComparison.Ordinal);

    private static void WriteNullSchema(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "null");
        writer.WriteEndObject();
    }

    private static bool IsObjectType(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var typeProperty))
        {
            return false;
        }

        return typeProperty.ValueKind switch
        {
            JsonValueKind.String => string.Equals(typeProperty.GetString(), "object", StringComparison.Ordinal),
            JsonValueKind.Array => typeProperty.EnumerateArray().Any(static item =>
                item.ValueKind == JsonValueKind.String &&
                string.Equals(item.GetString(), "object", StringComparison.Ordinal)),
            _ => false,
        };
    }

    private static string AppendDescription(string? description, IReadOnlyList<string> extraDescriptionLines)
    {
        if (extraDescriptionLines.Count == 0)
        {
            return description ?? string.Empty;
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(description))
        {
            builder.Append(description.Trim());
            builder.AppendLine();
        }

        foreach (var line in extraDescriptionLines)
        {
            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }
}
