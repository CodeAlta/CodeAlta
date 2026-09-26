using System.Text;

namespace CodeAlta.Catalog.Skills;

/// <summary>Point-in-time explicit candidate read outcome, never an effective skill inventory decision.</summary>
public enum SkillCandidateMetadataStatus
{
    /// <summary>Complete strictly decoded bytes yielded supported local metadata.</summary>
    Parsed,
    /// <summary>Complete decoded content was invalid, including parsed name mismatch with a matching expected parent.</summary>
    Invalid,
    /// <summary>Metadata parsing refused a YAML form outside its supported subset.</summary>
    Unsupported,
    /// <summary>A byte or parser budget was exceeded; no partial metadata is returned.</summary>
    TooLarge,
    /// <summary>The supplied path or expected literal parent name was invalid; no I/O was attempted.</summary>
    InvalidPath,
    /// <summary>A required path component or candidate file was missing when checked/opened.</summary>
    Missing,
    /// <summary>An expected directory was a file, or the candidate was a directory.</summary>
    NotDirectory,
    /// <summary>An observed root ancestor, child directory, or candidate was a link/reparse point.</summary>
    Linked,
    /// <summary>Access was denied.</summary>
    Denied,
    /// <summary>An I/O error prevented a complete read.</summary>
    ReadError,
    /// <summary>Complete bytes were not strictly decodable as a supported encoding.</summary>
    InvalidEncoding,
}

/// <summary>Bounded evidence for an explicit candidate, with metadata only on <see cref="SkillCandidateMetadataStatus.Parsed"/>.</summary>
/// <param name="Status">Local read/parse result; not proof of path ownership, trust or skill visibility.</param>
/// <param name="ParserDiagnostic">Fixed parser refusal code; <see cref="SkillMetadataDiagnostic.None"/> for I/O/path/encoding outcomes.</param>
/// <param name="Frontmatter">Supported metadata only on a parsed result; no full file text is returned.</param>
/// <param name="BytesRead">Complete original byte count after EOF (including encoding/parser refusals), cap plus sentinel on byte overflow, or zero on path/I/O failures.</param>
public sealed record SkillCandidateMetadataResult(SkillCandidateMetadataStatus Status, SkillMetadataDiagnostic ParserDiagnostic,
    SkillFrontmatter? Frontmatter, int BytesRead);

/// <summary>Opt-in bounded strict byte read of one explicitly supplied raw SKILL.md candidate; no discovery or enumeration.</summary>
public static class SkillCandidateMetadataReader
{
    /// <summary>Maximum original file bytes admitted for decoding, plus one sentinel byte for overflow detection.</summary>
    public const int MaximumBytes = SkillBoundedTextReader.MaximumBytes;

