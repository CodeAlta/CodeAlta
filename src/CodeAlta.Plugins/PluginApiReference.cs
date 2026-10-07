using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>One type of the plugin API, as the author of a plugin reads it.</summary>
public sealed record PluginApiType
{
    /// <summary>Gets the name of the type; a nested type is named with the type that holds it.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the namespace to import.</summary>
    public required string Namespace { get; init; }

    /// <summary>Gets what the type is: <c>class</c>, <c>record</c>, <c>struct</c>, <c>interface</c>, <c>enum</c> or <c>delegate</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the declaration of the type as C#, with its base type; for a delegate, with its parameters.</summary>
    public required string Declaration { get; init; }

    /// <summary>Gets the first sentences of the documentation of the type, when the application has it.</summary>
    public string? Summary { get; init; }

    /// <summary>Gets the public members of the type: those it declares, then those of its base types in the API.</summary>
    public IReadOnlyList<PluginApiMember> Members { get; init; } = [];

    /// <summary>Gets the names of the types of the API that derive from this one or implement it.</summary>
    public IReadOnlyList<string> DerivedTypes { get; init; } = [];
}

/// <summary>One public member of a type of the plugin API.</summary>
/// <param name="Name">The name of the member.</param>
/// <param name="Signature">The member as C#: its type, its name and its parameters.</param>
/// <param name="Summary">The first sentences of its documentation, when the application has it.</param>
public sealed record PluginApiMember(string Name, string Signature, string? Summary);

/// <summary>
/// The API a source plugin is written against, read from the assemblies the running application has: the types
/// of <c>CodeAlta.Plugins.Abstractions</c> and <c>CodeAlta.Plugins.Tui</c>, and the types of <c>CodeAlta.Agent</c>
/// that their members name (tools, events). It is what an author looks a type or a member up in, in place of the
/// source of CodeAlta.
/// </summary>
/// <remarks>
/// The signatures come from reflection, so they are those of the running version. The summaries come from the XML
/// documentation files beside the assemblies and are left out when a file is missing.
/// </remarks>
public sealed partial class PluginApiReference
{
    private const int MaximumSummaryLength = 220;
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static readonly string[] Keywords = ["void", "bool", "byte", "char", "decimal", "double", "float", "int", "long", "object", "short", "string", "uint", "ulong", "sbyte", "ushort"];
    private static readonly Type[] KeywordTypes =
        [typeof(void), typeof(bool), typeof(byte), typeof(char), typeof(decimal), typeof(double), typeof(float), typeof(int), typeof(long), typeof(object), typeof(short), typeof(string), typeof(uint), typeof(ulong), typeof(sbyte), typeof(ushort)];
    private readonly Dictionary<string, string> _documentation = new(StringComparer.Ordinal);
    private readonly HashSet<Type> _types = [];
    private readonly NullabilityInfoContext _nullability = new();

