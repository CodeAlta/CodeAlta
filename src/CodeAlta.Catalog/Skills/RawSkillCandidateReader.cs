using System.IO.Enumeration;

namespace CodeAlta.Catalog.Skills;

/// <summary>Traversal outcome for an explicit raw skill-file candidate scan, not effective skill discovery.</summary>
public enum RawSkillCandidateStatus
{
    /// <summary>All eligible raw entries under the fixed exclusions were visited within the budgets.</summary>
    Complete,
    /// <summary>Some subtree, entry, or candidate was omitted; absence cannot be inferred.</summary>
    Incomplete,
    /// <summary>The supplied root is not a canonical, bounded, absolute non-root local path.</summary>
    Invalid,
    /// <summary>The supplied root or one of its ancestors did not exist.</summary>
    Missing,
    /// <summary>The supplied root or one of its ancestors was a file.</summary>
    NotDirectory,
    /// <summary>Access to the root or an enumerated directory was denied.</summary>
    Denied,
    /// <summary>A linked root component or filesystem failure prevented a trustworthy traversal.</summary>
    ReadError,
}

/// <summary>Bounded flags explaining raw traversal omissions or refusal, without filesystem exception text.</summary>
[Flags]
public enum RawSkillCandidateDiagnostics
{
    /// <summary>No observed raw traversal omission.</summary>
    None = 0,
    /// <summary>The global entry count reached its one additional sentinel.</summary>
    EntryLimit = 1 << 0,
    /// <summary>The maximum number of opened directories was reached.</summary>
    DirectoryLimit = 1 << 1,
    /// <summary>A child directory could not fit in the bounded pending stack.</summary>
    PendingDirectoryLimit = 1 << 2,
    /// <summary>A deeper directory was not traversed.</summary>
    DepthLimit = 1 << 3,
    /// <summary>An entry name or potential path exceeded its allocation bound.</summary>
    PathLimit = 1 << 4,
    /// <summary>Another matching file exceeded the bounded candidate count.</summary>
    CandidateLimit = 1 << 5,
    /// <summary>Another matching path exceeded the aggregate result-character budget.</summary>
    CandidateCharactersLimit = 1 << 6,
    /// <summary>A linked/reparse entry was skipped without traversal.</summary>
    LinkedEntry = 1 << 7,
    /// <summary>A fixed VCS metadata directory was excluded without traversal.</summary>
    MetadataDirectory = 1 << 8,
    /// <summary>Reading a component or directory failed; partial observations are not authoritative.</summary>
    ReadFailure = 1 << 9,
    /// <summary>The supplied root contained a known linked/reparse ancestor.</summary>
    LinkedRoot = 1 << 10,
    /// <summary>The supplied root has more components than the root-verification budget.</summary>
    RootComponentLimit = 1 << 11,
}

/// <summary>A sorted bounded subset of raw SKILL.md paths, not validated or visible skills.</summary>
/// <param name="Status">Raw traversal status only; even Complete does not certify ignore, shadowing, or enablement semantics.</param>
/// <param name="CandidatePaths">Observed full paths sorted within the bounded subset only, never globally first.</param>
/// <param name="EntriesVisited">All encountered entries, including nonmatches and a possible over-budget sentinel.</param>
/// <param name="DirectoriesOpened">Directories for which an enumeration was attempted, including the supplied root.</param>
/// <param name="Diagnostics">Bounded omission/refusal flags; no arbitrary path or exception strings.</param>
public sealed record RawSkillCandidateResult(RawSkillCandidateStatus Status, IReadOnlyList<string> CandidatePaths,
    int EntriesVisited, int DirectoriesOpened, RawSkillCandidateDiagnostics Diagnostics);

/// <summary>Opt-in explicit-root raw skill-file traversal; never used by ordinary skill discovery or activation.</summary>
public static class RawSkillCandidateReader
{
    /// <summary>Maximum UTF-16 units for the explicit root and each constructed child/candidate path.</summary>
    public const int MaximumPathLength = 1024;
    /// <summary>Maximum UTF-16 units copied from one encountered filename.</summary>
    public const int MaximumNameLength = 255;
    /// <summary>Maximum root components verified before any directory enumeration.</summary>
    public const int MaximumRootComponents = 64;
    /// <summary>Maximum entries across all directories, plus one encountered sentinel.</summary>
    public const int MaximumEntries = 256;
    /// <summary>Maximum directory-enumeration attempts, including the supplied root.</summary>
    public const int MaximumDirectories = 64;
    /// <summary>Maximum pending directory paths retained at one time.</summary>
    public const int MaximumPendingDirectories = 32;
    /// <summary>Maximum directory depth below the supplied root (root is zero).</summary>
    public const int MaximumDepth = 6;
    /// <summary>Maximum returned raw file paths.</summary>
    public const int MaximumCandidates = 16;
    /// <summary>Maximum UTF-16 units across returned candidate paths.</summary>
    public const int MaximumTotalCandidateCharacters = 8192;

