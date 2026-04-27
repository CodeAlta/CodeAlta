using System.Text;
using CodeAlta.Agent.LocalRuntime.Tools;
using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.Model;

namespace CodeAlta.Agent.LocalRuntime;

internal sealed class LocalAgentTurnFileChangeTracker
{
    private const int UnifiedDiffContextLineCount = 3;

    private readonly string _rootPath;
    private readonly Dictionary<string, FileChangeState> _changes = new(StringComparer.OrdinalIgnoreCase);

    public LocalAgentTurnFileChangeTracker(string? workingDirectory)
    {
        _rootPath = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
    }

    public async Task CaptureBeforeAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        => await CaptureAsync(paths, ChangeCapturePhase.Before, cancellationToken).ConfigureAwait(false);

    public async Task CaptureAfterAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        => await CaptureAsync(paths, ChangeCapturePhase.After, cancellationToken).ConfigureAwait(false);

    public string? CreateUnifiedDiff()
    {
        var builder = new StringBuilder();
        foreach (var state in _changes.Values.OrderBy(static state => state.DisplayPath, StringComparer.OrdinalIgnoreCase))
        {
            if (state.Before is null || state.After is null || SnapshotsEqual(state.Before, state.After))
            {
                continue;
            }

            AppendFileDiff(builder, state.DisplayPath, state.Before, state.After);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private async Task CaptureAsync(
        IReadOnlyList<string> paths,
        ChangeCapturePhase phase,
        CancellationToken cancellationToken)
    {
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                await CaptureFileAsync(fullPath, phase, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (Directory.Exists(fullPath))
            {
                foreach (var filePath in EnumerateFiles(fullPath))
                {
                    await CaptureFileAsync(filePath, phase, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            CaptureMissing(fullPath, phase);
        }
    }

    private async Task CaptureFileAsync(
        string fullPath,
        ChangeCapturePhase phase,
        CancellationToken cancellationToken)
    {
        try
        {
            FileSnapshot snapshot;
            if (LocalAgentFileTypeDetector.IsProbablyBinaryFile(fullPath))
            {
                snapshot = FileSnapshot.BinaryExists;
            }
            else
            {
                var text = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
                snapshot = new FileSnapshot(Exists: true, IsBinary: false, Text: text);
            }

            SetSnapshot(fullPath, phase, snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CaptureMissing(fullPath, phase);
        }
    }

    private void CaptureMissing(string fullPath, ChangeCapturePhase phase)
    {
        SetSnapshot(fullPath, phase, FileSnapshot.Missing);
        if (phase is ChangeCapturePhase.After)
        {
            foreach (var state in _changes.Values.Where(state => IsSamePathOrChild(fullPath, state.FullPath)))
            {
                state.After = FileSnapshot.Missing;
            }
        }
    }

    private void SetSnapshot(string fullPath, ChangeCapturePhase phase, FileSnapshot snapshot)
    {
        if (!_changes.TryGetValue(fullPath, out var state))
        {
            state = new FileChangeState(fullPath, GetDisplayPath(fullPath));
            _changes[fullPath] = state;
        }

        if (phase is ChangeCapturePhase.Before)
        {
            state.Before ??= snapshot;
            return;
        }

        state.Before ??= FileSnapshot.Missing;
        state.After = snapshot;
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> childDirectories;
            IEnumerable<string> files;
            try
            {
                childDirectories = Directory.EnumerateDirectories(current).ToArray();
                files = Directory.EnumerateFiles(current).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var childDirectory in childDirectories)
            {
                pending.Push(childDirectory);
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private string GetDisplayPath(string fullPath)
    {
        var relativePath = Path.GetRelativePath(_rootPath, fullPath);
        if (!Path.IsPathRooted(relativePath) &&
            !string.Equals(relativePath, "..", StringComparison.Ordinal) &&
            !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return NormalizeDiffPath(relativePath);
        }

        return NormalizeDiffPath(fullPath);
    }

    private static string NormalizeDiffPath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static bool IsSamePathOrChild(string parentPath, string path)
    {
        if (string.Equals(parentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parentWithSeparator = parentPath.EndsWith(Path.DirectorySeparatorChar) || parentPath.EndsWith(Path.AltDirectorySeparatorChar)
            ? parentPath
            : parentPath + Path.DirectorySeparatorChar;
        return path.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SnapshotsEqual(FileSnapshot before, FileSnapshot after)
        => before.Exists == after.Exists &&
           before.IsBinary == after.IsBinary &&
           string.Equals(before.Text, after.Text, StringComparison.Ordinal);

    private static void AppendFileDiff(StringBuilder builder, string path, FileSnapshot before, FileSnapshot after)
    {
        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.AppendLine();
        }

        builder.Append("diff --git a/").Append(path).Append(" b/").Append(path).AppendLine();
        if (!before.Exists && after.Exists)
        {
            builder.AppendLine("new file mode 100644");
        }
        else if (before.Exists && !after.Exists)
        {
            builder.AppendLine("deleted file mode 100644");
        }

        if (before.IsBinary || after.IsBinary)
        {
            AppendBinaryDiff(builder, path, before, after);
            return;
        }

        builder.Append(before.Exists ? $"--- a/{path}" : "--- /dev/null").AppendLine();
        builder.Append(after.Exists ? $"+++ b/{path}" : "+++ /dev/null").AppendLine();

        var beforeText = before.Text ?? string.Empty;
        var afterText = after.Text ?? string.Empty;
        AppendUnifiedHunks(builder, beforeText, afterText);
    }

    private static void AppendBinaryDiff(StringBuilder builder, string path, FileSnapshot before, FileSnapshot after)
    {
        var beforePath = before.Exists ? $"a/{path}" : "/dev/null";
        var afterPath = after.Exists ? $"b/{path}" : "/dev/null";
        builder.Append("Binary files ").Append(beforePath).Append(" and ").Append(afterPath).AppendLine(" differ");
    }

    private static void AppendUnifiedHunks(StringBuilder builder, string beforeText, string afterText)
    {
        var diffResult = Differ.Instance.CreateDiffs(
            beforeText,
            afterText,
            ignoreWhiteSpace: false,
            ignoreCase: false,
            LineEndingsPreservingChunker.Instance);
        if (diffResult.DiffBlocks.Count == 0)
        {
            return;
        }

        var edits = BuildDiffPlexEdits(diffResult);
        AppendHunks(builder, edits);
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

    private static void AppendHunks(StringBuilder builder, IReadOnlyList<DiffEdit> edits)
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
            var hunkStart = Math.Max(0, changeIndexes[nextChangeCursor] - UnifiedDiffContextLineCount);
            var hunkEnd = Math.Min(edits.Count - 1, changeIndexes[nextChangeCursor] + UnifiedDiffContextLineCount);
            nextChangeCursor++;

            while (nextChangeCursor < changeIndexes.Length && changeIndexes[nextChangeCursor] <= hunkEnd + UnifiedDiffContextLineCount)
            {
                hunkEnd = Math.Min(edits.Count - 1, changeIndexes[nextChangeCursor] + UnifiedDiffContextLineCount);
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
            return $"{Math.Max(0, start - 1)},0";
        }

        return count == 1 ? start.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{start},{count}";
    }

    private enum ChangeCapturePhase
    {
        Before,
        After,
    }

    private sealed class FileChangeState(string fullPath, string displayPath)
    {
        public string FullPath { get; } = fullPath;

        public string DisplayPath { get; } = displayPath;

        public FileSnapshot? Before { get; set; }

        public FileSnapshot? After { get; set; }
    }

    private sealed record FileSnapshot(bool Exists, bool IsBinary, string? Text)
    {
        public static FileSnapshot Missing { get; } = new(Exists: false, IsBinary: false, Text: null);

        public static FileSnapshot BinaryExists { get; } = new(Exists: true, IsBinary: true, Text: null);
    }

    private sealed record DiffEdit(char Kind, string Line);
}
