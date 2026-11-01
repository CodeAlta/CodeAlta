using System.Text;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace CodeAlta.Catalog;

/// <summary>
/// Puts back the comments of the user that a save rebuilt from the table model of <c>config.toml</c> drops: the comments
/// on lines of their own, with the blank lines around them, above a table, above a setting, and at the end of the file.
/// The comments at the end of a line are kept by the save itself.
/// </summary>
/// <remarks>
/// The lines of comments above a table or a setting go back above the same table or setting of the new text. Those of a
/// setting the save removed (a value back to its default) go above the next one that is still there, and at the end of
/// the file when none is: a comment of the user is moved, never lost. The lines above an item are the blank lines and the
/// comments right above its first line: the line above them ends the previous value, so a line of a multi-line string or
/// array is never taken for one.
/// </remarks>
internal static class ConfigComments
{
    /// <summary>Gives the new text of a configuration with the comments its previous text had on lines of their own.</summary>
    /// <param name="previous">The text before the save.</param>
    /// <param name="saved">The text the save wrote, from the table model.</param>
    /// <returns>The saved text with the comments; the saved text as it is when either text cannot be read as TOML.</returns>
    internal static string Restore(string previous, string saved)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(saved);
        if (!previous.Contains('#', StringComparison.Ordinal)) return saved;
        DocumentSyntax before, after;
        try
        {
            before = SyntaxParser.Parse(previous, validate: false);
            after = SyntaxParser.Parse(saved, validate: false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The comments are a courtesy: the save goes on without them.
            return saved;
        }

        if (before.HasErrors || after.HasErrors) return saved;
        var newLine = saved.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var targets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, start) in Anchors(after)) targets.TryAdd(id, LineStart(saved, start));

        var edits = new List<(int Start, int End, string Text)>();
        var pending = new StringBuilder();
        foreach (var (id, start) in Anchors(before))
        {
            var lineStart = LineStart(previous, start);
            var lines = previous[CommentLinesStart(previous, lineStart)..lineStart];
            if (!targets.TryGetValue(id, out var target))
            {
                if (lines.Contains('#', StringComparison.Ordinal)) pending.Append(lines);
                continue;
            }

            var savedStart = CommentLinesStart(saved, target);
            // An item of the new text that has comments of its own keeps them; those carried from a removed setting go first.
            if (saved.AsSpan(savedStart, target - savedStart).Contains('#'))
            {
                if (pending.Length > 0) edits.Add((savedStart, savedStart, pending.ToString()));
            }
            else if (pending.Length > 0 || lines.Contains('#', StringComparison.Ordinal))
            {
                edits.Add((savedStart, target, pending.Append(lines).ToString()));
            }

            pending.Clear();
        }

        // The end of the file: the lines after the last item, after the comments of settings removed at the end.
        var tail = pending.Append(previous[TailStart(previous)..]).ToString();
        var savedTail = TailStart(saved);
        if (tail.Contains('#', StringComparison.Ordinal) && !saved.AsSpan(savedTail).Contains('#'))
        {
            var separator = saved.Length > 0 && saved[^1] != '\n' ? newLine : "";
            edits.Add((saved.Length, saved.Length, separator + tail));
        }

        if (edits.Count == 0) return saved;
        var restored = new StringBuilder(saved);
        foreach (var (start, end, text) in edits.OrderByDescending(static edit => edit.Start))
        {
            restored.Remove(start, end - start).Insert(start, NewLines(text, newLine));
        }

        var result = restored.ToString();
        // Only lines of comments changed; a text that would not read back as a configuration is not written.
        try
        {
            return SyntaxParser.Parse(result, validate: false).HasErrors ? saved : result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return saved;
        }
    }

    // The tables and the settings of a document, in their order, each with the offset of its first character and an id
    // that names it whatever its spelling: a table by its kind, its name and its rank among the tables of that name, a
    // setting by its table and its key.
    private static IEnumerable<(string Id, int Start)> Anchors(DocumentSyntax document)
    {
        for (var index = 0; index < document.KeyValues.ChildrenCount; index++)
        {
            if (document.KeyValues.GetChild(index) is KeyValueSyntax setting && KeyText(setting.Key) is { } key) yield return ($"=/{key}", setting.Span.Start.Offset);
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < document.Tables.ChildrenCount; index++)
        {
            if (document.Tables.GetChild(index) is not TableSyntaxBase table || KeyText(table.Name) is not { } name) continue;
            var kind = table is TableArraySyntax ? "[[]]" : "[]";
            var rank = seen.TryGetValue(kind + name, out var count) ? count : 0;
            seen[kind + name] = rank + 1;
            var tableId = $"{kind}{name}#{rank}";
            yield return (tableId, table.Span.Start.Offset);
            for (var item = 0; item < table.Items.ChildrenCount; item++)
            {
                if (table.Items.GetChild(item) is KeyValueSyntax setting && KeyText(setting.Key) is { } key) yield return ($"{tableId}/{key}", setting.Span.Start.Offset);
            }
        }
    }

    // A dotted key as its parts say it, bare or quoted alike: "a".b and a.b are the same key.
    private static string? KeyText(KeySyntax? key)
    {
        if (key is null || Part(key.Key) is not { } first) return null;
        var parts = new List<string> { first };
        for (var index = 0; index < key.DotKeys.ChildrenCount; index++)
        {
            if (key.DotKeys.GetChild(index) is not DottedKeyItemSyntax dotted || Part(dotted.Key) is not { } part) return null;
            parts.Add(part);
        }

        // Joined with a character a key part does not hold in a configuration.
        return string.Join('\u001f', parts);
    }

    private static string? Part(BareKeyOrStringValueSyntax? part) => part switch
    {
        BareKeySyntax bare => bare.Key?.Text,
        StringValueSyntax text => text.Value,
        _ => null,
    };

    // The start of the line of an offset.
    private static int LineStart(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        return offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
    }

    // The start of the blank lines and the lines of comments right above a line.
    private static int CommentLinesStart(string text, int lineStart)
    {
        var start = lineStart;
        while (start > 0)
        {
            var end = start - 1; // the '\n' that ends the line above
            var above = end == 0 ? 0 : text.LastIndexOf('\n', end - 1) + 1;
            var line = text.AsSpan(above, end - above).Trim();
            if (line.Length > 0 && line[0] != '#') break;
            start = above;
        }

        return start;
    }

    // The start of the blank lines and the lines of comments at the end of a text, whose last line may have no line break.
    private static int TailStart(string text)
    {
        if (text.Length == 0 || text[^1] == '\n') return CommentLinesStart(text, text.Length);
        var last = LineStart(text, text.Length);
        var line = text.AsSpan(last).Trim();
        return line.Length > 0 && line[0] != '#' ? text.Length : CommentLinesStart(text, last);
    }

    private static string NewLines(string text, string newLine)
    {
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return newLine == "\n" ? lf : lf.Replace("\n", newLine, StringComparison.Ordinal);
    }
}
