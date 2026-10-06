using System.Text.RegularExpressions;
using CodeAlta.Catalog;

namespace CodeAlta.Desktop.Rpc;

/// <summary>What a search through the files of a project looks for, and in which files.</summary>
/// <param name="Pattern">Matches inside one line.</param>
/// <param name="Include">The file is searched when this matches its path, or when it has no item.</param>
/// <param name="Exclude">The file is skipped when this matches its path.</param>
internal sealed record ProjectFileSearchQuery(Regex Pattern, ProjectFilePathFilter Include, ProjectFilePathFilter Exclude);

/// <summary>One match of a search inside a file.</summary>
/// <param name="Line">The 1-based line.</param>
/// <param name="Column">The 1-based column of the first matched UTF-16 unit.</param>
/// <param name="Length">The length of the match, in UTF-16 units.</param>
/// <param name="Text">The line, without its leading white space and cut around the match when it is long.</param>
/// <param name="Start">Where the match starts in <paramref name="Text"/>.</param>
/// <param name="Shown">How much of the match <paramref name="Text"/> holds.</param>
internal sealed record ProjectFileSearchMatch(int Line, int Column, int Length, string Text, int Start, int Shown);

/// <summary>
/// The text search of the project code editor: a literal text or a regular expression matched line by line,
/// in the files that the include and exclude globs select.
/// </summary>
internal static class ProjectFileSearch
{
    /// <summary>Longest text or expression searched for, and longest list of globs.</summary>
    internal const int MaximumQueryLength = 1024;

    /// <summary>Largest file searched, in bytes.</summary>
    internal const int MaximumFileBytes = 1024 * 1024;

    /// <summary>Matches reported by one search.</summary>
    internal const int MaximumMatches = 2000;

    /// <summary>Matches reported in one file.</summary>
    internal const int MaximumFileMatches = 200;

    /// <summary>Files with matches reported by one search.</summary>
    internal const int MaximumFiles = 500;

    /// <summary>Longest text shown for a match.</summary>
    internal const int MaximumPreviewLength = 240;

    // How long one line may take to match before the search is given up.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Builds a search and returns <c>ok</c>, or <c>invalid</c> for an empty or oversized request,
    /// <c>invalid_pattern</c> for an expression that does not parse and <c>invalid_glob</c> for a glob that does not.
    /// </summary>
    internal static string TryCreate(string? text, bool regex, bool matchCase, bool wholeWord, string? include, string? exclude, out ProjectFileSearchQuery? query)
    {
        query = null;
        if (text is not { Length: > 0 and <= MaximumQueryLength } || text.AsSpan().ContainsAny('\r', '\n', '\0')) return "invalid";
        if (include is { Length: > MaximumQueryLength } || exclude is { Length: > MaximumQueryLength }) return "invalid";
        var source = regex ? text : Regex.Escape(text);
        if (wholeWord) source = $@"(?<!\w)(?:{source})(?!\w)";
        Regex pattern;
        try
        {
            pattern = new Regex(source, RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), MatchTimeout);
        }
        catch (ArgumentException)
        {
            return "invalid_pattern";
        }

        if (!ProjectFilePathFilter.TryParse(include, out var included) || !ProjectFilePathFilter.TryParse(exclude, out var excluded)) return "invalid_glob";
        query = new(pattern, included, excluded);
        return "ok";
    }

    /// <summary>Whether a search looks into the file at a project-relative path.</summary>
    internal static bool Selects(ProjectFileSearchQuery query, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(query);
        return (query.Include.IsEmpty || query.Include.IsMatch(relativePath)) && !query.Exclude.IsMatch(relativePath);
    }

    /// <summary>The matches of a search in a text, at most <paramref name="maximum"/> of them.</summary>
    /// <param name="query">The search.</param>
    /// <param name="text">The whole text of a file.</param>
    /// <param name="maximum">The largest number of matches returned.</param>
    /// <param name="truncated">Whether the text holds more matches than were returned.</param>
    /// <exception cref="RegexMatchTimeoutException">A line took too long to match.</exception>
    internal static List<ProjectFileSearchMatch> Find(ProjectFileSearchQuery query, string text, int maximum, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(text);
        truncated = false;
        var matches = new List<ProjectFileSearchMatch>();
        var span = text.AsSpan();
        var number = 0;
        // Lines end where the editor ends them: at CR LF, LF or CR.
        while (true)
        {
            number++;
            var end = span.IndexOfAny('\r', '\n');
            var line = end < 0 ? span : span[..end];
            foreach (var match in query.Pattern.EnumerateMatches(line))
            {
                if (match.Length == 0) continue;
                if (matches.Count >= maximum)
                {
                    truncated = true;
                    return matches;
                }

                matches.Add(Preview(line, number, match.Index, match.Length));
            }

            if (end < 0) return matches;
            span = span[(end + (span[end] == '\r' && end + 1 < span.Length && span[end + 1] == '\n' ? 2 : 1))..];
        }
    }

    private static ProjectFileSearchMatch Preview(ReadOnlySpan<char> line, int number, int index, int length)
    {
        var leading = line.Length - line.TrimStart().Length;
        var start = Math.Min(leading, index);
        // A match far into a long line is shown with a little of what precedes it.
        if (index - start > MaximumPreviewLength / 3) start = index - MaximumPreviewLength / 6;
        // Not in the middle of a surrogate pair.
        if (start > 0 && start < line.Length && char.IsLowSurrogate(line[start])) start--;
        var end = Math.Min(line.Length, start + MaximumPreviewLength);
        if (end < line.Length && char.IsHighSurrogate(line[end - 1])) end--;
        var before = start > leading ? "…" : string.Empty;
        var text = string.Concat(before, line[start..end], end < line.Length ? "…" : string.Empty);
        return new(number, index + 1, length, text, before.Length + index - start, Math.Min(length, end - index));
    }
}
