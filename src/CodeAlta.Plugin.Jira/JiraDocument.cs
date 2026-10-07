using System.Text;
using System.Text.Json;

namespace CodeAlta.Plugin.Jira;

/// <summary>
/// Turns a text of Jira into Markdown. Jira keeps descriptions and comments in the Atlassian Document Format: a
/// tree of paragraphs, headings, lists, code blocks and marked text. What the format has that Markdown has not
/// (a panel, a media file, a status lozenge) is kept as its text, or left out when it has none.
/// </summary>
internal static class JiraDocument
{
    /// <summary>Gets the Markdown of a field that holds a document, a plain text, or nothing.</summary>
    public static string ToMarkdown(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        if (value.ValueKind != JsonValueKind.Object) return string.Empty;
        var text = new StringBuilder();
        Blocks(value, text, string.Empty);
        return text.ToString().Trim();
    }

    private static IEnumerable<JsonElement> Content(JsonElement node)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array ? content.EnumerateArray() : [];

    private static string Type(JsonElement node)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() ?? string.Empty : string.Empty;

    private static string? Attribute(JsonElement node, string name)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty("attrs", out var attributes) && attributes.ValueKind == JsonValueKind.Object
           && attributes.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null }
            : null;

    private static void Blocks(JsonElement parent, StringBuilder text, string indent)
    {
        foreach (var node in Content(parent)) Block(node, text, indent);
    }

    private static void Block(JsonElement node, StringBuilder text, string indent)
    {
        switch (Type(node))
        {
            case "paragraph":
                var line = Inline(node);
                if (line.Length > 0) text.Append(indent).Append(line.Replace("\n", "\n" + indent, StringComparison.Ordinal)).Append("\n\n");
                break;
            case "heading":
                var level = int.TryParse(Attribute(node, "level"), out var parsed) ? Math.Clamp(parsed, 1, 6) : 2;
                text.Append(indent).Append('#', level).Append(' ').Append(Inline(node)).Append("\n\n");
                break;
            case "bulletList":
            case "taskList":
                foreach (var item in Content(node)) Item(item, text, indent, Type(item) == "taskItem" ? Attribute(item, "state") == "DONE" ? "- [x] " : "- [ ] " : "- ");
                text.Append('\n');
                break;
            case "orderedList":
                var number = int.TryParse(Attribute(node, "order"), out var first) ? first : 1;
                foreach (var item in Content(node)) Item(item, text, indent, $"{number++}. ");
                text.Append('\n');
                break;
            case "codeBlock":
                var code = string.Concat(Content(node).Select(static part => part.TryGetProperty("text", out var value) ? value.GetString() : null));
                // A fence longer than any run of backticks in the code cannot be closed by the code.
                var fence = new string('`', Math.Max(3, LongestRun(code, '`') + 1));
                text.Append(indent).Append(fence).Append(Attribute(node, "language") ?? string.Empty).Append('\n')
                    .Append(indent).Append(code.Replace("\n", "\n" + indent, StringComparison.Ordinal)).Append('\n').Append(indent).Append(fence).Append("\n\n");
                break;
            case "blockquote":
            case "panel":
                var quoted = new StringBuilder();
                Blocks(node, quoted, string.Empty);
                foreach (var quotedLine in quoted.ToString().TrimEnd().Split('\n')) text.Append(indent).Append("> ").Append(quotedLine).Append('\n');
                text.Append('\n');
                break;
            case "rule":
                text.Append(indent).Append("---\n\n");
                break;
            case "table":
                Table(node, text, indent);
                break;
            case "mediaSingle":
            case "mediaGroup":
                break;
            default:
                // A block this does not know is read as what it holds.
                if (Content(node).Any()) Blocks(node, text, indent);
                break;
        }
    }

    private static void Item(JsonElement item, StringBuilder text, string indent, string mark)
    {
        var inner = new StringBuilder();
        if (Type(item) == "taskItem") inner.Append(Inline(item)).Append('\n');
        else Blocks(item, inner, string.Empty);
        var lines = inner.ToString().TrimEnd().Split('\n');
        var hanging = new string(' ', mark.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Length == 0 && index > 0) { text.Append('\n'); continue; }
            text.Append(indent).Append(index == 0 ? mark : hanging).Append(lines[index]).Append('\n');
        }
    }

    private static void Table(JsonElement table, StringBuilder text, string indent)
    {
        var first = true;
        foreach (var row in Content(table))
        {
            var cells = Content(row).Select(static cell =>
            {
                var inner = new StringBuilder();
                Blocks(cell, inner, string.Empty);
                return inner.ToString().Trim().Replace("\n\n", "<br>", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);
            }).ToArray();
            if (cells.Length == 0) continue;
            text.Append(indent).Append("| ").AppendJoin(" | ", cells).Append(" |\n");
            if (first) text.Append(indent).Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", cells.Length))).Append('\n');
            first = false;
        }

        text.Append('\n');
    }

    private static string Inline(JsonElement parent)
    {
        var text = new StringBuilder();
        foreach (var node in Content(parent))
        {
            switch (Type(node))
            {
                case "text":
                    text.Append(Marked(node));
                    break;
                case "hardBreak":
                    text.Append("  \n");
                    break;
                case "mention":
                    text.Append(Attribute(node, "text") ?? "@someone");
                    break;
                case "emoji":
                    text.Append(Attribute(node, "text") ?? Attribute(node, "shortName") ?? string.Empty);
                    break;
                case "inlineCard":
                case "blockCard":
                    if (Attribute(node, "url") is { } url) text.Append('<').Append(url).Append('>');
                    break;
                case "status":
                    text.Append('`').Append(Attribute(node, "text") ?? string.Empty).Append('`');
                    break;
                case "date":
                    if (long.TryParse(Attribute(node, "timestamp"), out var milliseconds)) text.Append(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    text.Append(Inline(node));
                    break;
            }
        }

        return text.ToString();
    }

    private static string Marked(JsonElement node)
    {
        var value = node.TryGetProperty("text", out var raw) ? raw.GetString() ?? string.Empty : string.Empty;
        if (!node.TryGetProperty("marks", out var marks) || marks.ValueKind != JsonValueKind.Array) return Escape(value);
        var kinds = marks.EnumerateArray().Select(Type).ToHashSet(StringComparer.Ordinal);
        if (kinds.Contains("code")) return "`" + value.Replace("`", "'", StringComparison.Ordinal) + "`";
        var text = Escape(value);
        // The marks go around the words, not around the spaces beside them: Markdown would not read them otherwise.
        var lead = text[..(text.Length - text.TrimStart().Length)];
        var tail = text[text.TrimEnd().Length..];
        var core = text.Trim();
        if (core.Length == 0) return text;
        if (kinds.Contains("strong")) core = "**" + core + "**";
        if (kinds.Contains("em")) core = "*" + core + "*";
        if (kinds.Contains("strike")) core = "~~" + core + "~~";
        var link = marks.EnumerateArray().FirstOrDefault(static mark => Type(mark) == "link");
        if (link.ValueKind == JsonValueKind.Object && Attribute(link, "href") is { } address && Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && uri.Scheme is "https" or "http" or "mailto") core = "[" + core + "](" + address.Replace(")", "%29", StringComparison.Ordinal) + ")";
        return lead + core + tail;
    }

    // What Markdown would read as a mark at this place is written as itself.
    private static string Escape(string value)
    {
        if (value.AsSpan().IndexOfAny("\\*_`[]<>") < 0) return value;
        var text = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            if (character is '\\' or '*' or '_' or '`' or '[' or ']' or '<' or '>') text.Append('\\');
            text.Append(character);
        }

        return text.ToString();
    }

    private static int LongestRun(string text, char character)
    {
        int longest = 0, run = 0;
        foreach (var current in text)
        {
            run = current == character ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }
}
