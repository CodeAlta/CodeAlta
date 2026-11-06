using System.Text;

namespace CodeAlta.Browser.Annotations;

/// <summary>
/// Texts of a page in the Markdown of a prompt: the timeline shows the prompt, so what a page names (a selector, the name
/// of a function, a text) must show as written, never as a link, an image or HTML.
/// </summary>
internal static class AnnotationMarkdown
{
    /// <summary>
    /// A text in a code span on one line, whatever backticks it holds: its fence is longer than any run of backticks in it,
    /// so that nothing in it closes the span. A line break would end the item of the list it is in, whatever the span.
    /// </summary>
    public static string Code(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = AnnotationRedaction.OneLine(text);
        var ticks = new string('`', LongestRun(text, '`') + 1);
        var pad = text.StartsWith('`') || text.EndsWith('`') ? " " : string.Empty;
        return ticks + pad + text + pad + ticks;
    }

    /// <summary>
    /// A text on one line whose code spans stay as they are, and where every other character of Markdown is escaped:
    /// only its spans, made with <see cref="Code"/>, format it.
    /// </summary>
    public static string Inline(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = AnnotationRedaction.OneLine(text);
        var builder = new StringBuilder(line.Length + 8);
        var index = 0;
        while (index < line.Length)
        {
            var character = line[index];
            if (character == '`')
            {
                // A run of backticks opens a code span when a run of the same length closes it, as in CommonMark; else it
                // is text.
                var run = RunAt(line, index);
                var close = ClosingRun(line, index + run, run);
                if (close >= 0)
                {
                    builder.Append(line, index, close + run - index);
                    index = close + run;
                    continue;
                }

                for (var tick = 0; tick < run; tick++) builder.Append("\\`");
                index += run;
                continue;
            }

            if (character is '\\' or '*' or '_' or '[' or ']' or '<' or '>' or '&' or '~' or '|') builder.Append('\\');
            builder.Append(character);
            index++;
        }

        return builder.ToString();
    }

    /// <summary>The length of the longest run of a character in a text.</summary>
    public static int LongestRun(string text, char character)
    {
        ArgumentNullException.ThrowIfNull(text);
        int longest = 0, run = 0;
        foreach (var current in text)
        {
            run = current == character ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    private static int RunAt(string text, int index)
    {
        var end = index;
        while (end < text.Length && text[end] == '`') end++;
        return end - index;
    }

    // The start of the first run of backticks of a length from an index, or -1.
    private static int ClosingRun(string text, int index, int length)
    {
        while (index < text.Length)
        {
            if (text[index] != '`')
            {
                index++;
                continue;
            }

            var run = RunAt(text, index);
            if (run == length) return index;
            index += run;
        }

        return -1;
    }
}
