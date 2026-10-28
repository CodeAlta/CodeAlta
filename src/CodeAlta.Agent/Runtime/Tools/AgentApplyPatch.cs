using System.Text;

namespace CodeAlta.Agent.Runtime.Tools;

/// <summary>
/// Applies the apply_patch format (the envelope of OpenAI Codex) to the files of a working directory.
/// </summary>
/// <remarks>
/// Every operation is parsed and verified in memory first (files exist, every hunk matches, the new
/// contents are computed); files are written only once the whole patch is known to apply.
/// </remarks>
internal static class AgentApplyPatch
{
    private const string BeginPatchMarker = "*** Begin Patch";
    private const string EndPatchMarker = "*** End Patch";
    private const string AddFileMarker = "*** Add File: ";
    private const string DeleteFileMarker = "*** Delete File: ";
    private const string UpdateFileMarker = "*** Update File: ";
    private const string MoveToMarker = "*** Move to: ";
    private const string EndOfFileMarker = "*** End of File";
    private const string NothingChanged = "No file was changed.";

    // Lines quoted back in an error, and their width, so that a large hunk does not flood the answer.
    private const int MaxQuotedLines = 20;
    private const int MaxQuotedLineLength = 200;

    // How far before an '@@' anchor the context of a hunk may start (it often repeats the anchor line).
    private const int AnchorLookbackLines = 8;

