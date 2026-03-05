using System.Text.Json;
using System.Text.Json.Nodes;
using NJsonSchema;

namespace CodeAlta.CodexSdk.Generator;

/// <summary>
/// A resolved definition ready for code generation.
/// </summary>
public record TypeDef(
    string Name,
    string CsNamespace,
    JsonSchema Schema,
    string JsonPointer);

/// <summary>
/// Discriminator info detected from a oneOf.
/// </summary>
public record DiscriminatorInfo(
    string PropertyName,
    IReadOnlyList<DiscriminatorVariant> Variants);

public record DiscriminatorVariant(
    string TagValue,
    string Title,
    JsonSchema Schema);

/// <summary>
/// Walks the schema document and builds a flat list of <see cref="TypeDef"/> values,
/// including supplemental fragments and nested namespace conventions when present.
/// </summary>
public static class SchemaWalker
{
    /// <summary>
    /// Load the schema and return all type definitions with resolved namespaces.
    /// Pre-processes the JSON to fix boolean schemas (true/false -> {}/{"not":{}})
    /// which NJsonSchema doesn't handle.
    /// </summary>
    public static async Task<List<TypeDef>> LoadDefinitionsAsync(
        string schemaFilePath, string rootNamespace)
    {
        // Pre-process: merge supplemental fragments, fix boolean schemas, and hoist nested namespaces
        var jsonText = await File.ReadAllTextAsync(schemaFilePath);
        var node = JsonNode.Parse(jsonText) as JsonObject
            ?? throw new InvalidOperationException($"Schema root in '{schemaFilePath}' must be a JSON object.");
        MergeSupplementalSchemas(node, schemaFilePath);
        ReplaceBooleanSchemas(node);

        // Detect and hoist nested namespace definitions
        // e.g. definitions.v2.Foo -> definitions.v2__Foo
        // and rewrite all $ref pointers accordingly
        var namespacePrefixes = new Dictionary<string, string>(); // "v2" -> "V2"
        var defsObj = node["definitions"]?.AsObject();
        if (defsObj != null)
        {
            var namespacesToHoist = new List<(string nsName, JsonObject nsObj)>();
            foreach (var (key, value) in defsObj)
            {
                if (value is JsonObject nsObj && IsJsonNamespaceObject(nsObj))
                {
                    namespacesToHoist.Add((key, nsObj));
                    var csNs = char.ToUpperInvariant(key[0]) + key[1..];
                    namespacePrefixes[key] = csNs;
                }
            }

            foreach (var (nsName, nsObj) in namespacesToHoist)
            {
                // Move each child to top-level definitions with prefix
                var children = nsObj.ToList();
                foreach (var (childName, childValue) in children)
                {
                    nsObj.Remove(childName);
                    defsObj[$"{nsName}__{childName}"] = childValue;
                }
                // Remove the namespace container
                defsObj.Remove(nsName);
            }

            // Rewrite all $ref pointers: #/definitions/v2/Foo -> #/definitions/v2__Foo
            foreach (var (nsName, _) in namespacesToHoist)
            {
                RewriteRefs(node, $"#/definitions/{nsName}/", $"#/definitions/{nsName}__");
            }

            // Some Codex schema bundles reference root definitions that only exist under a nested namespace
            // (e.g. ThreadId is only defined under definitions/v2 but referenced as #/definitions/ThreadId).
            // Add alias definitions to keep $ref targets resolvable.
            FixupMissingRootDefinitionAliases(node, defsObj, namespacesToHoist.Select(static x => x.nsName).ToArray());
        }

        var processedJson = node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false
        });

        var schema = await JsonSchema.FromJsonAsync(processedJson, schemaFilePath);
        var defs = new List<TypeDef>();

        foreach (var (name, defSchema) in schema.Definitions)
        {
            // Check if this was a hoisted nested namespace type
            var foundNs = false;
            foreach (var (nsPrefix, csNsSuffix) in namespacePrefixes)
            {
                var prefix = nsPrefix + "__";
                if (name.StartsWith(prefix))
                {
                    var originalName = name[prefix.Length..];
                    var csNs = rootNamespace + "." + csNsSuffix;
                    defs.Add(new TypeDef(
                        originalName, csNs, defSchema,
                        $"#/definitions/{name}"));
                    foundNs = true;
                    break;
                }
            }

            if (!foundNs)
            {
                defs.Add(new TypeDef(
                    name, rootNamespace, defSchema,
                    $"#/definitions/{name}"));
            }
        }
        return defs;
    }

    private static void MergeSupplementalSchemas(JsonObject schemaRoot, string schemaFilePath)
    {
        ArgumentNullException.ThrowIfNull(schemaRoot);
        ArgumentNullException.ThrowIfNull(schemaFilePath);

        var schemaDir = Path.GetDirectoryName(schemaFilePath);
        if (string.IsNullOrEmpty(schemaDir))
        {
            return;
        }

        var definitions = EnsureDefinitionsObject(schemaRoot);
        foreach (var fragment in EnumerateSupplementalFragments(schemaDir))
        {
            MergeSupplementalSchema(
                definitions,
                fragment.Path,
                trimLegacyServerRequests: fragment.TrimLegacyServerRequests);
        }
    }

    private static JsonObject EnsureDefinitionsObject(JsonObject schemaRoot)
    {
        if (schemaRoot["definitions"] is JsonObject definitions)
        {
            return definitions;
        }

        definitions = [];
        schemaRoot["definitions"] = definitions;
        return definitions;
    }

    private static IEnumerable<(string Path, bool TrimLegacyServerRequests)> EnumerateSupplementalFragments(string schemaDir)
    {
        yield return (Path.Combine(schemaDir, "v1", "InitializeResponse.json"), false);
        yield return (Path.Combine(schemaDir, "ServerRequest.json"), true);
        yield return (Path.Combine(schemaDir, "CommandExecutionRequestApprovalResponse.json"), false);
        yield return (Path.Combine(schemaDir, "FileChangeRequestApprovalResponse.json"), false);
        yield return (Path.Combine(schemaDir, "ToolRequestUserInputResponse.json"), false);
        yield return (Path.Combine(schemaDir, "DynamicToolCallResponse.json"), false);
        yield return (Path.Combine(schemaDir, "ChatgptAuthTokensRefreshResponse.json"), false);
        yield return (Path.Combine(schemaDir, "McpServerElicitationRequestResponse.json"), false);
    }

    private static void MergeSupplementalSchema(
        JsonObject targetDefinitions,
        string fragmentPath,
        bool trimLegacyServerRequests)
    {
        ArgumentNullException.ThrowIfNull(targetDefinitions);
        ArgumentNullException.ThrowIfNull(fragmentPath);

        if (!File.Exists(fragmentPath))
        {
            return;
        }

        var fragmentRoot = JsonNode.Parse(File.ReadAllText(fragmentPath)) as JsonObject
            ?? throw new InvalidOperationException($"Supplemental schema '{fragmentPath}' must be a JSON object.");

        if (trimLegacyServerRequests)
        {
            TrimLegacyServerRequestVariants(fragmentRoot);
        }

        var title = fragmentRoot["title"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException($"Supplemental schema '{fragmentPath}' is missing a root title.");
        }

        if (targetDefinitions.ContainsKey(title))
        {
            return;
        }

        var rootDefinition = CreateRootDefinition(fragmentRoot);
        targetDefinitions[title] = rootDefinition;

        if (fragmentRoot["definitions"] is not JsonObject fragmentDefinitions || fragmentDefinitions.Count == 0)
        {
            return;
        }

        var reachableDefinitions = CollectReachableDefinitions(rootDefinition, fragmentDefinitions);
        foreach (var definitionName in reachableDefinitions)
        {
            if (targetDefinitions.ContainsKey(definitionName))
            {
                continue;
            }

            if (fragmentDefinitions[definitionName] is JsonNode definitionNode)
            {
                targetDefinitions[definitionName] = definitionNode.DeepClone();
            }
        }
    }

    private static JsonObject CreateRootDefinition(JsonObject fragmentRoot)
    {
        var rootDefinition = (JsonObject)fragmentRoot.DeepClone();
        rootDefinition.Remove("$schema");
        rootDefinition.Remove("definitions");
        return rootDefinition;
    }

    private static HashSet<string> CollectReachableDefinitions(
        JsonObject rootDefinition,
        JsonObject fragmentDefinitions)
    {
        var reachableDefinitions = new HashSet<string>(StringComparer.Ordinal);
        var pendingNodes = new Queue<JsonNode?>();
        pendingNodes.Enqueue(rootDefinition);

        while (pendingNodes.Count > 0)
        {
            var currentNode = pendingNodes.Dequeue();
            var referencedDefinitions = new HashSet<string>(StringComparer.Ordinal);
            CollectRootDefinitionRefs(currentNode, referencedDefinitions);

            foreach (var referencedDefinition in referencedDefinitions)
            {
                if (!reachableDefinitions.Add(referencedDefinition))
                {
                    continue;
                }

                if (fragmentDefinitions[referencedDefinition] is JsonNode definitionNode)
                {
                    pendingNodes.Enqueue(definitionNode);
                }
            }
        }

        return reachableDefinitions;
    }

    private static void TrimLegacyServerRequestVariants(JsonObject serverRequestRoot)
    {
        if (serverRequestRoot["oneOf"] is not JsonArray variants)
        {
            return;
        }

        for (var index = variants.Count - 1; index >= 0; index--)
        {
            if (variants[index] is not JsonObject variant)
            {
                continue;
            }

            var method = TryGetSingleMethodValue(variant);
            if (method is "applyPatchApproval" or "execCommandApproval")
            {
                variants.RemoveAt(index);
            }
        }
    }

    private static string? TryGetSingleMethodValue(JsonObject requestVariant)
    {
        if (requestVariant["properties"] is not JsonObject properties ||
            properties["method"] is not JsonObject methodProperty ||
            methodProperty["enum"] is not JsonArray enumValues ||
            enumValues.Count != 1 ||
            enumValues[0] is not JsonValue enumValue ||
            enumValue.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }

        return enumValue.GetValue<string>();
    }

    /// <summary>
    /// Checks if a JsonObject looks like a namespace container (all children
    /// are objects, no JSON Schema keywords at the top level).
    /// </summary>
    private static bool IsJsonNamespaceObject(JsonObject obj)
    {
        // Quick heuristic: if none of the standard schema keywords are present
        // and it has children that are objects, it's a namespace container.
        string[] schemaKeywords = ["type", "oneOf", "anyOf", "allOf", "$ref",
            "enum", "$schema", "properties", "required", "items"];
        foreach (var kw in schemaKeywords)
        {
            if (obj.ContainsKey(kw)) return false;
        }
        // Must have at least some children that are objects (schemas)
        return obj.Count > 0 && obj.Any(kv => kv.Value is JsonObject);
    }

    /// <summary>
    /// Rewrites all $ref strings in the JSON tree.
    /// </summary>
    private static void RewriteRefs(JsonNode? node, string oldPrefix, string newPrefix)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("$ref") && obj["$ref"] is JsonValue refVal)
            {
                var refStr = refVal.GetValue<string>();
                if (refStr.StartsWith(oldPrefix))
                {
                    obj["$ref"] = newPrefix + refStr[oldPrefix.Length..];
                }
            }
            foreach (var (_, child) in obj)
                RewriteRefs(child, oldPrefix, newPrefix);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
                RewriteRefs(item, oldPrefix, newPrefix);
        }
    }

    private static void FixupMissingRootDefinitionAliases(
        JsonNode rootNode,
        JsonObject definitions,
        IReadOnlyList<string> hoistedNamespaces)
    {
        ArgumentNullException.ThrowIfNull(rootNode);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(hoistedNamespaces);

        if (hoistedNamespaces.Count == 0 || definitions.Count == 0)
        {
            return;
        }

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        CollectRootDefinitionRefs(rootNode, referenced);

        foreach (var name in referenced)
        {
            if (definitions.ContainsKey(name))
            {
                continue;
            }

            foreach (var ns in hoistedNamespaces)
            {
                var candidate = $"{ns}__{name}";
                if (definitions.ContainsKey(candidate))
                {
                    definitions[name] = new JsonObject
                    {
                        ["$ref"] = $"#/definitions/{candidate}"
                    };
                    break;
                }
            }
        }
    }

    private static void CollectRootDefinitionRefs(JsonNode? node, HashSet<string> referencedNames)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("$ref") && obj["$ref"] is JsonValue refVal && refVal.GetValueKind() == JsonValueKind.String)
            {
                var refStr = refVal.GetValue<string>();
                const string prefix = "#/definitions/";
                if (refStr.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var rest = refStr[prefix.Length..];
                    if (!rest.Contains('/', StringComparison.Ordinal))
                    {
                        referencedNames.Add(rest);
                    }
                }
            }

            foreach (var (_, child) in obj)
            {
                CollectRootDefinitionRefs(child, referencedNames);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                CollectRootDefinitionRefs(item, referencedNames);
            }
        }
    }

    /// <summary>
    /// Tries to detect a discriminator property from a oneOf schema.
    /// Returns null if this is not a tagged union.
    /// </summary>
    public static DiscriminatorInfo? DetectDiscriminator(JsonSchema schema)
    {
        if (schema.OneOf.Count < 2) return null;

        // All variants must be objects
        var variants = schema.OneOf
            .Select(v => v.HasReference ? v.Reference! : v)
            .ToList();
        if (!variants.All(v => v!.Type.HasFlag(JsonObjectType.Object)))
            return null;

        // Find required properties common to ALL variants
        var commonRequired = variants
            .Select(v => v.RequiredProperties.ToHashSet(StringComparer.Ordinal))
            .Aggregate((a, b) => { a.IntersectWith(b); return a; });

        foreach (var prop in commonRequired)
        {
            var tagValues = new List<(string tag, string title, JsonSchema variantSchema)>();
            var allSingleEnum = true;

            foreach (var variant in variants)
            {
                if (!variant.Properties.TryGetValue(prop, out var propSchema))
                {
                    allSingleEnum = false;
                    break;
                }

                var resolved = propSchema.HasReference ? propSchema.Reference! : propSchema;
                if (resolved.Type != JsonObjectType.String ||
                    resolved.Enumeration?.Count != 1)
                {
                    allSingleEnum = false;
                    break;
                }

                var tag = resolved.Enumeration!.First()!.ToString()!;
                var title = variant.Title ?? ToPascalCase(tag);
                tagValues.Add((tag, title, variant));
            }

            if (!allSingleEnum) continue;

            // Ensure all tag values are distinct
            if (tagValues.Select(t => t.tag).Distinct().Count() != tagValues.Count)
                continue;

            return new DiscriminatorInfo(prop,
                tagValues.Select(t =>
                    new DiscriminatorVariant(t.tag, t.title, t.variantSchema)).ToList());
        }

        return null;
    }

    /// <summary>
    /// Resolve through references, single-element allOf/oneOf wrappers to get the actual schema.
    /// </summary>
    public static JsonSchema Resolve(JsonSchema schema)
    {
        if (schema.HasReference)
            return Resolve(schema.Reference!);
        // allOf with single element -> unwrap
        if (schema.AllOf.Count == 1 && schema.Type == JsonObjectType.None)
            return Resolve(schema.AllOf.First());
        // oneOf with single element -> unwrap (Rust serde single-variant enums)
        if (schema.OneOf.Count == 1 && schema.Type == JsonObjectType.None)
            return Resolve(schema.OneOf.First());
        return schema;
    }

    public static string ToPascalCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var parts = s.Split('_', '-', '/', '.');
        return string.Concat(parts.Select(p =>
            p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + p[1..]));
    }

    /// <summary>
    /// Recursively replaces boolean schemas (true -> {}, false -> {"not":{}})
    /// in JSON nodes. JSON Schema draft-07 allows true/false as schemas,
    /// but NJsonSchema can't parse them.
    /// </summary>
    private static void ReplaceBooleanSchemas(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            // Properties that can contain schemas (either directly or as values in a map)
            var keysToFix = new List<string>();
            foreach (var (key, value) in obj)
            {
                if (value is JsonValue jv && jv.GetValueKind() == JsonValueKind.True)
                    keysToFix.Add(key);
                else if (value is JsonValue jv2 && jv2.GetValueKind() == JsonValueKind.False)
                    keysToFix.Add(key);
                else
                    ReplaceBooleanSchemas(value);
            }
            foreach (var key in keysToFix)
            {
                var val = obj[key]!.AsValue();
                if (val.GetValueKind() == JsonValueKind.True)
                    obj[key] = new JsonObject(); // {} accepts anything
                else
                    obj[key] = new JsonObject
                    {
                        ["not"] = new JsonObject() // {"not":{}} rejects everything
                    };
            }
        }
        else if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                var item = arr[i];
                if (item is JsonValue jv && jv.GetValueKind() == JsonValueKind.True)
                    arr[i] = new JsonObject();
                else if (item is JsonValue jv2 && jv2.GetValueKind() == JsonValueKind.False)
                    arr[i] = new JsonObject { ["not"] = new JsonObject() };
                else
                    ReplaceBooleanSchemas(item);
            }
        }
    }
}
