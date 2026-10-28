using System.Globalization;
using System.Text;

namespace CodeAlta.CodeMode;

/// <summary>The lines of a text a cut kept, numbered from 1 as <c>read_file</c> numbers them.</summary>
/// <param name="TotalLines">The lines of the whole text.</param>
/// <param name="HeadLastLine">The last line of the start that is shown, 0 when none is.</param>
/// <param name="TailFirstLine">The first line of the end that is shown.</param>
/// <param name="FirstCutLine">The first line left out, wholly or in part.</param>
/// <param name="LastCutLine">The last line left out, wholly or in part.</param>
public readonly record struct CodeModeExcerptLines(int TotalLines, int HeadLastLine, int TailFirstLine, int FirstCutLine, int LastCutLine)
{
    /// <summary>Gets the number of lines left out.</summary>
    public int CutLines => Math.Max(0, LastCutLine - FirstCutLine + 1);

    /// <summary>Gives the lines of a cut: the text shown is <c>text[..headLength]</c> and <c>text[tailStart..]</c>.</summary>
    /// <param name="text">The whole text.</param>
    /// <param name="headLength">The length of the start that is shown.</param>
    /// <param name="tailStart">Where the end that is shown starts.</param>
    /// <returns>The lines.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static CodeModeExcerptLines Of(string text, int headLength, int tailStart)
    {
        ArgumentNullException.ThrowIfNull(text);
        // The line breaks at the edges of the cut belong to the lines around it, not to what is left out.
        var first = headLength;
        while (first < tailStart && text[first] is '\n' or '\r')
        {
            first++;
        }

        var last = tailStart - 1;
        while (last >= first && text[last] is '\n' or '\r')
        {
            last--;
        }

        var total = text.Length == 0 ? 0 : LineOf(text, text[^1] == '\n' ? text.Length - 2 : text.Length - 1);
        return new CodeModeExcerptLines(
            total,
            headLength == 0 ? 0 : LineOf(text, headLength - 1),
            tailStart >= text.Length ? total + 1 : LineOf(text, tailStart),
            LineOf(text, first),
            LineOf(text, Math.Max(first, last)));
    }

    private static int LineOf(string text, int position)
        => position < 0 ? 1 : 1 + text.AsSpan(0, Math.Min(position, text.Length)).Count('\n');
}

/// <summary>
/// Cuts a Markdown text to its start and its end at whole lines, so that what is left still reads as Markdown: a block
/// of code cut open is closed at the end of the start, and opened again at the start of the end.
/// </summary>
public static class CodeModeMarkdownExcerpt
{
    /// <summary>Cuts a text longer than a budget: two thirds for its start, one third for its end, and a line between them.</summary>
    /// <param name="text">The text.</param>
    /// <param name="maxCharacters">The budget.</param>
    /// <param name="where">What the line between says of what is left out, such as "they are in the full result", or null.</param>
    /// <returns>The text, or its start and its end when it is longer than the budget.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static string Cut(string text, int maxCharacters, string? where) => Cut(text, maxCharacters, where, out _);