    /// <summary>Reads the plugin API of the running application.</summary>
    /// <param name="documentationDirectory">The folder that holds the XML documentation files; the folder of the application by default.</param>
    public PluginApiReference(string? documentationDirectory = null)
    {
        var abstractions = typeof(PluginBase).Assembly;
        var assemblies = new List<Assembly> { abstractions };
        if (TryLoad("CodeAlta.Plugins.Tui") is { } terminal) assemblies.Add(terminal);
        var agent = typeof(CodeAlta.Agent.AgentToolDefinition).Assembly;
        foreach (var assembly in assemblies.Append(agent)) ReadDocumentation(documentationDirectory ?? AppContext.BaseDirectory, assembly);

        var types = new List<Type>();
        var seen = _types;
        var pending = new Queue<Type>(assemblies.SelectMany(static assembly => assembly.GetExportedTypes()).Where(static type => !IsCompilerGenerated(type)));
        while (pending.TryDequeue(out var type))
        {
            if (!seen.Add(type)) continue;
            types.Add(type);
            // What the members of the API name in CodeAlta.Agent is part of what an author writes against.
            foreach (var named in Named(type).Concat(agent == type.Assembly ? Derived(type) : []))
            {
                if (named.Assembly == agent && named.IsVisible && !seen.Contains(named)) pending.Enqueue(named);
            }
        }

        Types = [.. types.Select(Describe).OrderBy(static type => type.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Gets every type of the API, by name.</summary>
    public IReadOnlyList<PluginApiType> Types { get; }

    /// <summary>
    /// Finds the types a text names: the type of that name with the types nested in it; else the types whose name
    /// contains the text; else the types that have a member whose name contains it.
    /// </summary>
    /// <param name="query">A type name, a part of one, or a member name.</param>
    /// <returns>The types, by name; empty when nothing matches.</returns>
    /// <exception cref="ArgumentException"><paramref name="query"/> is blank.</exception>
    public IReadOnlyList<PluginApiType> Find(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var text = query.Trim();
        PluginApiType[] exact = [.. Types.Where(type => string.Equals(type.Name, text, StringComparison.OrdinalIgnoreCase) || type.Name.StartsWith(text + ".", StringComparison.OrdinalIgnoreCase))];
        if (exact.Any(type => string.Equals(type.Name, text, StringComparison.OrdinalIgnoreCase))) return exact;
        PluginApiType[] named = [.. Types.Where(type => type.Name.Contains(text, StringComparison.OrdinalIgnoreCase))];
        return named.Length > 0 ? named : [.. Types.Where(type => type.Members.Any(member => member.Name.Contains(text, StringComparison.OrdinalIgnoreCase)))];
    }

    private PluginApiType Describe(Type type)
    {
        var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : IsDelegate(type) ? "delegate" : type.IsValueType ? (IsRecord(type) ? "record struct" : "struct") : IsRecord(type) ? "record" : "class";
        return new PluginApiType
        {
            Name = TypeName(type),
            Namespace = type.Namespace ?? string.Empty,
            Kind = kind,
            Declaration = Declaration(type, kind),
            Summary = Documentation("T:" + DocumentationName(type)),
            Members = type.IsEnum ? EnumMembers(type) : IsDelegate(type) ? [] : [.. Members(type), .. Inherited(type)],
            DerivedTypes = [.. _types.Where(other => other.BaseType == type || type.IsInterface && other.GetInterfaces().Except(other.BaseType?.GetInterfaces() ?? []).Contains(type))
                .Select(static other => TypeName(other)).Order(StringComparer.OrdinalIgnoreCase)],
        };
    }

    // The members of the base types that are part of the API: a context has the project and the session of its base.
    private IEnumerable<PluginApiMember> Inherited(Type type)
    {
        var parents = type.IsInterface ? type.GetInterfaces().Where(_types.Contains) : Parents(type);
        var names = new HashSet<string>(Members(type).Select(static member => member.Name), StringComparer.Ordinal);
        foreach (var parent in parents)
        {
            foreach (var member in Members(parent))
            {
                if (member.Name != ".ctor" && names.Add(member.Name)) yield return member;
            }
        }
    }

    private IEnumerable<Type> Parents(Type type)
    {
        for (var parent = type.BaseType; parent is not null && _types.Contains(parent); parent = parent.BaseType) yield return parent;
    }

    private string Declaration(Type type, string kind)
    {
        if (IsDelegate(type))
        {
            var invoke = type.GetMethod("Invoke")!;
            return $"public delegate {TypeName(invoke.ReturnType, _nullability.Create(invoke.ReturnParameter))} {TypeName(type)}({Parameters(invoke)})";
        }

        var modifiers = type.IsEnum || type.IsInterface || type.IsValueType ? string.Empty : type is { IsAbstract: true, IsSealed: true } ? "static " : type.IsAbstract ? "abstract " : type.IsSealed ? "sealed " : string.Empty;
        var bases = new List<string>();
        if (type.BaseType is { } parent && parent != typeof(object) && parent != typeof(ValueType) && parent != typeof(Enum)) bases.Add(TypeName(parent));
        bases.AddRange(type.GetInterfaces().Except(type.BaseType?.GetInterfaces() ?? []).Where(static item => item.IsVisible && !item.Name.StartsWith("IEquatable", StringComparison.Ordinal)).Select(item => TypeName(item)));
        return $"public {modifiers}{kind} {TypeName(type)}{(bases.Count > 0 ? " : " + string.Join(", ", bases) : string.Empty)}";
    }

    private IReadOnlyList<PluginApiMember> EnumMembers(Type type)
        => [.. Enum.GetNames(type).Select(name => new PluginApiMember(name, $"{name} = {Convert.ToInt64(Enum.Parse(type, name), System.Globalization.CultureInfo.InvariantCulture)}",
            Documentation($"F:{DocumentationName(type)}.{name}")))];

    private IReadOnlyList<PluginApiMember> Members(Type type)
    {
        var members = new List<PluginApiMember>();
        var typeName = TypeName(type);
        var documentationName = DocumentationName(type);
        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Where(static constructor => constructor.GetParameters().Length > 0))
        {
            members.Add(new(".ctor", $"new {typeName}({Parameters(constructor)})", Documentation($"M:{documentationName}.#ctor")));
        }

        foreach (var field in type.GetFields(Declared).Where(static field => !field.IsSpecialName))
        {
            var constant = field.IsLiteral ? $"const {TypeName(field.FieldType)} {field.Name} = {Literal(field.GetRawConstantValue())}" : $"{(field.IsStatic ? "static " : string.Empty)}{TypeName(field.FieldType, _nullability.Create(field))} {field.Name}";
            members.Add(new(field.Name, constant, Documentation($"F:{documentationName}.{field.Name}")));
        }

        foreach (var property in type.GetProperties(Declared).Where(static property => property.Name != "EqualityContract" && property.GetIndexParameters().Length == 0))
        {
            var getter = property.GetMethod is { IsPublic: true } ? property.GetMethod : null;
            var setter = property.SetMethod is { IsPublic: true } ? property.SetMethod : null;
            if (getter is null && setter is null) continue;
            var accessor = getter ?? setter!;
            var modifiers = (accessor.IsStatic ? "static " : string.Empty) + (accessor is { IsAbstract: true } && !type.IsInterface ? "abstract " : string.Empty)
                + (property.IsDefined(typeof(RequiredMemberAttribute), inherit: false) ? "required " : string.Empty);
            var accessors = (getter is null ? string.Empty : "get; ") + (setter is null ? string.Empty : IsInitOnly(setter) ? "init; " : "set; ");
            members.Add(new(property.Name, $"{modifiers}{TypeName(property.PropertyType, _nullability.Create(property))} {property.Name} {{ {accessors}}}", Documentation($"P:{documentationName}.{property.Name}")));
        }

        foreach (var method in type.GetMethods(Declared).Where(method => !method.IsSpecialName && !IsGeneratedMethod(method)))
        {
            var modifiers = (method.IsStatic ? "static " : string.Empty) + (type.IsInterface ? string.Empty : method.IsAbstract ? "abstract " : method is { IsVirtual: true, IsFinal: false } && method.GetBaseDefinition() == method ? "virtual " : string.Empty);
            var generic = method.IsGenericMethodDefinition ? "<" + string.Join(", ", method.GetGenericArguments().Select(static argument => argument.Name)) + ">" : string.Empty;
            members.Add(new(method.Name, $"{modifiers}{TypeName(method.ReturnType, _nullability.Create(method.ReturnParameter))} {method.Name}{generic}({Parameters(method)})", Documentation($"M:{documentationName}.{method.Name}")));
        }

        foreach (var item in type.GetEvents(Declared))
        {
            members.Add(new(item.Name, $"event {TypeName(item.EventHandlerType!)} {item.Name}", Documentation($"E:{documentationName}.{item.Name}")));
        }

        return members;
    }

    private string Parameters(MethodBase method)
        => string.Join(", ", method.GetParameters().Select(parameter =>
        {
            var type = parameter.ParameterType;
            var prefix = parameter.IsDefined(typeof(ParamArrayAttribute), inherit: false) ? "params " : parameter.IsOut ? "out " : type.IsByRef ? "ref " : string.Empty;
            var name = TypeName(type.IsByRef ? type.GetElementType()! : type, _nullability.Create(parameter));
            // The default of a value type that is not a number: a cancellation token.
            var value = !parameter.HasDefaultValue ? string.Empty : " = " + (parameter.DefaultValue is null && type.IsValueType && Nullable.GetUnderlyingType(type) is null ? "default" : Literal(parameter.DefaultValue));
            return $"{prefix}{name} {parameter.Name}{value}";
        }));

    private static string Literal(object? value)
        => value switch
        {
            null => "null",
            string text => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
            bool flag => flag ? "true" : "false",
            char character => $"'{character}'",
            Enum item => $"{item.GetType().Name}.{item}",
            IFormattable number => number.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => "default",
        };

    // The name of a type as C# writes it: a nested type with its holder, a generic type with its arguments.
    private static string TypeName(Type type, NullabilityInfo? nullability = null)
    {
        if (type.IsGenericParameter) return type.Name + Optional(nullability);
        if (type.IsArray) return TypeName(type.GetElementType()!, nullability?.ElementType) + "[]" + Optional(nullability);
        if (Nullable.GetUnderlyingType(type) is { } underlying) return TypeName(underlying) + "?";
        var keyword = Array.IndexOf(KeywordTypes, type);
        if (keyword >= 0) return Keywords[keyword] + Optional(nullability);
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0) name = name[..tick];
        if (type.IsGenericType)
        {
            // A nested type lists the arguments of its holder first: only its own are written with it.
            var arguments = type.GetGenericArguments();
            var own = arguments.Skip(type.DeclaringType?.GetGenericArguments().Length ?? 0).ToArray();
            var infos = nullability?.GenericTypeArguments ?? [];
            if (own.Length > 0)
            {
                name += "<" + string.Join(", ", own.Select((argument, index) =>
                    TypeName(argument, infos.Length == arguments.Length ? infos[arguments.Length - own.Length + index] : null))) + ">";
            }
        }

        if (type.DeclaringType is { } holder) name = TypeName(holder) + "." + name;
        return name + (type.IsValueType ? string.Empty : Optional(nullability));
    }

    private static string Optional(NullabilityInfo? nullability) => nullability?.ReadState == NullabilityState.Nullable || nullability?.WriteState == NullabilityState.Nullable ? "?" : string.Empty;

    // The types the public members of a type name, with the arguments of generic types.
    private static IEnumerable<Type> Named(Type type)
    {
        IEnumerable<Type> signature = [];
        if (IsDelegate(type))
        {
            var invoke = type.GetMethod("Invoke")!;
            signature = invoke.GetParameters().Select(static parameter => parameter.ParameterType).Append(invoke.ReturnType);
        }
        else if (!type.IsEnum)
        {
            signature = type.GetProperties(Declared).Select(static property => property.PropertyType)
                .Concat(type.GetFields(Declared).Select(static field => field.FieldType))
                .Concat(type.GetMethods(Declared).SelectMany(static method => method.GetParameters().Select(static parameter => parameter.ParameterType).Append(method.ReturnType)))
                .Concat(type.GetConstructors().SelectMany(static constructor => constructor.GetParameters().Select(static parameter => parameter.ParameterType)))
                .Concat(type.GetNestedTypes(BindingFlags.Public));
        }

        return signature.Concat(type.BaseType is { } parent ? [parent] : []).SelectMany(Flatten).Distinct();
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        if (type.HasElementType) return Flatten(type.GetElementType()!);
        if (type.IsGenericParameter) return [];
        return type.IsGenericType ? type.GetGenericArguments().SelectMany(Flatten).Append(type.GetGenericTypeDefinition()) : [type];
    }

    // The kinds of an abstract type of CodeAlta.Agent: the events, the items of a tool result.
    private static IEnumerable<Type> Derived(Type type)
        => type is { IsAbstract: true, IsInterface: false, IsSealed: false } ? type.Assembly.GetExportedTypes().Where(candidate => candidate.BaseType == type) : [];

    private static bool IsDelegate(Type type) => typeof(Delegate).IsAssignableFrom(type);

    private static bool IsRecord(Type type) => type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null
        || type.IsValueType && type.GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Instance) is not null;

