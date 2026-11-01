using System.Text.Json;

namespace CodeAlta.Browser.Cdp.Generator;

/// <summary>The item type of an array in the CDP protocol files.</summary>
/// <param name="Type">The primitive type, or null when <paramref name="Ref"/> names a type.</param>
/// <param name="Ref">The referenced type, as <c>Type</c> in its domain or <c>Domain.Type</c>.</param>
internal sealed record CdpItems(string? Type, string? Ref);

/// <summary>A property of a type, a parameter of a command or event, or a return value of a command.</summary>
internal sealed record CdpProperty(string Name, string? Type, string? Ref, bool Optional, string? Description, CdpItems? Items);

/// <summary>A type declared by a domain.</summary>
internal sealed record CdpType(string Id, string Type, string? Description, IReadOnlyList<CdpProperty>? Properties, IReadOnlyList<string>? Enum, CdpItems? Items);

/// <summary>A command of a domain.</summary>
internal sealed record CdpCommand(string Name, string? Description, IReadOnlyList<CdpProperty> Parameters, IReadOnlyList<CdpProperty> Returns);

/// <summary>An event of a domain.</summary>
internal sealed record CdpEvent(string Name, string? Description, IReadOnlyList<CdpProperty> Parameters);

/// <summary>A domain of the protocol.</summary>
internal sealed record CdpDomain(string Name, IReadOnlyList<CdpType> Types, IReadOnlyList<CdpCommand> Commands, IReadOnlyList<CdpEvent> Events);

/// <summary>
/// The Chrome DevTools Protocol as <c>browser_protocol.json</c> and <c>js_protocol.json</c> of
/// <c>ChromeDevTools/devtools-protocol</c> declare it: domains of types, commands and events. It is not JSON Schema.
/// </summary>
internal sealed class CdpProtocol
{
    private readonly Dictionary<string, CdpDomain> _domains;

    private CdpProtocol(Dictionary<string, CdpDomain> domains) => _domains = domains;

    /// <summary>Gets the domains, by name.</summary>
    public IReadOnlyDictionary<string, CdpDomain> Domains => _domains;

    /// <summary>Reads the protocol files.</summary>
    /// <param name="files">The JSON texts of the protocol files.</param>
    /// <exception cref="InvalidDataException">A file is not a protocol file.</exception>
    public static CdpProtocol Parse(IEnumerable<string> files)
    {
        var domains = new Dictionary<string, CdpDomain>(StringComparer.Ordinal);
        foreach (var text in files)
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("domains", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("A protocol file has no \"domains\" array.");
            foreach (var domain in list.EnumerateArray())
            {
                var name = domain.GetProperty("domain").GetString()!;
                domains[name] = new CdpDomain(
                    name,
                    Items(domain, "types", ReadType),
                    Items(domain, "commands", static command => new CdpCommand(
                        command.GetProperty("name").GetString()!, Text(command, "description"),
                        Items(command, "parameters", ReadProperty), Items(command, "returns", ReadProperty))),
                    Items(domain, "events", static item => new CdpEvent(
                        item.GetProperty("name").GetString()!, Text(item, "description"), Items(item, "parameters", ReadProperty))));
            }
        }

        return new CdpProtocol(domains);
    }

    /// <summary>Finds a type from a reference, which names a type of <paramref name="domain"/> or <c>Domain.Type</c>.</summary>
    /// <exception cref="InvalidDataException">No type has that name.</exception>
    public (string Domain, CdpType Type) Resolve(string domain, string reference)
    {
        var dot = reference.IndexOf('.');
        var (owner, id) = dot < 0 ? (domain, reference) : (reference[..dot], reference[(dot + 1)..]);
        if (_domains.TryGetValue(owner, out var found) && found.Types.FirstOrDefault(type => type.Id == id) is { } type) return (owner, type);
        throw new InvalidDataException($"The type {reference} of domain {domain} is not declared.");
    }

    private static CdpType ReadType(JsonElement value) => new(
        value.GetProperty("id").GetString()!, value.GetProperty("type").GetString()!, Text(value, "description"),
        value.TryGetProperty("properties", out _) ? Items(value, "properties", ReadProperty) : null,
        value.TryGetProperty("enum", out var values) ? [.. values.EnumerateArray().Select(static entry => entry.GetString()!)] : null,
        ReadItems(value));

    private static CdpProperty ReadProperty(JsonElement value) => new(
        value.GetProperty("name").GetString()!, Text(value, "type"), Text(value, "$ref"),
        value.TryGetProperty("optional", out var optional) && optional.ValueKind == JsonValueKind.True,
        Text(value, "description"), ReadItems(value));

    private static CdpItems? ReadItems(JsonElement value)
        => value.TryGetProperty("items", out var items) ? new CdpItems(Text(items, "type"), Text(items, "$ref")) : null;

    private static string? Text(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static IReadOnlyList<T> Items<T>(JsonElement value, string name, Func<JsonElement, T> read)
        => value.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? [.. list.EnumerateArray().Select(read)] : [];
}
