using System.Text;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace CodeAlta.Catalog.Skills;

// Used only by CodeAltaConfigStore for skill enablement. Leave every unrelated TOML token intact.
internal static class SkillConfigSyntax
{
    internal static string UpdateDisabledNames(string content, IReadOnlyList<string> names)
    {
        var syntax = SyntaxParser.ParseStrict(content);
        var value = new ArraySyntax(names.ToArray()).ToString();
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        foreach (var pair in syntax.KeyValues)
        {
            if (IsKey(pair.Key, "skills", "disabled"))
            {
                return ReplaceArray(content, pair.Value, value);
            }

            if (IsKey(pair.Key, "skills") && pair.Value is InlineTableSyntax inline)
            {
                foreach (var item in inline.Items)
                {
                    if (IsKey(item.KeyValue?.Key, "disabled"))
                    {
                        return ReplaceArray(content, item.KeyValue!.Value, value);
                    }
                }

                return content.Insert(inline.CloseBrace!.Span.Start.Offset,
                    (inline.Items.LastOrDefault() is { Comma: null } ? ", " : " ") + "disabled = " + value + " ");
            }
        }

        foreach (var table in syntax.Tables)
        {
            if (table is not TableSyntax || !IsKey(table.Name, "skills")) continue;
            foreach (var pair in table.Items)
            {
                if (IsKey(pair.Key, "disabled")) return ReplaceArray(content, pair.Value, value);
            }

            var offset = table.EndOfLineToken is { } endOfLine
                ? Math.Min(content.Length, endOfLine.Span.End.Offset + 1) : content.Length;
            return content.Insert(offset, (offset > 0 && content[offset - 1] != '\n' ? newline : string.Empty) + "disabled = " + value + newline);
        }

        // A root dotted key also works alongside other root skills.* keys and implicit [skills.*] tables.
        return "skills.disabled = " + value + newline + content;
    }

    private static string ReplaceArray(string content, ValueSyntax? oldValue, string newValue)
    {
        if (oldValue is not ArraySyntax array)
        {
            throw new InvalidDataException("skills.disabled must be a TOML array.");
        }

        // Remove only old values and commas, preserving whitespace and comments even inside the array.
        var removed = array.Items.SelectMany(static item => item.Comma is null
                ? new[] { item.Value!.Span } : new[] { item.Value!.Span, item.Comma.Span })
            .OrderByDescending(static span => span.Start.Offset);
        var builder = new StringBuilder(content);
        foreach (var span in removed) builder.Remove(span.Start.Offset, span.Length);
        builder.Insert(array.OpenBracket!.Span.End.Offset + 1, newValue[1..^1]);
        return builder.ToString();
    }

    private static bool IsKey(KeySyntax? key, params ReadOnlySpan<string> parts)
    {
        if (key?.Key is null || GetPart(key.Key) != parts[0]) return false;
        var dotted = key.DotKeys.ToArray();
        if (dotted.Length + 1 != parts.Length) return false;
        for (var i = 0; i < dotted.Length; i++)
        {
            if (GetPart(dotted[i].Key) != parts[i + 1]) return false;
        }

        return true;
    }

    private static string? GetPart(BareKeyOrStringValueSyntax? part)
        => part switch { BareKeySyntax bare => bare.Key?.Text, StringValueSyntax text => text.Value, _ => null };
}
