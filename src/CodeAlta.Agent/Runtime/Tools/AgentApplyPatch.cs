using System.Text;

namespace CodeAlta.Agent.Runtime.Tools;

internal static class AgentApplyPatch
{
    public static AgentToolResult Apply(string input, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (!TryParse(input, out var document, out var error))
        {
            return new AgentToolResult(false, [new AgentToolResultItem.Text(error)], error);
        }

        var rootPath = Path.GetFullPath(workingDirectory);
        var patchNewline = DetectPatchNewline(input);
        var summaries = new List<string>(document.Operations.Count);

        foreach (var operation in document.Operations)
        {
            switch (operation)
            {
                case AddFileOperation addFile:
                {
                    var path = ResolvePatchPath(rootPath, addFile.Path);
                    if (File.Exists(path) || Directory.Exists(path))
                    {
                        return Failure($"Cannot add '{addFile.Path}' because it already exists.");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                    // Added files have no existing newline convention to inherit from, so use the
                    // patch envelope's newline style. This keeps the tool deterministic for agents.
                    var content = JoinLines(addFile.Lines, patchNewline, hadTrailingNewline: addFile.Lines.Count > 0);
                    File.WriteAllText(path, content);
                    summaries.Add($"A {addFile.Path}");
                    break;
                }
                case DeleteFileOperation deleteFile:
                {
                    var path = ResolvePatchPath(rootPath, deleteFile.Path);
                    if (Directory.Exists(path))
                    {
                        return Failure($"Cannot delete '{deleteFile.Path}' because it is a directory.");
                    }

                    if (!File.Exists(path))
                    {
                        return Failure($"Cannot delete '{deleteFile.Path}' because it does not exist.");
                    }

                    if (AgentFileTypeDetector.IsProbablyBinaryFile(path))
                    {
                        return Failure($"Cannot delete '{deleteFile.Path}' with apply_patch because it appears to be a binary file. Use delete_file_or_dir instead.");
                    }

                    File.Delete(path);
                    summaries.Add($"D {deleteFile.Path}");
                    break;
                }
                case UpdateFileOperation updateFile:
                {
                    var sourcePath = ResolvePatchPath(rootPath, updateFile.Path);
                    if (Directory.Exists(sourcePath))
                    {
                        return Failure($"Cannot update '{updateFile.Path}' because it is a directory.");
                    }

                    if (!File.Exists(sourcePath))
                    {
                        return Failure($"Cannot update '{updateFile.Path}' because it does not exist.");
                    }

                    if (AgentFileTypeDetector.IsProbablyBinaryFile(sourcePath))
                    {
                        return Failure($"Cannot update '{updateFile.Path}' with apply_patch because it appears to be a binary file.");
                    }

                    var originalText = File.ReadAllText(sourcePath);
                    string updatedText;
                    if (updateFile.Hunks.Count == 0)
                    {
                        updatedText = originalText;
                    }
                    else
                    {
                        var newline = DetectNewline(originalText);
                        var lines = SplitLines(originalText);
                        if (!TryApplyHunks(lines, updateFile.Hunks, out var updatedLines, out error))
                        {
                            return Failure($"Failed to apply patch to '{updateFile.Path}': {error}");
                        }

                        updatedText = JoinLines(updatedLines, newline, hadTrailingNewline: updatedLines.Count > 0);
                    }

                    if (updateFile.MoveTo is null)
                    {
                        File.WriteAllText(sourcePath, updatedText);
                        summaries.Add($"M {updateFile.Path}");
                        break;
                    }

                    var destinationPath = ResolvePatchPath(rootPath, updateFile.MoveTo);
                    if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                    {
                        return Failure($"Cannot move '{updateFile.Path}' to '{updateFile.MoveTo}' because the destination already exists.");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    if (updateFile.Hunks.Count == 0)
                    {
                        File.Move(sourcePath, destinationPath);
                    }
                    else
                    {
                        File.WriteAllText(destinationPath, updatedText);
                        File.Delete(sourcePath);
                    }

                    summaries.Add($"R {updateFile.Path} -> {updateFile.MoveTo}");
                    break;
                }
                default:
                    return Failure($"Unsupported patch operation '{operation.GetType().Name}'.");
            }
        }

        var summaryText = summaries.Count == 0
            ? "Patch applied with no file changes."
            : $"Patch applied:{Environment.NewLine}{string.Join(Environment.NewLine, summaries)}";
        return new AgentToolResult(true, [new AgentToolResultItem.Text(summaryText)]);
    }

    public static IReadOnlyList<string> GetTouchedPaths(string input, string workingDirectory)
    {
        if (!TryParse(input, out var document, out _))
        {
            return [];
        }

        var rootPath = Path.GetFullPath(workingDirectory);
        var paths = new List<string>();
        foreach (var operation in document.Operations)
        {
            switch (operation)
            {
                case AddFileOperation addFile:
                    paths.Add(ResolvePatchPath(rootPath, addFile.Path));
                    break;
                case DeleteFileOperation deleteFile:
                    paths.Add(ResolvePatchPath(rootPath, deleteFile.Path));
                    break;
                case UpdateFileOperation updateFile:
                    paths.Add(ResolvePatchPath(rootPath, updateFile.Path));
                    if (updateFile.MoveTo is not null)
                    {
                        paths.Add(ResolvePatchPath(rootPath, updateFile.MoveTo));
                    }

                    break;
            }
        }

        return paths;
    }

    private static AgentToolResult Failure(string message)
        => new(false, [new AgentToolResultItem.Text(message)], message);

    private static string ResolvePatchPath(string workingDirectory, string patchPath)
    {
        if (string.IsNullOrWhiteSpace(patchPath))
        {
            throw new InvalidOperationException("Patch paths must not be empty.");
        }

        // Resolve relative patch paths from the session working directory, but do not confine
        // edits to that directory. Agents sometimes need to update sibling checkouts or shared
        // local references using paths such as ../Library/File.cs.
        return Path.GetFullPath(Path.IsPathRooted(patchPath)
            ? patchPath
            : Path.Combine(workingDirectory, patchPath));
    }

    private static bool TryApplyHunks(
        List<string> lines,
        IReadOnlyList<PatchHunk> hunks,
        out List<string> updatedLines,
        out string error)
    {
        updatedLines = [.. lines];
        var currentIndex = 0;

        foreach (var hunk in hunks)
        {
            var section = BuildSection(hunk);

            if (!TryFindHunkMatch(updatedLines, section.ContextLines, hunk, currentIndex, out var matchIndex))
            {
                error = BuildMissingContextError(hunk, section.ContextLines);
                return false;
            }

            // Apply the concrete add/remove chunks from the bottom upward so the relative indices
            // computed from the matched context remain stable. This preserves the file's exact
            // context lines, even when a fuzzy match was used to locate the hunk.
            for (var index = section.Chunks.Count - 1; index >= 0; index--)
            {
                var chunk = section.Chunks[index];
                var absoluteIndex = matchIndex + chunk.RelativeIndex;
                updatedLines.RemoveRange(absoluteIndex, chunk.DeleteLines.Count);
                updatedLines.InsertRange(absoluteIndex, chunk.InsertLines);
            }

            currentIndex = matchIndex + section.ResultLineCount;
        }

        error = string.Empty;
        return true;
    }

    private static string BuildMissingContextError(PatchHunk hunk, IReadOnlyList<string> oldLines)
    {
        var builder = new StringBuilder("The hunk context was not found in the target file.");
        if (hunk.Anchors.Count > 0)
        {
            builder.Append(" Anchors: '").Append(string.Join("' -> '", hunk.Anchors)).Append("'.");
        }

        if (hunk.PreferEndOfFile)
        {
            builder.Append(" The hunk requested an end-of-file match.");
        }

        if (oldLines.Count > 0)
        {
            builder.Append(" Context preview:");
            foreach (var line in oldLines.Take(3))
            {
                builder.Append(Environment.NewLine).Append("  ").Append(line);
            }
        }

        return builder.ToString();
    }

    private static bool TryFindHunkMatch(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> hunkLines,
        PatchHunk hunk,
        int currentIndex,
        out int matchIndex)
    {
        if (hunkLines.Count == 0)
        {
            if (hunk.PreferEndOfFile)
            {
                matchIndex = lines.Count;
                return true;
            }

            if (TryAdvanceToAnchors(lines, hunk.Anchors, currentIndex, out var anchorIndex))
            {
                matchIndex = Math.Clamp(anchorIndex, 0, lines.Count);
                return true;
            }

            matchIndex = Math.Clamp(currentIndex, 0, lines.Count);
            return true;
        }

        if (hunk.PreferEndOfFile)
        {
            var endCandidate = Math.Max(0, lines.Count - hunkLines.Count);
            if (IsMatchAt(lines, hunkLines, endCandidate, LineMatchTolerance.Exact) ||
                IsMatchAt(lines, hunkLines, endCandidate, LineMatchTolerance.TrimEnd) ||
                IsMatchAt(lines, hunkLines, endCandidate, LineMatchTolerance.TrimBoth))
            {
                matchIndex = endCandidate;
                return true;
            }
        }

        var searchStarts = new List<int>(3);
        if (TryAdvanceToAnchors(lines, hunk.Anchors, currentIndex, out var resolvedAnchorIndex))
        {
            // Search slightly before the anchor as a convenience for agents: the header usually
            // names a nearby landmark, but the actual hunk context may begin a couple of lines earlier.
            searchStarts.Add(Math.Max(0, resolvedAnchorIndex - Math.Min(hunkLines.Count, 8)));
        }

        searchStarts.Add(Math.Max(0, currentIndex));
        if (currentIndex > 0)
        {
            searchStarts.Add(0);
        }

        foreach (var startIndex in searchStarts.Distinct())
        {
            if (TryFindMatchFrom(startIndex, lines, hunkLines, out matchIndex))
            {
                return true;
            }
        }

        matchIndex = -1;
        return false;
    }

    private static bool TryAdvanceToAnchors(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> anchors,
        int currentIndex,
        out int anchorIndex)
    {
        anchorIndex = -1;
        if (anchors.Count == 0)
        {
            return false;
        }

        var searchIndex = Math.Max(0, currentIndex);
        foreach (var anchor in anchors)
        {
            if (!TryFindAnchor(lines, anchor, searchIndex, out var resolvedAnchorIndex))
            {
                anchorIndex = -1;
                return false;
            }

            searchIndex = Math.Clamp(resolvedAnchorIndex + 1, 0, lines.Count);
        }

        anchorIndex = searchIndex;
        return true;
    }

    private static bool TryFindAnchor(
        IReadOnlyList<string> lines,
        string anchor,
        int currentIndex,
        out int anchorIndex)
    {
        if (TryFindLine(lines, anchor, Math.Max(0, currentIndex), LineMatchTolerance.Exact, out anchorIndex) ||
            TryFindLine(lines, anchor, Math.Max(0, currentIndex), LineMatchTolerance.TrimBoth, out anchorIndex) ||
            TryFindLine(lines, anchor, 0, LineMatchTolerance.Exact, out anchorIndex) ||
            TryFindLine(lines, anchor, 0, LineMatchTolerance.TrimBoth, out anchorIndex))
        {
            return true;
        }

        anchorIndex = -1;
        return false;
    }

    private static bool TryFindLine(
        IReadOnlyList<string> lines,
        string expectedLine,
        int startIndex,
        LineMatchTolerance tolerance,
        out int lineIndex)
    {
        for (var candidate = Math.Max(0, startIndex); candidate < lines.Count; candidate++)
        {
            if (LineEquals(lines[candidate], expectedLine, tolerance))
            {
                lineIndex = candidate;
                return true;
            }
        }

        lineIndex = -1;
        return false;
    }

    private static bool TryFindMatchFrom(
        int startIndex,
        IReadOnlyList<string> lines,
        IReadOnlyList<string> hunkLines,
        out int matchIndex)
    {
        foreach (var tolerance in LineMatchToleranceOrder)
        {
            for (var candidate = Math.Max(0, startIndex); candidate <= lines.Count - hunkLines.Count; candidate++)
            {
                if (IsMatchAt(lines, hunkLines, candidate, tolerance))
                {
                    matchIndex = candidate;
                    return true;
                }
            }
        }

        matchIndex = -1;
        return false;
    }

    private static bool IsMatchAt(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> hunkLines,
        int candidate,
        LineMatchTolerance tolerance)
    {
        if (candidate < 0 || candidate + hunkLines.Count > lines.Count)
        {
            return false;
        }

        for (var index = 0; index < hunkLines.Count; index++)
        {
            if (!LineEquals(lines[candidate + index], hunkLines[index], tolerance))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LineEquals(string left, string right, LineMatchTolerance tolerance)
        => tolerance switch
        {
            LineMatchTolerance.Exact => string.Equals(left, right, StringComparison.Ordinal),
            LineMatchTolerance.TrimEnd => string.Equals(left.TrimEnd(), right.TrimEnd(), StringComparison.Ordinal),
            LineMatchTolerance.TrimBoth => string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal),
            _ => false,
        };

    private static bool TryParse(string input, out PatchDocument document, out string error)
    {
        var normalized = input.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var state = new ParseState(lines);

        if (!state.TryReadExact("*** Begin Patch"))
        {
            document = PatchDocument.Empty;
            error = "Patch input must start with '*** Begin Patch'.";
            return false;
        }

        var operations = new List<PatchOperation>();
        while (!state.IsAtEnd)
        {
            if (state.IsCurrentDirective("*** End Patch"))
            {
                state.Advance();
                document = new PatchDocument(operations);
                error = string.Empty;
                return true;
            }

            if (TryReadPathDirective(state.CurrentLine, "*** Add File: ", out var path))
            {
                if (!TryValidatePatchPath(path, state, out error))
                {
                    document = PatchDocument.Empty;
                    return false;
                }

                state.Advance();
                var contentLines = new List<string>();
                while (!state.IsAtEnd && !IsFileHeaderOrEnd(state.CurrentLine))
                {
                    if (!state.CurrentLine.StartsWith('+'))
                    {
                        document = PatchDocument.Empty;
                        error = $"Invalid add-file line at patch line {state.LineNumber}: '{state.CurrentLine}'. Added file content must start with '+'.";
                        return false;
                    }

                    contentLines.Add(state.CurrentLine[1..]);
                    state.Advance();
                }

                operations.Add(new AddFileOperation(path, contentLines));
                continue;
            }

            if (TryReadPathDirective(state.CurrentLine, "*** Delete File: ", out path))
            {
                if (!TryValidatePatchPath(path, state, out error))
                {
                    document = PatchDocument.Empty;
                    return false;
                }

                operations.Add(new DeleteFileOperation(path));
                state.Advance();
                continue;
            }

            if (TryReadPathDirective(state.CurrentLine, "*** Update File: ", out path))
            {
                if (!TryValidatePatchPath(path, state, out error))
                {
                    document = PatchDocument.Empty;
                    return false;
                }

                state.Advance();

                string? moveTo = null;
                if (!state.IsAtEnd && TryReadPathDirective(state.CurrentLine, "*** Move to: ", out var moveToPath))
                {
                    moveTo = moveToPath;
                    if (!TryValidatePatchPath(moveTo, state, out error))
                    {
                        document = PatchDocument.Empty;
                        return false;
                    }

                    state.Advance();
                }

                var hunks = new List<PatchHunk>();
                while (!state.IsAtEnd && !IsFileHeaderOrEnd(state.CurrentLine))
                {
                    if (!IsHunkHeader(state.CurrentLine))
                    {
                        document = PatchDocument.Empty;
                        error = $"Expected a hunk header ('@@' or '@@ anchor text') at patch line {state.LineNumber}, found '{state.CurrentLine}'. Use '@@' before each changed region.";
                        return false;
                    }

                    var anchors = new List<string>();
                    while (!state.IsAtEnd && IsHunkHeader(state.CurrentLine))
                    {
                        var anchor = ParseHunkAnchor(state.CurrentLine);
                        if (!string.IsNullOrWhiteSpace(anchor))
                        {
                            anchors.Add(anchor);
                        }

                        state.Advance();
                    }

                    var hunkLines = new List<PatchLine>();
                    var preferEndOfFile = false;
                    while (!state.IsAtEnd)
                    {
                        if (state.IsCurrentDirective("*** End of File"))
                        {
                            preferEndOfFile = true;
                            state.Advance();
                            break;
                        }

                        if (IsHunkHeader(state.CurrentLine) ||
                            IsFileHeaderOrEnd(state.CurrentLine))
                        {
                            break;
                        }

                        if (!TryParseHunkLine(state.CurrentLine, out var patchLine, out error))
                        {
                            document = PatchDocument.Empty;
                            error = $"Invalid hunk line at patch line {state.LineNumber}: {error}";
                            return false;
                        }

                        hunkLines.Add(patchLine);
                        state.Advance();
                    }

                    if (hunkLines.Count == 0)
                    {
                        document = PatchDocument.Empty;
                        error = $"Each hunk must contain at least one line (patch line {state.LineNumber}).";
                        return false;
                    }

                    hunks.Add(new PatchHunk(anchors, preferEndOfFile, hunkLines));
                }

                if (hunks.Count == 0 && moveTo is null)
                {
                    document = PatchDocument.Empty;
                    error = $"Updated file '{path}' did not contain any hunks.";
                    return false;
                }

                operations.Add(new UpdateFileOperation(path, moveTo, hunks));
                continue;
            }

            document = PatchDocument.Empty;
            error = $"Unexpected patch line at line {state.LineNumber}: '{state.CurrentLine}'.";
            return false;
        }

        document = PatchDocument.Empty;
        error = "Patch input must end with '*** End Patch'.";
        return false;
    }

    private static bool TryValidatePatchPath(string path, ParseState state, out string error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            error = $"Patch path at line {state.LineNumber} must not be empty.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParseHunkLine(string line, out PatchLine patchLine, out string error)
    {
        if (line.Length == 0)
        {
            // Treat a raw blank line as unchanged blank context. This is more forgiving for models
            // that forget to prefix blank context lines with a single leading space.
            patchLine = new PatchLine(PatchLineKind.Context, string.Empty);
            error = string.Empty;
            return true;
        }

        var kind = line[0] switch
        {
            ' ' => PatchLineKind.Context,
            '+' => PatchLineKind.Add,
            '-' => PatchLineKind.Remove,
            _ => PatchLineKind.Invalid,
        };

        if (kind == PatchLineKind.Invalid)
        {
            patchLine = default;
            error = $"Expected a leading space, '+', or '-'; received '{line}'.";
            return false;
        }

        patchLine = new PatchLine(kind, line[1..]);
        error = string.Empty;
        return true;
    }

    private static bool IsHunkHeader(string? line)
        => line is not null &&
           (string.Equals(NormalizeDirectiveLine(line), "@@", StringComparison.Ordinal) ||
            NormalizeDirectiveLine(line).StartsWith("@@ ", StringComparison.Ordinal));

    private static string? ParseHunkAnchor(string line)
    {
        var normalized = NormalizeDirectiveLine(line);
        if (string.Equals(normalized, "@@", StringComparison.Ordinal))
        {
            return null;
        }

        var anchor = normalized.StartsWith("@@ ", StringComparison.Ordinal)
            ? normalized[3..]
            : normalized.Length > 2
                ? normalized[2..]
                : string.Empty;
        if (anchor.StartsWith(' '))
        {
            anchor = anchor[1..];
        }

        return string.IsNullOrWhiteSpace(anchor) ? null : anchor;
    }

    private static bool IsFileHeaderOrEnd(string? line)
        => line is not null &&
           (TryReadPathDirective(line, "*** Add File: ", out _) ||
            TryReadPathDirective(line, "*** Delete File: ", out _) ||
            TryReadPathDirective(line, "*** Update File: ", out _) ||
            string.Equals(NormalizeDirectiveLine(line), "*** End Patch", StringComparison.Ordinal));

    private static string DetectPatchNewline(string text)
        => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string DetectNewline(string text)
        => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static List<string> SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static string JoinLines(IReadOnlyList<string> lines, string newline, bool hadTrailingNewline)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var index = 0; index < lines.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(newline);
            }

            builder.Append(lines[index]);
        }

        if (hadTrailingNewline)
        {
            builder.Append(newline);
        }

        return builder.ToString();
    }

    private static HunkSection BuildSection(PatchHunk hunk)
    {
        var contextLines = new List<string>(hunk.Lines.Count);
        var deleteLines = new List<string>();
        var insertLines = new List<string>();
        var chunks = new List<PatchChunk>();
        var lastKind = PatchLineKind.Context;

        foreach (var line in hunk.Lines)
        {
            var switchingToContext = line.Kind == PatchLineKind.Context && lastKind != PatchLineKind.Context;
            if (switchingToContext && (deleteLines.Count > 0 || insertLines.Count > 0))
            {
                chunks.Add(new PatchChunk(contextLines.Count - deleteLines.Count, [.. deleteLines], [.. insertLines]));
                deleteLines.Clear();
                insertLines.Clear();
            }

            switch (line.Kind)
            {
                case PatchLineKind.Remove:
                    deleteLines.Add(line.Text);
                    contextLines.Add(line.Text);
                    break;
                case PatchLineKind.Add:
                    insertLines.Add(line.Text);
                    break;
                case PatchLineKind.Context:
                    contextLines.Add(line.Text);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported patch line kind '{line.Kind}'.");
            }

            lastKind = line.Kind;
        }

        if (deleteLines.Count > 0 || insertLines.Count > 0)
        {
            chunks.Add(new PatchChunk(contextLines.Count - deleteLines.Count, [.. deleteLines], [.. insertLines]));
        }

        var resultLineCount = contextLines.Count;
        foreach (var chunk in chunks)
        {
            resultLineCount -= chunk.DeleteLines.Count;
            resultLineCount += chunk.InsertLines.Count;
        }

        return new HunkSection([.. contextLines], chunks, resultLineCount);
    }

    private sealed record PatchDocument(IReadOnlyList<PatchOperation> Operations)
    {
        public static PatchDocument Empty { get; } = new([]);
    }

    private abstract record PatchOperation;

    private sealed record AddFileOperation(string Path, IReadOnlyList<string> Lines) : PatchOperation;

    private sealed record DeleteFileOperation(string Path) : PatchOperation;

    private sealed record UpdateFileOperation(string Path, string? MoveTo, IReadOnlyList<PatchHunk> Hunks) : PatchOperation;

    private sealed record PatchHunk(IReadOnlyList<string> Anchors, bool PreferEndOfFile, IReadOnlyList<PatchLine> Lines);

    private readonly record struct PatchLine(PatchLineKind Kind, string Text);

    private sealed record HunkSection(IReadOnlyList<string> ContextLines, IReadOnlyList<PatchChunk> Chunks, int ResultLineCount);

    private sealed record PatchChunk(int RelativeIndex, IReadOnlyList<string> DeleteLines, IReadOnlyList<string> InsertLines);

    private enum PatchLineKind
    {
        Invalid,
        Context,
        Add,
        Remove,
    }

    private enum LineMatchTolerance
    {
        Exact,
        TrimEnd,
        TrimBoth,
    }

    private static IReadOnlyList<LineMatchTolerance> LineMatchToleranceOrder { get; } =
    [
        LineMatchTolerance.Exact,
        LineMatchTolerance.TrimEnd,
        LineMatchTolerance.TrimBoth,
    ];

    private sealed class ParseState(string[] lines)
    {
        private int _index;

        public bool IsAtEnd => _index >= lines.Length;

        public string CurrentLine => lines[_index];

        public int LineNumber => _index + 1;

        public void Advance() => _index++;

        public bool IsCurrentDirective(string value)
            => !IsAtEnd && string.Equals(NormalizeDirectiveLine(CurrentLine), value, StringComparison.Ordinal);

        public bool TryReadExact(string value)
        {
            if (!IsCurrentDirective(value))
            {
                return false;
            }

            Advance();
            return true;
        }
    }

    private static string NormalizeDirectiveLine(string line)
        => line.Trim();

    private static bool TryReadPathDirective(string line, string prefix, out string path)
    {
        var normalized = NormalizeDirectiveLine(line);
        if (normalized.StartsWith(prefix, StringComparison.Ordinal))
        {
            path = normalized[prefix.Length..];
            return true;
        }

        path = string.Empty;
        return false;
    }
}
