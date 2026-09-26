namespace CodeAlta.Catalog.Skills;

/// <summary>Local outcome of reading one explicit ignore file, never an effective ignore decision.</summary>
public enum SkillIgnoreSourceReadStatus
{
    /// <summary>Observed EOF within the byte cap and parsed the complete bytes (possibly empty).</summary>
    Parsed,
    /// <summary>A supplied canonical boundary/file path failed validation before I/O.</summary>
    InvalidPath,
    /// <summary>The supplied canonical relative parse base failed validation before I/O.</summary>
    InvalidBase,
    /// <summary>An ancestor or source file was not present when checked or opened.</summary>
    Missing,
    /// <summary>An expected directory was a file, or the source file was a directory.</summary>
    NotDirectory,
    /// <summary>An observed ancestor or source was a link/reparse point.</summary>
    Linked,
    /// <summary>Access was denied.</summary>
    Denied,
    /// <summary>A filesystem error prevented a complete read.</summary>
    ReadError,
    /// <summary>A sentinel byte was observed past the maximum original byte count; parsing was not attempted.</summary>
    ByteLimit,
    /// <summary>Complete bytes were read but the in-memory parser refused them.</summary>
    ParserRefused,
}

/// <summary>Bounded read/parse evidence for one explicitly supplied file, without partial rules on refusal.</summary>
public sealed class SkillIgnoreSourceReadResult
{
    internal SkillIgnoreSourceReadResult(SkillIgnoreSourceReadStatus status, SkillIgnoreSourceStatus? parserStatus,
        IReadOnlyList<SkillIgnoreRule> rules, int bytesRead)
    {
        Status = status;
        ParserStatus = parserStatus;
        Rules = rules;
        BytesRead = bytesRead;
    }

    /// <summary>Fixed local outcome, not source applicability or effective ignoredness.</summary>
    public SkillIgnoreSourceReadStatus Status { get; }

    /// <summary>Parser code after observed complete EOF; null if parsing was not attempted.</summary>
    public SkillIgnoreSourceStatus? ParserStatus { get; }

    /// <summary>Read-only ordered rule snapshots only on <see cref="SkillIgnoreSourceReadStatus.Parsed"/>.</summary>
    public IReadOnlyList<SkillIgnoreRule> Rules { get; }

    /// <summary>Complete original byte count after EOF, cap plus sentinel on overflow, or zero on path/I/O failures.</summary>
    public int BytesRead { get; }
}

/// <summary>Opt-in bounded acquisition of one explicitly supplied ignore file; no discovery or matching.</summary>
public static class SkillIgnoreSourceReader
{
    /// <summary>Maximum original bytes accepted for parsing; one additional sentinel detects overflow.</summary>
    public const int MaximumBytes = SkillIgnoreSourceParser.MaximumBytes;

