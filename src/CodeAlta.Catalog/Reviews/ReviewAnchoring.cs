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

    /// <summary>
    /// Places a thread in the current lines of its file: where its anchor is, the match nearest to its old place
    /// when the text moved, or its old place marked outdated when the text is gone.
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
        if (Matches(lines, anchor, thread.StartLine))
        {
            return thread.Outdated || thread.EndLine != thread.StartLine + anchor.Length - 1
                ? thread with { Outdated = false, EndLine = thread.StartLine + anchor.Length - 1 }
                : thread;
        }

        var best = -1;
        for (var start = 1; start + anchor.Length - 1 <= lines.Length; start++)
        {
            if (Matches(lines, anchor, start) && (best < 0 || Math.Abs(start - thread.StartLine) < Math.Abs(best - thread.StartLine)))
            {
                best = start;
            }
        }

        if (best < 0)
        {
            return thread.Outdated ? thread : thread with { Outdated = true };
        }

        return thread with { StartLine = best, EndLine = best + anchor.Length - 1, Outdated = false };
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