    /// <summary>
    /// Cuts a text longer than a budget, as <see cref="Cut(string, int, string?)"/> does, and gives the lines it kept: the
    /// line between the start and the end names the lines left out, so that a reader of the whole text reads only those.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="maxCharacters">The budget.</param>
    /// <param name="where">What the line between says of what is left out, such as "they are in the full result", or null.</param>
    /// <param name="lines">The lines kept and left out, or the default when the text is not cut.</param>
    /// <returns>The text, or its start and its end when it is longer than the budget.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static string Cut(string text, int maxCharacters, string? where, out CodeModeExcerptLines lines)
    {
        ArgumentNullException.ThrowIfNull(text);
        lines = default;
        if (text.Length <= maxCharacters)
        {
            return text;
        }

        var budget = Math.Max(0, maxCharacters);
        var headEnd = LineStartAtOrBefore(text, budget * 2 / 3);
        var tailStart = LineStartAtOrAfter(text, text.Length - (budget - budget * 2 / 3));
        if (tailStart < headEnd)
        {
            tailStart = headEnd;
        }

        var head = text[..headEnd].TrimEnd('\n', '\r');
        var tail = text[tailStart..];
        var builder = new StringBuilder(budget + 200);
        builder.Append(head);
        if (OpenFenceAt(text, headEnd) is { } open)
        {
            builder.Append('\n').Append(ClosingOf(open));
        }

        var leftOut = text.Length - head.Length - tail.Length;
        lines = CodeModeExcerptLines.Of(text, head.Length, tailStart);
        builder.Append("\n\n… lines ").Append(lines.FirstCutLine.ToString("N0", CultureInfo.InvariantCulture)).Append('–')
            .Append(lines.LastCutLine.ToString("N0", CultureInfo.InvariantCulture)).Append(" left out (")
            .Append(lines.CutLines.ToString("N0", CultureInfo.InvariantCulture)).Append(lines.CutLines == 1 ? " line, " : " lines, ")
            .Append(leftOut.ToString("N0", CultureInfo.InvariantCulture)).Append(" characters)");
        if (where is not null)
        {
            builder.Append(": ").Append(where);
        }

        builder.Append(" …\n\n");
        if (OpenFenceAt(text, tailStart) is { } reopened)
        {
            builder.Append(reopened).Append('\n');
        }

        return builder.Append(tail).ToString();
    }

    /// <summary>Gives a fence of backticks longer than any run of backticks a text has, at least three.</summary>
    /// <param name="text">The text the fence holds.</param>
    /// <returns>The fence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static string FenceFor(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int longest = 0, run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return new string('`', Math.Max(3, longest + 1));
    }

    // The start of the line that holds a position, or the position itself when the line starts too far back.
    private static int LineStartAtOrBefore(string text, int position)
    {
        var index = text.LastIndexOf('\n', Math.Clamp(position, 0, text.Length - 1));
        return index >= position / 2 ? index + 1 : WholeCharacter(text, position);
    }

    // A cut in the middle of a line never splits a surrogate pair.
    private static int WholeCharacter(string text, int position)
        => position > 0 && position < text.Length && char.IsHighSurrogate(text[position - 1]) ? position - 1 : position;

    private static int LineStartAtOrAfter(string text, int position)
    {
        position = Math.Clamp(position, 0, text.Length);
        var index = position < text.Length ? text.IndexOf('\n', position) : -1;
        return index >= 0 && index - position < Math.Max(200, (text.Length - position) / 2) ? index + 1 : WholeCharacter(text, position);
    }

    // The line that opened the block of code a position is in, or null when it is in none.
    private static string? OpenFenceAt(string text, int position)
    {
        string? open = null;
        var start = 0;
        while (start < position)
        {
            var end = text.IndexOf('\n', start);
            var line = (end < 0 || end > position ? text[start..position] : text[start..end]).TrimEnd('\r');
            if (Fence(line) is { } fence)
            {
                if (open is null)
                {
                    open = line.Trim();
                }
                else if (fence.Char == open[0] && fence.Length >= Fence(open)!.Value.Length && line.Trim().Length == fence.Length)
                {
                    open = null;
                }
            }

            if (end < 0)
            {
                break;
            }

            start = end + 1;
        }

        return open;
    }

    private static string ClosingOf(string open) => new(open[0], Fence(open)!.Value.Length);

    // A fence: three backticks or tildes or more, after at most three spaces.
    private static (char Char, int Length)? Fence(string line)
    {
        var trimmed = line.TrimStart(' ');
        if (line.Length - trimmed.Length > 3 || trimmed.Length < 3 || trimmed[0] is not ('`' or '~'))
        {
            return null;
        }

        var length = 0;
        while (length < trimmed.Length && trimmed[length] == trimmed[0])
        {
            length++;
        }

        return length >= 3 ? (trimmed[0], length) : null;
    }
}
