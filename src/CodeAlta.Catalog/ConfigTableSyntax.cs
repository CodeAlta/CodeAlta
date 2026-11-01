using System.Text;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace CodeAlta.Catalog;

/// <summary>
/// Writes one setting of a table into the TOML text of a configuration, leaving every other token as it is: comments,
/// blank lines, the order of the tables and the settings that this version does not know.
/// </summary>
/// <remarks>
/// The table is the one of a <c>[header]</c>. Its keys are compared as TOML compares them, with their case. A missing
/// table is added at the end of the text; a table the text defines as an inline table or with dotted keys is refused.
/// Combine it with <see cref="CodeAltaConfigStore.EditConfigFile"/> to edit a file.
/// </remarks>
public static class ConfigTableSyntax
{
    /// <summary>Sets <c>key = true</c> or <c>key = false</c> in a table.</summary>
    /// <param name="content">The text of the configuration.</param>
    /// <param name="table">The path of the table, for example <c>["plugins", "mcp", "servers", "docs"]</c>.</param>
    /// <param name="key">The key in the table.</param>
    /// <param name="value">The value.</param>
    /// <returns>The new text; the same text when nothing changes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> or <paramref name="table"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="table"/> is empty, or <paramref name="key"/> is empty.</exception>
    /// <exception cref="InvalidDataException">The text is not valid TOML, or defines the table in a form that is not edited here.</exception>
    public static string SetValue(string content, IReadOnlyList<string> table, string key, bool value)
    {
        Validate(content, table, key);
        return Edit(content, table, key, value ? "true" : "false", createTable: true, StringComparison.Ordinal);
    }

    /// <summary>Sets <c>key = ["...", ...]</c> in a table, or removes the line of the key for an empty list.</summary>
    /// <param name="content">The text of the configuration.</param>
    /// <param name="table">The path of the table, for example <c>["plugins", "mcp", "servers", "docs"]</c>.</param>
    /// <param name="key">The key in the table.</param>
    /// <param name="values">The list; empty removes the key.</param>
    /// <returns>The new text; the same text when nothing changes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/>, <paramref name="table"/> or <paramref name="values"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="table"/> is empty, or <paramref name="key"/> is empty.</exception>
    /// <exception cref="InvalidDataException">The text is not valid TOML, or defines the table in a form that is not edited here.</exception>
    public static string SetValue(string content, IReadOnlyList<string> table, string key, IReadOnlyList<string> values)
    {
        Validate(content, table, key);
        ArgumentNullException.ThrowIfNull(values);
        return Edit(content, table, key, List(values), createTable: true, StringComparison.Ordinal);
    }

    /// <summary>Sets <c>key = "..."</c> in a table, or removes the line of the key.</summary>
    /// <param name="content">The text of the configuration.</param>
    /// <param name="table">The path of the table, for example <c>["azure_devops", "views", "Mine", "team"]</c>.</param>
    /// <param name="key">The key in the table.</param>
    /// <param name="value">The text, an empty one included; null removes the key.</param>
    /// <returns>The new text; the same text when nothing changes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> or <paramref name="table"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="table"/> is empty, or <paramref name="key"/> is empty.</exception>
    /// <exception cref="InvalidDataException">The text is not valid TOML, or defines the table in a form that is not edited here.</exception>
    public static string SetText(string content, IReadOnlyList<string> table, string key, string? value)
    {
        Validate(content, table, key);
        return Edit(content, table, key, value is null ? null : new StringValueSyntax(value).ToString(), createTable: true, StringComparison.Ordinal);
    }

    /// <summary>Sets <c>key = 12</c> or <c>key = 1.5</c> in a table, or removes the line of the key.</summary>
    /// <param name="content">The text of the configuration.</param>
    /// <param name="table">The path of the table.</param>
    /// <param name="key">The key in the table.</param>
    /// <param name="value">The number, written as a whole number when it is one; null removes the key.</param>
    /// <returns>The new text; the same text when nothing changes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> or <paramref name="table"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="table"/> is empty, <paramref name="key"/> is empty, or <paramref name="value"/> is not finite.</exception>
    /// <exception cref="InvalidDataException">The text is not valid TOML, or defines the table in a form that is not edited here.</exception>
    public static string SetNumber(string content, IReadOnlyList<string> table, string key, double? value)
    {
        Validate(content, table, key);
        if (value is { } number && !double.IsFinite(number)) throw new ArgumentException("The number is not finite.", nameof(value));
        var literal = value is not { } given ? null
            : given == Math.Floor(given) && Math.Abs(given) < 9e15 ? ((long)given).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : given.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return Edit(content, table, key, literal, createTable: true, StringComparison.Ordinal);
    }

