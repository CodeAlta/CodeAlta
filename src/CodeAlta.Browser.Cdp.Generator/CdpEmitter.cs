using System.Text;

namespace CodeAlta.Browser.Cdp.Generator;

/// <summary>
/// Writes the C# of the commands and events a method map selects: records for their parameters, results and the types
/// they reach, a typed client per domain on <c>CdpSession</c>, and the source-generated JSON context of them all.
/// </summary>
/// <remarks>
/// Every member is lenient on what a browser sends: nothing is <c>required</c>, string enums stay strings, and objects
/// without declared properties are <see cref="System.Text.Json.JsonElement"/>, so that a newer browser that adds a
/// value or leaves one out never fails a read.
/// </remarks>
internal sealed class CdpEmitter
{
    private readonly CdpProtocol _protocol;
    private readonly CdpMethodMap _map;
    private readonly string _namespace;
    // The object types the selection reaches, by C# name, with their domain.
    private readonly SortedDictionary<string, (string Domain, CdpType Type)> _types = new(StringComparer.Ordinal);
    private readonly List<string> _roots = [];

    public CdpEmitter(CdpProtocol protocol, CdpMethodMap map, string ns)
    {
        _protocol = protocol;
        _map = map;
        _namespace = ns;
    }

    /// <summary>Gets the problems found in the map, such as a command that the protocol does not have.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Writes the files: one per domain, the domains of the session, the JSON context and the metadata.</summary>
    /// <param name="revision">The revision of the protocol files, for the metadata.</param>
    public IReadOnlyList<(string FileName, string Content)> EmitAll(string revision)
    {
        var commands = Select(_map.Commands, static domain => domain.Commands.Select(static command => command.Name));
        var events = Select(_map.Events, static domain => domain.Events.Select(static item => item.Name));
        foreach (var (domain, name) in commands)
        {
            var command = _protocol.Domains[domain].Commands.First(item => item.Name == name);
            foreach (var property in command.Parameters.Concat(command.Returns)) Reach(domain, property.Type, property.Ref, property.Items);
        }

        foreach (var (domain, name) in events)
        {
            foreach (var property in _protocol.Domains[domain].Events.First(item => item.Name == name).Parameters) Reach(domain, property.Type, property.Ref, property.Items);
        }

        var files = new List<(string, string)>();
        var domains = commands.Select(static item => item.Domain).Concat(events.Select(static item => item.Domain)).Distinct().Order(StringComparer.Ordinal).ToList();
        foreach (var domain in domains)
        {
            files.Add(($"Cdp.{domain}.gen.cs", EmitDomain(domain,
                [.. commands.Where(item => item.Domain == domain).Select(item => _protocol.Domains[domain].Commands.First(command => command.Name == item.Name))],
                [.. events.Where(item => item.Domain == domain).Select(item => _protocol.Domains[domain].Events.First(entry => entry.Name == item.Name))])));
        }

        files.Add(("CdpSession.Domains.gen.cs", EmitSessionDomains(domains)));
        files.Add(("CdpJsonContext.gen.cs", EmitContext()));
        files.Add(("Cdp.Metadata.gen.cs", EmitMetadata(revision, commands.Count, events.Count)));
        return files;
    }

    private List<(string Domain, string Name)> Select(IReadOnlyList<string> names, Func<CdpDomain, IEnumerable<string>> members)
    {
        var selected = new List<(string, string)>();
        foreach (var qualified in names)
        {
            var dot = qualified.IndexOf('.');
            if (dot <= 0 || !_protocol.Domains.TryGetValue(qualified[..dot], out var domain) || !members(domain).Contains(qualified[(dot + 1)..]))
            {
                Warnings.Add($"{qualified} is not in the protocol.");
                continue;
            }

            selected.Add((qualified[..dot], qualified[(dot + 1)..]));
        }

        return selected;
    }

    private void Reach(string domain, string? type, string? reference, CdpItems? items)
    {
        if (reference is not null)
        {
            var (owner, declared) = _protocol.Resolve(domain, reference);
            if (IsRecord(declared))
            {
                if (_types.TryAdd(TypeName(owner, declared.Id), (owner, declared)))
                {
                    foreach (var property in declared.Properties!) Reach(owner, property.Type, property.Ref, property.Items);
                }
            }
            else if (declared.Type == "array" && declared.Items is { } inner) Reach(owner, inner.Type, inner.Ref, null);
        }
        else if (type == "array" && items is not null) Reach(domain, items.Type, items.Ref, null);
    }