    private static bool IsCompilerGenerated(Type type) => type.Name.Contains('<', StringComparison.Ordinal) || type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);

    private static bool IsInitOnly(MethodInfo setter) => setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));

    // What the compiler adds to a record, and what every object has.
    private static bool IsGeneratedMethod(MethodInfo method)
        => method.Name is "<Clone>$" or "PrintMembers" or "Deconstruct" or "GetHashCode" or "ToString" or "Equals" || method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);

    private static Assembly? TryLoad(string name)
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal)) ?? Assembly.Load(name);
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException)
        {
            return null;
        }
    }

    private static string DocumentationName(Type type) => (type.FullName ?? type.Name).Replace('+', '.');

    private string? Documentation(string id) => _documentation.GetValueOrDefault(id);

    // The summaries of an assembly, by documentation id; a method by its name, whatever its parameters.
    private void ReadDocumentation(string directory, Assembly assembly)
    {
        var path = Path.Combine(directory, assembly.GetName().Name + ".xml");
        try
        {
            if (!File.Exists(path)) return;
            foreach (var member in XDocument.Load(path).Descendants("member"))
            {
                if (member.Attribute("name")?.Value is not { Length: > 2 } id || member.Element("summary") is not { } summary) continue;
                var parenthesis = id.IndexOf('(', StringComparison.Ordinal);
                var key = parenthesis > 0 ? id[..parenthesis] : id;
                // A generic method is "Name``1": the reference names it without its arity.
                key = Arity().Replace(key, string.Empty);
                _documentation.TryAdd(key, Text(summary));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Without the file the reference has the signatures only.
        }
    }

    private static string Text(XElement summary)
    {
        var builder = new StringBuilder();
        foreach (var node in summary.Nodes())
        {
            if (node is XText text) builder.Append(text.Value);
            else if (node is XElement element)
            {
                // <see cref="T:Namespace.Type"/> reads as "Type"; <c>x</c> and the rest read as their text.
                var reference = element.Attribute("cref")?.Value ?? element.Attribute("langword")?.Value ?? element.Attribute("name")?.Value;
                builder.Append(reference is null ? element.Value : reference[(reference.LastIndexOfAny(['.', ':']) + 1)..]);
            }
        }

        var result = Spaces().Replace(builder.ToString(), " ").Trim();
        return result.Length <= MaximumSummaryLength ? result : result[..MaximumSummaryLength].TrimEnd() + "…";
    }

    /// <summary>
    /// Returns the names of the types that are close to a name no type and no member has: the types that have
    /// the last word of the name (<c>PluginUiContext</c> gives the types named <c>...Context</c>), or else
    /// those that have a word before it.
    /// </summary>
    /// <param name="query">The name that was not found.</param>
    /// <param name="maximum">The most names to return.</param>
    /// <returns>The names, in the order of the reference; empty when no word of the name is in a type name.</returns>
    /// <exception cref="ArgumentException"><paramref name="query"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximum"/> is negative.</exception>
    public IReadOnlyList<string> Suggest(string query, int maximum = 32)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        // The words of a name, the last one first; "Plugin" is in most names and says nothing.
        foreach (var word in NameWord().Matches(query).Select(static match => match.Value).Reverse())
        {
            if (word.Length < 3 || string.Equals(word, "Plugin", StringComparison.OrdinalIgnoreCase)) continue;
            string[] near = [.. Types.Where(type => type.Name.Contains(word, StringComparison.OrdinalIgnoreCase)).Select(static type => type.Name).Take(maximum)];
            if (near.Length > 0) return near;
        }

        return [];
    }

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z0-9]+")]
    private static partial Regex NameWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"``\d+$")]
    private static partial Regex Arity();
}