    /// <summary>Checks supplied paths and observed attributes, then reads to EOF before parsing the bytes.</summary>
    /// <remarks>
    /// Boundary and file must be canonical fully-qualified non-root local paths of at most 1,024 UTF-16 units;
    /// the file must be contained under the boundary by at most six directories. Each path component, including
    /// boundary ancestors, is at most 255 units and at most 64 ancestor directories are checked. Any literal
    /// contained filename is allowed, including .git/info/exclude: filename is not evidence of ignore-source
    /// identity, and the separately supplied bounded relative parse base is NOT authenticated against either path.
    /// UNC/device forms and observed links/reparse points are refused. Attribute checks and opening/reading are
    /// not atomic against external swaps or edits, and this is not a sandbox/provenance guarantee. Network/mapped
    /// mounts cannot be reliably excluded; filesystem calls/disposal may block despite cancellation. Neither this
    /// reader nor the parser establishes case mode, applicable sources/absence, Git configuration, ancestor pruning,
    /// root overlays, visibility, trust or activation. No path, arbitrary error message or file content is returned.
    /// </remarks>
    /// <param name="boundaryPath">Explicit canonical non-root local directory, not discovered by this method.</param>
    /// <param name="sourcePath">Explicit canonical contained file path; no filename or Git discovery is performed.</param>
    /// <param name="baseDirectory">Independent canonical relative parse base; caller alone asserts its association.</param>
    /// <param name="cancellationToken">Checked before/between filesystem reads and bounded parsing, not a blocking-call deadline.</param>
    /// <returns>Bounded local syntax evidence; refusal always has zero rules.</returns>
    /// <exception cref="OperationCanceledException">The caller canceled before or during the read/parse.</exception>
    public static async Task<SkillIgnoreSourceReadResult> ReadAsync(string? boundaryPath, string? sourcePath,
        string? baseDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        static SkillIgnoreSourceReadResult Refused(SkillIgnoreSourceReadStatus status, int bytesRead = 0,
            SkillIgnoreSourceStatus? parserStatus = null) => new(status, parserStatus, Array.Empty<SkillIgnoreRule>(), bytesRead);
        if (!ValidPath(boundaryPath) || !ValidPath(sourcePath) || boundaryPath is null || sourcePath is null)
            return Refused(SkillIgnoreSourceReadStatus.InvalidPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!sourcePath.StartsWith(boundaryPath + Path.DirectorySeparatorChar, comparison))
            return Refused(SkillIgnoreSourceReadStatus.InvalidPath);
        var components = sourcePath[(boundaryPath.Length + 1)..].Split(Path.DirectorySeparatorChar);
        if (components.Length > RawSkillCandidateReader.MaximumDepth + 1 ||
            components.Any(static part => part.Length is < 1 or > RawSkillCandidateReader.MaximumNameLength))
            return Refused(SkillIgnoreSourceReadStatus.InvalidPath);

        var parent = Path.GetDirectoryName(sourcePath);
        if (parent is null) return Refused(SkillIgnoreSourceReadStatus.InvalidPath);
        var ancestors = new Stack<string>();
        for (var directory = parent; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (ancestors.Count == RawSkillCandidateReader.MaximumRootComponents ||
                Path.GetFileName(directory).Length > RawSkillCandidateReader.MaximumNameLength)
                return Refused(SkillIgnoreSourceReadStatus.InvalidPath);
            ancestors.Push(directory);
        }
        if (!ValidBase(baseDirectory)) return Refused(SkillIgnoreSourceReadStatus.InvalidBase);

        try
        {
            while (ancestors.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(ancestors.Pop());
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) return Refused(SkillIgnoreSourceReadStatus.Linked);
                if (!attributes.HasFlag(FileAttributes.Directory)) return Refused(SkillIgnoreSourceReadStatus.NotDirectory);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var sourceAttributes = File.GetAttributes(sourcePath);
            if (sourceAttributes.HasFlag(FileAttributes.ReparsePoint)) return Refused(SkillIgnoreSourceReadStatus.Linked);
            if (sourceAttributes.HasFlag(FileAttributes.Directory)) return Refused(SkillIgnoreSourceReadStatus.NotDirectory);

            await using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytes = new byte[MaximumBytes + 1];
            var count = 0;
            var reachedEof = false;
            while (count < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var received = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (received == 0) { reachedEof = true; break; }
                count += received;
            }
            if (count > MaximumBytes) return Refused(SkillIgnoreSourceReadStatus.ByteLimit, count);
            // Never assert completeness for the parser without a zero-byte read within the sentinel cap.
            if (!reachedEof) return Refused(SkillIgnoreSourceReadStatus.ReadError);
            var parsed = SkillIgnoreSourceParser.Parse(bytes.AsSpan(0, count), baseDirectory, isComplete: true, cancellationToken);
            if (parsed.Status != SkillIgnoreSourceStatus.Parsed)
                return Refused(SkillIgnoreSourceReadStatus.ParserRefused, count, parsed.Status);
            return new SkillIgnoreSourceReadResult(SkillIgnoreSourceReadStatus.Parsed, parsed.Status, parsed.Rules, count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (FileNotFoundException) { return Refused(SkillIgnoreSourceReadStatus.Missing); }
        catch (DirectoryNotFoundException) { return Refused(SkillIgnoreSourceReadStatus.Missing); }
        catch (UnauthorizedAccessException) { return Refused(SkillIgnoreSourceReadStatus.Denied); }
        catch (System.Security.SecurityException) { return Refused(SkillIgnoreSourceReadStatus.Denied); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        { return Refused(SkillIgnoreSourceReadStatus.ReadError); }
    }

    private static bool ValidBase(string? baseDirectory)
    {
        if (baseDirectory is null || baseDirectory.Length > SkillIgnoreSourceParser.MaximumBaseLength) return false;
        if (baseDirectory.Length == 0) return true;
        if (baseDirectory.AsSpan().IndexOfAny('\\', ':', '\0') >= 0) return false;
        var components = baseDirectory.Split('/');
        if (components.Length > SkillIgnoreSourceParser.MaximumBaseDepth) return false;
        foreach (var component in components)
        {
            if (component.Length is < 1 or > RawSkillCandidateReader.MaximumNameLength ||
                component is "." or ".." || component.Any(char.IsControl)) return false;
        }
        return true;
    }

    private static bool ValidPath(string? path)
    {
        if (path is not { Length: > 0 and <= RawSkillCandidateReader.MaximumPathLength } ||
            path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            Path.EndsInDirectorySeparator(path)) return false;
        try
        {
            var root = Path.GetPathRoot(path);
            return Path.IsPathFullyQualified(path) && root is not null &&
                !string.Equals(root, path, StringComparison.Ordinal) &&
                string.Equals(path, Path.GetFullPath(path), StringComparison.Ordinal) &&
                (!OperatingSystem.IsWindows() || root.Length == 3 && char.IsLetter(root[0]) && root[1] == ':' && root[2] == '\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
