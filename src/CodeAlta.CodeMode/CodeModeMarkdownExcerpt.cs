using System.Globalization;
using System.Text;

namespace CodeAlta.CodeMode;

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
    public static string Cut(string text, int maxCharacters, string? where)
    {
        ArgumentNullException.ThrowIfNull(text);
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
        builder.Append("\n\n… ").Append(leftOut.ToString("N0", CultureInfo.InvariantCulture)).Append(" characters left out");
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
