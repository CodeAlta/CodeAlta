using System.Collections.Frozen;
using Tomlyn;
using Tomlyn.Model;

namespace CodeAlta.Catalog;

/// <summary>The kinds of resource that are assigned to spaces in the global configuration.</summary>
public enum SpaceResourceKind
{
    /// <summary>A model provider: <c>[providers.&lt;key&gt;] spaces</c>.</summary>
    Provider,

    /// <summary>An MCP server, through its policy entry: <c>[plugins.mcp.servers.&lt;key&gt;] spaces</c>.</summary>
    McpServer,

    /// <summary>A plugin: <c>[plugins.&lt;id&gt;] spaces</c>.</summary>
    Plugin,

    /// <summary>A skill, by its name: <c>[skills.spaces] &lt;name&gt; = [...]</c>.</summary>
    Skill,

    /// <summary>An agent prompt, by its id: <c>[prompts.spaces] &lt;id&gt; = [...]</c>.</summary>
    Prompt,
}

/// <summary>
/// The spaces each resource is assigned to, as the global <c>config.toml</c> says it: one list <c>spaces</c>
/// beside the definition of each resource, or in a map for the skills and the agent prompts, whose definitions
/// are files that cannot always be written. A resource that is not listed is in every space (see
/// <see cref="SpaceScope"/>).
/// </summary>
/// <remarks>Immutable. Keys are compared without case, as the configuration compares the keys of providers and plugins.</remarks>
public sealed class SpaceAssignments
{
    private static readonly FrozenDictionary<string, IReadOnlyList<string>> None
        = FrozenDictionary.Create<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    private SpaceAssignments(
        FrozenDictionary<string, IReadOnlyList<string>> providers,
        FrozenDictionary<string, IReadOnlyList<string>> mcpServers,
        FrozenDictionary<string, IReadOnlyList<string>> plugins,
        FrozenDictionary<string, IReadOnlyList<string>> skills,
        FrozenDictionary<string, IReadOnlyList<string>> prompts)
        => (Providers, McpServers, Plugins, Skills, Prompts) = (providers, mcpServers, plugins, skills, prompts);

    /// <summary>Gets assignments that list nothing: every resource is in every space.</summary>
    public static SpaceAssignments Empty { get; } = new(None, None, None, None, None);

    /// <summary>Gets the spaces of the providers that list some, by provider key.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Providers { get; }

    /// <summary>Gets the spaces of the MCP servers that list some, by server key.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> McpServers { get; }

    /// <summary>Gets the spaces of the plugins that list some, by plugin id.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Plugins { get; }

    /// <summary>Gets the spaces of the skills that list some, by skill name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Skills { get; }

    /// <summary>Gets the spaces of the agent prompts that list some, by prompt id.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Prompts { get; }

    /// <summary>Gets the spaces a resource is assigned to.</summary>
    /// <param name="kind">The kind of the resource.</param>
    /// <param name="key">Its key, name or id.</param>
    /// <returns>The spaces; empty when the resource is in every space.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty.</exception>
    public IReadOnlyList<string> Of(SpaceResourceKind kind, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Map(kind).TryGetValue(key.Trim(), out var spaces) ? spaces : [];
    }

    /// <summary>Tells whether a resource is available in the space of a session.</summary>
    /// <param name="kind">The kind of the resource.</param>
    /// <param name="key">Its key, name or id.</param>
    /// <param name="sessionSpaceId">The space of the session; <see langword="null"/> for the default space.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty.</exception>
    public bool IsAvailable(SpaceResourceKind kind, string key, string? sessionSpaceId)
        => SpaceScope.IsAvailable(Of(kind, key), sessionSpaceId);

    /// <summary>Gets the assignments of one kind of resource.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The spaces of the resources that list some.</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Map(SpaceResourceKind kind)
        => kind switch
        {
            SpaceResourceKind.Provider => Providers,
            SpaceResourceKind.McpServer => McpServers,
            SpaceResourceKind.Plugin => Plugins,
            SpaceResourceKind.Skill => Skills,
            SpaceResourceKind.Prompt => Prompts,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of resource."),
        };

    /// <summary>
    /// Reads the assignments of a configuration. A value of another type than a list of strings (or one string) is
    /// not a list of spaces and is ignored.
    /// </summary>
    /// <param name="content">The TOML text of the configuration.</param>
    /// <param name="sourceName">The file it comes from, for the messages; <see langword="null"/> for none.</param>
    /// <returns>The assignments.</returns>
    /// <exception cref="InvalidDataException">The text is not valid TOML.</exception>
    public static SpaceAssignments Parse(string? content, string? sourceName = null)
    {
        if (string.IsNullOrWhiteSpace(content)) return Empty;
        TomlTable root;
        try
        {
            var options = sourceName is null ? TomlSerializerOptions.Default : TomlSerializerOptions.Default with { SourceName = sourceName };
            root = TomlSerializer.Deserialize<TomlTable>(content, options) ?? new TomlTable();
        }
        catch (Exception exception) when (exception is TomlException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"Failed to parse CodeAlta config '{sourceName ?? "config.toml"}'.", exception);
        }

        return Read(root);
    }

    /// <summary>Reads the assignments of a configuration already parsed.</summary>
    /// <param name="root">The root table of the configuration.</param>
    /// <returns>The assignments.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is <see langword="null"/>.</exception>
    public static SpaceAssignments Read(TomlTable root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var plugins = Table(root, "plugins");
        return new(
            ListsOfTables(Table(root, "providers")),
            ListsOfTables(Table(Table(plugins, "mcp"), "servers")),
            ListsOfTables(plugins),
            Lists(Table(Table(root, "skills"), "spaces")),
            Lists(Table(Table(root, "prompts"), "spaces")));
    }

    // [<table>.<key>] spaces = [...]
    private static FrozenDictionary<string, IReadOnlyList<string>> ListsOfTables(TomlTable? parent)
    {
        if (parent is null) return None;
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parent)
        {
            if (value is TomlTable table && table.TryGetValue("spaces", out var spaces) && List(spaces) is { Count: > 0 } list)
                result[key.Trim()] = list;
        }

        return result.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    // [<table>] <key> = [...]
    private static FrozenDictionary<string, IReadOnlyList<string>> Lists(TomlTable? table)
    {
        if (table is null) return None;
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in table)
        {
            if (List(value) is { Count: > 0 } list) result[key.Trim()] = list;
        }

        return result.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string>? List(object? value)
        => value switch
        {
            TomlArray array => SpaceScope.Normalize(array.OfType<string>()),
            string one => SpaceScope.Normalize([one]),
            _ => null,
        };

    private static TomlTable? Table(TomlTable? parent, string key)
    {
        if (parent is null) return null;
        if (parent.TryGetValue(key, out var exact)) return exact as TomlTable;
        foreach (var (name, value) in parent)
        {
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) return value as TomlTable;
        }

        return null;
    }

    /// <summary>The table and the key where the list of a resource is written.</summary>
    internal static (string[] Table, string Key) Location(SpaceResourceKind kind, string key)
        => kind switch
        {
            SpaceResourceKind.Provider => (["providers", key], "spaces"),
            SpaceResourceKind.McpServer => (["plugins", "mcp", "servers", key], "spaces"),
            SpaceResourceKind.Plugin => (["plugins", key], "spaces"),
            SpaceResourceKind.Skill => (["skills", "spaces"], key),
            SpaceResourceKind.Prompt => (["prompts", "spaces"], key),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of resource."),
        };
}