    private static bool IsRecord(CdpType type) => type.Type == "object" && type.Properties is { Count: > 0 };

    private static string TypeName(string domain, string id) => domain + id;

    private static string Pascal(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    private string CSharpType(string domain, string? type, string? reference, CdpItems? items)
    {
        if (reference is not null)
        {
            var (owner, declared) = _protocol.Resolve(domain, reference);
            if (IsRecord(declared)) return TypeName(owner, declared.Id);
            return CSharpType(owner, declared.Type, null, declared.Items);
        }

        return type switch
        {
            "string" or "binary" => "string",
            "integer" => "int",
            "number" => "double",
            "boolean" => "bool",
            "array" when items is not null => CSharpType(domain, items.Type, items.Ref, null) + "[]",
            _ => "JsonElement",
        };
    }

    private string EmitDomain(string domain, IReadOnlyList<CdpCommand> commands, IReadOnlyList<CdpEvent> events)
    {
        var builder = Header();
        foreach (var (name, (owner, type)) in _types.Where(entry => entry.Value.Domain == domain))
        {
            EmitRecord(builder, owner, name, type.Description, type.Properties!);
        }

        foreach (var command in commands)
        {
            EmitRecord(builder, domain, $"{domain}{Pascal(command.Name)}Params", $"The parameters of <c>{domain}.{command.Name}</c>.", command.Parameters);
            if (command.Returns.Count != 0) EmitRecord(builder, domain, $"{domain}{Pascal(command.Name)}Result", $"The result of <c>{domain}.{command.Name}</c>.", command.Returns);
        }

        foreach (var item in events)
        {
            EmitRecord(builder, domain, $"{domain}{Pascal(item.Name)}Event", $"The parameters of the event <c>{domain}.{item.Name}</c>.", item.Parameters);
        }

        builder.AppendLine($"/// <summary>The commands and events of the <c>{domain}</c> domain on a session.</summary>");
        builder.AppendLine($"internal readonly struct Cdp{domain}Domain(CdpSession session)");
        builder.AppendLine("{");
        foreach (var command in commands)
        {
            var method = $"{domain}.{command.Name}";
            var parameters = $"{domain}{Pascal(command.Name)}Params";
            var result = $"{domain}{Pascal(command.Name)}Result";
            var optional = command.Parameters.All(static parameter => parameter.Optional);
            Doc(builder, command.Description ?? method, "    ");
            var signature = optional ? $"{parameters}? parameters = null, CancellationToken cancellationToken = default" : $"{parameters} parameters, CancellationToken cancellationToken = default";
            if (command.Returns.Count != 0)
            {
                builder.AppendLine($"    public Task<{result}> {Pascal(command.Name)}Async({signature})");
                builder.AppendLine($"        => session.SendAsync(\"{method}\", parameters ?? new(), CdpJsonContext.Default.{parameters}, CdpJsonContext.Default.{result}, cancellationToken);");
            }
            else
            {
                builder.AppendLine($"    public Task {Pascal(command.Name)}Async({signature})");
                builder.AppendLine($"        => session.SendAsync(\"{method}\", parameters ?? new(), CdpJsonContext.Default.{parameters}, cancellationToken);");
            }

            builder.AppendLine();
            _roots.Add(parameters);
            if (command.Returns.Count != 0) _roots.Add(result);
        }

        foreach (var item in events)
        {
            var record = $"{domain}{Pascal(item.Name)}Event";
            Doc(builder, $"Calls a handler for each <c>{domain}.{item.Name}</c> of the session, in the order the browser sent them, until the subscription is disposed.", "    ");
            builder.AppendLine($"    public IDisposable On{Pascal(item.Name)}(Action<{record}> handler)");
            builder.AppendLine($"        => session.On(\"{domain}.{item.Name}\", CdpJsonContext.Default.{record}, handler);");
            builder.AppendLine();
            _roots.Add(record);
        }

        TrimBlankLine(builder);
        builder.AppendLine("}");
        return builder.ToString();
    }

    private void EmitRecord(StringBuilder builder, string domain, string name, string? description, IReadOnlyList<CdpProperty> properties)
    {
        Doc(builder, description ?? name, string.Empty);
        builder.AppendLine($"internal sealed record {name}");
        builder.AppendLine("{");
        foreach (var property in properties)
        {
            var type = CSharpType(domain, property.Type, property.Ref, property.Items);
            var member = Pascal(property.Name);
            if (member == name) member += "Value";
            // An object of any shape is nullable whatever the protocol says: a default JsonElement cannot be written.
            var nullable = property.Optional || type == "JsonElement";
            var initializer = nullable ? string.Empty : type switch
            {
                "string" => " = \"\";",
                _ when type.EndsWith("[]", StringComparison.Ordinal) => " = [];",
                "int" or "double" or "bool" => string.Empty,
                _ => " = null!;",
            };
            Doc(builder, property.Description ?? property.Name, "    ");
            builder.AppendLine($"    [JsonPropertyName(\"{property.Name}\")]");
            builder.AppendLine($"    public {type}{(nullable ? "?" : string.Empty)} {member} {{ get; init; }}{initializer}");
            builder.AppendLine();
        }

        TrimBlankLine(builder);
        builder.AppendLine("}");
        builder.AppendLine();
    }

    private string EmitSessionDomains(IReadOnlyList<string> domains)
    {
        var builder = Header();
        builder.AppendLine("internal sealed partial class CdpSession");
        builder.AppendLine("{");
        foreach (var domain in domains)
        {
            builder.AppendLine($"    /// <summary>Gets the commands and events of the <c>{domain}</c> domain.</summary>");
            builder.AppendLine($"    public Cdp{domain}Domain {domain} => new(this);");
            builder.AppendLine();
        }

        TrimBlankLine(builder);
        builder.AppendLine("}");
        return builder.ToString();
    }

    private string EmitContext()
    {
        var builder = Header();
        builder.AppendLine("/// <summary>The source-generated JSON metadata of the commands and events CodeAlta sends and reads.</summary>");
        builder.AppendLine("[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, NumberHandling = JsonNumberHandling.AllowReadingFromString)]");
        foreach (var root in _roots.Distinct().Order(StringComparer.Ordinal)) builder.AppendLine($"[JsonSerializable(typeof({root}))]");
        builder.AppendLine("internal sealed partial class CdpJsonContext : JsonSerializerContext;");
        return builder.ToString();
    }

    private string EmitMetadata(string revision, int commands, int events)
    {
        var builder = Header();
        builder.AppendLine("/// <summary>Where the bindings come from.</summary>");
        builder.AppendLine("internal static class CdpProtocolInfo");
        builder.AppendLine("{");
        builder.AppendLine("    /// <summary>The revision of <c>ChromeDevTools/devtools-protocol</c> the bindings were generated from.</summary>");
        builder.AppendLine($"    public const string Revision = \"{revision}\";");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>The number of commands the bindings have.</summary>");
        builder.AppendLine($"    public const int CommandCount = {commands};");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>The number of events the bindings have.</summary>");
        builder.AppendLine($"    public const int EventCount = {events};");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private StringBuilder Header()
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated>");
        builder.AppendLine("// Generated by CodeAlta.Browser.Cdp.Generator from ChromeDevTools/devtools-protocol. Do not edit.");
        builder.AppendLine("// </auto-generated>");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("using System.Text.Json;");
        builder.AppendLine("using System.Text.Json.Serialization;");
        builder.AppendLine();
        builder.AppendLine($"namespace {_namespace};");
        builder.AppendLine();
        return builder;
    }

    private static void Doc(StringBuilder builder, string text, string indent)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(static part => part.Trim()));
        if (line.Length > 400) line = line[..400] + "…";
        // A description of the protocol is text; the summary of a generated member keeps the markup of the generator.
        if (!line.Contains("<c>", StringComparison.Ordinal)) line = line.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        builder.AppendLine($"{indent}/// <summary>{line}</summary>");
    }

    private static void TrimBlankLine(StringBuilder builder)
    {
        var newline = Environment.NewLine;
        while (builder.Length >= newline.Length * 2 && builder.ToString(builder.Length - newline.Length * 2, newline.Length * 2) == newline + newline)
        {
            builder.Length -= newline.Length;
        }
    }
}
