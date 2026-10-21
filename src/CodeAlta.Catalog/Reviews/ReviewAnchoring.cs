namespace CodeAlta.Catalog.Reviews;

/// <summary>
/// Keeps a thread on its lines while the file changes: the text of the lines it was written on is looked for
/// again in the file as it is now.
/// </summary>
internal static class ReviewAnchoring
{
    /// <summary>Splits a text into lines: line endings of any kind, a leading byte order mark dropped.</summary>
    internal static string[] SplitLines(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        // A final line ending does not start a line of its own.
        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>The text of lines of a file, as an anchor stores it.</summary>
    internal static string Excerpt(string[] lines, int startLine, int endLine)
        => string.Join('\n', lines.AsSpan(startLine - 1, endLine - startLine + 1).ToArray());

    /// <summary>The nearest line that is not blank above a range of lines, and the one below it, which find the range again once its text changed.</summary>
    internal static (string? Before, string? After) Context(string[] lines, int startLine, int endLine)
    {
        string? before = null, after = null;
        for (var index = startLine - 2; index >= 0 && before is null; index--)
        {
            if (!string.IsNullOrWhiteSpace(lines[index])) before = lines[index].TrimEnd();
        }

        for (var index = endLine; index < lines.Length && after is null; index++)
        {
            if (!string.IsNullOrWhiteSpace(lines[index])) after = lines[index].TrimEnd();
        }

        return (before, after);
    }

    /// <summary>
    /// Places a thread in the current lines of its file: where its anchor is, the match nearest to its old place
    /// when the text moved. When the text changed (an agent revised the lines it is about), the thread stays near
    /// its text, marked outdated: on the lines most like its anchor, else between the lines that were around it,
    /// else at its old place. It is never taken off the file: the user finds its replies where they commented.
    /// </summary>
    /// <param name="thread">The thread.</param>
    /// <param name="lines">The lines of the side of the file it is on; null when the file cannot be read.</param>
    /// <returns>The thread, the same instance when nothing changed.</returns>
    internal static ReviewThread Relocate(ReviewThread thread, string[]? lines)
    {
        if (thread.WholeFile)
        {
            return thread.Outdated == (lines is null) ? thread : thread with { Outdated = lines is null };
        }

        if (lines is null)
        {
            return thread.Outdated ? thread : thread with { Outdated = true };
        }

        var anchor = SplitLines(thread.Anchor);
        var found = Matches(lines, anchor, thread.StartLine) ? thread.StartLine : -1;
        for (var start = 1; found != thread.StartLine && start + anchor.Length - 1 <= lines.Length; start++)
        {
            if (Matches(lines, anchor, start) && (found < 0 || Math.Abs(start - thread.StartLine) < Math.Abs(found - thread.StartLine)))
            {
                found = start;
            }
        }

        if (found > 0)
        {
            // Found: the lines around it are where it is now.
            var (before, after) = Context(lines, found, found + anchor.Length - 1);
            var placed = thread with { StartLine = found, EndLine = found + anchor.Length - 1, Outdated = false, Before = before, After = after };
            return placed == thread ? thread : placed;
        }

        var (startLine, endLine) = Similar(lines, anchor, thread.StartLine) ?? Between(lines, thread, anchor.Length) ?? Clamp(lines.Length, thread.StartLine, thread.EndLine);
        return thread.Outdated && thread.StartLine == startLine && thread.EndLine == endLine
            ? thread
            : thread with { StartLine = startLine, EndLine = endLine, Outdated = true };
    }

    // The most similar lines are words in common: the line an agent revised keeps most of its words. Windows of
    // one line less to one line more than the anchor are tried, the nearest to its old place on a near tie.
    private const double SimilarEnough = 0.5;
    private const int MaximumComparisons = 4_000_000;

    private static (int Start, int End)? Similar(string[] lines, string[] anchor, int oldStart)
    {
        var wanted = Words(anchor);
        var total = wanted.Values.Sum();
        if (total == 0 || (long)lines.Length * (anchor.Length + 1) > MaximumComparisons)
        {
            return null;
        }

        var words = lines.Select(static line => Words([line])).ToArray();
        var candidates = new List<(int Start, int End, double Score)>();
        for (var size = Math.Max(1, anchor.Length - 1); size <= anchor.Length + 1; size++)
        {
            for (var start = 1; start + size - 1 <= lines.Length; start++)
            {
                if (string.IsNullOrWhiteSpace(lines[start - 1]) || string.IsNullOrWhiteSpace(lines[start + size - 2]))
                {
                    continue;
                }

                var common = 0;
                var count = 0;
                var used = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var index = start - 1; index < start + size - 1; index++)
                {
                    foreach (var (word, times) in words[index])
                    {
                        count += times;
                        var left = wanted.GetValueOrDefault(word) - used.GetValueOrDefault(word);
                        if (left <= 0) continue;
                        var taken = Math.Min(left, times);
                        used[word] = used.GetValueOrDefault(word) + taken;
                        common += taken;
                    }
                }

                var score = 2.0 * common / (total + count);
                if (score >= SimilarEnough) candidates.Add((start, start + size - 1, score));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var top = candidates.Max(static candidate => candidate.Score);
        var best = candidates.Where(candidate => candidate.Score >= top - 0.05)
            .OrderBy(candidate => Math.Abs(candidate.Start - oldStart)).ThenByDescending(static candidate => candidate.Score).ThenBy(static candidate => candidate.Start).First();
        return (best.Start, best.End);
    }

    private static Dictionary<string, int> Words(IEnumerable<string> lines)
    {
        var words = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var start = -1;
            for (var index = 0; index <= line.Length; index++)
            {
                var inWord = index < line.Length && char.IsLetterOrDigit(line[index]);
                if (inWord && start < 0) start = index;
                else if (!inWord && start >= 0)
                {
                    var word = line[start..index].ToLowerInvariant();
                    words[word] = words.GetValueOrDefault(word) + 1;
                    start = -1;
                }
            }
        }

        return words;
    }

    // Between the lines that were around the thread: what replaced its text. Text that was taken out leaves the
    // thread on the line that was above it.
    private static (int Start, int End)? Between(string[] lines, ReviewThread thread, int length)
    {
        static int Nearest(string[] lines, string? text, int from, int after)
        {
            var found = -1;
            if (text is null) return found;
            for (var line = after + 1; line <= lines.Length; line++)
            {
                if (lines[line - 1].AsSpan().TrimEnd().SequenceEqual(text) && (found < 0 || Math.Abs(line - from) < Math.Abs(found - from))) found = line;
            }

            return found;
        }

        var before = Nearest(lines, thread.Before, thread.StartLine - 1, 0);
        var after = Nearest(lines, thread.After, thread.EndLine + 1, Math.Max(before, 0));
        if (before < 0 && after < 0)
        {
            return null;
        }

        var start = before > 0 ? before + 1 : Math.Max(1, after - length);
        var end = after > 0 ? after - 1 : Math.Min(lines.Length, start + length - 1);
        while (start <= end && string.IsNullOrWhiteSpace(lines[start - 1])) start++;
        while (end >= start && string.IsNullOrWhiteSpace(lines[end - 1])) end--;
        if (start > end)
        {
            var line = Math.Max(1, before > 0 ? before : after);
            return (line, line);
        }

        // Far apart, the lines around it hold more than its text: it goes at the start of what is between them.
        return (start, Math.Min(end, start + Math.Max(length, 1) * 3 + 10));
    }

    private static (int Start, int End) Clamp(int count, int startLine, int endLine)
    {
        var last = Math.Max(1, count);
        var start = Math.Clamp(startLine, 1, last);
        return (start, Math.Clamp(endLine, start, last));
    }

    // Lines are equal when they are equal but for white space at their end.
    private static bool Matches(string[] lines, string[] anchor, int start)
    {
        if (start < 1 || start + anchor.Length - 1 > lines.Length)
        {
            return false;
        }

        for (var index = 0; index < anchor.Length; index++)
        {
            if (!lines[start - 1 + index].AsSpan().TrimEnd().SequenceEqual(anchor[index].AsSpan().TrimEnd()))
            {
                return false;
            }
        }

        return true;
    }
}
