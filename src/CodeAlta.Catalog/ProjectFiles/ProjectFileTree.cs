using System.IO.Enumeration;
using XenoAtom.Glob.Git;
using XenoAtom.Glob.IO;
using XenoAtom.Glob.Ignore;

namespace CodeAlta.Catalog;

/// <summary>One entry of a folder of a project.</summary>
/// <param name="Name">The name of the file or folder.</param>
/// <param name="IsDirectory">Whether it is a folder.</param>
/// <param name="IsIgnored">Whether an ignore rule of the repository excludes it.</param>
public readonly record struct ProjectFolderEntry(string Name, bool IsDirectory, bool IsIgnored);

/// <summary>The entries of one folder of a project.</summary>
/// <param name="Entries">Folders first, then files, each in the order of <see cref="ProjectFileNameComparer"/>.</param>
/// <param name="Truncated">Whether the folder holds more entries than were asked for.</param>
public sealed record ProjectFolderListing(IReadOnlyList<ProjectFolderEntry> Entries, bool Truncated);

/// <summary>A file found by walking a project.</summary>
/// <param name="RelativePath">The path relative to the project folder, with forward slashes.</param>
/// <param name="FullPath">The full path of the file.</param>
/// <param name="Length">The size of the file in bytes.</param>
public readonly record struct ProjectTreeFile(string RelativePath, string FullPath, long Length);

/// <summary>
/// Reads the files of a project the way its file search sees them: what git ignores (<c>.gitignore</c>,
/// <c>.git/info/exclude</c>, the global excludes file), the folders of version control systems and links
/// are left out.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ListFolder"/> reads one folder and nothing below it, so a tree can be discovered as it is
/// opened. <see cref="EnumerateFiles"/> walks the whole project with the XenoAtom.Glob scanner.
/// </para>
/// <para>
/// The ignore rules of a folder are those of the nearest git work tree at or above it; a folder outside any
/// work tree uses the <c>.gitignore</c> files between the project folder and itself. Parsed ignore files are
/// kept by this instance while they do not change. An instance may be used from several threads.
/// </para>
/// </remarks>
public sealed class ProjectFileTree
{
    // The folders of version control systems are never part of a project's files.
    internal static readonly IgnoreRuleSet FixedExclusionRules = IgnoreRuleSet.ParseGitIgnore(".git/\n.hg/\n.svn/\n.jj/\n.sl/\n");

    // A work tree's excludes files are looked up again after this long.
    private const long RepositoryLifetimeMilliseconds = 30_000;

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CachedRules> _rules = new(PathComparer);
    private readonly Dictionary<string, CachedRepository> _repositories = new(PathComparer);
    private readonly FileTreeWalker _walker = new();

    /// <summary>Lists the entries of one folder of a project, without reading the folders below it.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="relativeFolder">The folder relative to the project folder, or an empty string for the project folder itself.</param>
    /// <param name="includeIgnored">Returns the ignored entries too, marked as such, instead of leaving them out.</param>
    /// <param name="maximumEntries">The largest number of entries returned.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The entries, folders first.</returns>
    /// <exception cref="ArgumentException">A path is blank, rooted or names a parent folder.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumEntries"/> is not positive.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="IOException">The folder cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the folder is denied.</exception>
    /// <exception cref="OperationCanceledException">The listing was canceled.</exception>
    public ProjectFolderListing ListFolder(string projectRoot, string relativeFolder, bool includeIgnored, int maximumEntries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relativeFolder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        var root = ProjectFilePathUtilities.NormalizeProjectRoot(projectRoot);
        var folder = relativeFolder.Replace('\\', '/').Trim('/');
        if (folder.Length > 0 && (Path.IsPathRooted(folder) || folder.Contains(':') || folder.Split('/').Any(static part => part is "" or "." or "..")))
        {
            throw new ArgumentException("The folder must be a path inside the project folder.", nameof(relativeFolder));
        }

        var directory = folder.Length == 0 ? root : Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
        var scope = OpenScope(root, directory);
        using var evaluator = scope.Matcher.CreateEvaluator();
        var entries = new List<ProjectFolderEntry>();
        var listed = new FileSystemEnumerable<(string Name, bool IsDirectory)>(directory,
            static (ref FileSystemEntry entry) => (entry.FileName.ToString(), entry.IsDirectory),
            new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false })
        {
            // Links are not followed, as in the file search: what they name may be outside the project.
            ShouldIncludePredicate = static (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0 && !IsRepositoryEntry(entry.FileName, entry.IsDirectory),
        };
        foreach (var (name, isDirectory) in listed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ignored = IsIgnored(evaluator, scope.Prefix.Length == 0 ? name : $"{scope.Prefix}/{name}", isDirectory);
            if (ignored && !includeIgnored) continue;
            entries.Add(new(name, isDirectory, ignored));
        }

        entries.Sort(static (left, right) => left.IsDirectory != right.IsDirectory ? (left.IsDirectory ? -1 : 1) : ProjectFileNameComparer.Instance.Compare(left.Name, right.Name));
        var truncated = entries.Count > maximumEntries;
        if (truncated) entries.RemoveRange(maximumEntries, entries.Count - maximumEntries);
        return new(entries, truncated);
    }

