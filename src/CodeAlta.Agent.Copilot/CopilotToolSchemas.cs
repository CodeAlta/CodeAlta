using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Agent.Copilot;

/// <summary>
/// The tool schemas of a Copilot request, rewritten for what the Copilot API refuses with an HTTP 400 without detail,
/// whatever the endpoint, as GitHub Copilot's own client rewrites them (<c>normalizeToolSchema</c> of
/// vscode-copilot-chat): a choice at the root (<c>anyOf</c>, <c>oneOf</c>, <c>allOf</c>, which the client removes and
/// CodeAlta merges into one object, see <see cref="AgentToolRootChoices"/>) and the other keywords the client removes from
/// the root (<c>not</c>, <c>if</c>, <c>then</c>, <c>else</c>), a required name that is not a declared property, and for a
/// GPT-4 model the keywords it does not take. The schema of a tool, an MCP one included, is otherwise sent as it is.
/// </summary>
internal static class CopilotToolSchemas
{
    // The keywords Copilot refuses in the tool schemas of a GPT-4 model.
    private static readonly FrozenSet<string> Gpt4UnsupportedKeywords = new[]
    {
        "minLength", "maxLength", "pattern", "default", "format", "minimum", "maximum", "multipleOf", "patternProperties",
        "unevaluatedProperties", "propertyNames", "minProperties", "maxProperties", "unevaluatedItems", "contains",
        "minContains", "maxContains", "minItems", "maxItems", "uniqueItems",
    }.ToFrozenSet(StringComparer.Ordinal);

    // The keywords Copilot's own client removes from the root of every schema, besides the choices it merges ("validated
    // this fails both for claude and 4o").
    private static readonly string[] RootUnsupportedKeywords = ["not", "if", "then", "else"];

    // The keywords whose value is a schema or an array of schemas, and those whose value maps names to schemas.
    private static readonly FrozenSet<string> SubschemaKeywords = new[]
    {
        "items", "prefixItems", "additionalProperties", "anyOf", "allOf", "oneOf", "not", "if", "then", "else", "contains",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SubschemaMapKeywords = new[]
    {
        "properties", "patternProperties", "dependencies", "$defs", "definitions",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Rewrites the schemas of the tools of a request that Copilot would refuse.
    /// </summary>
    /// <param name="tools">The tools of the request.</param>
    /// <param name="gpt4">Whether the model is of the GPT-4 family.</param>
    /// <returns>The same list when no schema changes, else a list with the rewritten tools.</returns>
    internal static IReadOnlyList<AgentToolDefinition> Normalize(IReadOnlyList<AgentToolDefinition> tools, bool gpt4)
    {
        ArgumentNullException.ThrowIfNull(tools);
        List<AgentToolDefinition>? normalized = null;
        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            if (TryNormalize(tool.Spec.InputSchema, gpt4, out var schema))
            {
                normalized ??= [.. tools.Take(index)];
                normalized.Add(tool with { Spec = tool.Spec with { InputSchema = schema } });
            }
            else
            {
                normalized?.Add(tool);
            }
        }

        return normalized ?? tools;
    }

    /// <summary>
    /// Rewrites a tool input schema that Copilot would refuse.
    /// </summary>
    /// <param name="schema">The tool input schema.</param>
    /// <param name="gpt4">Whether the model is of the GPT-4 family.</param>
    /// <param name="normalized">Receives the rewritten schema, or <paramref name="schema"/> when nothing changes.</param>
    /// <returns><see langword="true"/> when the schema was rewritten.</returns>
    internal static bool TryNormalize(JsonElement schema, bool gpt4, out JsonElement normalized)
    {
        normalized = schema;
        if (schema.ValueKind != JsonValueKind.Object || JsonNode.Parse(schema.GetRawText()) is not JsonObject root ||
            !Normalize(root, isRoot: true, gpt4))
        {
            return false;
        }

        using var document = JsonDocument.Parse(root.ToJsonString());
        normalized = document.RootElement.Clone();
        return true;
    }

    private static bool Normalize(JsonObject schema, bool isRoot, bool gpt4)
    {
        var changed = false;
        if (isRoot)
        {
            changed |= AgentToolRootChoices.Merge(schema);
            foreach (var keyword in RootUnsupportedKeywords)
            {
                changed |= schema.Remove(keyword);
            }
        }

        if (gpt4)
        {
            foreach (var keyword in Gpt4UnsupportedKeywords)
            {
                changed |= schema.Remove(keyword);
            }
        }

        changed |= RemoveUndeclaredRequiredNames(schema, isRoot);
        foreach (var (keyword, value) in schema)
        {
            if (SubschemaKeywords.Contains(keyword))
            {
                changed |= NormalizeSubschemas(value, gpt4);
            }
            else if (SubschemaMapKeywords.Contains(keyword) && value is JsonObject map)
            {
                foreach (var (_, subschema) in map)
                {
                    changed |= NormalizeSubschemas(subschema, gpt4);
                }
            }
        }

        return changed;
    }

    private static bool NormalizeSubschemas(JsonNode? value, bool gpt4)
    {
        switch (value)
        {
            case JsonObject subschema:
                return Normalize(subschema, isRoot: false, gpt4);
            case JsonArray subschemas:
                var changed = false;
                foreach (var item in subschemas)
                {
                    if (item is JsonObject subschema)
                    {
                        changed |= Normalize(subschema, isRoot: false, gpt4);
                    }
                }

                return changed;
            default:
                return false;
        }
    }

    // Copilot's own client checks the objects that declare properties; the root always has them once sent, if only empty.
    private static bool RemoveUndeclaredRequiredNames(JsonObject schema, bool isRoot)
    {
        if (schema["required"] is not JsonArray required)
        {
            return false;
        }

        var properties = schema["properties"] as JsonObject;
        if (!isRoot && (properties is null || schema["type"] is not JsonValue type || type.GetValueKind() != JsonValueKind.String ||
                        type.GetValue<string>() != "object"))
        {
            return false;
        }

        var changed = false;
        for (var index = required.Count - 1; index >= 0; index--)
        {
            if (required[index] is JsonValue name && name.GetValueKind() == JsonValueKind.String &&
                properties?.ContainsKey(name.GetValue<string>()) != true)
            {
                required.RemoveAt(index);
                changed = true;
            }
        }

        return changed;
    }
}
