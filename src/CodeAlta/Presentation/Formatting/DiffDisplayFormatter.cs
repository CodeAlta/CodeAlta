using System.Globalization;
using System.Text;
using CodeAlta.Models;
using CodeAlta.Presentation.Styling;
using XenoAtom.Ansi;

namespace CodeAlta.Presentation.Formatting;

internal static class DiffDisplayFormatter
{
    public static void AppendChangeCountsMarkup(StringBuilder builder, int additions, int deletions)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Append('[')
            .Append(UiPalette.GetToolStatusMarkup(ToolCallDisplayStatus.Completed))
            .Append("]+")
            .Append(additions.ToString(CultureInfo.InvariantCulture))
            .Append("[/] [")
            .Append(UiPalette.GetToolStatusMarkup(ToolCallDisplayStatus.Failed))
            .Append("]-")
            .Append(deletions.ToString(CultureInfo.InvariantCulture))
            .Append("[/]");
    }

    public static bool TryGetDiffStats(string? diffText, out int additions, out int deletions)
    {
        additions = 0;
        deletions = 0;
        if (string.IsNullOrWhiteSpace(diffText))
        {
            return false;
        }

        foreach (var line in SplitLines(diffText!))
        {
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('+'))
            {
                additions++;
            }
            else if (line.StartsWith('-'))
            {
                deletions++;
            }
        }

        return additions > 0 || deletions > 0;
    }

    public static string GetDiffLineMarkup(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        string? markup = line switch
        {
            _ when line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)
                => UiPalette.GetToolStatusMarkup(ToolCallDisplayStatus.Completed),
            _ when line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)
                => UiPalette.GetToolStatusMarkup(ToolCallDisplayStatus.Failed),
            _ when line.StartsWith("@@", StringComparison.Ordinal)
                => UiPalette.GetToolStatusMarkup(ToolCallDisplayStatus.Running),
            _ when line.StartsWith("diff --git", StringComparison.Ordinal)
                || line.StartsWith("--- ", StringComparison.Ordinal)
                || line.StartsWith("+++ ", StringComparison.Ordinal)
                || line.StartsWith("index ", StringComparison.Ordinal)
                || line.StartsWith("new file mode", StringComparison.Ordinal)
                || line.StartsWith("deleted file mode", StringComparison.Ordinal)
                => UiPalette.MutedMarkup,
            _ => null,
        };

        var escaped = AnsiMarkup.Escape(line);
        return string.IsNullOrWhiteSpace(markup)
            ? escaped
            : $"[{markup}]{escaped}[/]";
    }

    public static string CreateDiffCodeBlock(string diffText)
    {
        ArgumentNullException.ThrowIfNull(diffText);
        var fence = CreateFence(diffText);
        return new StringBuilder()
            .Append(fence).AppendLine("diff")
            .AppendLine(diffText.TrimEnd())
            .Append(fence)
            .ToString();
    }

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string CreateFence(string text)
    {
        var maxRun = 0;
        var currentRun = 0;
        foreach (var character in text)
        {
            if (character == '`')
            {
                currentRun++;
                maxRun = Math.Max(maxRun, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }

        return new string('`', Math.Max(3, maxRun + 1));
    }
}