    /// <summary>Walks every file of a project that is not ignored, with the XenoAtom.Glob scanner.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>A lazy sequence of files; enumerate it from one thread.</returns>
    /// <exception cref="ArgumentException">The project folder is blank.</exception>
    public IEnumerable<ProjectTreeFile> EnumerateFiles(string projectRoot, CancellationToken cancellationToken)
    {
        var root = ProjectFilePathUtilities.NormalizeProjectRoot(projectRoot);
        return Walk(root, CreateWalkOptions(_walker, root, includeDirectories: false, cancellationToken, out _));
    }

    private IEnumerable<ProjectTreeFile> Walk(string root, FileTreeWalkOptions options)
    {
        foreach (var entry in _walker.Enumerate(root, options))
        {
            // The scanner names a file relative to the work tree, which may start above the project folder.
            var relative = entry.FullPath.Length > root.Length + 1 && entry.FullPath.StartsWith(root, StringComparison.Ordinal)
                ? entry.FullPath[(root.Length + 1)..] : Path.GetRelativePath(root, entry.FullPath);
            yield return new(relative.Replace('\\', '/'), entry.FullPath, entry.Length);
        }
    }

    /// <summary>
    /// The options of a walk of a whole project: the ignore rules of its git work tree, or, outside one, of
    /// every <c>.gitignore</c> the project holds.
    /// </summary>
    internal static FileTreeWalkOptions CreateWalkOptions(FileTreeWalker walker, string normalizedRoot, bool includeDirectories, CancellationToken cancellationToken, out bool isGitAware)
    {
        isGitAware = RepositoryDiscovery.TryDiscover(normalizedRoot, out var repositoryContext);
        return new FileTreeWalkOptions
        {
            IncludeDirectories = includeDirectories,
            CancellationToken = cancellationToken,
            RepositoryContext = repositoryContext,
            AdditionalRuleSets = isGitAware ? [FixedExclusionRules] : BuildNonGitRuleSets(walker, normalizedRoot, cancellationToken),
        };
    }

    private static IReadOnlyList<IgnoreRuleSet> BuildNonGitRuleSets(FileTreeWalker walker, string projectRoot, CancellationToken cancellationToken)
    {
        var ruleSets = new List<IgnoreRuleSet> { FixedExclusionRules };
        var discoveryOptions = new FileTreeWalkOptions { CancellationToken = cancellationToken, AdditionalRuleSets = [FixedExclusionRules] };
        foreach (var entry in walker.Enumerate(projectRoot, discoveryOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(entry.Name, ".gitignore", StringComparison.Ordinal)) continue;
            var baseDirectory = Path.GetDirectoryName(entry.RelativePath)?.Replace('\\', '/') ?? string.Empty;
            ruleSets.Add(IgnoreRuleSet.ParseGitIgnore(File.ReadAllText(entry.FullPath), baseDirectory: baseDirectory, sourcePath: entry.FullPath));
        }

        return ruleSets;
    }

    private static bool IsRepositoryEntry(ReadOnlySpan<char> name, bool isDirectory)
        => name is ".git" || isDirectory && name is ".hg" or ".svn" or ".jj" or ".sl";

    private static bool IsIgnored(IgnoreMatcherEvaluator evaluator, string path, bool isDirectory)
    {
        try
        {
            return evaluator.Evaluate(path, isDirectory).IsIgnored;
        }
        catch (ArgumentException)
        {
            // A name the rules cannot be asked about (a backslash in a name on Linux) is not ignored.
            return false;
        }
    }

