using System.Text;
using NJsonSchema;

namespace CodeNoesis.CodexSdk.Generator;

/// <summary>
/// Emits concise C# code from resolved schema definitions.
///
/// Design goals (Rust-like conciseness):
///   - Records for data objects
///   - C# enums with [JsonStringEnumMemberName] for string enums
///   - [JsonPolymorphic] + [JsonDerivedType] for tagged unions
///   - Nullable reference/value types for optional fields
///   - Dictionary&lt;string,T&gt; for additionalProperties maps
/// </summary>
public class CSharpEmitter
{
    private readonly Dictionary<string, TypeDef> _pointerToTypeDef = new();
    private readonly string _rootNamespace;
    private readonly List<string> _serializableTypes = [];
    private readonly HashSet<string> _collectionTypes = new(StringComparer.Ordinal);

    public CSharpEmitter(IReadOnlyList<TypeDef> allDefs, string rootNamespace)
    {
        _rootNamespace = rootNamespace;
        foreach (var def in allDefs)
        {
            _pointerToTypeDef[def.JsonPointer] = def;
        }
    }

    /// <summary>
    /// Generate all files grouped by namespace → (filename, content).
    /// </summary>
    public Dictionary<string, List<(string FileName, string Content)>> EmitAll(
        IReadOnlyList<TypeDef> defs)
    {
        var result = new Dictionary<string, List<(string, string)>>();
        _serializableTypes.Clear();
        _collectionTypes.Clear();

        foreach (var def in defs)
        {
            var code = EmitType(def);
            if (code == null) continue;

            if (!result.TryGetValue(def.CsNamespace, out var list))
            {
                list = new List<(string, string)>();
                result[def.CsNamespace] = list;
            }
            list.Add(($"{def.Name}.gen.cs", code));
            _serializableTypes.Add($"{def.CsNamespace}.{def.Name}");
        }
        return result;
    }

    public string? EmitType(TypeDef def)
    {
        var sb = new StringBuilder();
        var schema = SchemaWalker.Resolve(def.Schema);

        // Detect what kind of type this is
        if (IsStringEnum(schema))
            return EmitStringEnum(def, schema);

        if (schema.OneOf.Count >= 2)
        {
            var disc = SchemaWalker.DetectDiscriminator(schema);
            if (disc != null)
                return EmitTaggedUnion(def, schema, disc);
            return EmitUntypedOneOf(def, schema);
        }

        if (schema.AnyOf.Count >= 2 && !IsNullableAnyOf(schema))
            return EmitAnyOfUnion(def, schema);

        if (schema.Type.HasFlag(JsonObjectType.Object) || HasProperties(schema))
            return EmitRecord(def, schema);

        if (schema.Type.HasFlag(JsonObjectType.String) && schema.Enumeration?.Count == 0)
            return EmitTypeAlias(def, "string");

        // Primitive alias
        var csType = MapType(schema, def.CsNamespace);
        if (csType != "JsonElement" && csType != def.Name)
            return EmitTypeAlias(def, csType);

        // Fallback: JsonElement wrapper (or self-referencing alias)
        return EmitJsonElementWrapper(def, schema);
    }

    #region String Enum

    private bool IsStringEnum(JsonSchema schema)
    {
        if (schema.Type == JsonObjectType.String && schema.Enumeration?.Count > 0)
            return true;
        // oneOf where every variant is a string with single enum (schemars split enums)
        if (schema.OneOf.Count >= 2 && schema.OneOf.All(v =>
            {
                var r = SchemaWalker.Resolve(v);
                return r.Type == JsonObjectType.String && r.Enumeration?.Count == 1;
            }))
            return true;
        return false;
    }

