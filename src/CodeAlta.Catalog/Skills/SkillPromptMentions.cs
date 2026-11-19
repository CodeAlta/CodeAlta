using System.Text.RegularExpressions;

namespace CodeAlta.Catalog.Skills;

/// <summary>
/// Finds the skills a prompt names as <c>/name</c>, as the composer of CodeAlta Desktop inserts them: a slash at the
/// start of the prompt, after white space or an opening bracket, then the name, ended by white space, a punctuation mark
/// or the end of the text. A path such as <c>src/app</c> or <c>/usr/bin</c> names nothing, nor does code (a fenced block,
/// a <c>`code span`</c>); the catalog decides which names are skills, and the others stay text.
/// </summary>
public static partial class SkillPromptMentions
{
    /// <summary>The longest name a mention can have: a skill name has at most 64 characters, one of a plugin more.</summary>
    public const int MaximumNameLength = 128;

    /// <summary>
    /// Gets the names a prompt mentions, in the order of their first mention, each once whatever its case.
    /// </summary>
    /// <param name="text">The prompt text.</param>
    /// <returns>The mentioned names, possibly empty.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('/', StringComparison.Ordinal)) return [];
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in MentionPattern().Matches(CodeMasked(text)))
        {
            var name = match.Groups["name"].Value;
            if (name.Length <= MaximumNameLength && seen.Add(name)) names.Add(name);
        }

        return names;
    }

    // The text with its code hidden, as the page hides it (skillMentions.ts): each character of a fenced block or of a code
    // span becomes U+0000, line breaks kept, so that no mention is found there.
    private static string CodeMasked(string text)
    {
        if (!text.Contains('`', StringComparison.Ordinal) && !text.Contains('~', StringComparison.Ordinal)) return text;
        var masked = text.ToCharArray();
        void Hide(int from, int to)
        {
            for (var at = from; at < to; at++)
                if (masked[at] != '\n') masked[at] = '\0';
        }

        (char Char, int Length)? fence = null;
        for (var start = 0; start <= text.Length;)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            var line = text[start..end];
            var marker = FenceMarker().Match(line);
            if (fence is { } open)
            {
                Hide(start, end);
                var run = marker.Groups["run"].Value;
                if (marker.Success && run[0] == open.Char && run.Length >= open.Length && string.IsNullOrWhiteSpace(line[marker.Length..])) fence = null;
            }
            else if (marker.Success && !(marker.Groups["run"].Value[0] == '`' && line[marker.Length..].Contains('`', StringComparison.Ordinal)))
            {
                fence = (marker.Groups["run"].Value[0], marker.Groups["run"].Length);
                Hide(start, end);
            }

            start = end + 1;
        }

        // A code span: a run of backticks to the next run of as many, outside the fenced blocks.
        for (var at = 0; at < masked.Length;)
        {
            if (masked[at] != '`')
            {
                at++;
                continue;
            }

            var run = at;
            while (run < masked.Length && masked[run] == '`') run++;
            var ticks = run - at;
            var close = run;
            while (true)
            {
                close = Array.IndexOf(masked, '`', close);
                if (close < 0) break;
                var after = close;
                while (after < masked.Length && masked[after] == '`') after++;
                if (after - close == ticks)
                {
                    Hide(at, after);
                    break;
                }

                close = after;
            }

            at = close < 0 ? run : close + ticks;
        }

        return new string(masked);
    }

    // A name starts and ends with a letter or a digit; `-`, `_`, `.` and `:` may join its parts (`code-review`, `plugin:skill`).
    [GeneratedRegex(@"(?<=^|[\s(\[{])/(?<name>[\p{L}\p{N}](?:[\p{L}\p{N}_.:-]*[\p{L}\p{N}])?)(?=$|[\s.,;:!?)\]}""'])", RegexOptions.CultureInvariant)]
    private static partial Regex MentionPattern();

    // The fence that opens or closes a block of code: three backticks or tildes or more, after at most three spaces.
    [GeneratedRegex(@"^ {0,3}(?<run>`{3,}|~{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex FenceMarker();
}