    // The rules that apply inside a folder: those of its work tree, or of the project folder outside one.
    private IgnoreScope OpenScope(string root, string directory)
    {
        var repository = FindRepository(directory);
        var scopeRoot = repository?.Root ?? root;
        var prefix = PathComparer.Equals(scopeRoot, directory) ? string.Empty
            : Path.GetRelativePath(scopeRoot, directory).Replace('\\', '/').Trim('/');
        var ruleSets = new List<IgnoreRuleSet>();
        if (repository is not null)
        {
            if (repository.GlobalExcludePath is { } global) AddRules(ruleSets, global, string.Empty);
            AddRules(ruleSets, repository.InfoExcludePath, string.Empty);
        }

        AddRules(ruleSets, Path.Combine(scopeRoot, ".gitignore"), string.Empty);
        var current = string.Empty;
        foreach (var segment in prefix.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? segment : $"{current}/{segment}";
            AddRules(ruleSets, Path.Combine(scopeRoot, current.Replace('/', Path.DirectorySeparatorChar), ".gitignore"), current);
        }

        return new(new IgnoreMatcher(ruleSets), prefix);
    }

    // Adds the rules of an ignore file, parsed once for each content it has had.
    private void AddRules(List<IgnoreRuleSet> ruleSets, string path, string baseDirectory)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            // Git does not read an ignore file through a link.
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0) return;
            var key = $"{baseDirectory}\n{path}";
            lock (_gate)
            {
                if (_rules.TryGetValue(key, out var cached) && cached.WriteTimeUtc == file.LastWriteTimeUtc && cached.Length == file.Length)
                {
                    ruleSets.Add(cached.Rules);
                    return;
                }
            }

            var rules = IgnoreRuleSet.ParseGitIgnore(File.ReadAllText(path), baseDirectory: baseDirectory, sourcePath: path);
            lock (_gate) _rules[key] = new(file.LastWriteTimeUtc, file.Length, rules);
            ruleSets.Add(rules);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            // An ignore file that cannot be read ignores nothing.
        }
    }

    // The nearest folder at or above a folder that is a git work tree, with where its excludes files are.
    private CachedRepository? FindRepository(string directory)
    {
        for (var current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            var marker = Path.Combine(current, ".git");
            if (!Directory.Exists(marker) && !File.Exists(marker)) continue;
            var now = Environment.TickCount64;
            lock (_gate)
            {
                if (_repositories.TryGetValue(current, out var cached) && now - cached.ReadAt < RepositoryLifetimeMilliseconds) return cached;
            }

            CachedRepository repository;
            try
            {
                repository = RepositoryDiscovery.TryDiscover(current, out var context) && context is not null
                    ? new(current, context.GlobalExcludePath, context.InfoExcludePath, now)
                    : new(current, null, Path.Combine(marker, "info", "exclude"), now);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                // A work tree whose git folder cannot be read still has its .gitignore files.
                repository = new(current, null, Path.Combine(marker, "info", "exclude"), now);
            }

            lock (_gate) _repositories[current] = repository;
            return repository;
        }

        return null;
    }

    private sealed record IgnoreScope(IgnoreMatcher Matcher, string Prefix);

    private sealed record CachedRepository(string Root, string? GlobalExcludePath, string InfoExcludePath, long ReadAt);

    private readonly record struct CachedRules(DateTime WriteTimeUtc, long Length, IgnoreRuleSet Rules);
}

/// <summary>
/// Orders file names the way a file explorer does: without regard to case, and with the numbers in a name
/// compared by value, so that <c>file2</c> comes before <c>file10</c>.
/// </summary>
public sealed class ProjectFileNameComparer : IComparer<string>
{
    private ProjectFileNameComparer()
    {
    }

    /// <summary>The comparer.</summary>
    public static ProjectFileNameComparer Instance { get; } = new();

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int left = 0, right = 0;
        while (left < x.Length && right < y.Length)
        {
            if (char.IsAsciiDigit(x[left]) && char.IsAsciiDigit(y[right]))
            {
                // Two numbers: the one with more digits after its leading zeros is the larger.
                while (left < x.Length && x[left] == '0') left++;
                while (right < y.Length && y[right] == '0') right++;
                int leftStart = left, rightStart = right;
                while (left < x.Length && char.IsAsciiDigit(x[left])) left++;
                while (right < y.Length && char.IsAsciiDigit(y[right])) right++;
                var digits = (left - leftStart) - (right - rightStart);
                if (digits == 0) digits = x.AsSpan(leftStart, left - leftStart).SequenceCompareTo(y.AsSpan(rightStart, right - rightStart));
                if (digits != 0) return digits;
                continue;
            }

            char a = char.ToLowerInvariant(x[left]), b = char.ToLowerInvariant(y[right]);
            if (a != b) return a.CompareTo(b);
            left++;
            right++;
        }

        var rest = (x.Length - left) - (y.Length - right);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }
}
