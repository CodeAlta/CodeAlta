using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAlta.Agent.Runtime.Tools;

/// <summary>
/// Merges a choice at the root of a tool input schema (<c>anyOf</c>, <c>oneOf</c>, <c>allOf</c>) into one object, for a
/// provider that refuses it: the Anthropic API ("input_schema does not support oneOf, allOf, or anyOf at the top level"),
/// and GitHub Copilot for every model, whose own client removes them. The provider that needs it calls it in its request
/// building; the schema of a tool is otherwise sent as it is.
/// </summary>
/// <remarks>
/// The merged schema keeps every argument the branches declare, so that the model can still give them: the properties
/// of the root as the root writes them, then those of the branches (a name the branches define differently takes any of
/// their definitions, with an <c>anyOf</c> inside the property, which providers take). A name is required when every
/// valid value has it: required by the root, by a part of an <c>allOf</c>, or by every branch of an <c>anyOf</c> or a
/// <c>oneOf</c>. A choice of the root is said in the description of the schema, since the schema no longer says it. The
/// tool still checks its arguments against its own schema.
/// </remarks>
internal static class AgentToolRootChoices
{
    private const int MaxDepth = 8;

    private static readonly string[] ChoiceKeywords = ["anyOf", "oneOf", "allOf"];

    /// <summary>
    /// Says whether a tool input schema has a choice at its root.
    /// </summary>
    /// <param name="schema">The tool input schema.</param>
    /// <returns><see langword="true"/> when the root has <c>anyOf</c>, <c>oneOf</c> or <c>allOf</c>.</returns>
    internal static bool HasRootChoice(JsonElement schema)
        => schema.ValueKind == JsonValueKind.Object && ChoiceKeywords.Any(keyword => schema.TryGetProperty(keyword, out _));

    /// <summary>
    /// Merges the choice at the root of a tool input schema into one object.
    /// </summary>
    /// <param name="schema">The tool input schema.</param>
    /// <param name="merged">Receives the merged schema, or <paramref name="schema"/> when its root has no choice.</param>
    /// <returns><see langword="true"/> when the schema was merged.</returns>
    internal static bool TryMerge(JsonElement schema, out JsonElement merged)
    {
        merged = schema;
        if (!HasRootChoice(schema) || JsonNode.Parse(schema.GetRawText()) is not JsonObject root || !Merge(root))
        {
            return false;
        }

        using var document = JsonDocument.Parse(root.ToJsonString());
        merged = document.RootElement.Clone();
        return true;
    }

    /// <summary>
    /// Merges the choice at the root of a tool input schema into one object, in place.
    /// </summary>
    /// <param name="root">The root of the tool input schema.</param>
    /// <returns><see langword="true"/> when the schema changed.</returns>
    internal static bool Merge(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!ChoiceKeywords.Any(root.ContainsKey))
        {
            return false;
        }

        var shape = Collect(root, root, depth: 0);
        var note = DescribeChoices(root);
        foreach (var keyword in ChoiceKeywords)
        {
            root.Remove(keyword);
        }

        if (!root.ContainsKey("type"))
        {
            root["type"] = "object";
        }

        var properties = new JsonObject();
        foreach (var (name, definitions) in shape.Properties)
        {
            properties[name] = definitions.Count == 1
                ? definitions[0].DeepClone()
                : new JsonObject { ["anyOf"] = new JsonArray(definitions.Select(static definition => definition.DeepClone()).ToArray()) };
        }

        root["properties"] = properties;
        var required = shape.Required.Where(shape.Properties.ContainsKey).ToArray();
        if (required.Length > 0)
        {
            root["required"] = new JsonArray(required.Select(static name => (JsonNode?)name).ToArray());
        }
        else
        {
            root.Remove("required");
        }

        if (note is not null)
        {
            root["description"] = root["description"] is JsonValue description &&
                                  description.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                ? $"{text.TrimEnd()}\n\n{note}"
                : note;
        }

