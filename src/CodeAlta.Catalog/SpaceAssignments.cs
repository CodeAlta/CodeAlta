using System.Collections.Frozen;
using System.Text;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

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

// Writes one list of spaces into the TOML text of a configuration, leaving every other token as it is: comments,
// blank lines, the order of the tables and the settings that this version does not know.
internal static class SpaceAssignmentsSyntax
{
    /// <summary>Sets or removes <c>key = [...]</c> in the table <paramref name="table"/>.</summary>
    /// <param name="content">The text of the configuration.</param>
    /// <param name="table">The path of the table.</param>
    /// <param name="key">The key in the table.</param>
    /// <param name="values">The list; empty removes the key.</param>
    /// <param name="createTable">Whether a missing table may be added at the end of the file.</param>
    /// <returns>The new text; the same text when nothing changes.</returns>
    /// <exception cref="InvalidDataException">The text is not valid TOML, or defines the table in a form that is not edited here.</exception>
    /// <exception cref="InvalidOperationException">The table is missing and may not be created.</exception>
    internal static string Set(string content, IReadOnlyList<string> table, string key, IReadOnlyList<string> values, bool createTable)
    {
        DocumentSyntax syntax;
        try
        {
            syntax = SyntaxParser.ParseStrict(content);
        }
        catch (Exception exception) when (exception is TomlException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The configuration is not valid TOML.", exception);
        }

        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var line = KeyText(key) + " = " + new ArraySyntax(values.ToArray()).ToString();
        foreach (var candidate in syntax.Tables)
        {
            if (candidate is not TableSyntax header || !IsPath(header.Name, table)) continue;
            foreach (var pair in header.Items)
            {
                if (!IsPath(pair.Key, [key])) continue;
                return values.Count == 0 ? RemoveLine(content, pair) : Replace(content, pair.Value!, line[(line.IndexOf(" = ", StringComparison.Ordinal) + 3)..]);
            }

            if (values.Count == 0) return content;
            // After the line of the last setting of the table (its comment included), else after the header.
            var after = header.Items.LastOrDefault()?.Value is { } last ? last.Span.End.Offset : header.CloseBracket!.Span.End.Offset;
            var newlineAt = content.IndexOf('\n', Math.Min(content.Length, after + 1));
            var end = newlineAt < 0 ? content.Length : newlineAt + 1;
            return content.Insert(end, (end > 0 && content[end - 1] != '\n' ? newline : string.Empty) + line + newline);
        }

        if (values.Count == 0) return content;
        if (Defined(content, table))
            throw new InvalidDataException($"[{string.Join('.', table.Select(KeyText))}] is written in a form CodeAlta does not edit: write its spaces by hand, or make it a table.");
        if (!createTable)
            throw new InvalidOperationException($"[{string.Join('.', table.Select(KeyText))}] is not in the configuration.");
        var separator = content.Length == 0 ? string.Empty : content.EndsWith('\n') ? newline : newline + newline;
        return content + separator + "[" + string.Join('.', table.Select(KeyText)) + "]" + newline + line + newline;
    }

    // Whether the table exists in the file through another form: an inline table or dotted keys.
    private static bool Defined(string content, IReadOnlyList<string> table)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        object? current = TomlSerializer.Deserialize<TomlTable>(content);
        foreach (var part in table)
        {
            if (current is not TomlTable parent) return false;
            current = parent.FirstOrDefault(pair => string.Equals(pair.Key, part, StringComparison.OrdinalIgnoreCase)).Value;
        }

        return current is not null;
    }

    private static string Replace(string content, ValueSyntax value, string text)
        => content.Remove(value.Span.Start.Offset, value.Span.Length).Insert(value.Span.Start.Offset, text);

    // The whole line of the setting, its end-of-line comment included.
    private static string RemoveLine(string content, KeyValueSyntax pair)
    {
        var start = pair.Key!.Span.Start.Offset;
        while (start > 0 && content[start - 1] is ' ' or '\t') start--;
        var end = pair.EndOfLineToken is { } endOfLine ? Math.Min(content.Length, endOfLine.Span.End.Offset + 1) : content.Length;
        return content.Remove(start, end - start);
    }

    private static bool IsPath(KeySyntax? key, IReadOnlyList<string> parts)
    {
        if (key?.Key is null || !string.Equals(Part(key.Key), parts[0], StringComparison.OrdinalIgnoreCase)) return false;
        var dotted = key.DotKeys.ToArray();
        if (dotted.Length + 1 != parts.Count) return false;
        for (var i = 0; i < dotted.Length; i++)
        {
            if (!string.Equals(Part(dotted[i].Key), parts[i + 1], StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    private static string? Part(BareKeyOrStringValueSyntax? part)
        => part switch { BareKeySyntax bare => bare.Key?.Text, StringValueSyntax text => text.Value, _ => null };

    // A key as TOML writes it: bare when it can be, quoted otherwise.
    internal static string KeyText(string key)
    {
        if (key.Length > 0 && key.All(static ch => ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'))
            return key;
        var builder = new StringBuilder("\"");
        foreach (var ch in key)
        {
            switch (ch)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case < ' ' or '\u007f': builder.Append("\\u").Append(((int)ch).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)); break;
                default: builder.Append(ch); break;
            }
        }

        return builder.Append('"').ToString();
    }
}