    /// <summary>Scans an explicit local absolute non-root directory for raw SKILL.md filenames only.</summary>
    /// <remarks>
    /// This is a synchronous, opt-in, single-root raw filesystem observation. It does not invoke root providers,
    /// repository discovery, the Git walker, ignore readers, frontmatter/content readers, shadowing, enablement,
    /// or activation. Hidden paths and ignore-file contents are not filtered or read. Fixed VCS metadata directories
    /// are skipped with Incomplete; linked/reparse entries are skipped with Incomplete and linked root ancestors
    /// refuse the scan. Complete means only that this raw traversal finished within its stated exclusions and budgets;
    /// it never proves effective discovery, absence under ignore rules, or a candidate's ownership/importability.
    /// Root/entry attribute checks are point-in-time, not handles pinned against external path swaps. Network mounts
    /// cannot be reliably distinguished from local paths. Filesystem operations and enumerator disposal can block
    /// despite cancellation, so these are entry/allocation budgets, not a wall-clock or atomic security boundary.
    /// </remarks>
    /// <param name="rootPath">Explicit canonical, fully qualified non-root local directory; no expansion or discovery.</param>
    /// <param name="cancellationToken">Checked before and between filesystem operations.</param>
    /// <returns>Bounded sorted observed subset and raw traversal evidence only.</returns>
    /// <exception cref="OperationCanceledException">The caller canceled before or between filesystem operations.</exception>
    public static RawSkillCandidateResult Scan(string? rootPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RawSkillCandidateResult refused(RawSkillCandidateStatus status, RawSkillCandidateDiagnostics reason = RawSkillCandidateDiagnostics.None)
            => new(status, [], 0, 0, reason);
        if (rootPath is not { Length: > 0 and <= MaximumPathLength }) return refused(RawSkillCandidateStatus.Invalid);
        try
        {
            if (!Path.IsPathFullyQualified(rootPath) || rootPath.StartsWith("//", StringComparison.Ordinal) ||
                rootPath.StartsWith(@"\\", StringComparison.Ordinal) ||
                !string.Equals(rootPath, Path.GetFullPath(rootPath), StringComparison.Ordinal) ||
                string.Equals(rootPath, Path.GetPathRoot(rootPath), StringComparison.Ordinal))
                return refused(RawSkillCandidateStatus.Invalid);
            var root = Path.GetPathRoot(rootPath);
            if (root is null || OperatingSystem.IsWindows() &&
                (root.Length != 3 || !char.IsLetter(root[0]) || root[1] != ':' || root[2] != '\\'))
                return refused(RawSkillCandidateStatus.Invalid);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return refused(RawSkillCandidateStatus.Invalid); }

        var ancestors = new Stack<DirectoryInfo>();
        try
        {
            for (var directory = new DirectoryInfo(rootPath); directory is not null; directory = directory.Parent)
            {
                if (ancestors.Count == MaximumRootComponents)
                    return refused(RawSkillCandidateStatus.Invalid, RawSkillCandidateDiagnostics.RootComponentLimit);
                if (IsMetadataDirectory(directory.Name)) return refused(RawSkillCandidateStatus.Invalid);
                ancestors.Push(directory);
            }
            while (ancestors.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(ancestors.Pop().FullName);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    return refused(RawSkillCandidateStatus.ReadError, RawSkillCandidateDiagnostics.LinkedRoot);
                if (!attributes.HasFlag(FileAttributes.Directory)) return refused(RawSkillCandidateStatus.NotDirectory);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return refused(RawSkillCandidateStatus.Missing); }
        catch (UnauthorizedAccessException) { return refused(RawSkillCandidateStatus.Denied, RawSkillCandidateDiagnostics.ReadFailure); }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        { return refused(RawSkillCandidateStatus.ReadError, RawSkillCandidateDiagnostics.ReadFailure); }

        var entries = 0;
        var directories = 0;
        var candidateCharacters = 0;
        var diagnostics = RawSkillCandidateDiagnostics.None;
        var candidates = new List<string>(MaximumCandidates);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((rootPath, 0));
        RawSkillCandidateResult result(RawSkillCandidateStatus status)
        {
            candidates.Sort(static (left, right) =>
            {
                var comparison = StringComparer.OrdinalIgnoreCase.Compare(left, right);
                return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left, right);
            });
            return new(status, candidates.ToArray(), entries, directories, diagnostics);
        }
        try
        {
            while (pending.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (directories == MaximumDirectories)
                {
                    diagnostics |= RawSkillCandidateDiagnostics.DirectoryLimit;
                    return result(RawSkillCandidateStatus.Incomplete);
                }
                var (directory, depth) = pending.Pop();
                directories++;
                // Only enumerate this one directory; names are materialized one at a time and only within the
                // global entry budget. XenoAtom's walker filters entries before yielding and reads ignore state.
                var enumerable = new FileSystemEnumerable<(string? Name, FileAttributes Attributes)>(directory,
                    static (ref FileSystemEntry entry) => (entry.FileName.Length <= MaximumNameLength ? entry.FileName.ToString() : null,
                        entry.Attributes), new EnumerationOptions
                    {
                        AttributesToSkip = 0,
                        IgnoreInaccessible = false,
                        RecurseSubdirectories = false,
                    });
                using var enumerator = enumerable.GetEnumerator();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!enumerator.MoveNext()) break;
                    if (++entries > MaximumEntries)
                    {
                        diagnostics |= RawSkillCandidateDiagnostics.EntryLimit;
                        return result(RawSkillCandidateStatus.Incomplete);
                    }
                    var (name, attributes) = enumerator.Current;
                    if (name is null)
                    {
                        diagnostics |= RawSkillCandidateDiagnostics.PathLimit;
                        continue;
                    }
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        diagnostics |= RawSkillCandidateDiagnostics.LinkedEntry;
                        continue;
                    }
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        if (IsMetadataDirectory(name))
                        {
                            diagnostics |= RawSkillCandidateDiagnostics.MetadataDirectory;
                            continue;
                        }
                        if (depth == MaximumDepth)
                        {
                            diagnostics |= RawSkillCandidateDiagnostics.DepthLimit;
                            continue;
                        }
                        if (directory.Length + 1 + name.Length > MaximumPathLength)
                        {
                            diagnostics |= RawSkillCandidateDiagnostics.PathLimit;
                            continue;
                        }
                        if (pending.Count == MaximumPendingDirectories)
                        {
                            diagnostics |= RawSkillCandidateDiagnostics.PendingDirectoryLimit;
                            continue;
                        }
                        pending.Push((Path.Join(directory, name), depth + 1));
                        continue;
                    }
                    if (!string.Equals(name, "SKILL.md", StringComparison.OrdinalIgnoreCase)) continue;
                    if (directory.Length + 1 + name.Length > MaximumPathLength)
                    {
                        diagnostics |= RawSkillCandidateDiagnostics.PathLimit;
                        continue;
                    }
                    if (candidates.Count == MaximumCandidates)
                    {
                        diagnostics |= RawSkillCandidateDiagnostics.CandidateLimit;
                        return result(RawSkillCandidateStatus.Incomplete);
                    }
                    var candidate = Path.Join(directory, name);
                    if (candidateCharacters + candidate.Length > MaximumTotalCandidateCharacters)
                    {
                        diagnostics |= RawSkillCandidateDiagnostics.CandidateCharactersLimit;
                        return result(RawSkillCandidateStatus.Incomplete);
                    }
                    candidates.Add(candidate);
                    candidateCharacters += candidate.Length;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result(diagnostics == RawSkillCandidateDiagnostics.None ? RawSkillCandidateStatus.Complete : RawSkillCandidateStatus.Incomplete);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException)
        {
            diagnostics |= RawSkillCandidateDiagnostics.ReadFailure;
            return result(RawSkillCandidateStatus.Denied);
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            diagnostics |= RawSkillCandidateDiagnostics.ReadFailure;
            return result(RawSkillCandidateStatus.ReadError);
        }
    }

    private static bool IsMetadataDirectory(string name) =>
        name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals(".hg", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".svn", StringComparison.OrdinalIgnoreCase) || name.Equals(".jj", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".sl", StringComparison.OrdinalIgnoreCase);
}