    /// <summary>Sets <c>key = ["...", ...]</c> in a table, an empty list included, or removes the line of the key.</summary>
    /// <param name="content">The text of the configuration.</param>
    /// <param name="table">The path of the table.</param>
    /// <param name="key">The key in the table.</param>
    /// <param name="values">The list, which may be empty; null removes the key.</param>
    /// <returns>The new text; the same text when nothing changes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> or <paramref name="table"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="table"/> is empty, or <paramref name="key"/> is empty.</exception>
    /// <exception cref="InvalidDataException">The text is not valid TOML, or defines the table in a form that is not edited here.</exception>
    public static string SetList(string content, IReadOnlyList<string> table, string key, IReadOnlyList<string>? values)
    {
        Validate(content, table, key);
        return Edit(content, table, key, values is null ? null : values.Count == 0 ? "[]" : new ArraySyntax(values.ToArray()).ToString(), createTable: true, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes a <c>[header]</c> table and its settings, up to the next table or the end of the text. A table that is not
    /// written as a header leaves the text as it is.
    /// </summary>
    /// <param name="content">The text of the configuration.</param>
    /// <param name="table">The path of the table.</param>
    /// <returns>The new text; the same text when nothing changes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> or <paramref name="table"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="table"/> is empty.</exception>
    /// <exception cref="InvalidDataException">The text is not valid TOML.</exception>
    public static string RemoveTable(string content, IReadOnlyList<string> table)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(table);
        if (table.Count == 0) throw new ArgumentException("The path of the table is empty.", nameof(table));
        var syntax = Parse(content);
        var tables = syntax.Tables.ToList();
        for (var index = 0; index < tables.Count; index++)
        {
            if (tables[index] is not TableSyntax header || !IsPath(header.Name, table, StringComparison.Ordinal)) continue;
            var start = LineStart(content, header.OpenBracket!.Span.Start.Offset);
            var end = index + 1 < tables.Count ? LineStart(content, tables[index + 1].OpenBracket!.Span.Start.Offset) : content.Length;
            // The comment lines right above the next header are about it: they stay.
            while (end > start && LineStart(content, end - 1) is var previous && content.AsSpan(previous, end - previous).TrimStart().StartsWith("#")) end = previous;
            return content.Remove(start, end - start);
        }

        return content;
    }

    /// <summary>Gets where the first <c>[header]</c> of a table or of a table under it starts: the start of its line.</summary>
    /// <param name="content">The text of the configuration.</param>
    /// <param name="table">The path of the table.</param>
    /// <returns>The offset of the line of the header; null when no header names the table or a table under it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> or <paramref name="table"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="table"/> is empty.</exception>
    /// <exception cref="InvalidDataException">The text is not valid TOML.</exception>
    public static int? TableStart(string content, IReadOnlyList<string> table)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(table);
        if (table.Count == 0) throw new ArgumentException("The path of the table is empty.", nameof(table));
        var found = Parse(content).Tables.OfType<TableSyntax>().FirstOrDefault(header => Path(header.Name) is { } path && path.Count >= table.Count
            && path.Take(table.Count).SequenceEqual(table, StringComparer.Ordinal));
        return found is null ? null : LineStart(content, found.OpenBracket!.Span.Start.Offset);
    }

