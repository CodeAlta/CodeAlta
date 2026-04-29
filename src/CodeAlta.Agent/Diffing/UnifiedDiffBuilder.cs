using System.Globalization;
using System.Text;
using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.Model;

namespace CodeAlta.Agent.Diffing;

/// <summary>
/// Creates unified diffs using DiffPlex.
/// </summary>
public static class UnifiedDiffBuilder
{
    /// <summary>
    /// The default number of unchanged context lines to include before and after each diff hunk.
    /// </summary>
    public const int DefaultContextLineCount = 3;

    /// <summary>
    /// Creates a unified diff between two text values.
    /// </summary>
    /// <param name="oldText">The original text.</param>
    /// <param name="newText">The updated text.</param>
    /// <param name="oldLabel">The label to use for the original side of the diff.</param>
    /// <param name="newLabel">The label to use for the updated side of the diff.</param>
    /// <param name="contextLineCount">The number of unchanged context lines to include around each hunk.</param>
    /// <param name="includeHeaderWhenTextEqual">Whether to emit the diff headers when the text values are equal.</param>
    /// <returns>A unified diff, or an empty string when the text values are equal and <paramref name="includeHeaderWhenTextEqual" /> is <see langword="false" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="oldText" /> or <paramref name="newText" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="oldLabel" /> or <paramref name="newLabel" /> is empty or consists only of white-space characters.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="contextLineCount" /> is negative.</exception>
    public static string CreateUnifiedDiff(
        string oldText,
        string newText,
        string oldLabel,
        string newLabel,
        int contextLineCount = DefaultContextLineCount,
        bool includeHeaderWhenTextEqual = false)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(newLabel);
        ArgumentOutOfRangeException.ThrowIfNegative(contextLineCount);

        var diffResult = Differ.Instance.CreateDiffs(
            oldText,
            newText,
            ignoreWhiteSpace: false,
            ignoreCase: false,
            LineEndingsPreservingChunker.Instance);
        if (diffResult.DiffBlocks.Count == 0 && !includeHeaderWhenTextEqual)
        {
            return string.Empty;
        }

        var builder = new StringBuilder()
            .Append("--- ").AppendLine(oldLabel)
            .Append("+++ ").AppendLine(newLabel);
        if (diffResult.DiffBlocks.Count > 0)
        {
            var edits = BuildDiffPlexEdits(diffResult);
            AppendHunks(builder, edits, contextLineCount);
        }

        return builder.ToString();
    }

    private static IReadOnlyList<DiffEdit> BuildDiffPlexEdits(DiffResult diffResult)
    {
        var edits = new List<DiffEdit>();
        var oldCursor = 0;
        var newCursor = 0;
        foreach (var block in diffResult.DiffBlocks)
        {
            while (oldCursor < block.DeleteStartA && newCursor < block.InsertStartB)
            {
                edits.Add(new DiffEdit(' ', FormatDiffLineText(diffResult.PiecesOld[oldCursor])));
                oldCursor++;
                newCursor++;
            }

            oldCursor = block.DeleteStartA;
            newCursor = block.InsertStartB;

            for (var index = 0; index < block.DeleteCountA; index++)
            {
                edits.Add(new DiffEdit('-', FormatDiffLineText(diffResult.PiecesOld[oldCursor++])));
            }

            for (var index = 0; index < block.InsertCountB; index++)
            {
                edits.Add(new DiffEdit('+', FormatDiffLineText(diffResult.PiecesNew[newCursor++])));
            }
        }

        while (oldCursor < diffResult.PiecesOld.Count && newCursor < diffResult.PiecesNew.Count)
        {
            edits.Add(new DiffEdit(' ', FormatDiffLineText(diffResult.PiecesOld[oldCursor])));
            oldCursor++;
            newCursor++;
        }

        return edits;
    }

    private static string FormatDiffLineText(string value)
        => value.EndsWith("\r\n", StringComparison.Ordinal)
            ? value[..^2]
            : value.EndsWith('\r') || value.EndsWith('\n')
                ? value[..^1]
                : value;

    private static void AppendHunks(StringBuilder builder, IReadOnlyList<DiffEdit> edits, int contextLineCount)
    {
        var oldLineBefore = new int[edits.Count];
        var newLineBefore = new int[edits.Count];
        var oldLine = 1;
        var newLine = 1;
        for (var index = 0; index < edits.Count; index++)
        {
            oldLineBefore[index] = oldLine;
            newLineBefore[index] = newLine;
            if (edits[index].Kind is ' ' or '-')
            {
                oldLine++;
            }

            if (edits[index].Kind is ' ' or '+')
            {
                newLine++;
            }
        }

        var changeIndexes = edits
            .Select((edit, index) => edit.Kind == ' ' ? -1 : index)
            .Where(static index => index >= 0)
            .ToArray();
        var nextChangeCursor = 0;
        while (nextChangeCursor < changeIndexes.Length)
        {
            var hunkStart = contextLineCount == int.MaxValue
                ? 0
                : Math.Max(0, changeIndexes[nextChangeCursor] - contextLineCount);
            var hunkEnd = contextLineCount == int.MaxValue
                ? edits.Count - 1
                : AddContext(changeIndexes[nextChangeCursor], contextLineCount, edits.Count - 1);
            nextChangeCursor++;

            while (nextChangeCursor < changeIndexes.Length &&
                   (contextLineCount == int.MaxValue || changeIndexes[nextChangeCursor] <= AddContext(hunkEnd, contextLineCount, edits.Count - 1)))
            {
                hunkEnd = contextLineCount == int.MaxValue
                    ? edits.Count - 1
                    : AddContext(changeIndexes[nextChangeCursor], contextLineCount, edits.Count - 1);
                nextChangeCursor++;
            }

            var oldStart = oldLineBefore[hunkStart];
            var newStart = newLineBefore[hunkStart];
            var oldCount = 0;
            var newCount = 0;
            for (var index = hunkStart; index <= hunkEnd; index++)
            {
                if (edits[index].Kind is ' ' or '-')
                {
                    oldCount++;
                }

                if (edits[index].Kind is ' ' or '+')
                {
                    newCount++;
                }
            }

            builder.Append("@@ -")
                .Append(FormatRange(oldStart, oldCount))
                .Append(" +")
                .Append(FormatRange(newStart, newCount))
                .AppendLine(" @@");
            for (var index = hunkStart; index <= hunkEnd; index++)
            {
                builder.Append(edits[index].Kind).Append(edits[index].Line).AppendLine();
            }
        }
    }

    private static string FormatRange(int start, int count)
    {
        if (count == 0)
        {
            return $"{Math.Max(0, start - 1).ToString(CultureInfo.InvariantCulture)},0";
        }

        return count == 1
            ? start.ToString(CultureInfo.InvariantCulture)
            : $"{start.ToString(CultureInfo.InvariantCulture)},{count.ToString(CultureInfo.InvariantCulture)}";
    }

    private static int AddContext(int index, int contextLineCount, int max)
        => contextLineCount > max - index ? max : index + contextLineCount;

    private sealed record DiffEdit(char Kind, string Line);
}
