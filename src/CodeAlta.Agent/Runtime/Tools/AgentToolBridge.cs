using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace CodeAlta.Agent.Runtime.Tools;

/// <summary>
/// Converts agent tool definitions into provider-agnostic chat-tool declarations.
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

    // The keywords that only narrow one value, which the strict rewrite moves to the description: the model reads them and
    // the tool checks them. The other keywords of UnsupportedOpenAIStrictKeywords (not, patternProperties, contains...)
    // change which values or keys are valid, so a schema that has them is not sent strict.
    private static readonly FrozenSet<string> OpenAIStrictDescriptionKeywords = new[]
    {
        "contentEncoding",
        "contentMediaType",
        "minLength",
        "maxLength",
        "pattern",
        "format",
        "minimum",
        "maximum",
        "multipleOf",
        "minItems",
        "maxItems",
        "minProperties",
        "maxProperties",
        "uniqueItems",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> JsonSchemaTypeNames = new[]
    {
        "string",
        "number",
        "integer",
        "boolean",
        "object",
        "array",
        "null",
    }.ToFrozenSet(StringComparer.Ordinal);

    // The first limits OpenAI documented for strict schemas, lower than the later ones, so that an endpoint that kept them
    // (an older Azure OpenAI deployment) does not refuse a schema either.
    private const int OpenAIStrictMaxNesting = 5;
    private const int OpenAIStrictMaxProperties = 100;
    private const int OpenAIStrictMaxEnumValues = 500;
    private const int OpenAIStrictMaxCharacters = 15_000;

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
    /// Creates the input schema of a tool for OpenAI function calling (Responses and Chat Completions), and says whether
    /// it is sent in strict mode.
    /// </summary>
    /// <remarks>
    /// A tool is sent with its own schema, strict mode off, as Codex sends every tool: strict mode accepts only a subset
    /// of JSON Schema (no <c>allOf</c>, <c>not</c>, <c>if</c>, open maps, limits of size and nesting) and makes every
    /// optional argument required and nullable, so it cannot take any valid MCP schema as it is. Only when the provider
    /// allows strict tools (<c>supports_strict_tools</c>) and the schema fits strict mode once rewritten
    /// (<see cref="CreateOpenAIStrictInputSchema"/>) is the tool sent strict.
    /// </remarks>
    /// <param name="schema">The tool input schema.</param>
    /// <param name="allowStrict">Whether the provider may receive tools in strict mode.</param>
    /// <param name="strict">Receives whether the schema returned is for strict mode.</param>
    /// <returns>The schema to send.</returns>
    internal static JsonElement CreateOpenAIInputSchema(JsonElement schema, bool allowStrict, out bool strict)
    {
        if (allowStrict && FitsOpenAIStrictMode(schema))
        {
            strict = true;
            return CreateOpenAIStrictInputSchema(schema);
        }

        strict = false;
        return CreateOpenAIObjectInputSchema(schema);
    }

    /// <summary>
    /// Says whether a tool schema can be sent in OpenAI strict mode once rewritten by
    /// <see cref="CreateOpenAIStrictInputSchema"/>: an object at the root, only the keywords strict mode takes or the
    /// rewrite moves to the description, closed objects, local references, and within the limits of size and nesting of
    /// strict mode. Anything else, an unknown keyword included, does not fit.
    /// </summary>
    /// <param name="schema">The tool input schema.</param>
    /// <returns><see langword="true"/> when the schema fits strict mode.</returns>
    internal static bool FitsOpenAIStrictMode(JsonElement schema)
    {
        // The root is an object, not a choice of schemas.
        if (schema.ValueKind != JsonValueKind.Object ||
            !(IsObjectType(schema) || schema.TryGetProperty("properties", out _)) ||
            schema.TryGetProperty("anyOf", out _) ||
            schema.TryGetProperty("oneOf", out _))
        {
            return false;
        }

        var budget = new OpenAIStrictBudget();
        return FitsOpenAIStrictMode(schema, nesting: 1, budget) &&
               budget.Properties <= OpenAIStrictMaxProperties &&
               budget.EnumValues <= OpenAIStrictMaxEnumValues &&
               budget.Characters <= OpenAIStrictMaxCharacters;
    }

    private static bool FitsOpenAIStrictMode(JsonElement schema, int nesting, OpenAIStrictBudget budget)
    {
        if (schema.ValueKind != JsonValueKind.Object || nesting > OpenAIStrictMaxNesting)
        {
            return false;
        }

        JsonElement? properties = null;
        JsonElement? required = null;
        var closed = false;
        foreach (var keyword in schema.EnumerateObject())
        {
            var value = keyword.Value;
            switch (keyword.Name)
            {
                case "properties":
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        return false;
                    }

                    properties = value;
                    foreach (var property in value.EnumerateObject())
                    {
                        budget.Properties++;
                        budget.Characters += property.Name.Length;
                        if (!FitsOpenAIStrictMode(property.Value, nesting + 1, budget))
                        {
                            return false;
                        }
                    }

                    break;
                case "items":
                    if (!FitsOpenAIStrictMode(value, nesting + 1, budget))
                    {
                        return false;
                    }

                    break;
                case "anyOf" or "oneOf":
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                    {
                        return false;
                    }

                    foreach (var variant in value.EnumerateArray())
                    {
                        if (!FitsOpenAIStrictMode(variant, nesting, budget))
                        {
                            return false;
                        }
                    }

                    break;
                case "$defs" or "definitions":
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        return false;
                    }

                    foreach (var definition in value.EnumerateObject())
                    {
                        budget.Characters += definition.Name.Length;
                        if (!FitsOpenAIStrictMode(definition.Value, nesting, budget))
                        {
                            return false;
                        }
                    }

                    break;
                case "$ref":
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } reference || !reference.StartsWith('#'))
                    {
                        return false;
                    }

                    break;
                case "required":
                    if (value.ValueKind != JsonValueKind.Array ||
                        value.EnumerateArray().Any(static name => name.ValueKind != JsonValueKind.String))
                    {
                        return false;
                    }

                    required = value;
                    break;
                case "additionalProperties":
                    // Strict mode closes every object: a map open to other keys cannot be sent.
                    if (value.ValueKind != JsonValueKind.False)
                    {
                        return false;
                    }

                    closed = true;
                    break;
                case "type":
                    if (!IsKnownSchemaType(value))
                    {
                        return false;
                    }

                    break;
                case "enum":
                    if (value.ValueKind != JsonValueKind.Array)
                    {
                        return false;
                    }

                    foreach (var item in value.EnumerateArray())
                    {
                        budget.EnumValues++;
                        budget.Characters += item.ValueKind == JsonValueKind.String ? item.GetString()!.Length : item.GetRawText().Length;
                    }

                    break;
                case "const":
                    budget.Characters += value.ValueKind == JsonValueKind.String ? value.GetString()!.Length : value.GetRawText().Length;
                    break;
                case "description" or "title":
                    break;
                default:
                    if (!OpenAIStrictDescriptionKeywords.Contains(keyword.Name))
                    {
                        return false;
                    }

                    break;
            }
        }

        // Strict mode takes a reference alone, without keywords beside it.
        if (schema.TryGetProperty("$ref", out _) && schema.EnumerateObject().Count() > 1)
        {
            return false;
        }

        // An object without properties is a free-form object, unless it is closed: strict mode would accept only {}.
        if (IsObjectType(schema) && properties is null && !closed)
        {
            return false;
        }

        // A required name without its property describes a key strict mode cannot send.
        return required is not { } requiredNames ||
               requiredNames.EnumerateArray().All(name => properties is { } declared && declared.TryGetProperty(name.GetString()!, out _));
    }

    private static bool IsKnownSchemaType(JsonElement type)
        => type.ValueKind switch
        {
            JsonValueKind.String => JsonSchemaTypeNames.Contains(type.GetString()!),
            JsonValueKind.Array => type.GetArrayLength() > 0 && type.EnumerateArray().All(static item =>
                item.ValueKind == JsonValueKind.String && JsonSchemaTypeNames.Contains(item.GetString()!)),
            _ => false,
        };

    // The tool's own schema, as an object with properties at its root, which the OpenAI endpoints require of any function
    // (MCP requires the type too, but some servers omit the properties of a tool without arguments, or set them to null).
    private static JsonElement CreateOpenAIObjectInputSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            using var empty = JsonDocument.Parse("""{"type":"object","properties":{}}""");
            return empty.RootElement.Clone();
        }

        var hasType = schema.TryGetProperty("type", out _);
        var hasProperties = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;
        if (hasType && hasProperties)
        {
            return schema;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (!hasType)
            {
                writer.WriteString("type", "object");
            }

            foreach (var property in schema.EnumerateObject())
            {
                if (hasProperties || !property.NameEquals("properties"))
                {
                    property.WriteTo(writer);
                }
            }

            if (!hasProperties)
            {
                writer.WriteStartObject("properties");
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private sealed class OpenAIStrictBudget
    {
        public int Properties { get; set; }

        public int EnumValues { get; set; }

        public int Characters { get; set; }
    }

    /// <summary>
    /// Normalizes a tool schema for OpenAI strict function calling: optional properties become required and nullable,
    /// objects are closed, and the keywords strict mode refuses go to the description.
    /// </summary>
    /// <param name="schema">The original tool input schema.</param>
    /// <returns>A schema compatible with OpenAI strict mode, for a schema that <see cref="FitsOpenAIStrictMode(JsonElement)"/>.</returns>
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
    /// Rewrites each value of a tool schema that has several types in one <c>type</c> array, such as
    /// <c>["string", "array", "null"]</c>, as an <c>anyOf</c> of one schema per type, for an endpoint whose schemas have
    /// a single type. The keywords of one type (<c>items</c> of an array, <c>properties</c> of an object) go to its
    /// schema; the others (<c>description</c>, <c>enum</c>) stay beside the <c>anyOf</c>. A single type that may be null
    /// stays as it is.
    /// </summary>
    /// <param name="schema">The tool input schema.</param>
    /// <returns>The same schema, without type arrays of several types.</returns>
    internal static JsonElement CreateInputSchemaWithoutTypeUnions(JsonElement schema)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteSchemaWithoutTypeUnions(writer, schema);
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteSchemaWithoutTypeUnions(Utf8JsonWriter writer, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            schema.WriteTo(writer);
            return;
        }

        if (!TryGetTypeUnion(schema, out var types))
        {
            writer.WriteStartObject();
            foreach (var property in schema.EnumerateObject())
            {
                WriteSchemaKeywordWithoutTypeUnions(writer, property);
            }

            writer.WriteEndObject();
            return;
        }

        writer.WriteStartObject();
        foreach (var property in schema.EnumerateObject())
        {
            if (!property.NameEquals("type") && TypeOfKeyword(property.Name) is null)
            {
                WriteSchemaKeywordWithoutTypeUnions(writer, property);
            }
        }

        writer.WritePropertyName("anyOf");
        writer.WriteStartArray();
        foreach (var type in types)
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            foreach (var property in schema.EnumerateObject())
            {
                if (TypeOfKeyword(property.Name) is { } keywordType &&
                    (string.Equals(keywordType, type, StringComparison.Ordinal) ||
                     keywordType == "number" && type == "integer"))
                {
                    WriteSchemaKeywordWithoutTypeUnions(writer, property);
                }
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    // A type array of more than one type besides null, in a schema that has no anyOf or oneOf to merge it with.
    private static bool TryGetTypeUnion(JsonElement schema, out List<string> types)
    {
        types = [];
        if (!schema.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.Array ||
            schema.TryGetProperty("anyOf", out _) ||
            schema.TryGetProperty("oneOf", out _))
        {
            return false;
        }

        foreach (var item in type.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name && !types.Contains(name))
            {
                types.Add(name);
            }
        }

        return types.Count(static name => name != "null") > 1;
    }

    private static void WriteSchemaKeywordWithoutTypeUnions(Utf8JsonWriter writer, JsonProperty property)
    {
        writer.WritePropertyName(property.Name);
        switch (property.Name)
        {
            case "properties" or "patternProperties" or "$defs" or "definitions" or "dependentSchemas"
                when property.Value.ValueKind == JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var child in property.Value.EnumerateObject())
                {
                    writer.WritePropertyName(child.Name);
                    WriteSchemaWithoutTypeUnions(writer, child.Value);
                }

                writer.WriteEndObject();
                break;
            case "anyOf" or "oneOf" or "allOf" or "prefixItems" or "items"
                when property.Value.ValueKind == JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in property.Value.EnumerateArray())
                {
                    WriteSchemaWithoutTypeUnions(writer, item);
                }

                writer.WriteEndArray();
                break;
            case "items" or "additionalProperties" or "additionalItems" or "unevaluatedItems" or "unevaluatedProperties" or
                "contains" or "propertyNames" or "not" or "if" or "then" or "else":
                WriteSchemaWithoutTypeUnions(writer, property.Value);
                break;
            default:
                property.Value.WriteTo(writer);
                break;
        }
    }

    // The type a keyword applies to; null for a keyword of any type, such as description or enum.
    private static string? TypeOfKeyword(string keyword)
        => keyword switch
        {
            "items" or "prefixItems" or "additionalItems" or "unevaluatedItems" or "contains" or "minContains" or
                "maxContains" or "minItems" or "maxItems" or "uniqueItems" => "array",
            "properties" or "required" or "additionalProperties" or "patternProperties" or "propertyNames" or
                "unevaluatedProperties" or "minProperties" or "maxProperties" or "dependentRequired" or
                "dependentSchemas" => "object",
            "minLength" or "maxLength" or "pattern" or "format" or "contentEncoding" or "contentMediaType" => "string",
            "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" or "multipleOf" => "number",
            _ => null,
        };

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
            !schema.TryGetProperty("anyOf", out _) &&
            !schema.TryGetProperty("oneOf", out _))
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
        var isObjectShape = hasProperties || IsObjectType(schema);
        var requiredPropertyNames = hasProperties
            ? GetRequiredPropertyNames(schema)
            : null;
        var shouldWriteAdditionalPropertiesFalse = isObjectShape;
        var hasAnyOf = schema.TryGetProperty("anyOf", out _);
        var wroteProperties = false;

        writer.WriteStartObject();
        foreach (var property in schema.EnumerateObject())
        {
            if (isObjectShape && property.NameEquals("required"))
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
                wroteProperties = true;
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

            // Strict mode refuses oneOf: an anyOf of the same schemas accepts every value the oneOf accepts, and the
            // tool still checks its arguments. Beside an anyOf, it goes to the description like the keywords refused.
            if (property.NameEquals("oneOf"))
            {
                if (hasAnyOf)
                {
                    extraDescriptionLines.Add($"{property.Name}: {property.Value.GetRawText()}");
                    continue;
                }

                writer.WritePropertyName("anyOf");
                WriteSchemaVariantArray(writer, property.Value, appendNullVariant: forceNullable);
                continue;
            }

            if (property.NameEquals("allOf"))
            {
                writer.WritePropertyName(property.Name);
                WriteSchemaVariantArray(writer, property.Value, appendNullVariant: false);
                continue;
            }

            if ((property.NameEquals("$defs") || property.NameEquals("definitions")) &&
                property.Value.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName(property.Name);
                writer.WriteStartObject();
                foreach (var definition in property.Value.EnumerateObject())
                {
                    writer.WritePropertyName(definition.Name);
                    WriteOpenAIStrictSchema(writer, definition.Value, forceNullable: false);
                }

                writer.WriteEndObject();
                continue;
            }

            if (property.NameEquals("additionalProperties"))
            {
                if (!shouldWriteAdditionalPropertiesFalse)
                {
                    writer.WritePropertyName(property.Name);
                    WriteOpenAIStrictSchema(writer, property.Value, forceNullable: false);
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

        if (isObjectShape && !wroteProperties)
        {
            writer.WriteStartObject("properties");
            writer.WriteEndObject();
        }

        if (isObjectShape)
        {
            writer.WritePropertyName("required");
            writer.WriteStartArray();
            if (hasProperties)
            {
                foreach (var propertyName in properties.EnumerateObject().Select(static property => property.Name))
                {
                    writer.WriteStringValue(propertyName);
                }
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