    /// <summary>Removes a <c>[header]</c> table and every table under it (<c>[a.b]</c> and <c>[a.b.c]</c> for <c>a.b</c>), with their settings.</summary>
    /// <param name="content">The text of the configuration.</param>
    /// <param name="table">The path of the table.</param>
    /// <returns>The new text; the same text when nothing changes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> or <paramref name="table"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="table"/> is empty.</exception>
    /// <exception cref="InvalidDataException">The text is not valid TOML.</exception>
    public static string RemoveTables(string content, IReadOnlyList<string> table)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(table);
        if (table.Count == 0) throw new ArgumentException("The path of the table is empty.", nameof(table));
        // One table at a time, from the last: the offsets of those before stay true.
        for (;;)
        {
            var found = Parse(content).Tables.OfType<TableSyntax>().LastOrDefault(header => Path(header.Name) is { } path && path.Count >= table.Count
                && path.Take(table.Count).SequenceEqual(table, StringComparer.Ordinal));
            if (found is null) return content;
            var next = RemoveTable(content, Path(found.Name)!);
            if (next == content) return content;
            content = next;
        }
    }

    /// <summary>Adds an empty <c>[header]</c> table at the end of the text when the text does not define it.</summary>
    /// <param name="content">The text of the configuration.</param>
    /// <param name="table">The path of the table.</param>
    /// <returns>The new text; the same text when the table is there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> or <paramref name="table"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="table"/> is empty.</exception>
    /// <exception cref="InvalidDataException">The text is not valid TOML.</exception>
    public static string EnsureTable(string content, IReadOnlyList<string> table)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(table);
        if (table.Count == 0) throw new ArgumentException("The path of the table is empty.", nameof(table));
        if (Parse(content).Tables.OfType<TableSyntax>().Any(header => IsPath(header.Name, table, StringComparison.Ordinal))) return content;
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var separator = content.Length == 0 ? string.Empty : content.EndsWith('\n') ? newline : newline + newline;
        return content + separator + "[" + string.Join('.', table.Select(KeyText)) + "]" + newline;
    }

    // The path a header names, its parts unquoted; null for a header this class does not read.
    private static IReadOnlyList<string>? Path(KeySyntax? key)
    {
        if (key?.Key is null || Part(key.Key) is not { } first) return null;
        var parts = new List<string> { first };
        foreach (var dotted in key.DotKeys)
        {
            if (Part(dotted.Key) is not { } part) return null;
            parts.Add(part);
        }

        return parts;
    }

    private static int LineStart(string content, int offset)
    {
        var newline = content.LastIndexOf('\n', Math.Max(0, offset - 1));
        return offset == 0 ? 0 : newline < 0 ? 0 : newline + 1;
    }

    private static DocumentSyntax Parse(string content)
    {
        try
        {
            return SyntaxParser.ParseStrict(content);
        }
        catch (Exception exception) when (exception is TomlException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The configuration is not valid TOML.", exception);
        }
    }

    /// <summary>Sets or removes <c>key = [...]</c> in the table <paramref name="table"/>, its keys compared without case.</summary>
    /// <param name="content">The text of the configuration.</param>
    /// <param name="table">The path of the table.</param>
    /// <param name="key">The key in the table.</param>
    /// <param name="values">The list; empty removes the key.</param>
    /// <param name="createTable">Whether a missing table may be added at the end of the file.</param>
    /// <returns>The new text; the same text when nothing changes.</returns>
    /// <exception cref="InvalidDataException">The text is not valid TOML, or defines the table in a form that is not edited here.</exception>
    /// <exception cref="InvalidOperationException">The table is missing and may not be created.</exception>
    internal static string Set(string content, IReadOnlyList<string> table, string key, IReadOnlyList<string> values, bool createTable)
        => Edit(content, table, key, List(values), createTable, StringComparison.OrdinalIgnoreCase);

    private static void Validate(string content, IReadOnlyList<string> table, string key)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(table);
        if (table.Count == 0) throw new ArgumentException("The path of the table is empty.", nameof(table));
        ArgumentException.ThrowIfNullOrEmpty(key);
    }

    private static string? List(IReadOnlyList<string> values)
        => values.Count == 0 ? null : new ArraySyntax(values.ToArray()).ToString();

    // A null value removes the key.
    private static string Edit(string content, IReadOnlyList<string> table, string key, string? value, bool createTable, StringComparison comparison)
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
        var line = KeyText(key) + " = " + value;
        foreach (var candidate in syntax.Tables)
        {
            if (candidate is not TableSyntax header || !IsPath(header.Name, table, comparison)) continue;
            foreach (var pair in header.Items)
            {
                if (!IsPath(pair.Key, [key], comparison)) continue;
                return value is null ? RemoveLine(content, pair) : Replace(content, pair.Value!, value);
            }

            if (value is null) return content;
            // After the line of the last setting of the table (its comment included), else after the header.
            var after = header.Items.LastOrDefault()?.Value is { } last ? last.Span.End.Offset : header.CloseBracket!.Span.End.Offset;
            var newlineAt = content.IndexOf('\n', Math.Min(content.Length, after + 1));
            var end = newlineAt < 0 ? content.Length : newlineAt + 1;
            return content.Insert(end, (end > 0 && content[end - 1] != '\n' ? newline : string.Empty) + line + newline);
        }

        if (value is null) return content;
        if (Defined(content, table, comparison))
            throw new InvalidDataException($"[{string.Join('.', table.Select(KeyText))}] is written in a form CodeAlta does not edit: change it by hand, or make it a table.");
        if (!createTable)
            throw new InvalidOperationException($"[{string.Join('.', table.Select(KeyText))}] is not in the configuration.");
        var separator = content.Length == 0 ? string.Empty : content.EndsWith('\n') ? newline : newline + newline;
        return content + separator + "[" + string.Join('.', table.Select(KeyText)) + "]" + newline + line + newline;
    }

    // Whether the table exists in the file through another form: an inline table or dotted keys.
    private static bool Defined(string content, IReadOnlyList<string> table, StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        object? current = TomlSerializer.Deserialize<TomlTable>(content);
        foreach (var part in table)
        {
            if (current is not TomlTable parent) return false;
            current = parent.FirstOrDefault(pair => string.Equals(pair.Key, part, comparison)).Value;
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

    private static bool IsPath(KeySyntax? key, IReadOnlyList<string> parts, StringComparison comparison)
    {
        if (key?.Key is null || !string.Equals(Part(key.Key), parts[0], comparison)) return false;
        var dotted = key.DotKeys.ToArray();
        if (dotted.Length + 1 != parts.Count) return false;
        for (var i = 0; i < dotted.Length; i++)
        {
            if (!string.Equals(Part(dotted[i].Key), parts[i + 1], comparison)) return false;
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
