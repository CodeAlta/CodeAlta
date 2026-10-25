using System.Text.Json.Serialization;

namespace CodeAlta.Catalog;

/// <summary>Represents the <c>[sandbox]</c> table: whether every session is sandboxed, and the profiles of the user.</summary>
public sealed class CodeAltaSandboxSettingsDocument
{
    /// <summary>Gets or sets the profile every session is sandboxed with; null for none.</summary>
    [JsonPropertyName("all_sessions")]
    public string? AllSessions { get; set; }

    /// <summary>Gets or sets the profiles of the user, by name; a name of a built-in profile changes it.</summary>
    [JsonPropertyName("profiles")]
    public Dictionary<string, CodeAltaSandboxProfileDocument>? Profiles { get; set; }
}

/// <summary>Represents one <c>[sandbox.profiles.&lt;name&gt;]</c> table.</summary>
public sealed class CodeAltaSandboxProfileDocument
{
    /// <summary>Gets or sets the profile it builds on.</summary>
    [JsonPropertyName("extends")]
    public string? Extends { get; set; }

    /// <summary>Gets or sets more folders, read-write.</summary>
    [JsonPropertyName("write")]
    public List<string>? Write { get; set; }

    /// <summary>Gets or sets more folders, read-only.</summary>
    [JsonPropertyName("read")]
    public List<string>? Read { get; set; }

    /// <summary>Gets or sets folders and files never readable.</summary>
    [JsonPropertyName("deny")]
    public List<string>? Deny { get; set; }

    /// <summary>Gets or sets the network: <c>off</c>, <c>hosts</c> or <c>on</c>.</summary>
    [JsonPropertyName("network")]
    public string? Network { get; set; }

    /// <summary>Gets or sets the hosts of the <c>hosts</c> network.</summary>
    [JsonPropertyName("hosts")]
    public List<string>? Hosts { get; set; }

    /// <summary>Gets or sets the ports of this computer the sandbox may reach.</summary>
    [JsonPropertyName("local_ports")]
    public List<int>? LocalPorts { get; set; }

    /// <summary>Gets or sets the names of the variables of the user's environment it receives.</summary>
    [JsonPropertyName("env")]
    public List<string>? Env { get; set; }

    /// <summary>Gets or sets the MCP servers its sessions may use.</summary>
    [JsonPropertyName("mcp_servers")]
    public List<string>? McpServers { get; set; }

    /// <summary>Gets or sets whether a command may be asked to run outside the sandbox: <c>ask</c> or <c>never</c>.</summary>
    [JsonPropertyName("outside")]
    public string? Outside { get; set; }
}

/// <summary>The <c>[sandbox]</c> settings of the user, read from <c>~/.alta/config.toml</c> only.</summary>
/// <param name="AllSessions">The profile every session is sandboxed with; null for none.</param>
/// <param name="Profiles">The profiles of the user.</param>
public sealed record CodeAltaSandboxSettings(string? AllSessions, IReadOnlyList<CodeAltaSandboxProfileSettings> Profiles)
{
    /// <summary>Gets settings that sandbox nothing and add no profile.</summary>
    public static CodeAltaSandboxSettings Empty { get; } = new(null, []);

    internal static CodeAltaSandboxSettings From(CodeAltaSandboxSettingsDocument? document)
        => document is null ? Empty : new(
            Text(document.AllSessions),
            [.. (document.Profiles ?? []).Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
                .Select(static pair => CodeAltaSandboxProfileSettings.From(pair.Key.Trim(), pair.Value ?? new CodeAltaSandboxProfileDocument()))]);

    internal CodeAltaSandboxSettingsDocument? ToDocument()
    {
        var profiles = Profiles.Where(static profile => !string.IsNullOrWhiteSpace(profile.Name))
            .ToDictionary(static profile => profile.Name.Trim(), static profile => profile.ToDocument(), StringComparer.OrdinalIgnoreCase);
        var document = new CodeAltaSandboxSettingsDocument { AllSessions = Text(AllSessions), Profiles = profiles.Count == 0 ? null : profiles };
        return document is { AllSessions: null, Profiles: null } ? null : document;
    }

    internal static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A sandbox profile as the user's settings write it; a null value is taken from the profile it changes or extends.</summary>
/// <param name="Name">The name of the profile.</param>
/// <param name="Extends">The profile it builds on.</param>
/// <param name="Write">More folders, read-write.</param>
/// <param name="Read">More folders, read-only.</param>
/// <param name="Deny">Folders and files never readable.</param>
/// <param name="Network">The network: <c>off</c>, <c>hosts</c> or <c>on</c>.</param>
/// <param name="Hosts">The hosts of the <c>hosts</c> network.</param>
/// <param name="LocalPorts">The ports of this computer it may reach.</param>
/// <param name="Env">The names of the variables of the user's environment it receives.</param>
/// <param name="McpServers">The MCP servers its sessions may use.</param>
/// <param name="Outside">Whether a command may be asked to run outside the sandbox: <c>ask</c> or <c>never</c>.</param>
public sealed record CodeAltaSandboxProfileSettings(
    string Name,
    string? Extends = null,
    IReadOnlyList<string>? Write = null,
    IReadOnlyList<string>? Read = null,
    IReadOnlyList<string>? Deny = null,
    string? Network = null,
    IReadOnlyList<string>? Hosts = null,
    IReadOnlyList<int>? LocalPorts = null,
    IReadOnlyList<string>? Env = null,
    IReadOnlyList<string>? McpServers = null,
    string? Outside = null)
{
    internal static CodeAltaSandboxProfileSettings From(string name, CodeAltaSandboxProfileDocument document)
        => new(name, CodeAltaSandboxSettings.Text(document.Extends), List(document.Write), List(document.Read), List(document.Deny),
            CodeAltaSandboxSettings.Text(document.Network)?.ToLowerInvariant(), List(document.Hosts), document.LocalPorts is null ? null : [.. document.LocalPorts],
            List(document.Env), List(document.McpServers), CodeAltaSandboxSettings.Text(document.Outside)?.ToLowerInvariant());

    internal CodeAltaSandboxProfileDocument ToDocument() => new()
    {
        Extends = CodeAltaSandboxSettings.Text(Extends),
        Write = Write is null ? null : [.. Write],
        Read = Read is null ? null : [.. Read],
        Deny = Deny is null ? null : [.. Deny],
        Network = CodeAltaSandboxSettings.Text(Network),
        Hosts = Hosts is null ? null : [.. Hosts],
        LocalPorts = LocalPorts is null ? null : [.. LocalPorts],
        Env = Env is null ? null : [.. Env],
        McpServers = McpServers is null ? null : [.. McpServers],
        Outside = CodeAltaSandboxSettings.Text(Outside),
    };

    private static IReadOnlyList<string>? List(List<string>? values)
        => values is null ? null : [.. values.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim())];
}
