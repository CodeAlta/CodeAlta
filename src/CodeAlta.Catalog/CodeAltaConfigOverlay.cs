using Tomlyn;
using Tomlyn.Model;

namespace CodeAlta.Catalog;

/// <summary>
/// A configuration merged over the user's <c>config.toml</c> without being written to it: the content of
/// <see cref="InlineContentVariable"/>, or one <c>-c key=value</c> of the command line. Immutable.
/// </summary>
public sealed class CodeAltaConfigOverlay
{
    /// <summary>The environment variable that holds a TOML configuration merged over the user's file.</summary>
    public const string InlineContentVariable = "CODEALTA_CONFIG_CONTENT";

    private const string ValueKey = "value";

    private CodeAltaConfigOverlay(string source, string content)
    {
        Source = source;
        Content = content;
    }

    /// <summary>
    /// Gets what the user wrote it with, as messages name it: <c>CODEALTA_CONFIG_CONTENT</c>, or <c>-c</c> and the key
    /// of the setting.
    /// </summary>
    public string Source { get; }

    /// <summary>Gets the TOML content of the overlay.</summary>
    public string Content { get; }

    /// <summary>Creates an overlay from a TOML configuration.</summary>
    /// <param name="source">What the user wrote it with, as messages name it.</param>
    /// <param name="content">The TOML content; empty for an overlay that changes nothing.</param>
    /// <returns>The overlay.</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The content is not TOML.</exception>
    public static CodeAltaConfigOverlay FromContent(string source, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(content);
        var overlay = new CodeAltaConfigOverlay(source.Trim(), content);
        _ = overlay.ToTable();
        return overlay;
    }

    /// <summary>Creates the overlay of the content of <see cref="InlineContentVariable"/>.</summary>
    /// <param name="content">The value of the variable.</param>
    /// <returns>The overlay, or <see langword="null"/> when the variable is unset or blank.</returns>
    /// <exception cref="InvalidDataException">The content is not TOML.</exception>
    public static CodeAltaConfigOverlay? FromInlineContent(string? content)
        => string.IsNullOrWhiteSpace(content) ? null : FromContent(InlineContentVariable, content);

    /// <summary>
    /// Creates the overlay of one <c>-c key=value</c>: a dotted TOML key (<c>providers.anthropic.api_key_env</c>), then a
    /// TOML value (<c>"MY_KEY"</c>, <c>true</c>, <c>[1, 2]</c>, <c>{ enabled = false }</c>). A value that is not one TOML
    /// value is taken as the text it is, so <c>default_provider=anthropic</c> sets a string.
    /// </summary>
    /// <param name="assignment">The text after <c>-c</c>.</param>
    /// <returns>The overlay, whose <see cref="Source"/> is <c>-c</c> and the key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assignment"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The text has no <c>=</c>, or what is before it is not a TOML key.</exception>
    public static CodeAltaConfigOverlay FromAssignment(string assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var separator = FindAssignmentSeparator(assignment);
        if (separator < 0)
        {
            throw new ArgumentException($"-c {assignment}: a setting is written key=value.", nameof(assignment));
        }

        var key = assignment[..separator].Trim();
        var source = "-c " + key;
        if (key.Length == 0 || !TryParse($"{key} = 0", out var keyTable) || !TryFindLeaf(keyTable, out var parent, out var leaf))
        {
            throw new ArgumentException($"{source}: '{key}' is not a TOML key.", nameof(assignment));
        }

        var text = assignment[(separator + 1)..];
        parent[leaf] = TryParse($"{ValueKey} = {text}", out var valueTable) && valueTable.Count == 1
            && valueTable.TryGetValue(ValueKey, out var value) && value is not null
            ? value
            : text;
        return new CodeAltaConfigOverlay(source, TomlSerializer.Serialize(keyTable));
    }

    /// <summary>Parses the content again: the table is the caller's to change.</summary>
    /// <exception cref="InvalidDataException">The content is not TOML.</exception>
    internal TomlTable ToTable()
    {
        if (string.IsNullOrWhiteSpace(Content)) return new TomlTable();
        try
        {
            return TomlSerializer.Deserialize<TomlTable>(Content, TomlSerializerOptions.Default with { SourceName = Source }) ?? new TomlTable();
        }
        catch (TomlException ex)
        {
            throw new InvalidDataException($"Failed to parse the CodeAlta configuration of {Source}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Merges <paramref name="overlay"/> into <paramref name="target"/>: tables merge key by key, and the overlay wins on a
    /// value, an array or a value of another kind. A key matches its spelling first, then the same key in another case,
    /// as the store finds the keys of a file.
    /// </summary>
    /// <param name="target">The table changed.</param>
    /// <param name="overlay">The table merged; its tables become parts of <paramref name="target"/>.</param>
    internal static void Merge(TomlTable target, TomlTable overlay)
    {
        foreach (var (key, value) in overlay)
        {
            var existingKey = FindKey(target, key);
            if (existingKey is not null && target[existingKey] is TomlTable existing && value is TomlTable table)
            {
                Merge(existing, table);
                continue;
            }

            if (existingKey is not null && !string.Equals(existingKey, key, StringComparison.Ordinal)) target.Remove(existingKey);
            target[existingKey ?? key] = value;
        }
    }

    internal static string? FindKey(TomlTable table, string key)
    {
        if (table.ContainsKey(key)) return key;
        foreach (var pair in table)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Key;
        }

        return null;
    }

    // The first '=' outside the quotes of the key: a quoted key may hold one.
    private static int FindAssignmentSeparator(string assignment)
    {
        var quote = '\0';
        for (var index = 0; index < assignment.Length; index++)
        {
            var c = assignment[index];
            if (quote == '\0')
            {
                if (c == '=') return index;
                if (c is '"' or '\'') quote = c;
            }
            else if (quote == '"' && c == '\\')
            {
                index++;
            }
            else if (c == quote)
            {
                quote = '\0';
            }
        }

        return -1;
    }

    private static bool TryParse(string text, out TomlTable table)
    {
        try
        {
            table = TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
            return true;
        }
        catch (TomlException)
        {
            table = new TomlTable();
            return false;
        }
    }

    // A key alone parses to a chain of tables of one entry each: the table that holds the value, and its key. Anything
    // else (two settings, a table header) is not one key.
    private static bool TryFindLeaf(TomlTable root, out TomlTable parent, out string leaf)
    {
        parent = root;
        leaf = string.Empty;
        while (parent.Count == 1)
        {
            var (key, value) = parent.First();
            if (value is TomlTable child)
            {
                parent = child;
                continue;
            }

            leaf = key;
            return value is long;
        }

        return false;
    }
}