    /// <summary>Checks explicit paths and observed link attributes, then reads one candidate to EOF within the byte cap.</summary>
    /// <remarks>
    /// Root and candidate must be canonical fully-qualified non-root local paths, at most 1,024 UTF-16 units each,
    /// with candidate within the supplied root at raw-reader depth at most six, named SKILL.md (ordinal-ignore-case).
    /// VCS metadata directories, components over 255 UTF-16 units (including root ancestors), links/reparse points
    /// and non-directory ancestors are refused.
    /// UTF-8 (with or without BOM), and BOM-bearing UTF-16/UTF-32 LE/BE are decoded strictly; BOM-less UTF-16/32,
    /// legacy code pages and other encodings are not supported. This reader never uses the replacement-decoding
    /// legacy/config text reader. Explicit directory-name comparison is local validation only, never visibility.
    /// Attribute/path checks and the read are not an atomic snapshot: external path swaps and file edits can race,
    /// filesystem operations can block despite cancellation, and this contract is not an outside-root security
    /// boundary against noncooperating writers. It does not resolve root providers, Git/ignore rules, shadowing,
    /// trust, enablement, or activation. No path, arbitrary exception text or full content is returned.
    /// </remarks>
    /// <param name="rootPath">Explicit canonical non-root root directory, not discovered by this method.</param>
    /// <param name="candidatePath">Explicit canonical SKILL.md path under the supplied root.</param>
    /// <param name="expectedDirectoryName">Optional exact literal parent folder and parsed name comparison; null skips both checks.</param>
    /// <param name="cancellationToken">Checked before/between filesystem operations, reads, and parser work.</param>
    /// <returns>Bounded local read/parse outcome; no partial metadata on any refusal.</returns>
    /// <exception cref="OperationCanceledException">The caller canceled before or during the operation.</exception>
    public static async Task<SkillCandidateMetadataResult> ReadAsync(string? rootPath, string? candidatePath,
        string? expectedDirectoryName = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SkillCandidateMetadataResult refused(SkillCandidateMetadataStatus status, int bytesRead = 0,
            SkillMetadataDiagnostic diagnostic = SkillMetadataDiagnostic.None) => new(status, diagnostic, null, bytesRead);
        if (!ValidPath(rootPath) || !ValidPath(candidatePath) || rootPath is null || candidatePath is null ||
            !string.Equals(Path.GetFileName(candidatePath), "SKILL.md", StringComparison.OrdinalIgnoreCase))
            return refused(SkillCandidateMetadataStatus.InvalidPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidatePath.StartsWith(rootPath + Path.DirectorySeparatorChar, comparison))
            return refused(SkillCandidateMetadataStatus.InvalidPath);
        var relative = candidatePath.AsSpan(rootPath.Length + 1);
        var components = relative.ToString().Split(Path.DirectorySeparatorChar);
        if (components.Length > RawSkillCandidateReader.MaximumDepth + 1 ||
            components.Any(static part => part.Length is < 1 or > RawSkillCandidateReader.MaximumNameLength || IsVcsDirectory(part)))
            return refused(SkillCandidateMetadataStatus.InvalidPath);
        var parent = Path.GetDirectoryName(candidatePath);
        if (parent is null || expectedDirectoryName is not null &&
            (expectedDirectoryName.Length is < 1 or > RawSkillCandidateReader.MaximumNameLength ||
             !string.Equals(expectedDirectoryName, Path.GetFileName(parent), StringComparison.Ordinal)))
            return refused(SkillCandidateMetadataStatus.InvalidPath);

        var ancestors = new Stack<string>();
        for (var directory = parent; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var name = Path.GetFileName(directory);
            if (ancestors.Count == RawSkillCandidateReader.MaximumRootComponents ||
                name.Length > RawSkillCandidateReader.MaximumNameLength || IsVcsDirectory(name))
                return refused(SkillCandidateMetadataStatus.InvalidPath);
            ancestors.Push(directory);
        }
        var byteCount = 0;
        try
        {
            while (ancestors.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(ancestors.Pop());
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) return refused(SkillCandidateMetadataStatus.Linked);
                if (!attributes.HasFlag(FileAttributes.Directory)) return refused(SkillCandidateMetadataStatus.NotDirectory);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var candidateAttributes = File.GetAttributes(candidatePath);
            if (candidateAttributes.HasFlag(FileAttributes.ReparsePoint)) return refused(SkillCandidateMetadataStatus.Linked);
            if (candidateAttributes.HasFlag(FileAttributes.Directory)) return refused(SkillCandidateMetadataStatus.NotDirectory);

            await using var stream = new FileStream(candidatePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytes = new byte[MaximumBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var received = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (received == 0) break;
                count += received;
            }
            if (count > MaximumBytes) return refused(SkillCandidateMetadataStatus.TooLarge, count);
            // A complete EOF was observed only if the sentinel byte was not reached.
            byteCount = count;
            var content = DecodeStrict(bytes.AsSpan(0, count));
            var parsed = SkillMetadataParser.Parse(content, cancellationToken);
            var status = parsed.Status switch
            {
                SkillMetadataStatus.Parsed => SkillCandidateMetadataStatus.Parsed,
                SkillMetadataStatus.Invalid => SkillCandidateMetadataStatus.Invalid,
                SkillMetadataStatus.Unsupported => SkillCandidateMetadataStatus.Unsupported,
                _ => SkillCandidateMetadataStatus.TooLarge,
            };
            if (status != SkillCandidateMetadataStatus.Parsed) return refused(status, count, parsed.Diagnostic);
            if (expectedDirectoryName is not null && !string.Equals(parsed.Frontmatter!.Name, expectedDirectoryName, StringComparison.Ordinal))
                return refused(SkillCandidateMetadataStatus.Invalid, count, SkillMetadataDiagnostic.Field);
            return new SkillCandidateMetadataResult(status, SkillMetadataDiagnostic.None, parsed.Frontmatter, count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (DecoderFallbackException) { return refused(SkillCandidateMetadataStatus.InvalidEncoding, byteCount); }
        catch (FileNotFoundException) { return refused(SkillCandidateMetadataStatus.Missing); }
        catch (DirectoryNotFoundException) { return refused(SkillCandidateMetadataStatus.Missing); }
        catch (UnauthorizedAccessException) { return refused(SkillCandidateMetadataStatus.Denied); }
        catch (System.Security.SecurityException) { return refused(SkillCandidateMetadataStatus.Denied); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        { return refused(SkillCandidateMetadataStatus.ReadError); }
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

    private static bool IsVcsDirectory(string name) =>
        name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals(".hg", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".svn", StringComparison.OrdinalIgnoreCase) || name.Equals(".jj", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".sl", StringComparison.OrdinalIgnoreCase);

    private static string DecodeStrict(ReadOnlySpan<byte> bytes)
    {
        var (encoding, bom) = bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => ((Encoding)new UTF8Encoding(true, true), 3),
            [0xFF, 0xFE, 0x00, 0x00, ..] => ((Encoding)new UTF32Encoding(false, true, true), 4),
            [0x00, 0x00, 0xFE, 0xFF, ..] => ((Encoding)new UTF32Encoding(true, true, true), 4),
            [0xFF, 0xFE, ..] => ((Encoding)new UnicodeEncoding(false, true, true), 2),
            [0xFE, 0xFF, ..] => ((Encoding)new UnicodeEncoding(true, true, true), 2),
            _ => ((Encoding)new UTF8Encoding(false, true), 0),
        };
        return encoding.GetString(bytes[bom..]);
    }
}
