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
/// The merged schema takes every value the original takes, and keeps every argument the branches declare, so that the
/// model can still give them: the properties of the root as the root writes them, then those of the branches. A name
/// that a branch of an <c>anyOf</c> or a <c>oneOf</c> does not declare takes there what that branch takes for its other
/// keys (<c>additionalProperties</c>, <c>patternProperties</c>; anything when it leaves them open, a closed branch
/// nothing), so that a name takes any of its definitions, with an <c>anyOf</c> inside the property, which providers take
/// (<c>{}</c> among them for a branch that takes anything, the definitions staying as a hint). The keys the root does
/// not declare take what any branch takes for them. A name is required when every valid value has it: required by the
/// root, by a part of an <c>allOf</c>, or by every branch of an <c>anyOf</c> or a <c>oneOf</c>. A choice of the root is
/// said in the description of the schema, since the schema no longer says it. The tool still checks its arguments
/// against its own schema.
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
        foreach (var (name, property) in shape.Properties)
        {
            properties[name] = property.ToSchema();
        }

        root["properties"] = properties;
        // A root that said nothing of its other keys takes those its branches take.
        if (!root.ContainsKey("additionalProperties") && shape.Extra.ToSchema() is { } extra)
        {
            root["additionalProperties"] = extra;
        }

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

    // The properties a schema and its branches declare, each name with its distinct definitions, the names every value of
    // the schema has, and what it takes for the keys it does not declare.
    private static Shape Collect(JsonNode? node, JsonObject root, int depth)
    {
        var shape = new Shape();
        if (depth > MaxDepth || Resolve(node, root) is not JsonObject schema)
        {
            return shape;
        }

        shape.Extra = Extra.Of(schema);
        var own = new HashSet<string>(StringComparer.Ordinal);
        if (schema["properties"] is JsonObject properties)
        {
            foreach (var (name, definition) in properties)
            {
                if (definition is not null)
                {
                    own.Add(name);
                    shape.Property(name).Declare(definition);
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

            // A branch that no value matches takes nothing and narrows nothing.
            var parts = branches
                .Where(static branch => !(branch is JsonValue value && value.TryGetValue<bool>(out var accepts) && !accepts))
                .Select(branch => Collect(branch, root, depth + 1))
                .ToList();
            var names = parts.SelectMany(static part => part.Properties.Keys).Where(name => !own.Contains(name)).Distinct(StringComparer.Ordinal).ToList();
            if (keyword == "allOf")
            {
                // Every part applies: any one of its definitions takes at least what all of them take together.
                foreach (var name in names)
                {
                    foreach (var part in parts)
                    {
                        if (part.Properties.TryGetValue(name, out var property))
                        {
                            shape.Property(name).Include(property);
                        }
                    }
                }

                foreach (var part in parts)
                {
                    foreach (var name in part.Required)
                    {
                        shape.Required.Add(name);
                    }
                }

                continue;
            }

            // A value matches one branch at least: a name takes what each branch takes for it.
            foreach (var name in names)
            {
                var merged = shape.Property(name);
                foreach (var part in parts)
                {
                    if (part.Properties.TryGetValue(name, out var property))
                    {
                        merged.Include(property);
                    }
                    else
                    {
                        merged.Allow(part.Extra);
                    }
                }
            }

            if (shape.Extra.IsAny && parts.Count > 0)
            {
                shape.Extra = Extra.Union(parts.Select(static part => part.Extra));
            }

            // What every branch of the choice requires, in the order of the first branch.
            List<string>? common = null;
            foreach (var part in parts)
            {
                if (common is null)
                {
                    common = [.. part.Required];
                }
                else
                {
                    common.RemoveAll(name => !part.Required.Contains(name));
                }
            }

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
        public OrderedDictionary<string, MergedProperty> Properties { get; } = new(StringComparer.Ordinal);

        public OrderedNameSet Required { get; } = new();

        // What the schema takes for the keys it does not declare: anything until it says otherwise.
        public Extra Extra { get; set; } = Extra.Any;

        public MergedProperty Property(string name)
        {
            if (!Properties.TryGetValue(name, out var property))
            {
                property = new MergedProperty();
                Properties[name] = property;
            }

            return property;
        }
    }

    // The definitions of a merged property: those the branches declare, then what branches that take other keys take
    // for it, and whether one of them takes anything.
    private sealed class MergedProperty
    {
        private readonly List<JsonNode> _declared = [];
        private readonly List<JsonNode> _taken = [];

        public bool Unconstrained { get; private set; }

        public void Declare(JsonNode definition) => AddDistinct(_declared, definition);

        public void Include(MergedProperty other)
        {
            other._declared.ForEach(Declare);
            other._taken.ForEach(definition => AddDistinct(_taken, definition));
            Unconstrained |= other.Unconstrained;
        }

        public void Allow(Extra extra)
        {
            if (extra.IsAny)
            {
                Unconstrained = true;
            }
            else
            {
                extra.Schemas.ForEach(definition => AddDistinct(_taken, definition));
            }
        }

        public JsonNode ToSchema()
        {
            var definitions = _declared.Concat(_taken.Where(taken => !_declared.Any(declared => Same(declared, taken))))
                .Select(static definition => definition.DeepClone()).ToList();
            if (Unconstrained)
            {
                definitions.Add(new JsonObject());
            }

            return definitions.Count == 1 ? definitions[0] : new JsonObject { ["anyOf"] = new JsonArray(definitions.ToArray()) };
        }
    }

    // What a schema takes for the keys it does not declare: anything, nothing (closed), or a value of one of some schemas.
    private sealed class Extra
    {
        public static Extra Any { get; } = new(open: true, []);

        private static readonly Extra Closed = new(open: false, []);

        private Extra(bool open, List<JsonNode> schemas)
        {
            Open = open;
            Schemas = schemas;
        }

        public bool Open { get; }

        public List<JsonNode> Schemas { get; }

        public bool IsAny => Open && Schemas.Count == 0;

        // additionalProperties left out, true or {} takes anything; false with no patternProperties takes nothing.
        public static Extra Of(JsonObject schema)
        {
            var schemas = new List<JsonNode>();
            switch (schema["additionalProperties"])
            {
                case null:
                case JsonValue value when value.TryGetValue<bool>(out var accepts) && accepts:
                case JsonObject { Count: 0 }:
                    return Any;
                case JsonObject additional:
                    schemas.Add(additional);
                    break;
            }

            if (schema["patternProperties"] is JsonObject patterns)
            {
                foreach (var (_, pattern) in patterns)
                {
                    switch (pattern)
                    {
                        case JsonValue value when value.TryGetValue<bool>(out var accepts) && accepts:
                        case JsonObject { Count: 0 }:
                            return Any;
                        case JsonObject definition:
                            AddDistinct(schemas, definition);
                            break;
                    }
                }
            }

            return schemas.Count > 0 ? new Extra(open: true, schemas) : Closed;
        }

        public static Extra Union(IEnumerable<Extra> extras)
        {
            var schemas = new List<JsonNode>();
            foreach (var extra in extras)
            {
                if (extra.IsAny)
                {
                    return Any;
                }

                extra.Schemas.ForEach(schema => AddDistinct(schemas, schema));
            }

            return schemas.Count > 0 ? new Extra(open: true, schemas) : Closed;
        }

        // The additionalProperties of the merged root, or null where nothing is to be said (closed: the root keeps its own).
        public JsonNode? ToSchema()
            => IsAny ? JsonValue.Create(true)
                : !Open ? null
                : Schemas.Count == 1 ? Schemas[0].DeepClone()
                : new JsonObject { ["anyOf"] = new JsonArray(Schemas.Select(static schema => schema.DeepClone()).ToArray()) };
    }

    private static void AddDistinct(List<JsonNode> definitions, JsonNode definition)
    {
        if (!definitions.Any(existing => Same(existing, definition)))
        {
            definitions.Add(definition);
        }
    }

    private static bool Same(JsonNode left, JsonNode right) => left.ToJsonString() == right.ToJsonString();

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