    private string EmitStringEnum(TypeDef def, JsonSchema schema)
    {
        var sb = new StringBuilder();
        WriteFileHeader(sb, def.CsNamespace);
        WriteDescription(sb, schema.Description);

        sb.AppendLine($"[JsonConverter(typeof(JsonStringEnumConverter<{def.Name}>))]");
        sb.AppendLine($"public enum {def.Name}");
        sb.AppendLine("{");

        var values = GetEnumValues(schema);
        foreach (var (jsonVal, idx) in values.Select((v, i) => (v, i)))
        {
            var csName = SchemaWalker.ToPascalCase(jsonVal);
            // Avoid name clashing with the enum type itself
            if (csName == def.Name) csName += "Value";

            var comma = idx < values.Count - 1 ? "," : "";
            if (csName != jsonVal)
                sb.AppendLine($"    [JsonStringEnumMemberName(\"{jsonVal}\")]");
            sb.AppendLine($"    {csName}{comma}");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private List<string> GetEnumValues(JsonSchema schema)
    {
        if (schema.Enumeration?.Count > 0)
            return schema.Enumeration.Select(e => e?.ToString() ?? "").ToList();
        // Split-enum pattern in oneOf
        return schema.OneOf
            .Select(v => SchemaWalker.Resolve(v))
            .SelectMany(v => v.Enumeration ?? Enumerable.Empty<object?>())
            .Select(e => e?.ToString() ?? "")
            .ToList();
    }

    #endregion

    #region Tagged Union (discriminated oneOf)

    private string EmitTaggedUnion(TypeDef def, JsonSchema schema,
        DiscriminatorInfo disc)
    {
        var sb = new StringBuilder();
        WriteFileHeader(sb, def.CsNamespace);
        WriteDescription(sb, schema.Description);

        // [JsonPolymorphic] with discriminator
        sb.AppendLine($"[JsonPolymorphic(TypeDiscriminatorPropertyName = \"{disc.PropertyName}\")]");
        foreach (var v in disc.Variants)
        {
            var variantTypeName = SanitizeVariantTitle(v.Title, def.Name);
            sb.AppendLine($"[JsonDerivedType(typeof({variantTypeName}), typeDiscriminator: \"{v.TagValue}\")]");
        }

        sb.AppendLine($"public abstract partial record {def.Name}");
        sb.AppendLine("{");

        // Emit each variant as a nested record
        foreach (var v in disc.Variants)
        {
            var variantTypeName = SanitizeVariantTitle(v.Title, def.Name);
            var props = GetVariantProperties(v.Schema, disc.PropertyName, def.CsNamespace);
            props = FixPropertyNameCollisions(props, variantTypeName);

            var desc = v.Schema.Description;
            if (!string.IsNullOrEmpty(desc))
            {
                sb.AppendLine($"    /// <summary>");
                foreach (var descLine in EscapeXml(desc).Split('\n'))
                    sb.AppendLine($"    /// {descLine.TrimEnd()}");
                sb.AppendLine($"    /// </summary>");
            }

            if (props.Count == 0)
            {
                sb.AppendLine($"    public sealed partial record {variantTypeName} : {def.Name};");
            }
            else
            {
                sb.AppendLine($"    public sealed partial record {variantTypeName} : {def.Name}");
                sb.AppendLine($"    {{");
                WriteProperties(sb, props, "        ");
                sb.AppendLine($"    }}");
            }
            _serializableTypes.Add($"{def.CsNamespace}.{def.Name}.{variantTypeName}");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private string SanitizeVariantTitle(string title, string parentName)
    {
        // Some titles like "ApiKeyv2::LoginAccountResponse" need cleanup
        var result = title;
        if (result.Contains("::"))
        {
            // Use the full qualified name to avoid duplicates
            // "ApiKeyv2::LoginAccountResponse" → "ApiKeyV2"
            // "Chatgptv2::LoginAccountResponse" → "ChatgptV2"
            var parts = result.Split("::");
            var prefix = SchemaWalker.ToPascalCase(parts[0].Trim());
            var suffix = SchemaWalker.ToPascalCase(parts[^1].Trim());
            // If the suffix matches the parent, use the prefix as the variant name
            result = suffix == parentName ? prefix : suffix;
        }
        else
        {
            result = SchemaWalker.ToPascalCase(result);
        }
        // If the variant name still equals the parent name, append "Variant"
        if (result == parentName)
            result += "Variant";
        return result;
    }

    private List<PropertyInfo> GetVariantProperties(
        JsonSchema variant, string discriminatorProp, string contextNs)
    {
        var props = new List<PropertyInfo>();
        var required = variant.RequiredProperties.ToHashSet(StringComparer.Ordinal);

        foreach (var (name, propSchema) in variant.Properties)
        {
            if (name == discriminatorProp) continue; // skip the tag
            // Also skip "title" properties that are part of the variant enum tag
            if (propSchema.Title?.EndsWith("Type") == true &&
                propSchema.Type == JsonObjectType.String &&
                propSchema.Enumeration?.Count == 1)
                continue;

            var isRequired = required.Contains(name);
            var csType = MapPropertyType(propSchema, isRequired, variant, contextNs);
            var csName = ToCsPropertyName(name);
            props.Add(new PropertyInfo(name, csName, csType, isRequired,
                propSchema.Description));
        }
        return props;
    }

    #endregion

    #region Untyped oneOf (no discriminator)

    private string EmitUntypedOneOf(TypeDef def, JsonSchema schema)
    {
        // For complex untyped unions, emit a wrapper with JsonElement
        var sb = new StringBuilder();
        WriteFileHeader(sb, def.CsNamespace);
        WriteDescription(sb, schema.Description);

        // Check if it's a Rust-style key-discriminated enum (like AgentStatus, Result<T,E>)
        var keyVariants = DetectKeyDiscriminated(schema);
        if (keyVariants != null)
            return EmitKeyDiscriminatedUnion(def, schema, keyVariants);

        // Fallback: JsonElement wrapper — no custom converter, deserialize manually
        sb.AppendLine($"public partial struct {def.Name}");
        sb.AppendLine("{");
        sb.AppendLine($"    public JsonElement Value {{ get; set; }}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private List<(string key, JsonSchema valueSchema, bool isString)>?
        DetectKeyDiscriminated(JsonSchema schema)
    {
        // Pattern: each variant is either a string enum or an object with
        // exactly one property and additionalProperties: false
        var results = new List<(string, JsonSchema, bool)>();
        foreach (var variant in schema.OneOf)
        {
            var v = SchemaWalker.Resolve(variant);
            if (v.Type == JsonObjectType.String && v.Enumeration?.Count >= 1)
            {
                foreach (var e in v.Enumeration)
                    results.Add((e?.ToString() ?? "", v, true));
            }
            else if (v.Type.HasFlag(JsonObjectType.Object) &&
                     v.Properties.Count == 1 &&
                     v.AdditionalPropertiesSchema == null)
            {
                var kv = v.Properties.First();
                results.Add((kv.Key, kv.Value, false));
            }
            else
            {
                return null; // Not this pattern
            }
        }
        return results.Count > 0 ? results : null;
    }

    private string EmitKeyDiscriminatedUnion(TypeDef def, JsonSchema schema,
        List<(string key, JsonSchema valueSchema, bool isString)> variants)
    {
        var sb = new StringBuilder();
        WriteFileHeader(sb, def.CsNamespace);
        WriteDescription(sb, schema.Description);

        // Use abstract record with derived types
        // String variants become unit records, object variants get properties
        sb.AppendLine($"public abstract partial record {def.Name}");
        sb.AppendLine("{");

        foreach (var (key, valSchema, isString) in variants)
        {
            var variantName = SchemaWalker.ToPascalCase(key);
            if (variantName == def.Name) variantName += "Value";

            if (isString)
            {
                sb.AppendLine($"    public sealed partial record {variantName} : {def.Name};");
            }
            else
            {
                var resolved = SchemaWalker.Resolve(valSchema);
                var innerType = MapType(resolved, def.CsNamespace);
                var defaultVal = GetDefaultValue(innerType);
                var defaultSuffix = defaultVal != null ? $" = {defaultVal};" : "";
                sb.AppendLine($"    public sealed partial record {variantName} : {def.Name}");
                sb.AppendLine($"    {{");
                sb.AppendLine($"        public {innerType} Value {{ get; set; }}{defaultSuffix}");
                sb.AppendLine($"    }}");
            }

            _serializableTypes.Add($"{def.CsNamespace}.{def.Name}.{variantName}");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    #endregion

    #region anyOf union

    private string EmitAnyOfUnion(TypeDef def, JsonSchema schema)
    {
        var sb = new StringBuilder();
        WriteFileHeader(sb, def.CsNamespace);
        WriteDescription(sb, schema.Description);

        // For anyOf with refs (like JSONRPCMessage, ContentBlock), emit abstract record
        var hasRefs = schema.AnyOf.Any(a => a.HasReference);
        if (hasRefs)
        {
            sb.AppendLine($"public abstract partial record {def.Name};");
            return sb.ToString();
        }

        // Fallback
        sb.AppendLine($"public partial struct {def.Name}");
        sb.AppendLine("{");
        sb.AppendLine($"    public JsonElement Value {{ get; set; }}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    #endregion

    #region Record (object type)

    private string EmitRecord(TypeDef def, JsonSchema schema)
    {
        var sb = new StringBuilder();
        WriteFileHeader(sb, def.CsNamespace);
        WriteDescription(sb, schema.Description);

        var props = GetObjectProperties(schema, def.CsNamespace);
        props = FixPropertyNameCollisions(props, def.Name);
        var hasAdditionalProps = schema.AdditionalPropertiesSchema != null &&
                                  schema.AdditionalPropertiesSchema.Type != JsonObjectType.None;

        if (props.Count == 0 && !hasAdditionalProps)
        {
            sb.AppendLine($"public sealed partial record {def.Name};");
            return sb.ToString();
        }

        sb.AppendLine($"public sealed partial record {def.Name}");
        sb.AppendLine("{");

        WriteProperties(sb, props, "    ");

        if (hasAdditionalProps)
        {
            var valueType = MapType(schema.AdditionalPropertiesSchema!, def.CsNamespace);
            sb.AppendLine($"    [JsonExtensionData]");
            sb.AppendLine($"    public Dictionary<string, {valueType}>? AdditionalProperties {{ get; set; }}");
        }
        else if (schema.AllowAdditionalProperties &&
                 schema.ExtensionData?.ContainsKey("additionalProperties") == true &&
                 schema.ExtensionData["additionalProperties"] is bool b && b)
        {
            sb.AppendLine($"    [JsonExtensionData]");
            sb.AppendLine($"    public Dictionary<string, JsonElement>? ExtensionData {{ get; set; }}");
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private List<PropertyInfo> GetObjectProperties(
        JsonSchema schema, string contextNamespace)
    {
        var props = new List<PropertyInfo>();
        var required = schema.RequiredProperties.ToHashSet(StringComparer.Ordinal);

        foreach (var (name, propSchema) in schema.Properties)
        {
            var isRequired = required.Contains(name);
            var csType = MapPropertyType(propSchema, isRequired, schema, contextNamespace);
            var csName = ToCsPropertyName(name);
            props.Add(new PropertyInfo(name, csName, csType, isRequired,
                propSchema.Description));
        }

        // Sort: required first, then optional
        return props
            .OrderByDescending(p => p.IsRequired)
            .ThenBy(p => p.CsName)
            .ToList();
    }

    /// <summary>
    /// Renames any property whose C# name collides with the enclosing type name.
    /// </summary>
    private static List<PropertyInfo> FixPropertyNameCollisions(
        List<PropertyInfo> props, string typeName)
    {
        return props.Select(p =>
            p.CsName == typeName
                ? p with { CsName = p.CsName + "Value" }
                : p).ToList();
    }

    #endregion

    #region Type Alias

    private string EmitTypeAlias(TypeDef def, string csType)
    {
        var sb = new StringBuilder();
        WriteFileHeader(sb, def.CsNamespace);
        WriteDescription(sb, def.Schema.Description);
        var defaultVal = GetDefaultValue(csType);
        var defaultSuffix = defaultVal != null ? $" = {defaultVal};" : "";
        sb.AppendLine($"public partial record struct {def.Name}");
        sb.AppendLine("{");
        if (defaultVal != null)
            sb.AppendLine($"    public {def.Name}() {{ }}");
        sb.AppendLine($"    public {csType} Value {{ get; set; }}{defaultSuffix}");
        sb.AppendLine($"    public static implicit operator {def.Name}({csType} value) => new() {{ Value = value }};");
        sb.AppendLine($"    public static implicit operator {csType}({def.Name} wrapper) => wrapper.Value;");
        sb.AppendLine($"    public override string ToString() => Value?.ToString() ?? \"\";");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private string EmitJsonElementWrapper(TypeDef def, JsonSchema schema)
    {
        var sb = new StringBuilder();
        WriteFileHeader(sb, def.CsNamespace);
        WriteDescription(sb, schema.Description);
        sb.AppendLine($"public partial record struct {def.Name}");
        sb.AppendLine("{");
        sb.AppendLine($"    public JsonElement Value {{ get; set; }}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    #endregion

    #region Type Mapping

    private string MapPropertyType(JsonSchema propSchema, bool isRequired,
        JsonSchema parent, string contextNs)
    {
        // Handle nullable anyOf: [ref, null] or [type, null]
        if (IsNullableAnyOf(propSchema))
        {
            var innerSchema = propSchema.AnyOf.First(a =>
                !(a.Type == JsonObjectType.Null));
            var innerType = MapType(SchemaWalker.Resolve(innerSchema),
                contextNs);
            return innerType + "?";
        }

        // Handle nullable type array: ["string", "null"]
        if (IsNullableTypeArray(propSchema))
        {
            var nonNullType = propSchema.Type & ~JsonObjectType.Null;
            var baseType = MapJsonObjectType(nonNullType, propSchema);
            return baseType + "?";
        }

        // allOf with single ref → just the ref type
        if (propSchema.AllOf.Count == 1 && propSchema.Type == JsonObjectType.None)
        {
            var resolved = SchemaWalker.Resolve(propSchema.AllOf.First());
            return MapType(resolved, contextNs);
        }

        // Direct $ref
        if (propSchema.HasReference)
            return MapType(SchemaWalker.Resolve(propSchema), contextNs);

        var csType = MapType(propSchema, contextNs);

        // Make optional properties nullable
        if (!isRequired && !csType.EndsWith("?"))
            return csType + "?";

        return csType;
    }

    private string MapType(JsonSchema schema, string contextNs)
    {
        // Check if this is a known definition we have a name for
        var refPath = GetDefinitionPointer(schema);
        if (refPath != null && _pointerToTypeDef.TryGetValue(refPath, out var targetDef))
        {
            // Use short name if same namespace, fully qualified otherwise
            return targetDef.CsNamespace == contextNs
                ? targetDef.Name
                : $"{targetDef.CsNamespace}.{targetDef.Name}";
        }

        // Primitive types
        if (schema.Type == JsonObjectType.String) return "string";
        if (schema.Type == JsonObjectType.Boolean) return "bool";
        if (schema.Type == JsonObjectType.Integer)
            return MapIntFormat(schema.Format);
        if (schema.Type == JsonObjectType.Number)
            return MapNumberFormat(schema.Format);

        // Array
        if (schema.Type.HasFlag(JsonObjectType.Array))
        {
            if (schema.Item != null)
            {
                var itemType = MapType(SchemaWalker.Resolve(schema.Item), contextNs);
                var listType = $"List<{itemType}>";
                TrackCollectionType(listType, contextNs);
                return listType;
            }
            _collectionTypes.Add("List<System.Text.Json.JsonElement>");
            return "List<JsonElement>";
        }

        // Object with additionalProperties (map type)
        if (schema.Type.HasFlag(JsonObjectType.Object) &&
            schema.AdditionalPropertiesSchema != null &&
            schema.Properties.Count == 0)
        {
            var valType = MapType(SchemaWalker.Resolve(
                schema.AdditionalPropertiesSchema), contextNs);
            var dictType = $"Dictionary<string, {valType}>";
            TrackCollectionType(dictType, contextNs);
            return dictType;
        }

        // Object
        if (schema.Type.HasFlag(JsonObjectType.Object))
            return "JsonElement"; // anonymous inline object

        // Nullable types
        if (schema.Type.HasFlag(JsonObjectType.Null))
        {
            var nonNull = schema.Type & ~JsonObjectType.Null;
            if (nonNull != JsonObjectType.None)
                return MapJsonObjectType(nonNull, schema) + "?";
        }

        // true schema (accepts anything)
        if (schema.Type == JsonObjectType.None &&
            schema.OneOf.Count == 0 && schema.AnyOf.Count == 0 &&
            schema.AllOf.Count == 0 && !schema.HasReference)
            return "JsonElement";

        return "JsonElement";
    }

    private static string MapJsonObjectType(JsonObjectType type, JsonSchema schema)
    {
        if (type == JsonObjectType.String) return "string";
        if (type == JsonObjectType.Boolean) return "bool";
        if (type == JsonObjectType.Integer) return MapIntFormat(schema.Format);
        if (type == JsonObjectType.Number) return MapNumberFormat(schema.Format);
        if (type.HasFlag(JsonObjectType.Array))
        {
            return "List<JsonElement>";
        }
        if (type.HasFlag(JsonObjectType.Object)) return "JsonElement";
        return "JsonElement";
    }

    private static string MapIntFormat(string? format) => format switch
    {
        "int32" => "int",
        "int64" => "long",
        "uint" => "uint",
        "uint16" => "ushort",
        "uint32" => "uint",
        "uint64" => "ulong",
        _ => "long" // default for integers
    };

    private static string MapNumberFormat(string? format) => format switch
    {
        "float" => "float",
        "double" => "double",
        _ => "double"
    };

    /// <summary>
    /// Records a collection type (e.g. <c>List&lt;Foo&gt;</c> or <c>Dictionary&lt;string, Foo&gt;</c>)
    /// with fully-qualified inner type names so that it can be registered with
    /// <c>[JsonSerializable]</c> in the serializer context.
    /// </summary>
    private void TrackCollectionType(string collectionType, string contextNs)
    {
        // Expand short type names to fully-qualified for the [JsonSerializable] attribute.
        // Primitives and well-known BCL types are left as-is; custom types are resolved
        // against _serializableTypes or assumed to be in contextNs.
        _collectionTypes.Add(FullyQualifyCollectionType(collectionType, contextNs));
    }

    private string FullyQualifyCollectionType(string type, string contextNs)
    {
        // Recursively resolve inner types for generics like List<Foo> or Dictionary<string, List<Bar>>
        var openIdx = type.IndexOf('<');
        if (openIdx < 0)
            return FullyQualifyTypeName(type, contextNs);

        var outer = type[..openIdx]; // "List" or "Dictionary"
        var inner = type[(openIdx + 1)..^1]; // strip < and >

        // Split top-level generic arguments (respecting nested <> depth)
        var args = SplitGenericArgs(inner);
        var fqArgs = args.Select(a => FullyQualifyCollectionType(a.Trim(), contextNs));
        return $"{outer}<{string.Join(", ", fqArgs)}>";
    }

    private string FullyQualifyTypeName(string shortName, string contextNs)
    {
        // Already fully qualified
        if (shortName.Contains('.'))
            return shortName;

        // Primitives / well-known types
        if (shortName is "string" or "bool" or "int" or "long" or "short"
            or "uint" or "ulong" or "ushort" or "float" or "double"
            or "decimal" or "byte" or "sbyte" or "char" or "object")
            return shortName;

        if (shortName == "JsonElement")
            return "System.Text.Json.JsonElement";

        // Look up in known type defs, preferring the one in the context namespace
        // (mirrors C# short-name resolution: same-namespace type wins).
        string? fallback = null;
        foreach (var (_, def) in _pointerToTypeDef)
        {
            if (def.Name != shortName)
                continue;

            if (def.CsNamespace == contextNs)
                return $"{def.CsNamespace}.{def.Name}";

            fallback ??= $"{def.CsNamespace}.{def.Name}";
        }

        if (fallback != null)
            return fallback;

        // Fallback: assume it's in the current context namespace
        return $"{contextNs}.{shortName}";
    }

    private static List<string> SplitGenericArgs(string args)
    {
        var results = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case ',' when depth == 0:
                    results.Add(args[start..i]);
                    start = i + 1;
                    break;
            }
        }
        results.Add(args[start..]);
        return results;
    }

    private string? GetDefinitionPointer(JsonSchema schema)
    {
        // NJsonSchema tracks the document path for definitions
        // We check if this schema object is one of our known defs
        foreach (var (pointer, def) in _pointerToTypeDef)
        {
            if (ReferenceEquals(schema, def.Schema))
                return pointer;
        }
        return null;
    }

    #endregion

    #region Helpers

    private static bool IsNullableAnyOf(JsonSchema schema) =>
        schema.AnyOf.Count == 2 &&
        schema.AnyOf.Any(a => a.Type == JsonObjectType.Null) &&
        schema.AnyOf.Any(a => a.Type != JsonObjectType.Null);

    private static bool IsNullableTypeArray(JsonSchema schema) =>
        schema.Type.HasFlag(JsonObjectType.Null) &&
        (schema.Type & ~JsonObjectType.Null) != JsonObjectType.None;

    private static bool HasProperties(JsonSchema schema) =>
        schema.Properties.Count > 0;

    private static string ToCsPropertyName(string jsonName)
    {
        // Convert snake_case/camelCase to PascalCase
        var pascal = SchemaWalker.ToPascalCase(jsonName);
        // Avoid C# keywords
        return pascal switch
        {
            "Class" => "Class",
            "Event" => "Event",
            "Default" => "Default",
            "Interface" => "Interface",
            _ => pascal
        };
    }

    private static void WriteFileHeader(StringBuilder sb, string ns)
    {
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
    }

    private static void WriteDescription(StringBuilder sb, string? description)
    {
        if (string.IsNullOrEmpty(description)) return;
        sb.AppendLine("/// <summary>");
        foreach (var line in description.Split('\n'))
            sb.AppendLine($"/// {EscapeXml(line.TrimEnd())}");
        sb.AppendLine("/// </summary>");
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private void WriteProperties(StringBuilder sb, List<PropertyInfo> props, string indent)
    {
        foreach (var p in props)
        {
            if (!string.IsNullOrEmpty(p.Description))
            {
                var desc = EscapeXml(p.Description).Replace("\r", "").Replace("\n", " ").Trim();
                sb.AppendLine($"{indent}/// <summary>{desc}</summary>");
            }

            if (p.JsonName != p.CsName)
                sb.AppendLine($"{indent}[JsonPropertyName(\"{p.JsonName}\")]");

            var defaultVal = GetDefaultValue(p.CsType);
            var defaultSuffix = defaultVal != null ? $" = {defaultVal};" : "";
            sb.AppendLine($"{indent}public {p.CsType} {p.CsName} {{ get; set; }}{defaultSuffix}");
        }
    }

    private static string? GetDefaultValue(string csType)
    {
        if (csType.EndsWith("?")) return null;
        if (csType == "string") return "string.Empty";
        if (csType.StartsWith("List<")) return "[]";
        if (csType.StartsWith("Dictionary<")) return "[]";
        // Value types have implicit defaults
        if (csType is "bool" or "int" or "long" or "short" or "uint" or "ulong"
            or "ushort" or "float" or "double" or "decimal" or "byte" or "sbyte"
            or "char" or "JsonElement")
            return null;
        // Other reference types (records, etc.)
        return "default!";
    }

    /// <summary>
    /// Generates the partial serializer context file with [JsonSerializable] attributes
    /// for all emitted types. Call after <see cref="EmitAll"/>.
    /// </summary>
    public string EmitSerializerContext(string contextClassName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable SYSLIB1031 // Duplicate type name");
        sb.AppendLine();
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine();
        sb.AppendLine($"namespace {_rootNamespace};");
        sb.AppendLine();

        // Detect duplicate short names across namespaces
        var nameCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var fqn in _serializableTypes)
        {
            var shortName = fqn[(fqn.LastIndexOf('.') + 1)..];
            nameCount[shortName] = nameCount.GetValueOrDefault(shortName) + 1;
        }

        foreach (var typeFqn in _serializableTypes.Order())
        {
            var displayName = typeFqn.StartsWith(_rootNamespace + ".")
                ? typeFqn[(_rootNamespace.Length + 1)..]
                : typeFqn;

            var shortName = typeFqn[(typeFqn.LastIndexOf('.') + 1)..];
            if (nameCount.GetValueOrDefault(shortName) > 1)
            {
                // Use the full display path (dots removed) to disambiguate
                var propName = displayName.Replace(".", string.Empty);
                sb.AppendLine($"[JsonSerializable(typeof({displayName}), TypeInfoPropertyName = \"{propName}\")]");
            }
            else
            {
                sb.AppendLine($"[JsonSerializable(typeof({displayName}))]");
            }
        }

        // Register collection types (List<T>, Dictionary<K,V>) used in properties
        // so that STJ source gen can serialize them without reflection.
        // Use TypeInfoPropertyName to disambiguate collections whose element
        // types share a short name across namespaces (e.g. List<UserInput> vs
        // List<V2.UserInput>); without this, SYSLIB1031 duplicates cause the
        // source generator to silently skip the second registration.
        foreach (var collectionType in _collectionTypes.Order())
        {
            var propName = CollectionTypeToPropName(collectionType);
            sb.AppendLine($"[JsonSerializable(typeof({collectionType}), TypeInfoPropertyName = \"{propName}\")]");
        }

        sb.AppendLine($"internal partial class {contextClassName};");
        return sb.ToString();
    }

    /// <summary>
    /// Converts a fully-qualified collection type like
    /// <c>List&lt;CodeNoesis.CodexSdk.V2.UserInput&gt;</c> into a unique property name
    /// like <c>ListCodeNoesisCodexSdkV2UserInput</c> for the <c>TypeInfoPropertyName</c>.
    /// </summary>
    private static string CollectionTypeToPropName(string collectionType)
    {
        // Strip namespace dots and angle brackets to produce a valid C# identifier.
        return collectionType
            .Replace(".", string.Empty)
            .Replace("<", string.Empty)
            .Replace(">", string.Empty)
            .Replace(",", string.Empty)
            .Replace(" ", string.Empty);
    }

    #endregion
}

public record PropertyInfo(
    string JsonName,
    string CsName,
    string CsType,
    bool IsRequired,
    string? Description);