    public static AgentToolResult Apply(string input, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (!TryParse(input, out var document, out var error))
        {
            return Failure(error);
        }

        if (document.Operations.Count == 0)
        {
            return Failure($"The patch contains no file operation. Add at least one '{AddFileMarker.TrimEnd()}', '{DeleteFileMarker.TrimEnd()}' or '{UpdateFileMarker.TrimEnd()}' section.");
        }

        var rootPath = Path.GetFullPath(workingDirectory);
        if (!TryPlan(document, rootPath, DetectPatchNewline(input), out var changes, out error))
        {
            return Failure($"{error}{Environment.NewLine}{NothingChanged}");
        }

        return WriteChanges(changes);
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
            paths.Add(ResolvePatchPath(rootPath, operation.Path));
            if (operation is UpdateFileOperation { MoveTo: { } moveTo })
            {
                paths.Add(ResolvePatchPath(rootPath, moveTo));
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

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static AgentToolResult WriteChanges(IReadOnlyList<PlannedChange> changes)
    {
        var applied = new List<string>(changes.Count);
        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            try
            {
                change.Write();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var builder = new StringBuilder()
                    .Append("Failed to write '").Append(change.DisplayPath).Append("': ").Append(ex.Message);
                if (applied.Count == 0)
                {
                    builder.Append(Environment.NewLine).Append(NothingChanged);
                }
                else
                {
                    builder.Append(Environment.NewLine).Append("The patch was applied only in part. Applied:");
                    foreach (var summary in applied)
                    {
                        builder.Append(Environment.NewLine).Append(summary);
                    }

                    builder.Append(Environment.NewLine).Append("Not applied:");
                    for (var rest = index; rest < changes.Count; rest++)
                    {
                        builder.Append(Environment.NewLine).Append(changes[rest].Summary);
                    }
                }

                return Failure(builder.ToString());
            }

            applied.Add(change.Summary);
        }

        var summaryText = $"Patch applied:{Environment.NewLine}{string.Join(Environment.NewLine, applied)}";
        return new AgentToolResult(true, [new AgentToolResultItem.Text(summaryText)]);
    }

    private static bool TryPlan(
        PatchDocument document,
        string rootPath,
        string patchNewline,
        out List<PlannedChange> changes,
        out string error)
    {
        changes = new List<PlannedChange>(document.Operations.Count);
        var claims = new Dictionary<string, PathClaim>(PathComparer);
        foreach (var operation in document.Operations)
        {
            try
            {
                var planned = operation switch
                {
                    AddFileOperation addFile => TryPlanAdd(addFile, rootPath, patchNewline, claims, changes, out error),
                    DeleteFileOperation deleteFile => TryPlanDelete(deleteFile, rootPath, claims, changes, out error),
                    UpdateFileOperation updateFile => TryPlanUpdate(updateFile, rootPath, claims, changes, out error),
                    _ => throw new InvalidOperationException($"Unsupported patch operation '{operation.GetType().Name}'."),
                };
                if (!planned)
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = $"Failed to read '{operation.Path}': {ex.Message}";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryPlanAdd(
        AddFileOperation addFile,
        string rootPath,
        string patchNewline,
        Dictionary<string, PathClaim> claims,
        List<PlannedChange> changes,
        out string error)
    {
        var path = ResolvePatchPath(rootPath, addFile.Path);
        var deletedEarlier = claims.TryGetValue(path, out var claim) && claim == PathClaim.Deleted;
        if (!TryClaim(claims, path, addFile.Path, allowAfterDelete: true, out error))
        {
            return false;
        }

        if (!deletedEarlier)
        {
            if (Directory.Exists(path))
            {
                error = $"Cannot add '{addFile.Path}' because it is an existing directory.";
                return false;
            }

            if (File.Exists(path))
            {
                error = $"Cannot add '{addFile.Path}' because it already exists. To change it, use '{UpdateFileMarker}{addFile.Path}' with '@@' hunks; to replace it whole, put '{DeleteFileMarker}{addFile.Path}' before '{AddFileMarker}{addFile.Path}'.";
                return false;
            }
        }

        // Added files have no existing newline convention to inherit from, so use the
        // patch envelope's newline style. This keeps the tool deterministic for agents.
        var content = new StringBuilder();
        foreach (var line in addFile.Lines)
        {
            content.Append(line).Append(patchNewline);
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content.ToString());
        changes.Add(new PlannedChange(addFile.Path, $"A {addFile.Path}", () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }));
        return true;
    }

    private static bool TryPlanDelete(
        DeleteFileOperation deleteFile,
        string rootPath,
        Dictionary<string, PathClaim> claims,
        List<PlannedChange> changes,
        out string error)
    {
        var path = ResolvePatchPath(rootPath, deleteFile.Path);
        if (!TryClaim(claims, path, deleteFile.Path, allowAfterDelete: false, out error))
        {
            return false;
        }

        if (Directory.Exists(path))
        {
            error = $"Cannot delete '{deleteFile.Path}' because it is a directory.";
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"Cannot delete '{deleteFile.Path}' because it does not exist.";
            return false;
        }

        if (AgentFileTypeDetector.IsProbablyBinaryFile(path))
        {
            error = $"Cannot delete '{deleteFile.Path}' with apply_patch because it appears to be a binary file. Use delete_file_or_dir instead.";
            return false;
        }

        claims[path] = PathClaim.Deleted;
        changes.Add(new PlannedChange(deleteFile.Path, $"D {deleteFile.Path}", () => File.Delete(path)));
        return true;
    }

    private static bool TryPlanUpdate(
        UpdateFileOperation updateFile,
        string rootPath,
        Dictionary<string, PathClaim> claims,
        List<PlannedChange> changes,
        out string error)
    {
        var sourcePath = ResolvePatchPath(rootPath, updateFile.Path);
        if (!TryClaim(claims, sourcePath, updateFile.Path, allowAfterDelete: false, out error))
        {
            return false;
        }

        if (Directory.Exists(sourcePath))
        {
            error = $"Cannot update '{updateFile.Path}' because it is a directory.";
            return false;
        }

        if (!File.Exists(sourcePath))
        {
            error = $"Cannot update '{updateFile.Path}' because it does not exist. Use '{AddFileMarker}{updateFile.Path}' to create it.";
            return false;
        }

        if (AgentFileTypeDetector.IsProbablyBinaryFile(sourcePath))
        {
            error = $"Cannot update '{updateFile.Path}' with apply_patch because it appears to be a binary file.";
            return false;
        }

        byte[]? updatedBytes = null;
        if (updateFile.Hunks.Count > 0)
        {
            var file = SourceFile.Read(sourcePath);
            if (!TryApplyHunks(file, updateFile.Hunks, updateFile.Path, out error))
            {
                return false;
            }

            updatedBytes = file.ToBytes();
        }

        if (updateFile.MoveTo is null)
        {
            if (updatedBytes is not null)
            {
                changes.Add(new PlannedChange(updateFile.Path, $"M {updateFile.Path}", () => File.WriteAllBytes(sourcePath, updatedBytes)));
            }

            return true;
        }

        var destinationPath = ResolvePatchPath(rootPath, updateFile.MoveTo);
        if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
        {
            if (updatedBytes is not null)
            {
                changes.Add(new PlannedChange(updateFile.Path, $"M {updateFile.Path}", () => File.WriteAllBytes(sourcePath, updatedBytes)));
            }

            return true;
        }

        var summary = $"R {updateFile.Path} -> {updateFile.MoveTo}";
        if (PathComparer.Equals(sourcePath, destinationPath))
        {
            // A rename that only changes the case of the name, on a file system that ignores case.
            changes.Add(new PlannedChange(updateFile.Path, summary, () =>
            {
                if (updatedBytes is not null)
                {
                    File.WriteAllBytes(sourcePath, updatedBytes);
                }

                File.Move(sourcePath, destinationPath);
            }));
            return true;
        }

        var destinationDeletedEarlier = claims.TryGetValue(destinationPath, out var claim) && claim == PathClaim.Deleted;
        if (!TryClaim(claims, destinationPath, updateFile.MoveTo, allowAfterDelete: true, out error))
        {
            return false;
        }

        if (!destinationDeletedEarlier && (File.Exists(destinationPath) || Directory.Exists(destinationPath)))
        {
            error = $"Cannot move '{updateFile.Path}' to '{updateFile.MoveTo}' because the destination already exists. To replace it, put '{DeleteFileMarker}{updateFile.MoveTo}' before '{UpdateFileMarker}{updateFile.Path}'; otherwise choose another destination.";
            return false;
        }

        changes.Add(new PlannedChange(updateFile.Path, summary, () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (updatedBytes is null)
            {
                File.Move(sourcePath, destinationPath);
            }
            else
            {
                File.WriteAllBytes(destinationPath, updatedBytes);
                File.Delete(sourcePath);
            }
        }));
        return true;
    }

    private static bool TryClaim(
        Dictionary<string, PathClaim> claims,
        string fullPath,
        string displayPath,
        bool allowAfterDelete,
        out string error)
    {
        // As in Codex, one patch changes a file once. A file deleted earlier in the same patch may be
        // created again, by an Add File or as the destination of a Move to: that replaces it whole.
        if (claims.TryGetValue(fullPath, out var claim) && !(allowAfterDelete && claim == PathClaim.Deleted))
        {
            error = $"Invalid patch: multiple operations target '{displayPath}'. Put all the hunks of a file under one '{UpdateFileMarker}{displayPath}' section.";
            return false;
        }

        claims[fullPath] = PathClaim.Changed;
        error = string.Empty;
        return true;
    }

    private static bool TryApplyHunks(SourceFile file, IReadOnlyList<PatchHunk> hunks, string displayPath, out string error)
    {
        var lines = file.Lines;
        var currentIndex = 0;

        foreach (var hunk in hunks)
        {
            var section = BuildSection(hunk.Lines);
            if (!TryFindHunkMatch(lines, section.ContextLines, hunk, currentIndex, out var matchIndex, out var failure))
            {
                // Like Codex, retry without a trailing empty line: it often stands for the final
                // newline of the file rather than for a blank line.
                if (hunk.Lines.Count > 1 &&
                    hunk.Lines[^1] is { Text.Length: 0, Kind: PatchLineKind.Context or PatchLineKind.Remove })
                {
                    var trimmedSection = BuildSection([.. hunk.Lines.Take(hunk.Lines.Count - 1)]);
                    if (TryFindHunkMatch(lines, trimmedSection.ContextLines, hunk, currentIndex, out matchIndex, out _))
                    {
                        section = trimmedSection;
                        failure = default;
                    }
                }

                if (failure.Kind != MatchFailureKind.None)
                {
                    error = BuildMatchError(displayPath, hunk, section.ContextLines, failure);
                    return false;
                }
            }

            // Apply the concrete add/remove chunks from the bottom upward so the relative indices
            // computed from the matched context remain stable. Context lines keep their exact text
            // and line ending, even when a fuzzy match was used to locate the hunk; inserted lines
            // take the preferred line ending of the file.
            for (var index = section.Chunks.Count - 1; index >= 0; index--)
            {
                var chunk = section.Chunks[index];
                var absoluteIndex = matchIndex + chunk.RelativeIndex;
                lines.RemoveRange(absoluteIndex, chunk.DeleteLines.Count);
                lines.InsertRange(absoluteIndex, chunk.InsertLines.Select(text => new SourceLine(text, file.PreferredEnding)));
            }

            currentIndex = matchIndex + section.ResultLineCount;
        }

        error = string.Empty;
        return true;
    }

    private static string BuildMatchError(
        string displayPath,
        PatchHunk hunk,
        IReadOnlyList<string> expectedLines,
        MatchFailure failure)
    {
        var builder = new StringBuilder();
        switch (failure.Kind)
        {
            case MatchFailureKind.AnchorNotFound:
                builder.Append("Failed to find context '").Append(failure.Anchor).Append("' in ").Append(displayPath)
                    .Append(" (hunk at patch line ").Append(hunk.LineNumber).Append(").");
                if (failure.Count > 1)
                {
                    builder.Append(" It is not after the previous hunk and appears ").Append(failure.Count)
                        .Append(" times before it; add an anchor or context lines that occur once.");
                }

                if (expectedLines.Count > 0)
                {
                    builder.Append(" The lines of the hunk do not match a single place without it:");
                }

                break;
            case MatchFailureKind.Ambiguous:
                builder.Append("The expected lines of the hunk at patch line ").Append(hunk.LineNumber)
                    .Append(" are not after the previous hunk or anchor in ").Append(displayPath)
                    .Append(" and appear ").Append(failure.Count)
                    .Append(" times before it; add an '@@' anchor or more context lines to choose one:");
                break;
            default:
                builder.Append("Failed to find expected lines in ").Append(displayPath)
                    .Append(" (hunk at patch line ").Append(hunk.LineNumber).Append(')');
                if (hunk.Anchors.Count > 0)
                {
                    builder.Append(" after '@@ ").Append(string.Join("' -> '@@ ", hunk.Anchors)).Append('\'');
                }

                if (hunk.PreferEndOfFile)
                {
                    builder.Append(" at the end of the file");
                }

                builder.Append(':');
                break;
        }

        if (failure.Kind != MatchFailureKind.AnchorNotFound || expectedLines.Count > 0)
        {
            var quoted = Math.Min(expectedLines.Count, MaxQuotedLines);
            for (var index = 0; index < quoted; index++)
            {
                var line = expectedLines[index];
                builder.Append(Environment.NewLine)
                    .Append(line.Length > MaxQuotedLineLength ? string.Concat(line.AsSpan(0, MaxQuotedLineLength), "...") : line);
            }

            if (expectedLines.Count > quoted)
            {
                builder.Append(Environment.NewLine).Append("... (").Append(expectedLines.Count - quoted).Append(" more lines)");
            }
        }

        builder.Append(Environment.NewLine).Append("Read the file again and copy its current lines into the hunk.");
        return builder.ToString();
    }

    private static bool TryFindHunkMatch(
        IReadOnlyList<SourceLine> lines,
        IReadOnlyList<string> hunkLines,
        PatchHunk hunk,
        int currentIndex,
        out int matchIndex,
        out MatchFailure failure)
    {
        currentIndex = Math.Clamp(currentIndex, 0, lines.Count);
        failure = default;

        var anchorIndex = -1;
        var anchorFailure = default(MatchFailure);
        if (hunk.Anchors.Count > 0 && !TryFindAnchors(lines, hunk.Anchors, currentIndex, out anchorIndex, out anchorFailure))
        {
            anchorIndex = -1;
        }

        if (hunkLines.Count == 0)
        {
            // An insert-only hunk goes at the end of the file, after its anchor, or after the
            // previous hunk. Without context lines a missing anchor cannot be recovered.
            if (hunk.PreferEndOfFile)
            {
                matchIndex = lines.Count;
                return true;
            }

            if (anchorFailure.Kind != MatchFailureKind.None)
            {
                matchIndex = -1;
                failure = anchorFailure;
                return false;
            }

            matchIndex = anchorIndex >= 0 ? anchorIndex + 1 : currentIndex;
            return true;
        }

        if (hunk.PreferEndOfFile)
        {
            var endCandidate = lines.Count - hunkLines.Count;
            foreach (var tolerance in LineMatchToleranceOrder)
            {
                if (IsMatchAt(lines, hunkLines, endCandidate, tolerance))
                {
                    matchIndex = endCandidate;
                    return true;
                }
            }
        }

        if (anchorIndex >= 0)
        {
            if (TryFindForward(lines, hunkLines, anchorIndex + 1, out matchIndex))
            {
                return true;
            }

            // The context often repeats the anchor line, or starts a few lines before it.
            var lookbackStart = Math.Max(currentIndex, anchorIndex + 1 - Math.Min(hunkLines.Count, AnchorLookbackLines));
            if (TryFindForward(lines, hunkLines, lookbackStart, out matchIndex))
            {
                return true;
            }
        }
        else if (hunk.Anchors.Count == 0 && TryFindForward(lines, hunkLines, currentIndex, out matchIndex))
        {
            return true;
        }

        // Otherwise accept a match anywhere in the file, but only when it is the only one: a hunk
        // must never land on an earlier duplicate of its context.
        var count = CountMatches(lines, hunkLines, out matchIndex);
        if (count == 1)
        {
            return true;
        }

        matchIndex = -1;
        failure = anchorFailure.Kind != MatchFailureKind.None
            ? anchorFailure
            : count > 1
                ? new MatchFailure(MatchFailureKind.Ambiguous, null, count)
                : new MatchFailure(MatchFailureKind.NotFound, null, 0);
        return false;
    }

    private static bool TryFindAnchors(
        IReadOnlyList<SourceLine> lines,
        IReadOnlyList<string> anchors,
        int currentIndex,
        out int anchorIndex,
        out MatchFailure failure)
    {
        var searchIndex = currentIndex;
        anchorIndex = -1;
        for (var index = 0; index < anchors.Count; index++)
        {
            string[] anchor = [anchors[index]];
            if (TryFindForward(lines, anchor, searchIndex, out var resolvedIndex))
            {
                anchorIndex = resolvedIndex;
            }
            else
            {
                // The first anchor may be before the previous hunk when it is the only such line.
                var count = index == 0 ? CountMatches(lines, anchor, out resolvedIndex) : 0;
                if (count != 1)
                {
                    failure = new MatchFailure(MatchFailureKind.AnchorNotFound, anchors[index], count);
                    return false;
                }

                anchorIndex = resolvedIndex;
            }

            searchIndex = anchorIndex + 1;
        }

        failure = default;
        return true;
    }

    private static bool TryFindForward(
        IReadOnlyList<SourceLine> lines,
        IReadOnlyList<string> pattern,
        int startIndex,
        out int matchIndex)
    {
        foreach (var tolerance in LineMatchToleranceOrder)
        {
            for (var candidate = Math.Max(0, startIndex); candidate <= lines.Count - pattern.Count; candidate++)
            {
                if (IsMatchAt(lines, pattern, candidate, tolerance))
                {
                    matchIndex = candidate;
                    return true;
                }
            }
        }

        matchIndex = -1;
        return false;
    }

    private static int CountMatches(IReadOnlyList<SourceLine> lines, IReadOnlyList<string> pattern, out int firstMatch)
    {
        // Count at the strictest tolerance that matches at all, as the forward search would pick it.
        foreach (var tolerance in LineMatchToleranceOrder)
        {
            var count = 0;
            firstMatch = -1;
            for (var candidate = 0; candidate <= lines.Count - pattern.Count; candidate++)
            {
                if (IsMatchAt(lines, pattern, candidate, tolerance))
                {
                    if (count == 0)
                    {
                        firstMatch = candidate;
                    }

                    count++;
                }
            }

            if (count > 0)
            {
                return count;
            }
        }

        firstMatch = -1;
        return 0;
    }

    private static bool IsMatchAt(
        IReadOnlyList<SourceLine> lines,
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
            if (!LineEquals(lines[candidate + index].Text, hunkLines[index], tolerance))
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
            LineMatchTolerance.TrimEnd => left.AsSpan().TrimEnd().SequenceEqual(right.AsSpan().TrimEnd()),
            LineMatchTolerance.TrimBoth => left.AsSpan().Trim().SequenceEqual(right.AsSpan().Trim()),
            LineMatchTolerance.Punctuation => PunctuationInsensitiveEquals(left.AsSpan().Trim(), right.AsSpan().Trim()),
            _ => false,
        };

    private static bool PunctuationInsensitiveEquals(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (NormalizePunctuation(left[index]) != NormalizePunctuation(right[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Maps typographic dashes, quotes and spaces to their ASCII form, as Codex's last matching pass does,
    /// so that a hunk written with plain ASCII still finds lines that use typographic punctuation.
    /// </summary>
    private static char NormalizePunctuation(char value)
        => value switch
        {
            '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' => '-',
            '\u2018' or '\u2019' or '\u201A' or '\u201B' => '\'',
            '\u201C' or '\u201D' or '\u201E' or '\u201F' => '"',
            '\u00A0' or '\u2002' or '\u2003' or '\u2004' or '\u2005' or '\u2006' or '\u2007' or '\u2008'
                or '\u2009' or '\u200A' or '\u202F' or '\u205F' or '\u3000' => ' ',
            _ => value,
        };

    private static bool TryParse(string input, out PatchDocument document, out string error)
    {
        document = PatchDocument.Empty;
        var lines = input.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        // Ignore blank lines and whitespace around the envelope, and unwrap the heredoc forms models
        // copy from shell usage (<<'EOF' ... EOF), as Codex does.
        var first = 0;
        var last = lines.Length - 1;
        SkipBlankLines(lines, ref first, ref last);
        if (last - first >= 3 &&
            TryReadHeredocOpener(lines[first], out var delimiter) &&
            string.Equals(lines[last].Trim(), delimiter, StringComparison.Ordinal))
        {
            first++;
            last--;
            SkipBlankLines(lines, ref first, ref last);
        }

        if (first > last || !IsDirective(lines[first], BeginPatchMarker))
        {
            error = "Patch input must start with '*** Begin Patch'.";
            return false;
        }

        var state = new ParseState(lines, first + 1, last + 1);
        var operations = new List<PatchOperation>();
        while (!state.IsAtEnd)
        {
            if (IsDirective(state.CurrentLine, EndPatchMarker))
            {
                state.Advance();
                if (!state.IsAtEnd)
                {
                    error = $"Unexpected text after '{EndPatchMarker}' at patch line {state.LineNumber}: '{state.CurrentLine}'. Put every operation before '{EndPatchMarker}', which ends the patch.";
                    return false;
                }

                document = new PatchDocument(operations);
                error = string.Empty;
                return true;
            }

            if (TryReadPathDirective(state.CurrentLine, AddFileMarker, out var path))
            {
                if (!TryValidatePatchPath(path, state, out error))
                {
                    return false;
                }

                state.Advance();
                var contentLines = new List<string>();
                while (!state.IsAtEnd && !IsFileHeaderOrEnd(state.CurrentLine))
                {
                    if (!state.CurrentLine.StartsWith('+'))
                    {
                        error = $"Invalid add-file line at patch line {state.LineNumber}: '{state.CurrentLine}'. Added file content must start with '+'.";
                        return false;
                    }

                    contentLines.Add(state.CurrentLine[1..]);
                    state.Advance();
                }

                operations.Add(new AddFileOperation(path, contentLines));
                continue;
            }

            if (TryReadPathDirective(state.CurrentLine, DeleteFileMarker, out path))
            {
                if (!TryValidatePatchPath(path, state, out error))
                {
                    return false;
                }

                operations.Add(new DeleteFileOperation(path));
                state.Advance();
                continue;
            }

            if (TryReadPathDirective(state.CurrentLine, UpdateFileMarker, out path))
            {
                if (!TryValidatePatchPath(path, state, out error) ||
                    !TryParseUpdate(state, path, out var update, out error))
                {
                    return false;
                }

                operations.Add(update);
                continue;
            }

            error = $"Unexpected patch line at line {state.LineNumber}: '{state.CurrentLine}'. Expected '{AddFileMarker}<path>', '{DeleteFileMarker}<path>', '{UpdateFileMarker}<path>' or '{EndPatchMarker}'.";
            return false;
        }

        error = "Patch input must end with '*** End Patch'.";
        return false;
    }

    private static bool TryParseUpdate(ParseState state, string path, out UpdateFileOperation update, out string error)
    {
        update = null!;
        state.Advance();

        string? moveTo = null;
        if (!state.IsAtEnd && TryReadPathDirective(state.CurrentLine, MoveToMarker, out var moveToPath))
        {
            if (!TryValidatePatchPath(moveToPath, state, out error))
            {
                return false;
            }

            moveTo = moveToPath;
            state.Advance();
        }

        var hunks = new List<PatchHunk>();
        var afterEndOfFile = false;
        while (!state.IsAtEnd && !IsFileHeaderOrEnd(state.CurrentLine))
        {
            var line = state.CurrentLine;
            var hunkLineNumber = state.LineNumber;
            var anchors = new List<string>();
            if (IsHunkHeader(line))
            {
                // Several '@@ anchor' lines in a row narrow the position step by step (class, then method).
                while (!state.IsAtEnd && IsHunkHeader(state.CurrentLine))
                {
                    if (ParseHunkAnchor(state.CurrentLine) is { } anchor)
                    {
                        anchors.Add(anchor);
                    }

                    state.Advance();
                }
            }
            else if ((afterEndOfFile && line.Trim().Length == 0) || (hunks.Count == 0 && line.Length == 0))
            {
                // Blank lines after '*** End of File' or before the first hunk only separate sections.
                state.Advance();
                continue;
            }
            else if (hunks.Count > 0 || afterEndOfFile || !IsHunkBodyLine(line))
            {
                // As in Codex, only the first hunk of a file may omit its '@@' line.
                error = $"Expected a hunk header ('@@' or '@@ anchor text') at patch line {hunkLineNumber}, found '{line}'. Use '@@' before each changed region.";
                return false;
            }

            var hunkLines = new List<PatchLine>();
            var preferEndOfFile = false;
            while (!state.IsAtEnd)
            {
                if (IsDirective(state.CurrentLine, EndOfFileMarker))
                {
                    preferEndOfFile = true;
                    state.Advance();
                    break;
                }

                if (IsHunkHeader(state.CurrentLine) || IsFileHeaderOrEnd(state.CurrentLine))
                {
                    break;
                }

                if (!TryParseHunkLine(state.CurrentLine, out var patchLine, out error))
                {
                    error = $"Invalid hunk line at patch line {state.LineNumber}: {error}";
                    return false;
                }

                hunkLines.Add(patchLine);
                state.Advance();
            }

            if (hunkLines.Count == 0)
            {
                error = $"Each hunk must contain at least one line (patch line {state.LineNumber}).";
                return false;
            }

            hunks.Add(new PatchHunk(hunkLineNumber, anchors, preferEndOfFile, hunkLines));
            afterEndOfFile = preferEndOfFile;
        }

        if (hunks.Count == 0 && moveTo is null)
        {
            error = $"Updated file '{path}' did not contain any hunks.";
            return false;
        }

        update = new UpdateFileOperation(path, moveTo, hunks);
        error = string.Empty;
        return true;
    }

    private static void SkipBlankLines(string[] lines, ref int first, ref int last)
    {
        while (first <= last && lines[first].Trim().Length == 0)
        {
            first++;
        }

        while (last >= first && lines[last].Trim().Length == 0)
        {
            last--;
        }
    }

    private static bool TryReadHeredocOpener(string line, out string delimiter)
    {
        // Accepts "<<EOF", "<<'EOF'", "<<\"EOF\"", "<<-EOF", optionally after "apply_patch".
        delimiter = string.Empty;
        var text = line.AsSpan().Trim();
        foreach (var command in (ReadOnlySpan<string>)["apply_patch", "applypatch"])
        {
            if (text.StartsWith(command, StringComparison.Ordinal))
            {
                text = text[command.Length..].TrimStart();
                break;
            }
        }

        if (!text.StartsWith("<<", StringComparison.Ordinal))
        {
            return false;
        }

        text = text[2..];
        if (text.StartsWith('-'))
        {
            text = text[1..];
        }

        text = text.Trim();
        if (text.Length >= 2 && text[0] is '\'' or '"' && text[^1] == text[0])
        {
            text = text[1..^1];
        }

        if (text.Length == 0)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        delimiter = text.ToString();
        return true;
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

    private static bool IsHunkBodyLine(string line)
        => line.Length == 0 || line[0] is ' ' or '+' or '-';

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

    // As in Codex, a hunk header starts the line: " @@ text" is a context line whose text is "@@ text".
    private static bool IsHunkHeader(string line)
    {
        var text = line.AsSpan().TrimEnd();
        return text.SequenceEqual("@@") || text.StartsWith("@@ ", StringComparison.Ordinal);
    }

    private static string? ParseHunkAnchor(string line)
    {
        var anchor = line.AsSpan().TrimEnd()[2..].Trim();

        // Tolerate a unified-diff range ("@@ -12,7 +12,8 @@ void Run()"): keep the text after it.
        if (anchor.StartsWith('-') && anchor.IndexOf(" +", StringComparison.Ordinal) is var plus and > 0)
        {
            var rest = anchor[(plus + 2)..];
            var close = rest.IndexOf("@@", StringComparison.Ordinal);
            if (close >= 0 &&
                IsUnifiedRange(anchor[1..plus]) &&
                IsUnifiedRange(rest[..close].TrimEnd()))
            {
                anchor = rest[(close + 2)..].Trim();
            }
        }

        return anchor.Length == 0 ? null : anchor.ToString();
    }

    private static bool IsUnifiedRange(ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var character in text)
        {
            if (!char.IsAsciiDigit(character) && character != ',')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFileHeaderOrEnd(string line)
        => TryReadPathDirective(line, AddFileMarker, out _) ||
           TryReadPathDirective(line, DeleteFileMarker, out _) ||
           TryReadPathDirective(line, UpdateFileMarker, out _) ||
           IsDirective(line, EndPatchMarker);

    private static bool IsDirective(string line, string directive)
        => line.AsSpan().Trim().SequenceEqual(directive);

    private static bool TryReadPathDirective(string line, string prefix, out string path)
    {
        var normalized = line.Trim();
        if (normalized.StartsWith(prefix, StringComparison.Ordinal))
        {
            path = normalized[prefix.Length..];
            return true;
        }

        path = string.Empty;
        return false;
    }

    private static string DetectPatchNewline(string text)
        => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static HunkSection BuildSection(IReadOnlyList<PatchLine> hunkLines)
    {
        var contextLines = new List<string>(hunkLines.Count);
        var deleteLines = new List<string>();
        var insertLines = new List<string>();
        var chunks = new List<PatchChunk>();
        var lastKind = PatchLineKind.Context;

        foreach (var line in hunkLines)
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

    /// <summary>
    /// The text of a file split into lines that keep their own line ending, as Codex's text_file does:
    /// unchanged lines keep "\n", "\r\n" or a lone "\r", and inserted lines take the first ending of the file.
    /// </summary>
    private sealed class SourceFile
    {
        private readonly bool _hasUtf8Bom;

        private SourceFile(bool hasUtf8Bom, List<SourceLine> lines, string preferredEnding)
        {
            _hasUtf8Bom = hasUtf8Bom;
            Lines = lines;
            PreferredEnding = preferredEnding;
        }

        public List<SourceLine> Lines { get; }

        public string PreferredEnding { get; }

        public static SourceFile Read(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var hasUtf8Bom = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]);
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                .GetString(bytes, hasUtf8Bom ? 3 : 0, bytes.Length - (hasUtf8Bom ? 3 : 0));
            return Parse(text, hasUtf8Bom);
        }

        private static SourceFile Parse(string text, bool hasUtf8Bom)
        {
            var lines = new List<SourceLine>();
            string? preferredEnding = null;
            var lineStart = 0;
            var cursor = 0;
            while (cursor < text.Length)
            {
                string ending;
                switch (text[cursor])
                {
                    case '\r' when cursor + 1 < text.Length && text[cursor + 1] == '\n':
                        ending = "\r\n";
                        break;
                    case '\r':
                        ending = "\r";
                        break;
                    case '\n':
                        ending = "\n";
                        break;
                    default:
                        cursor++;
                        continue;
                }

                preferredEnding ??= ending;
                lines.Add(new SourceLine(text[lineStart..cursor], ending));
                cursor += ending.Length;
                lineStart = cursor;
            }

            if (lineStart < text.Length)
            {
                lines.Add(new SourceLine(text[lineStart..], null));
            }

            return new SourceFile(hasUtf8Bom, lines, preferredEnding ?? "\n");
        }

        public byte[] ToBytes()
        {
            // Updates have always ended every line, the last one included, with a line ending.
            var builder = new StringBuilder();
            foreach (var line in Lines)
            {
                builder.Append(line.Text).Append(line.Ending ?? PreferredEnding);
            }

            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: _hasUtf8Bom);
            return [.. encoding.GetPreamble(), .. encoding.GetBytes(builder.ToString())];
        }
    }

    private readonly record struct SourceLine(string Text, string? Ending);

    private sealed record PlannedChange(string DisplayPath, string Summary, Action Write);

    private enum PathClaim
    {
        Changed,
        Deleted,
    }

    private sealed record PatchDocument(IReadOnlyList<PatchOperation> Operations)
    {
        public static PatchDocument Empty { get; } = new([]);
    }

    private abstract record PatchOperation(string Path);

    private sealed record AddFileOperation(string Path, IReadOnlyList<string> Lines) : PatchOperation(Path);

    private sealed record DeleteFileOperation(string Path) : PatchOperation(Path);

    private sealed record UpdateFileOperation(string Path, string? MoveTo, IReadOnlyList<PatchHunk> Hunks) : PatchOperation(Path);

    private sealed record PatchHunk(int LineNumber, IReadOnlyList<string> Anchors, bool PreferEndOfFile, IReadOnlyList<PatchLine> Lines);

    private readonly record struct PatchLine(PatchLineKind Kind, string Text);

    private sealed record HunkSection(IReadOnlyList<string> ContextLines, IReadOnlyList<PatchChunk> Chunks, int ResultLineCount);

    private sealed record PatchChunk(int RelativeIndex, IReadOnlyList<string> DeleteLines, IReadOnlyList<string> InsertLines);

    private readonly record struct MatchFailure(MatchFailureKind Kind, string? Anchor, int Count);

    private enum MatchFailureKind
    {
        None,
        NotFound,
        Ambiguous,
        AnchorNotFound,
    }

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
        Punctuation,
    }

    private static IReadOnlyList<LineMatchTolerance> LineMatchToleranceOrder { get; } =
    [
        LineMatchTolerance.Exact,
        LineMatchTolerance.TrimEnd,
        LineMatchTolerance.TrimBoth,
        LineMatchTolerance.Punctuation,
    ];

    private sealed class ParseState(string[] lines, int startIndex, int endIndex)
    {
        private int _index = startIndex;

        public bool IsAtEnd => _index >= endIndex;

        public string CurrentLine => lines[_index];

        public int LineNumber => _index + 1;

        public void Advance() => _index++;
    }
}