        return true;
    }

    // The properties a schema and its branches declare, each name with its distinct definitions, and the names every value
    // of the schema has.
    private static Shape Collect(JsonNode? node, JsonObject root, int depth)
    {
        var shape = new Shape();
        if (depth > MaxDepth || Resolve(node, root) is not JsonObject schema)
        {
            return shape;
        }

        var own = new HashSet<string>(StringComparer.Ordinal);
        if (schema["properties"] is JsonObject properties)
        {
            foreach (var (name, definition) in properties)
            {
                if (definition is not null)
                {
                    own.Add(name);
                    shape.Add(name, definition);
                }
            }
        }

        AddNames(shape.Required, schema["required"]);
        foreach (var keyword in ChoiceKeywords)
        {
            if (schema[keyword] is not JsonArray branches)
            {
                continue;
            }

            List<string>? common = null;
            foreach (var branch in branches)
            {
                // A branch that no value matches does not narrow what the others require.
                if (branch is JsonValue value && value.TryGetValue<bool>(out var accepts) && !accepts)
                {
                    continue;
                }

                var part = Collect(branch, root, depth + 1);
                foreach (var (name, definitions) in part.Properties)
                {
                    // The schema's own definition always applies; that of a branch only narrows it.
                    if (!own.Contains(name))
                    {
                        foreach (var definition in definitions)
                        {
                            shape.Add(name, definition);
                        }
                    }
                }

                if (keyword == "allOf")
                {
                    foreach (var name in part.Required)
                    {
                        shape.Required.Add(name);
                    }
                }
                else if (common is null)
                {
                    common = [.. part.Required];
                }
                else
                {
                    common.RemoveAll(name => !part.Required.Contains(name));
                }
            }

            // What every branch of the choice requires, in the order of the first branch.
            foreach (var name in common ?? [])
            {
                shape.Required.Add(name);
            }
        }

        return shape;
    }

    private static void AddNames(OrderedNameSet names, JsonNode? required)
    {
        if (required is not JsonArray array)
        {
            return;
        }

        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var name))
            {
                names.Add(name);
            }
        }
    }

    // A branch that is a local reference, such as {"$ref":"#/$defs/byKey"}, is the definition it names.
    private static JsonNode? Resolve(JsonNode? node, JsonObject root)
    {
        for (var hop = 0; hop < MaxDepth && node is JsonObject schema && schema["$ref"] is JsonValue reference &&
                          reference.TryGetValue<string>(out var path); hop++)
        {
            var parts = path.Split('/');
            if (parts is not ["#", "$defs" or "definitions", var name] || root[parts[1]] is not JsonObject definitions)
            {
                return node;
            }

            node = definitions[name.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)];
        }

        return node;
    }

    // The choices of the root, in a sentence for the description: each branch by the names it requires, else by those it
    // declares.
    private static string? DescribeChoices(JsonObject root)
    {
        var builder = new StringBuilder();
        foreach (var keyword in new[] { "oneOf", "anyOf" })
        {
            if (root[keyword] is not JsonArray branches)
            {
                continue;
            }

            var groups = new List<string>();
            foreach (var branch in branches)
            {
                var part = Collect(branch, root, depth: 1);
                var names = part.Required.Count > 0 ? part.Required.ToList() : [.. part.Properties.Keys];
                if (names.Count > 0)
                {
                    groups.Add($"({string.Join(", ", names)})");
                }
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            var quantity = keyword == "oneOf" ? "exactly one" : "at least one";
            builder.Append(groups.Count > 0
                ? $"Give the arguments of {quantity} of: {string.Join(" or ", groups)}."
                : $"Give the arguments of {quantity} of the choices of this tool.");
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }

    private sealed class Shape
    {
        public OrderedDictionary<string, List<JsonNode>> Properties { get; } = new(StringComparer.Ordinal);

        public OrderedNameSet Required { get; } = new();

        public void Add(string name, JsonNode definition)
        {
            if (!Properties.TryGetValue(name, out var definitions))
            {
                Properties[name] = [definition];
                return;
            }

            var text = definition.ToJsonString();
            if (!definitions.Any(existing => existing.ToJsonString() == text))
            {
                definitions.Add(definition);
            }
        }
    }

    // The names in the order they were first added.
    private sealed class OrderedNameSet : IEnumerable<string>
    {
        private readonly List<string> _order = [];
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);

        public int Count => _order.Count;

        public bool Contains(string name) => _names.Contains(name);

        public void Add(string name)
        {
            if (_names.Add(name))
            {
                _order.Add(name);
            }
        }

        public IEnumerator<string> GetEnumerator() => _order.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
