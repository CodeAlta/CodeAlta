using System.Buffers;
using System.Text;
using CodeAlta.Agent.Diffing;
using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Agent.Runtime;

internal sealed class AgentTurnFileChangeTracker
{
    // Bound capture as well as output: directory mutations may include entire build/dependency trees.
    private const int MaximumFileSnapshotByteCount = 1024 * 1024;
    private const int MaximumCapturedTextCharacterCount = 8 * 1024 * 1024;
    private const int MaximumDiffCharacterCount = 1024 * 1024;
    private const string DiffOmissionNotice = "[CodeAlta: Remaining file diffs omitted because the 1,048,576-character diff display limit was reached.]\n";

    private readonly string _rootPath;
    private readonly Dictionary<string, FileChangeState> _changes = new(StringComparer.OrdinalIgnoreCase);
    private int _capturedTextCharacterCount;

    public AgentTurnFileChangeTracker(string? workingDirectory)
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
        var fileBuilder = new StringBuilder();
        foreach (var state in _changes.Values.OrderBy(static state => state.DisplayPath, StringComparer.OrdinalIgnoreCase))
        {
            if (state.Before is null || state.After is null || SnapshotsEqual(state.Before, state.After))
            {
                continue;
            }

            fileBuilder.Clear();
            AppendFileDiff(fileBuilder, state.DisplayPath, state.Before, state.After);
            if (fileBuilder.Length > MaximumDiffCharacterCount - builder.Length - DiffOmissionNotice.Length)
            {
                // Keep complete per-file diffs rather than cutting through a hunk or UTF-16 pair.
                builder.Append(DiffOmissionNotice);
                break;
            }

            builder.Append(fileBuilder);
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
        _changes.TryGetValue(fullPath, out var state);
        if (phase is ChangeCapturePhase.Before && state?.Before is not null)
        {
            return;
        }

        try
        {
            FileSnapshot snapshot;
            if (AgentFileTypeDetector.IsProbablyBinaryFile(fullPath))
            {
                snapshot = FileSnapshot.BinaryExists;
            }
            else
            {
                var replacedTextLength = phase is ChangeCapturePhase.After ? state?.After?.Text?.Length ?? 0 : 0;
                var remainingCharacters = MaximumCapturedTextCharacterCount - _capturedTextCharacterCount + replacedTextLength;
                snapshot = await ReadTextSnapshotAsync(fullPath, remainingCharacters, cancellationToken).ConfigureAwait(false);
            }

            SetSnapshot(fullPath, phase, snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CaptureMissing(fullPath, phase);
        }
    }

    private static async Task<FileSnapshot> ReadTextSnapshotAsync(
        string fullPath,
        int remainingCharacters,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumFileSnapshotByteCount)
        {
            return FileSnapshot.TextOmitted;
        }

        using var reader = new StreamReader(stream);
        var characterLimit = Math.Min(MaximumFileSnapshotByteCount, remainingCharacters);
        var builder = new StringBuilder();
        var buffer = ArrayPool<char>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                // Probe one character past the budget, including if the file grows after opening.
                var read = await reader.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, characterLimit - builder.Length + 1)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return new FileSnapshot(Exists: true, IsBinary: false, Text: builder.ToString());
                }

                if (read > characterLimit - builder.Length)
                {
                    return FileSnapshot.TextOmitted;
                }

                builder.Append(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private void CaptureMissing(string fullPath, ChangeCapturePhase phase)
    {
        SetSnapshot(fullPath, phase, FileSnapshot.Missing);
        if (phase is ChangeCapturePhase.After)
        {
            foreach (var state in _changes.Values.Where(state => IsSamePathOrChild(fullPath, state.FullPath)))
            {
                SetSnapshot(state.FullPath, phase, FileSnapshot.Missing);
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
            if (state.Before is null)
            {
                state.Before = snapshot;
                _capturedTextCharacterCount += snapshot.Text?.Length ?? 0;
            }

            return;
        }

        state.Before ??= FileSnapshot.Missing;
        _capturedTextCharacterCount -= state.After?.Text?.Length ?? 0;
        state.After = snapshot;
        _capturedTextCharacterCount += snapshot.Text?.Length ?? 0;
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
        // Unknown text must not be reported as equal: retain explicit inspection-omission evidence.
        => !before.IsTextOmitted && !after.IsTextOmitted &&
           before.Exists == after.Exists &&
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

        if (before.IsTextOmitted || after.IsTextOmitted)
        {
            builder.AppendLine("[CodeAlta: File content diff omitted because a snapshot capture limit was exceeded (1 MiB per file or 8,388,608 captured text characters).]");
            return;
        }

        if (before.IsBinary || after.IsBinary)
        {
            AppendBinaryDiff(builder, path, before, after);
            return;
        }

        var beforeText = before.Text ?? string.Empty;
        var afterText = after.Text ?? string.Empty;
        builder.Append(UnifiedDiffBuilder.CreateUnifiedDiff(
            beforeText,
            afterText,
            before.Exists ? $"a/{path}" : "/dev/null",
            after.Exists ? $"b/{path}" : "/dev/null",
            includeHeaderWhenTextEqual: true));
    }

    private static void AppendBinaryDiff(StringBuilder builder, string path, FileSnapshot before, FileSnapshot after)
    {
        var beforePath = before.Exists ? $"a/{path}" : "/dev/null";
        var afterPath = after.Exists ? $"b/{path}" : "/dev/null";
        builder.Append("Binary files ").Append(beforePath).Append(" and ").Append(afterPath).AppendLine(" differ");
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

    private sealed record FileSnapshot(bool Exists, bool IsBinary, string? Text, bool IsTextOmitted = false)
    {
        public static FileSnapshot Missing { get; } = new(Exists: false, IsBinary: false, Text: null);

        public static FileSnapshot BinaryExists { get; } = new(Exists: true, IsBinary: true, Text: null);

        public static FileSnapshot TextOmitted { get; } = new(Exists: true, IsBinary: false, Text: null, IsTextOmitted: true);
    }
}
